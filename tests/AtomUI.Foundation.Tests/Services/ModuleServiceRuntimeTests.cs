using AtomUI.Lifetimes;
using AtomUI.Modularity;
using AtomUI.Modularity.Services;
using Shouldly;
using Xunit;

namespace AtomUI.Foundation.Tests.Services;

public sealed class ModuleServiceRuntimeTests
{
    private static readonly ModuleId Owner = ModuleId.FromModuleType<TestModule>();

    [Fact]
    public async Task Scoped_and_transient_instances_have_real_owners_and_scope_boundaries()
    {
        var runtime = new ModuleServiceRuntime();
        runtime.AddScopedFactory<Resource>(Owner, _ => new(), Release);
        runtime.AddTransientFactory<Transient>(Owner, _ => new(), _ => ValueTask.CompletedTask);
        runtime.Freeze();
        var first = runtime.Root.CreateScope();
        var second = runtime.Root.CreateScope();
        var a = first.Borrow<Resource>();
        var b = first.Borrow<Resource>();
        var c = second.Borrow<Resource>();
        a.Value.ShouldBeSameAs(b.Value);
        a.Value.ShouldNotBeSameAs(c.Value);
        using (var t1 = first.Borrow<Transient>())
        using (var t2 = first.Borrow<Transient>())
            t1.Value.ShouldNotBeSameAs(t2.Value);
        var value = a.Value;
        var close = first.BeginRetirement();
        close.FinalCompletion.IsCompleted.ShouldBeFalse();
        value.Releases.ShouldBe(0);
        a.Dispose(); b.Dispose();
        (await close.FinalCompletion).IsSuccess.ShouldBeTrue();
        value.Releases.ShouldBe(1);
        c.Dispose();
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_singleton_factory_runs_once()
    {
        var runtime = new ModuleServiceRuntime();
        var created = 0;
        runtime.AddSingletonFactory<Resource>(Owner, _ => { Interlocked.Increment(ref created); return new(); }, Release);
        runtime.Freeze();
        var leases = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => runtime.Root.Borrow<Resource>())));
        created.ShouldBe(1);
        leases.ShouldAllBe(lease => ReferenceEquals(lease.Value, leases[0].Value));
        foreach (var lease in leases) lease.Dispose();
        await runtime.DisposeAsync();
    }

    [Fact]
    public void Freeze_rejects_missing_cycle_and_transitive_scoped_capture_without_factories()
    {
        var missing = new ModuleServiceRuntime();
        var factories = 0;
        missing.AddSingletonFactory<Resource>(Owner, _ => { factories++; return new(); }, Release, dependencies: [typeof(Dependency)]);
        Should.Throw<ModuleServiceValidationException>(missing.Freeze).Diagnostics.ShouldNotBeEmpty();
        factories.ShouldBe(0);
        var cycle = new ModuleServiceRuntime();
        cycle.AddSingletonFactory<Resource>(Owner, _ => new(), Release, dependencies: [typeof(Dependency)]);
        cycle.AddSingletonFactory<Dependency>(Owner, _ => new(), _ => ValueTask.CompletedTask, dependencies: [typeof(Resource)]);
        Should.Throw<ModuleServiceValidationException>(cycle.Freeze);
        var captive = new ModuleServiceRuntime();
        captive.AddSingletonFactory<Resource>(Owner, _ => new(), Release, dependencies: [typeof(Dependency)]);
        captive.AddTransientFactory<Dependency>(Owner, _ => new(), _ => ValueTask.CompletedTask, dependencies: [typeof(Transient)]);
        captive.AddScopedFactory<Transient>(Owner, _ => new(), _ => ValueTask.CompletedTask);
        Should.Throw<ModuleServiceValidationException>(captive.Freeze);
    }

    [Fact]
    public async Task Factory_context_captures_declared_dependency_and_cannot_escape()
    {
        var runtime = new ModuleServiceRuntime();
        ModuleServiceActivationContext? captured = null;
        Resource? dependency = null;
        var order = new List<string>();
        runtime.AddScopedFactory<Resource>(Owner, _ => dependency = new(), value => { value.Releases++; order.Add("dependency"); return ValueTask.CompletedTask; });
        runtime.AddScopedFactory<Consumer>(Owner, context => { captured = context; return new(context.GetRequiredService<Resource>()); },
            _ => { order.Add("consumer"); return ValueTask.CompletedTask; }, dependencies: [typeof(Resource)]);
        runtime.Freeze();
        var scope = runtime.Root.CreateScope();
        var lease = scope.Borrow<Consumer>();
        lease.Value.Dependency.ShouldBeSameAs(dependency);
        Should.Throw<ObjectDisposedException>(() => captured!.GetRequiredService<Resource>());
        var retirement = scope.BeginRetirement();
        order.ShouldBeEmpty();
        lease.Dispose();
        (await retirement.FinalCompletion).IsSuccess.ShouldBeTrue();
        order.ShouldBe(["consumer", "dependency"]);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Sharing_two_consumers_does_not_create_or_reown_model_service()
    {
        var runtime = new ModuleServiceRuntime();
        var factories = 0;
        runtime.AddScopedFactory<Resource>(Owner, _ => { factories++; return new(); }, Release);
        runtime.Freeze();
        var model = runtime.Root.CreateScope();
        var view1 = runtime.Root.CreateScope();
        var view2 = runtime.Root.CreateScope();
        view1.ShareFrom<Resource>(model);
        view2.ShareFrom<Resource>(model);
        var first = view1.Borrow<Resource>();
        var second = view2.Borrow<Resource>();
        var value = first.Value;
        second.Value.ShouldBeSameAs(value);
        factories.ShouldBe(1);
        first.Dispose();
        await view1.DisposeAsync();
        value.Releases.ShouldBe(0);
        var retired = model.BeginRetirement();
        retired.FinalCompletion.IsCompleted.ShouldBeFalse();
        second.Value.ShouldBeSameAs(value);
        second.Dispose();
        (await retired.FinalCompletion).IsSuccess.ShouldBeTrue();
        value.Releases.ShouldBe(1);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Root_scope_cannot_share_from_a_shorter_lived_scope()
    {
        var runtime = new ModuleServiceRuntime();
        runtime.AddScopedFactory<Resource>(Owner, _ => new(), Release);
        runtime.Freeze();
        var child = runtime.Root.CreateScope();
        var childLease = child.Borrow<Resource>();

        Should.Throw<InvalidOperationException>(() => runtime.Root.ShareFrom<Resource>(child));

        var rootLease = runtime.Root.Borrow<Resource>();
        rootLease.Value.ShouldNotBeSameAs(childLease.Value);
        var childRetirement = child.BeginRetirement();
        childRetirement.FinalCompletion.IsCompleted.ShouldBeFalse();
        childLease.Dispose();
        (await childRetirement.FinalCompletion).IsSuccess.ShouldBeTrue();
        rootLease.Value.ShouldNotBeNull();
        rootLease.Dispose();
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Host_waits_for_result_lease_after_all_stop_and_module_callbacks()
    {
        var runtime = new ModuleServiceRuntime();
        var log = new List<string>();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Resource? value = null;
        runtime.AddSingletonFactory<Resource>(Owner, _ => value = new(), resource => { resource.Releases++; log.Add("release"); return ValueTask.CompletedTask; },
            _ => { log.Add("stop"); stopped.SetResult(); });
        var host = new ModuleHost([ModuleRegistration.For(ModuleDescriptor.For<TestModule>("test"), () => new TestModule(() => log.Add("module")))],
            new ModuleHostOptionsBuilder().Enable<TestModule>().UseLifetime(runtime).Build());
        (await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        var resultReader = runtime.Root.Borrow<Resource>();
        var closing = host.ShutdownAsync(TestContext.Current.CancellationToken).AsTask();
        await stopped.Task;
        closing.IsCompleted.ShouldBeFalse();
        value!.Releases.ShouldBe(0);
        resultReader.Value.ShouldBeSameAs(value);
        resultReader.Dispose();
        (await closing).IsSuccess.ShouldBeTrue();
        log.ShouldBe(["stop", "module", "release"]);
    }

    [Fact]
    public async Task Factory_retirement_race_keeps_late_instance_tracked()
    {
        var runtime = new ModuleServiceRuntime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finish = new ManualResetEventSlim();
        var resource = new Resource();
        runtime.AddScopedFactory<Resource>(Owner, _ => { entered.SetResult(); finish.Wait(); return resource; }, Release);
        runtime.Freeze();
        var scope = runtime.Root.CreateScope();
        var activating = Task.Run(() => { try { return scope.Borrow<Resource>(); } catch (ObjectDisposedException) { return null; } });
        await entered.Task;
        var close = scope.BeginRetirement();
        close.FinalCompletion.IsCompleted.ShouldBeFalse();
        finish.Set();
        (await activating)?.Dispose();
        (await close.FinalCompletion).IsSuccess.ShouldBeTrue();
        resource.Releases.ShouldBe(1);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Failed_consumer_retains_its_dependency_while_independent_service_is_released()
    {
        var runtime = new ModuleServiceRuntime();
        var resource = new Resource();
        var independent = 0;
        var attempts = 0;
        runtime.AddScopedFactory<Resource>(Owner, _ => resource, Release);
        runtime.AddScopedFactory<Consumer>(Owner, context => new(context.GetRequiredService<Resource>()),
            _ => { attempts++; throw new InvalidOperationException("release failed"); }, dependencies: [typeof(Resource)]);
        runtime.AddScopedFactory<Transient>(Owner, _ => new(), _ => { independent++; return ValueTask.CompletedTask; });
        runtime.Freeze();
        runtime.Root.Borrow<Consumer>().Dispose();
        runtime.Root.Borrow<Transient>().Dispose();
        var retirement = runtime.Root.BeginRetirement();
        var result = await retirement.FinalCompletion;
        result.IsSuccess.ShouldBeFalse();
        result.Owners.ShouldContain(owner => owner.State == LifetimeOwnerState.RetainedByFailedOwner);
        resource.Releases.ShouldBe(0);
        independent.ShouldBe(1);
        attempts.ShouldBe(1);
        runtime.Root.BeginRetirement().ShouldBeSameAs(retirement);
        var retainedOwnerId = result.Owners.Single(owner => owner.State == LifetimeOwnerState.RetainedByFailedOwner)
            .Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var shutdown = await Should.ThrowAsync<ModuleServiceShutdownException>(() => runtime.DisposeAsync().AsTask());
        Assert.Contains(shutdown.Diagnostics, diagnostic =>
            diagnostic.Code == "ATOMUISVC006" &&
            diagnostic.Context?.GetValueOrDefault("ownerId") == retainedOwnerId);
        attempts.ShouldBe(1);
    }

    [Fact]
    public async Task Factory_context_cannot_resolve_from_a_different_thread()
    {
        var runtime = new ModuleServiceRuntime();
        runtime.AddSingletonFactory<Resource>(Owner, _ => new(), Release);
        runtime.AddSingletonFactory<Consumer>(Owner, context =>
        {
            Task.Factory.StartNew(() => Should.Throw<InvalidOperationException>(() => context.GetRequiredService<Resource>()),
                TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult();
            return new(context.GetRequiredService<Resource>());
        }, _ => ValueTask.CompletedTask, dependencies: [typeof(Resource)]);
        runtime.Freeze();
        runtime.Root.Borrow<Consumer>().Dispose();
        await runtime.DisposeAsync();
    }

    [Fact]
    public void Factory_cannot_resolve_an_undeclared_dependency()
    {
        var runtime = new ModuleServiceRuntime();
        var created = 0;
        runtime.AddSingletonFactory<Resource>(Owner, _ => { created++; return new(); }, Release);
        runtime.AddSingletonFactory<Consumer>(Owner, context => new(context.GetRequiredService<Resource>()), _ => ValueTask.CompletedTask);
        runtime.Freeze();
        Should.Throw<InvalidOperationException>(() => runtime.Root.Borrow<Consumer>());
        created.ShouldBe(0);
    }

    private static ValueTask Release(Resource value) { value.Releases++; return ValueTask.CompletedTask; }
    private sealed class Resource { public int Releases; }
    private sealed class Dependency;
    private sealed class Transient;
    private sealed record Consumer(Resource Dependency);
    private sealed class TestModule(Action? shutdown = null) : ModuleBase
    {
        public override void Shutdown(ModuleShutdownContext context) => shutdown?.Invoke();
    }
}
