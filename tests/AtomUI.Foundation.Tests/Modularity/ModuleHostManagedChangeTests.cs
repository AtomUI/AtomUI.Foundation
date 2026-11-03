using AtomUI.Lifetimes;
using AtomUI.Modularity;
using AtomUI.Modularity.Services;
using Xunit;

namespace AtomUI.Foundation.Tests.Modularity;

public sealed class ModuleHostManagedChangeTests
{
    [Fact]
    public async Task Candidate_domain_abort_can_wait_for_its_private_service_scope_to_release()
    {
        var runtime = new ModuleServiceRuntime(); var ra = Register(() => new A());
        var host = new ModuleHost([ra], Options(runtime, typeof(A)));
        await host.InitializeAsync(TestContext.Current.CancellationToken);
        var released = 0;
        ModuleServiceBindings? candidateBindings = null;
        var b = new B(context => context.ManagedServices!.AddScopedFactory(ModuleId.FromModuleType<B>(),
            _ => new Feature(), _ => { released++; return ValueTask.CompletedTask; }), failInitialization: true);
        var participant = new Participant(context =>
        {
            candidateBindings = context.ManagedServices!;
            var scope = candidateBindings.Root.CreateScope();
            using var borrow = scope.Borrow<Feature>();
            return new ScopePreparation(scope);
        });
        var result = await host.ChangeAsync(new ModuleChangeRequest(host.Current, [ra, Register(() => b)],
            Options(runtime, typeof(A), typeof(B)), participant: participant), TestContext.Current.CancellationToken);
        Assert.Equal(ModuleChangeStatus.Rejected, result.Status);
        try { Assert.True((await result.Retirement.FinalCompletion.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)).IsSuccess); }
        finally
        {
            candidateBindings!.CompleteRetirement();
            await result.Retirement.FinalCompletion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        Assert.Equal(1, released);
        Assert.True((await host.ShutdownAsync(TestContext.Current.CancellationToken)).IsSuccess);
    }

    [Fact]
    public async Task Domain_borrower_without_required_edge_joins_full_admission_and_stop_set()
    {
        var runtime = new ModuleServiceRuntime(); var events = new List<string>();
        runtime.AddSingletonFactory(ModuleId.FromModuleType<B>(), _ => new Feature(),
            _ => { events.Add("B.release"); return ValueTask.CompletedTask; }, _ => events.Add("B.stop"));
        var a = new A(); var ra = Register(() => a);
        var b = new B(_ => { }) { ShutdownAction = () => { Assert.Contains("A.work.stop", events); events.Add("B.module"); } };
        var host = new ModuleHost([ra, Register(() => b)], Options(runtime, typeof(A), typeof(B)));
        await host.InitializeAsync(TestContext.Current.CancellationToken);
        var actualBorrow = runtime.Root.Borrow<Feature>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparation = new BorrowPreparation(actualBorrow, events, entered, proceed);
        var change = host.ChangeAsync(new ModuleChangeRequest(host.Current, [ra], Options(runtime, typeof(A)),
            participant: new Participant(_ => preparation)), TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => host.AcquireView([ModuleId.FromModuleType<A>()]));
        proceed.SetResult();
        var result = await change;
        Assert.Equal(ModuleChangeStatus.Committed, result.Status);
        Assert.True((await result.Retirement.FinalCompletion.WaitAsync(TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(new[] { "B.stop", "A.work.stop", "B.module", "B.release" }, events);
        Assert.Same(a, Assert.Single(host.EnabledModules).Module);
        await host.ShutdownAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Adding_bound_module_preserves_singleton_and_existing_model_scope_identity()
    {
        var runtime = new ModuleServiceRuntime(); var stable = new Stable(); var models = 0;
        runtime.AddSingletonFactory(ModuleId.FromModuleType<A>(), _ => stable, static _ => ValueTask.CompletedTask);
        runtime.AddScopedFactory(ModuleId.FromModuleType<A>(), _ => { models++; return new Model(); }, static _ => ValueTask.CompletedTask);
        var a = new A(); var ra = Register(() => a);
        var host = new ModuleHost([ra], Options(runtime, typeof(A)));
        await host.InitializeAsync(TestContext.Current.CancellationToken);
        var scope = runtime.Root.CreateScope();
        using var oldSingleton = runtime.Root.Borrow<Stable>(); using var oldModel = scope.Borrow<Model>();
        var created = 0;
        var b = new B(context => context.ManagedServices!.AddSingletonFactory(ModuleId.FromModuleType<B>(),
            _ => { created++; return new Feature(); }, static _ => ValueTask.CompletedTask));
        var participant = new Participant(context =>
        {
            using var lease = context.ManagedServices!.Root.Borrow<Feature>();
            return new Preparation(lease.Value);
        });
        var result = await host.ChangeAsync(new ModuleChangeRequest(host.Current, [ra, Register(() => b)],
            Options(runtime, typeof(A), typeof(B)), participant: participant), TestContext.Current.CancellationToken);
        Assert.Equal(ModuleChangeStatus.Committed, result.Status);
        using var singleton = runtime.Root.Borrow<Stable>();
        using var model = host.Current.ManagedServices!.BindScope(scope).Borrow<Model>();
        using var feature = runtime.Root.Borrow<Feature>();
        Assert.Same(oldSingleton.Value, singleton.Value);
        Assert.Same(oldModel.Value, model.Value);
        Assert.Same(feature.Value, result.View.State);
        Assert.Equal(1, created); Assert.Equal(1, models); Assert.Equal(1, a.Configured);
        feature.Dispose(); singleton.Dispose(); model.Dispose(); oldSingleton.Dispose(); oldModel.Dispose();
        await scope.DisposeAsync();
        Assert.True((await host.ShutdownAsync(TestContext.Current.CancellationToken)).IsSuccess);
    }

    [Fact]
    public async Task Removed_service_keeps_original_result_reader_until_real_exit()
    {
        var runtime = new ModuleServiceRuntime(); var stopped = 0; var released = 0;
        var a = new A(); var ra = Register(() => a);
        var b = new B(context => context.ManagedServices!.AddSingletonFactory(ModuleId.FromModuleType<B>(),
            _ => new Feature(), _ => { released++; return ValueTask.CompletedTask; }, _ => stopped++));
        var host = new ModuleHost([ra, Register(() => b)], Options(runtime, typeof(A), typeof(B)));
        await host.InitializeAsync(TestContext.Current.CancellationToken);
        var reader = runtime.Root.Borrow<Feature>(); var original = reader.Value;
        var result = await host.ChangeAsync(new ModuleChangeRequest(host.Current, [ra], Options(runtime, typeof(A))), TestContext.Current.CancellationToken);
        Assert.Equal(ModuleChangeStatus.Committed, result.Status);
        Assert.Equal(1, stopped); Assert.Equal(0, released);
        Assert.False(result.Retirement.FinalCompletion.IsCompleted);
        var retirement = Assert.IsType<LifetimeRetirement>(result.Retirement.ManagedRetirement);
        var stoppedOwners = await retirement.Completion.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Contains(stoppedOwners.Owners, owner => owner.ModuleId == ModuleId.FromModuleType<B>() && owner.Readers == 1 && owner.State == LifetimeOwnerState.Retiring);
        Assert.Same(original, reader.Value);
        Assert.Throws<InvalidOperationException>(() => runtime.Root.Borrow<Feature>());
        Assert.Same(a, Assert.Single(host.EnabledModules).Module);
        reader.Dispose();
        Assert.True((await result.Retirement.FinalCompletion.WaitAsync(TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(1, released);
        Assert.All((await retirement.FinalCompletion).Owners, owner => Assert.Equal(LifetimeOwnerState.Released, owner.State));
        await host.ShutdownAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Failed_candidate_service_retains_capture_without_closing_current_shared_owner()
    {
        var runtime = new ModuleServiceRuntime(); var stable = new Stable(); var oldReleased = 0;
        runtime.AddSingletonFactory(ModuleId.FromModuleType<A>(), _ => stable, _ => { oldReleased++; return ValueTask.CompletedTask; });
        var ra = Register(() => new A());
        var host = new ModuleHost([ra], Options(runtime, typeof(A)));
        await host.InitializeAsync(TestContext.Current.CancellationToken);
        var baseline = host.Current;
        var b = new B(context => context.ManagedServices!.AddSingletonFactory(ModuleId.FromModuleType<B>(),
            activation => new Feature(activation.GetRequiredService<Stable>()),
            _ => throw new InvalidOperationException("candidate release"), dependencies: [typeof(Stable)]));
        var participant = new Participant(context =>
        {
            using var candidate = context.ManagedServices!.Root.Borrow<Feature>();
            throw new InvalidOperationException("domain rejected initialized candidate");
        });
        var result = await host.ChangeAsync(new ModuleChangeRequest(baseline, [ra, Register(() => b)],
            Options(runtime, typeof(A), typeof(B)), participant: participant), TestContext.Current.CancellationToken);
        Assert.Equal(ModuleChangeStatus.Rejected, result.Status);
        Assert.False((await result.Retirement.FinalCompletion).IsSuccess);
        Assert.Same(baseline, host.Current);
        using (var available = runtime.Root.Borrow<Stable>()) Assert.Same(stable, available.Value);
        Assert.Equal(0, oldReleased);
        Assert.False((await host.ShutdownAsync(TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(0, oldReleased);
    }

    [Fact]
    public async Task Actual_capture_without_required_edge_stops_all_services_before_module_callbacks()
    {
        var runtime = new ModuleServiceRuntime(); var events = new List<string>();
        runtime.AddSingletonFactory(ModuleId.FromModuleType<B>(), _ => new Feature(),
            _ => { events.Add("B.release"); return ValueTask.CompletedTask; }, _ => events.Add("B.stop"));
        runtime.AddSingletonFactory(ModuleId.FromModuleType<A>(), context => new Consumer(context.GetRequiredService<Feature>()),
            _ => { events.Add("A.release"); return ValueTask.CompletedTask; }, _ => events.Add("A.stop"), typeof(Feature));
        LifetimeLease<Consumer>? lease = null;
        var a = new A { ShutdownAction = () => { events.Add("A.module"); lease!.Dispose(); } };
        var b = new B(_ => { }) { ShutdownAction = () =>
        {
            Assert.Contains("A.stop", events); Assert.Contains("B.stop", events);
            events.Add("B.module");
        } };
        var host = new ModuleHost([Register(() => a), Register(() => b)], Options(runtime, typeof(A), typeof(B)));
        await host.InitializeAsync(TestContext.Current.CancellationToken);
        lease = runtime.Root.Borrow<Consumer>();
        var result = await host.ChangeAsync(new ModuleChangeRequest(host.Current, [], Options(runtime)), TestContext.Current.CancellationToken);
        Assert.Equal(ModuleChangeStatus.Committed, result.Status);
        Assert.True((await result.Retirement.FinalCompletion.WaitAsync(TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(new[] { "A.stop", "B.stop", "B.module", "A.module", "A.release", "B.release" }, events);
        Assert.True((await host.ShutdownAsync(TestContext.Current.CancellationToken)).IsSuccess);
    }

    private static ModuleHostOptions Options(ModuleServiceRuntime runtime, params Type[] types) => new(types) { Lifetime = runtime };
    private static ModuleRegistration Register<T>(Func<T> factory) where T : IModule => ModuleRegistration.For(ModuleDescriptor.For<T>(typeof(T).Name), factory);
    private sealed class Participant(Func<ModuleChangeContext, ModuleChangePreparation> prepare) : IModuleChangeParticipant
    {
        public ValueTask<ModuleChangePreparation> PrepareAsync(ModuleChangeContext context, CancellationToken cancellationToken) => ValueTask.FromResult(prepare(context));
    }
    private sealed class Preparation(object state) : ModuleChangePreparation { public override object State => state; }
    private sealed class ScopePreparation(ModuleServiceScope scope) : ModuleChangePreparation
    {
        public override object State { get; } = new object();
        public override async ValueTask AbortAsync() => await scope.DisposeAsync();
    }
    private sealed class BorrowPreparation(LifetimeLease<Feature> borrow, List<string> events,
        TaskCompletionSource entered, TaskCompletionSource proceed) : ModuleChangePreparation
    {
        public override object State { get; } = new object();
        public override IReadOnlyCollection<ModuleId> AffectedModules => new[] { ModuleId.FromModuleType<A>() };
        public override async ValueTask SuspendAsync(CancellationToken cancellationToken) { entered.SetResult(); await proceed.Task; }
        public override void StopPrevious() { events.Add("A.work.stop"); borrow.Dispose(); }
    }
    private class A : ModuleBase
    {
        public int Configured;
        public Action? ShutdownAction;
        public override void ConfigureServices(ModuleServiceConfigurationContext context) => Configured++;
        public override void Shutdown(ModuleShutdownContext context) => ShutdownAction?.Invoke();
    }
    private sealed class B(Action<ModuleServiceConfigurationContext> configure, bool failInitialization = false) : A
    {
        public override void ConfigureServices(ModuleServiceConfigurationContext context) { base.ConfigureServices(context); configure(context); }
        public override void Initialize(ModuleInitializationContext context)
        {
            if (failInitialization) throw new InvalidOperationException("candidate initialization");
        }
    }
    private sealed class Model;
    private sealed class Stable;
    private sealed class Feature(Stable? dependency = null) { public Stable? Dependency { get; } = dependency; }
    private sealed record Consumer(Feature Feature);
}
