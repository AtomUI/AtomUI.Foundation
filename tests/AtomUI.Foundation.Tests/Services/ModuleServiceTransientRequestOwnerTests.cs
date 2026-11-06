using AtomUI.Lifetimes;
using AtomUI.Modularity;
using AtomUI.Modularity.Services;
using Xunit;

namespace AtomUI.Foundation.Tests.Services;

/// <summary>P2 acceptance: Transient instances are request owners (2026-11-05 optimization proposal, W2).</summary>
public sealed class ModuleServiceTransientRequestOwnerTests
{
    private static readonly ModuleId Owner = ModuleId.FromSerializedValue("Tests.Transient");

    [Fact]
    public async Task Transient_stays_alive_until_its_last_lease_returns()
    {
        var runtime = new ModuleServiceRuntime();
        var releases = 0;
        runtime.AddTransientFactory<Transient>(Owner, _ => new(), value => { value.Released = true; releases++; return ValueTask.CompletedTask; });
        runtime.Freeze();

        var lease = runtime.Root.Borrow<Transient>();
        var value = lease.Value;
        Assert.Equal(0, releases);
        Assert.False(value.Released);
        lease.Dispose();

        Assert.Equal(1, releases);
        Assert.True(value.Released);
        Assert.True((await runtime.CompleteShutdownAsync()).IsSuccess);
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task Transient_captured_by_singleton_lives_and_releases_with_the_singleton()
    {
        var runtime = new ModuleServiceRuntime();
        var events = new List<string>();
        runtime.AddTransientFactory<Transient>(Owner, _ => new(), _ => { events.Add("transient"); return ValueTask.CompletedTask; });
        runtime.AddSingletonFactory<Holder>(Owner, context => new(context.GetRequiredService<Transient>()),
            _ => { events.Add("singleton"); return ValueTask.CompletedTask; }, dependencies: [typeof(Transient)]);
        runtime.Freeze();

        using (var holder = runtime.Root.Borrow<Holder>()) Assert.False(holder.Value.Dependency.Released);
        Assert.Empty(events);

        Assert.True((await runtime.CompleteShutdownAsync()).IsSuccess);
        Assert.Equal(["singleton", "transient"], events);
    }

    [Fact]
    public async Task Transient_captured_by_scoped_releases_when_its_scope_retires()
    {
        var runtime = new ModuleServiceRuntime();
        var transientReleases = 0;
        runtime.AddTransientFactory<Transient>(Owner, _ => new(), _ => { transientReleases++; return ValueTask.CompletedTask; });
        runtime.AddScopedFactory<Holder>(Owner, context => new(context.GetRequiredService<Transient>()),
            _ => ValueTask.CompletedTask, dependencies: [typeof(Transient)]);
        runtime.Freeze();
        var scope = runtime.Root.CreateScope();

        scope.Borrow<Holder>().Dispose();
        Assert.Equal(0, transientReleases);
        Assert.True((await scope.BeginRetirement().FinalCompletion).IsSuccess);
        Assert.Equal(1, transientReleases);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_transient_requests_each_release_exactly_once()
    {
        var runtime = new ModuleServiceRuntime();
        var created = 0;
        var released = 0;
        var violations = 0;
        runtime.AddTransientFactory<Transient>(Owner, _ => { Interlocked.Increment(ref created); return new(); }, value =>
        {
            if (value.Released) Interlocked.Increment(ref violations);
            value.Released = true;
            Interlocked.Increment(ref released);
            return ValueTask.CompletedTask;
        });
        runtime.Freeze();
        var scope = runtime.Root.CreateScope();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var index = 0; index < 64; index++)
                using (var lease = scope.Borrow<Transient>())
                    if (lease.Value.Released) Interlocked.Increment(ref violations);
        }, TestContext.Current.CancellationToken)));

        Assert.Equal(512, Volatile.Read(ref created));
        Assert.Equal(512, Volatile.Read(ref released));
        Assert.Equal(0, violations);
        Assert.True((await scope.BeginRetirement().FinalCompletion).IsSuccess);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Singleton_capturing_transient_is_reported_as_info_without_failing_startup()
    {
        var runtime = new ModuleServiceRuntime();
        var diagnostics = new RecordingDiagnostics();
        runtime.AddTransientFactory<Transient>(Owner, _ => new(), _ => ValueTask.CompletedTask);
        runtime.AddSingletonFactory<Holder>(Owner, context => new(context.GetRequiredService<Transient>()),
            _ => ValueTask.CompletedTask, dependencies: [typeof(Transient)]);
        var host = new ModuleHost([ModuleRegistration.For(ModuleDescriptor.For<Probe>("Probe"), () => new Probe())],
            new ModuleHostOptionsBuilder().Enable<Probe>().UseDiagnostics(diagnostics).UseLifetime(runtime).Build());

        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);

        var note = Assert.Single(diagnostics.Reported, diagnostic => diagnostic.Code == ModuleServiceDiagnosticCodes.SingletonCapturesTransient);
        Assert.Equal(ModuleDiagnosticSeverity.Info, note.Severity);
        Assert.Equal(typeof(Holder).FullName, note.Context!["serviceType"]);
        Assert.True((await host.ShutdownAsync(TestContext.Current.CancellationToken)).IsSuccess);
    }

    private sealed class Transient
    {
        public volatile bool Released;
    }

    private sealed class Holder(Transient dependency)
    {
        public Transient Dependency { get; } = dependency;
    }

    private sealed class Probe : ModuleBase;

    private sealed class RecordingDiagnostics : IModuleDiagnostics
    {
        public List<ModuleDiagnostic> Reported { get; } = [];
        public void Report(ModuleDiagnostic diagnostic) => Reported.Add(diagnostic);
    }
}
