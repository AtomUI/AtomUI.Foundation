using AtomUI.Modularity;
using Xunit;
using static AtomUI.Foundation.Tests.Modularity.ModuleHostCoordinationTests;

namespace AtomUI.Foundation.Tests.Modularity;

public sealed class ModuleHostLifetimeTests
{
    [Fact]
    public async Task Adapter_FreezesBeforeInitializeAndCompletesAfterAllShutdownCallbacks()
    {
        var events = new List<string>();
        var lifetime = new LifetimeProbe(events);
        var options = new ModuleHostOptionsBuilder().Enable<First>().Enable<Second>().UseLifetime(lifetime).Build();
        var host = new ModuleHost([
            Register(() => new First { OnInitialize = _ => { events.Add("first.initialize"); return ValueTask.CompletedTask; }, OnShutdown = _ => { events.Add("first.shutdown"); return ValueTask.CompletedTask; } }),
            Register(() => new Second { OnInitialize = _ => { events.Add("second.initialize"); return ValueTask.CompletedTask; }, OnShutdown = _ => { events.Add("second.shutdown"); return ValueTask.CompletedTask; } })], options);

        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await host.ShutdownAsync(TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await host.ShutdownAsync(TestContext.Current.CancellationToken)).IsSuccess);

        Assert.Equal(new[] { "freeze", "first.initialize", "second.initialize", "stop", "second.shutdown", "first.shutdown", "complete" }, events);
    }

    [Fact]
    public async Task AdapterFailures_StillAttemptAllModuleCallbacksAndFinalCompletion()
    {
        var events = new List<string>();
        var lifetime = new LifetimeProbe(events) { StopFailure = new InvalidOperationException("stop failed"), CompleteFailure = new InvalidOperationException("complete failed") };
        var host = new ModuleHost([
            Register(() => new First { OnShutdown = _ => { events.Add("first.shutdown"); throw new InvalidOperationException("module failed"); } }),
            Register(() => new Second { OnShutdown = _ => { events.Add("second.shutdown"); return ValueTask.CompletedTask; } })],
            new ModuleHostOptions([typeof(First), typeof(Second)]) { Lifetime = lifetime });
        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);

        var result = await host.ShutdownAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(new[] { "freeze", "stop", "second.shutdown", "first.shutdown", "complete" }, events);
        Assert.Equal(3, result.Diagnostics.Count(diagnostic => diagnostic.Severity == ModuleDiagnosticSeverity.Error));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Stage == "Stop");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Stage == "CompleteShutdown");
        Assert.Same(result, await host.ShutdownAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AdapterFreezeFailure_RollsBackCreatedModulesAndCompletesShutdown()
    {
        var events = new List<string>();
        var lifetime = new LifetimeProbe(events) { FreezeFailure = new InvalidOperationException("freeze failed") };
        var host = new ModuleHost([Register(() => new First { OnInitialize = _ => { events.Add("initialize"); return ValueTask.CompletedTask; }, OnShutdown = _ => { events.Add("shutdown"); return ValueTask.CompletedTask; } })], new ModuleHostOptions([typeof(First)]) { Lifetime = lifetime });

        var result = await host.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(new[] { "freeze", "stop", "shutdown", "complete" }, events);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Stage == "FreezeServices");
        Assert.Equal(ModuleHostPhase.Failed, host.Phase);
    }

    [Fact]
    public async Task Managed_service_validation_diagnostics_survive_host_freeze()
    {
        var runtime = new AtomUI.Modularity.Services.ModuleServiceRuntime();
        runtime.AddSingletonFactory<Dependency>(
            ModuleId.FromModuleType<First>(),
            _ => new Dependency(),
            static _ => ValueTask.CompletedTask,
            dependencies: [typeof(Second)]);
        var host = new ModuleHost([], new ModuleHostOptions { Lifetime = runtime });

        var result = await host.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "ATOMUISVC001" &&
            diagnostic.Stage == "FreezeManagedServices" &&
            diagnostic.Context?.GetValueOrDefault("serviceType") == typeof(Dependency).FullName);
    }

    [Fact]
    public async Task ShutdownCancellation_CancelsOnlyWaitAndPreservesCompletionFailure()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var lifetime = new LifetimeProbe(events) { Complete = async () => { entered.SetResult(); await release.Task; return new ModuleHostResult([new ModuleDiagnostic("adapter.failed", "retained resource")]); } };
        var host = new ModuleHost([], new ModuleHostOptions { Lifetime = lifetime });
        using var cancellation = new CancellationTokenSource();
        var waiting = host.ShutdownAsync(cancellation.Token).AsTask();
        await entered.Task;
        cancellation.Cancel();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting); }
        finally { release.SetResult(); }

        var result = await host.ShutdownAsync(TestContext.Current.CancellationToken);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "adapter.failed");
        Assert.Equal(new[] { "stop", "complete" }, events);
    }

    [Fact]
    public async Task PreCanceledShutdown_DoesNotStartCleanup()
    {
        var events = new List<string>();
        var host = new ModuleHost([], new ModuleHostOptions { Lifetime = new LifetimeProbe(events) });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ShutdownAsync(cancellation.Token).AsTask());

        Assert.Empty(events);
        Assert.Equal(ModuleHostPhase.Created, host.Phase);
        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);
    }

    private sealed class LifetimeProbe(List<string> events) : IModuleHostLifetime
    {
        public Exception? FreezeFailure { get; init; }
        public Exception? StopFailure { get; init; }
        public Exception? CompleteFailure { get; init; }
        public Func<ValueTask<ModuleHostResult>>? Complete { get; init; }
        public void Freeze() { events.Add("freeze"); if (FreezeFailure is not null) { throw FreezeFailure; } }
        public void Stop() { events.Add("stop"); if (StopFailure is not null) { throw StopFailure; } }
        public ValueTask<ModuleHostResult> CompleteShutdownAsync() { events.Add("complete"); if (CompleteFailure is not null) { throw CompleteFailure; } return Complete?.Invoke() ?? ValueTask.FromResult(ModuleHostResult.Success); }
    }

    private sealed class Dependency;
}
