using AtomUI.Modularity;
using System.Runtime.CompilerServices;
using Xunit;

namespace AtomUI.Foundation.Tests.Modularity;

public sealed class ModuleHostChangeTransactionTests
{
    [Fact]
    public async Task Removing_neutral_override_restores_surviving_contribution_without_replaying_module()
    {
        var original = new Contribution();
        var a = new A { OnConfigure = context => context.Services.AddSingleton(original) };
        var ra = Register(() => a);
        var host = new ModuleHost([ra, Register(() => new B())], Options(typeof(A), typeof(B)));
        await host.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.NotSame(original, host.Services.GetRequiredService<Contribution>());
        var result = await host.ChangeAsync(new ModuleChangeRequest(host.Current, [ra], Options(typeof(A))), TestContext.Current.CancellationToken);
        Assert.True(result.IsCommitted);
        Assert.Same(original, host.Services.GetRequiredService<Contribution>());
        Assert.Equal(1, a.Configured);
        Assert.True((await result.Retirement.FinalCompletion).IsSuccess);
        await host.ShutdownAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Replacing_neutral_domain_binding_does_not_retain_its_superseded_value()
    {
        var (host, previous) = await ReplaceNeutralDomainValueAsync();
        for (var index = 0; index < 3; index++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(previous.IsAlive);
        Assert.IsType<Contribution>(host.Services.GetRequiredService<Contribution>());
        await host.ShutdownAsync(TestContext.Current.CancellationToken);
        GC.KeepAlive(host);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(ModuleHost, WeakReference)> ReplaceNeutralDomainValueAsync()
    {
        var ra = Register(() => new A());
        var host = new ModuleHost([ra], Options(typeof(A)));
        await host.InitializeAsync(TestContext.Current.CancellationToken);
        var original = new Contribution();
        var first = await host.ChangeAsync(new ModuleChangeRequest(host.Current, [ra], Options(typeof(A)),
            participant: new NeutralParticipant(original)), TestContext.Current.CancellationToken);
        Assert.True(first.IsCommitted);
        var second = await host.ChangeAsync(new ModuleChangeRequest(host.Current, [ra], Options(typeof(A)),
            participant: new NeutralParticipant(new Contribution())), TestContext.Current.CancellationToken);
        Assert.True(second.IsCommitted);
        return (host, new WeakReference(original));
    }

    [Fact]
    public async Task Final_shutdown_can_dispatch_consumer_that_returns_another_modules_view_lease()
    {
        var a = new A(); var b = new B();
        var host = new ModuleHost([Register(() => a), Register(() => b)], Options(typeof(A), typeof(B)));
        await host.InitializeAsync(TestContext.Current.CancellationToken);
        var borrow = host.AcquireView([ModuleId.FromModuleType<B>()]);
        a.OnShutdown = () =>
        {
            Assert.Equal(0, b.Stopped);
            Assert.Same(b, Assert.Single(borrow.View.Modules, entry => entry.Module is B).Module);
            borrow.Dispose();
        };
        var close = host.ShutdownAsync(TestContext.Current.CancellationToken).AsTask();
        try { Assert.True((await close.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)).IsSuccess); }
        finally { borrow.Dispose(); await close.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); }
        Assert.Equal(1, a.Stopped); Assert.Equal(1, b.Stopped);
    }

    [Fact]
    public async Task Required_dependency_shutdown_waits_for_the_consumers_async_callback_exit()
    {
        var entered = Barrier(); var proceed = Barrier(); var b = new B();
        var dependent = ModuleRegistration.For(ModuleDescriptor.For<AwaitingA>("A", [ModuleDependencyDescriptor.Required<B>()]),
            () => new AwaitingA(entered, proceed));
        var host = new ModuleHost([Register(() => b), dependent], Options(typeof(AwaitingA)));
        await host.InitializeAsync(TestContext.Current.CancellationToken);
        var close = host.ShutdownAsync(TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, b.Stopped);
        proceed.SetResult();
        Assert.True((await close).IsSuccess);
        Assert.Equal(1, b.Stopped);
    }

    [Fact]
    public async Task Failed_retired_module_remains_owned_after_it_leaves_the_current_catalog()
    {
        var (host, weak) = await RetireFailingModuleAsync();
        for (var index = 0; index < 3; index++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        Assert.True(weak.IsAlive);
        Assert.False((await host.ShutdownAsync(TestContext.Current.CancellationToken)).IsSuccess);
        GC.KeepAlive(host);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(ModuleHost, WeakReference)> RetireFailingModuleAsync()
    {
        var b = new B { FailShutdown = true }; var ra = Register(() => new A()); var rb = Register(() => b);
        var host = new ModuleHost([ra, rb], Options(typeof(A), typeof(B)));
        await host.InitializeAsync(TestContext.Current.CancellationToken);
        var result = await host.ChangeAsync(new ModuleChangeRequest(host.Current, [ra], Options(typeof(A))), TestContext.Current.CancellationToken);
        Assert.False((await result.Retirement.FinalCompletion).IsSuccess);
        return (host, new WeakReference(b));
    }

    [Fact]
    public async Task Explicit_cascade_removes_dependent_roots_even_when_disabled_registration_is_removed()
    {
        var b = new B(); var c = new C();
        var rb = Register(() => b);
        var rc = ModuleRegistration.For(ModuleDescriptor.For<C>("C", [ModuleDependencyDescriptor.Required<B>()]), () => c);
        var host = new ModuleHost([rb, rc], Options(typeof(C)));
        await host.InitializeAsync(TestContext.Current.CancellationToken);
        var result = await host.ChangeAsync(new ModuleChangeRequest(host.Current, [rc], Options(typeof(C)),
            disabledModules: [ModuleId.FromModuleType<B>()], cascade: true), TestContext.Current.CancellationToken);
        Assert.Equal(ModuleChangeStatus.Committed, result.Status);
        Assert.Empty(host.EnabledModules);
        Assert.True((await result.Retirement.FinalCompletion).IsSuccess);
        Assert.Equal(1, b.Stopped); Assert.Equal(1, c.Stopped);
        await host.ShutdownAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Participant_can_explicitly_publish_a_null_domain_payload()
    {
        var ra = Register(() => new A());
        var host = new ModuleHost([ra], Options(typeof(A)));
        await host.InitializeAsync(TestContext.Current.CancellationToken);
        var first = await host.ChangeAsync(new ModuleChangeRequest(host.Current, [ra], Options(typeof(A)),
            participant: new Participant((_, _) => ValueTask.FromResult<ModuleChangePreparation>(new Preparation(new object())))), TestContext.Current.CancellationToken);
        Assert.NotNull(first.View.State);
        var second = await host.ChangeAsync(new ModuleChangeRequest(host.Current, [ra], Options(typeof(A)),
            participant: new Participant((_, _) => ValueTask.FromResult<ModuleChangePreparation>(new Preparation(null)))), TestContext.Current.CancellationToken);
        Assert.Equal(ModuleChangeStatus.Committed, second.Status);
        Assert.Null(second.View.State);
        await host.ShutdownAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Domain_rejection_never_publishes_candidate_modules_or_services()
    {
        var a = new A(); var b = new B();
        var ra = Register(() => a);
        var host = new ModuleHost([ra], Options(typeof(A)));
        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);
        var baseline = host.Current;
        var result = await host.ChangeAsync(new ModuleChangeRequest(baseline, [ra, Register(() => b)],
            Options(typeof(A), typeof(B)), participant: new Participant((_, _) => throw new InvalidOperationException("domain rejection"))), TestContext.Current.CancellationToken);
        Assert.Equal(ModuleChangeStatus.Rejected, result.Status);
        Assert.Same(baseline, host.Current);
        Assert.Same(a, Assert.Single(host.Current.Modules).Module);
        Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<Contribution>());
        Assert.True((await result.Retirement.FinalCompletion).IsSuccess);
        Assert.Equal(1, b.Stopped);
        Assert.Equal(0, a.Stopped);
        await host.ShutdownAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Module_services_and_domain_payload_are_published_as_one_view()
    {
        var ra = Register(() => new A());
        var host = new ModuleHost([ra], Options(typeof(A)));
        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);
        var baseline = host.Current;
        var entered = Barrier(); var resume = Barrier();
        var state = new object();
        var preparation = new Preparation(state) { Suspend = async _ => { entered.SetResult(); await resume.Task; } };
        var pending = host.ChangeAsync(new ModuleChangeRequest(baseline, [ra, Register(() => new B())],
            Options(typeof(A), typeof(B)), participant: new Participant((_, _) => ValueTask.FromResult<ModuleChangePreparation>(preparation))),
            TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        using var oldReader = host.AcquireView([ModuleId.FromModuleType<A>()]);
        Assert.Same(baseline, oldReader.View);
        Assert.Null(oldReader.View.State);
        Assert.Single(oldReader.View.Modules);
        Assert.Throws<InvalidOperationException>(() => oldReader.View.Services.GetRequiredService<Contribution>());
        resume.SetResult();
        var result = await pending;
        using var reader = host.AcquireView();
        Assert.Same(state, reader.View.State);
        Assert.Equal(2, reader.View.Modules.Count);
        Assert.IsType<Contribution>(reader.View.Services.GetRequiredService<Contribution>());
        Assert.Same(reader.View, result.View);
        reader.Dispose(); oldReader.Dispose();
        await host.ShutdownAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Cancellation_after_admission_pause_restores_old_configuration()
    {
        var a = new A(); var b = new B();
        var ra = Register(() => a); var rb = Register(() => b);
        var host = new ModuleHost([ra, rb], Options(typeof(A), typeof(B)));
        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);
        var baseline = host.Current;
        using var canceled = new CancellationTokenSource();
        var entered = Barrier(); var proceed = Barrier();
        var preparation = new Preparation(new object())
        {
            Suspend = async token => { entered.SetResult(); await proceed.Task; token.ThrowIfCancellationRequested(); }
        };
        var pending = host.ChangeAsync(new ModuleChangeRequest(baseline, [ra, rb], Options(typeof(A)),
            participant: new Participant((_, _) => ValueTask.FromResult<ModuleChangePreparation>(preparation))), canceled.Token).AsTask();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => host.AcquireView([ModuleId.FromModuleType<B>()]));
        using (var unaffected = host.AcquireView([ModuleId.FromModuleType<A>()])) Assert.Same(baseline, unaffected.View);
        canceled.Cancel(); proceed.SetResult();
        var result = await pending;
        Assert.Equal(ModuleChangeStatus.Canceled, result.Status);
        Assert.Equal(1, preparation.Resumed);
        Assert.Equal(1, preparation.Aborted);
        Assert.Same(baseline, host.Current);
        using (var restored = host.AcquireView()) Assert.Equal(2, restored.View.Modules.Count);
        Assert.Equal(0, b.Stopped);
        await host.ShutdownAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Stale_and_foreign_baselines_cannot_create_modules_or_overwrite_current()
    {
        var ra = Register(() => new A());
        var host = new ModuleHost([ra], Options(typeof(A)));
        var other = new ModuleHost([Register(() => new A())], Options(typeof(A)));
        await host.InitializeAsync(TestContext.Current.CancellationToken);
        await other.InitializeAsync(TestContext.Current.CancellationToken);
        var baseline = host.Current;
        var committed = await host.ChangeAsync(new ModuleChangeRequest(baseline, [ra, Register(() => new B())], Options(typeof(A), typeof(B))), TestContext.Current.CancellationToken);
        var created = 0;
        foreach (var stale in new[] { baseline, other.Current })
        {
            var result = await host.ChangeAsync(new ModuleChangeRequest(stale, [ra, Register(() => { created++; return new B(); })], Options(typeof(A), typeof(B))), TestContext.Current.CancellationToken);
            Assert.Equal(ModuleChangeStatus.StaleBaseline, result.Status);
            Assert.Same(committed.View, host.Current);
        }
        Assert.Equal(0, created);
        await host.ShutdownAsync(TestContext.Current.CancellationToken);
        await other.ShutdownAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Shutdown_before_commit_aborts_candidate_without_failing_the_current_host()
    {
        var a = new A(); var b = new B(); var ra = Register(() => a);
        var host = new ModuleHost([ra], Options(typeof(A)));
        await host.InitializeAsync(TestContext.Current.CancellationToken);
        var entered = Barrier(); var proceed = Barrier();
        var preparation = new Preparation(new object()) { Suspend = async _ => { entered.SetResult(); await proceed.Task; } };
        var change = host.ChangeAsync(new ModuleChangeRequest(host.Current, [ra, Register(() => b)], Options(typeof(A), typeof(B)),
            participant: new Participant((_, _) => ValueTask.FromResult<ModuleChangePreparation>(preparation))), TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var shutdown = host.ShutdownAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.False(shutdown.IsCompleted);
        proceed.SetResult();
        Assert.Equal(ModuleChangeStatus.Rejected, (await change).Status);
        Assert.True((await shutdown).IsSuccess);
        Assert.Equal(ModuleHostPhase.Shutdown, host.Phase);
        Assert.Equal(1, a.Stopped); Assert.Equal(1, b.Stopped);
    }

    [Fact]
    public async Task Old_cleanup_failure_is_retained_without_rolling_back_new_view()
    {
        var a = new A(); var b = new B { FailShutdown = true }; var ra = Register(() => a); var rb = Register(() => b);
        var host = new ModuleHost([ra, rb], Options(typeof(A), typeof(B)));
        await host.InitializeAsync(TestContext.Current.CancellationToken);
        var result = await host.ChangeAsync(new ModuleChangeRequest(host.Current, [ra, rb], Options(typeof(A))), TestContext.Current.CancellationToken);
        Assert.Equal(ModuleChangeStatus.Committed, result.Status);
        Assert.False((await result.Retirement.FinalCompletion).IsSuccess);
        Assert.Same(result.View, host.Current);
        Assert.Same(a, Assert.Single(host.EnabledModules).Module);
        Assert.False((await host.ShutdownAsync(TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(1, b.Stopped);
    }

    private static TaskCompletionSource Barrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ModuleHostOptions Options(params Type[] types) => new(types);
    private static ModuleRegistration Register<T>(Func<T> factory) where T : IModule => ModuleRegistration.For(ModuleDescriptor.For<T>(typeof(T).Name), factory);

    private sealed class Participant(Func<ModuleChangeContext, CancellationToken, ValueTask<ModuleChangePreparation>> prepare) : IModuleChangeParticipant
    {
        public ValueTask<ModuleChangePreparation> PrepareAsync(ModuleChangeContext context, CancellationToken cancellationToken) => prepare(context, cancellationToken);
    }
    private sealed class NeutralParticipant(Contribution value) : IModuleChangeParticipant
    {
        public ValueTask ConfigureAsync(ModuleChangeContext context, CancellationToken cancellationToken)
        {
            context.Services.ReplaceSingleton(value);
            return ValueTask.CompletedTask;
        }
        public ValueTask<ModuleChangePreparation> PrepareAsync(ModuleChangeContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ModuleChangePreparation>(new Preparation(null));
    }
    private sealed class Preparation(object? state) : ModuleChangePreparation
    {
        public override object? State => state;
        public Func<CancellationToken, ValueTask>? Suspend;
        public int Resumed;
        public int Aborted;
        public override ValueTask SuspendAsync(CancellationToken cancellationToken) => Suspend?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;
        public override ValueTask ResumeAsync() { Resumed++; return ValueTask.CompletedTask; }
        public override ValueTask AbortAsync() { Aborted++; return ValueTask.CompletedTask; }
    }
    private class Probe : ModuleBase
    {
        public int Configured;
        public int Stopped;
        public bool FailShutdown;
        public Action? OnShutdown;
        public Action<ModuleServiceConfigurationContext>? OnConfigure;
        public override void ConfigureServices(ModuleServiceConfigurationContext context) { Configured++; OnConfigure?.Invoke(context); }
        public override void Shutdown(ModuleShutdownContext context)
        {
            Stopped++;
            OnShutdown?.Invoke();
            if (FailShutdown) throw new InvalidOperationException("cleanup");
        }
    }
    private sealed class A : Probe;
    private sealed class AwaitingA(TaskCompletionSource entered, TaskCompletionSource proceed) : ModuleBase
    {
        public override async ValueTask ShutdownAsync(ModuleShutdownContext context, CancellationToken cancellationToken = default)
        {
            entered.SetResult();
            await proceed.Task;
        }
    }
    private sealed class C : Probe;
    private sealed class B : Probe
    {
        public override void ConfigureServices(ModuleServiceConfigurationContext context) => context.Services.AddSingleton(new Contribution());
    }
    private sealed class Contribution;
}
