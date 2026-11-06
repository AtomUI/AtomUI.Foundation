using System.Collections;
using System.Runtime.CompilerServices;
using AtomUI.Modularity;
using AtomUI.Modularity.Services;
using Xunit;

namespace AtomUI.Foundation.Tests.Modularity;

public sealed class ModuleHostAuditRegressionTests
{
    [Theory]
    [InlineData("State")]
    [InlineData("AffectedModules")]
    [InlineData("Enumeration")]
    public async Task Preparation_user_members_reject_host_reentry(string member)
    {
        var registration = Register(() => new A());
        var host = new ModuleHost([registration], Options(typeof(A)));
        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);
        var calls = new List<Task<ModuleHostResult>>();
        void Reenter()
        {
            var call = host.ShutdownAsync(TestContext.Current.CancellationToken).AsTask();
            calls.Add(call);
            Assert.True(call.IsCompleted, "Reentry must be rejected without waiting for the current change.");
        }
        var result = await host.ChangeAsync(new(host.Current, [registration], Options(typeof(A)),
            participant: new Participant(new ReentryPreparation(member, Reenter))), TestContext.Current.CancellationToken);
        Assert.True(result.IsCommitted);
        Assert.False((await Assert.Single(calls)).IsSuccess);
        Assert.Equal(ModuleHostPhase.Initialized, host.Phase);
        Assert.True((await result.Retirement.FinalCompletion).IsSuccess);
        Assert.True((await host.ShutdownAsync(TestContext.Current.CancellationToken)).IsSuccess);
    }

    [Fact]
    public async Task Failed_resume_keeps_only_affected_admission_closed_until_shutdown()
    {
        var runtime = new ModuleServiceRuntime();
        runtime.AddSingletonFactory(ModuleId.FromModuleType<B>(), _ => new Resource(), _ => ValueTask.CompletedTask);
        var a = Register(() => new A()); var b = Register(() => new B());
        var host = new ModuleHost([a, b], new ModuleHostOptions([typeof(A), typeof(B)]) { Lifetime = runtime });
        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);
        using (runtime.Root.Borrow<Resource>()) { }
        var state = new RunningState();
        var first = await host.ChangeAsync(new(host.Current, [a, b], Options(typeof(A), typeof(B)),
            participant: new Participant(new StatePreparation(state))), TestContext.Current.CancellationToken);
        await first.Retirement.FinalCompletion;
        var baseline = host.Current;
        using var canceled = new CancellationTokenSource();
        var preparation = new FailedResumePreparation(state, canceled);
        var result = await host.ChangeAsync(new(baseline, [a], Options(typeof(A)), participant: new Participant(preparation)), canceled.Token);
        Assert.Equal(ModuleChangeStatus.Canceled, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Stage == "ResumeModules");
        Assert.Same(baseline, host.Current);
        Assert.False(state.Running);
        Assert.Throws<InvalidOperationException>(() => host.AcquireView([ModuleId.FromModuleType<B>()]));
        Assert.Throws<InvalidOperationException>(() => runtime.Root.Borrow<Resource>());
        using (var independent = host.AcquireView([ModuleId.FromModuleType<A>()])) Assert.Same(baseline, independent.View);
        var retry = await host.ChangeAsync(new(baseline, [a, b], Options(typeof(A), typeof(B))), TestContext.Current.CancellationToken);
        Assert.Equal(ModuleChangeStatus.Rejected, retry.Status);
        await result.Retirement.FinalCompletion;
        var shutdown = await host.ShutdownAsync(TestContext.Current.CancellationToken);
        Assert.False(shutdown.IsSuccess);
        Assert.Contains(shutdown.Diagnostics, diagnostic => diagnostic.Stage == "ResumeModules");
        Assert.Equal(ModuleHostPhase.Shutdown, host.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_module_retirement_does_not_retain_successful_independent_module(bool abortCandidate)
    {
        var (host, failed, released) = await RemoveModulesAsync(abortCandidate);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.True(failed.IsAlive);
        Assert.False(released.IsAlive);
        var shutdown = await host.ShutdownAsync(TestContext.Current.CancellationToken);
        Assert.False(shutdown.IsSuccess);
        Assert.Contains(shutdown.Diagnostics, diagnostic => diagnostic.ModuleId == ModuleId.FromModuleType<B>());
        GC.KeepAlive(host);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(ModuleHost Host, WeakReference Failed, WeakReference Released)> RemoveModulesAsync(bool abortCandidate)
    {
        var failed = new B { Fail = true }; var released = new C();
        var a = Register(() => new A());
        var b = Register(() => failed); var c = Register(() => released);
        var host = new ModuleHost(abortCandidate ? [a] : [a, b, c], abortCandidate ? Options(typeof(A)) : Options(typeof(A), typeof(B), typeof(C)));
        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);
        var change = await host.ChangeAsync(abortCandidate
            ? new(host.Current, [a, b, c], Options(typeof(A), typeof(B), typeof(C)), participant: new RejectingParticipant())
            : new(host.Current, [a], Options(typeof(A))), TestContext.Current.CancellationToken);
        Assert.Equal(!abortCandidate, change.IsCommitted);
        Assert.False((await change.Retirement.FinalCompletion).IsSuccess);
        Assert.Equal(1, released.Stopped);
        return (host, new(failed), new(released));
    }

    private static ModuleRegistration Register<T>(Func<T> factory) where T : IModule => ModuleRegistration.For(ModuleDescriptor.For<T>(typeof(T).Name), factory);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Failed_service_retains_its_owner_without_retaining_independent_module_factory(bool abortCandidate, bool declaredButUnusedDependency)
    {
        var (host, failedService, successfulModule) = RetireServices(abortCandidate, declaredButUnusedDependency);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.True(failedService.IsAlive);
        Assert.False(successfulModule.IsAlive);
        Assert.False((await host.ShutdownAsync(TestContext.Current.CancellationToken)).IsSuccess);
        GC.KeepAlive(host);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ModuleHost, WeakReference, WeakReference) RetireServices(bool abortCandidate, bool declaredButUnusedDependency) =>
        RetireServicesAsync(abortCandidate, declaredButUnusedDependency).GetAwaiter().GetResult();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(ModuleHost, WeakReference, WeakReference)> RetireServicesAsync(bool abortCandidate, bool declaredButUnusedDependency)
    {
        var runtime = new ModuleServiceRuntime();
        var a = Register(() => new A());
        var b = Register(() => new ManagedB(declaredButUnusedDependency));
        var healthy = new ManagedC(); var c = Register(() => healthy);
        var host = new ModuleHost(abortCandidate ? [a] : [a, b, c],
            new ModuleHostOptions(abortCandidate ? [typeof(A)] : [typeof(A), typeof(ManagedB), typeof(ManagedC)]) { Lifetime = runtime });
        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken).ConfigureAwait(false)).IsSuccess);
        WeakReference failed;
        ModuleChangeResult result;
        if (abortCandidate)
        {
            var participant = new ManagedRejectingParticipant();
            result = await host.ChangeAsync(new(host.Current, [a, b, c], Options(typeof(A), typeof(ManagedB), typeof(ManagedC)),
                participant: participant), TestContext.Current.CancellationToken).ConfigureAwait(false);
            failed = participant.Failed!;
        }
        else
        {
            using (var bad = runtime.Root.Borrow<BadResource>()) failed = new(bad.Value);
            using (runtime.Root.Borrow<GoodResource>()) { }
            result = await host.ChangeAsync(new(host.Current, [a], Options(typeof(A))), TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        Assert.Equal(!abortCandidate, result.IsCommitted);
        Assert.False((await result.Retirement.FinalCompletion.ConfigureAwait(false)).IsSuccess);
        return (host, failed, new(healthy));
    }
    private static ModuleHostOptions Options(params Type[] types) => new(types);
    private sealed class Participant(ModuleChangePreparation preparation) : IModuleChangeParticipant
    {
        public ValueTask<ModuleChangePreparation> PrepareAsync(ModuleChangeContext context, CancellationToken cancellationToken) => ValueTask.FromResult(preparation);
    }
    private sealed class RejectingParticipant : IModuleChangeParticipant
    {
        public ValueTask<ModuleChangePreparation> PrepareAsync(ModuleChangeContext context, CancellationToken cancellationToken) =>
            ValueTask.FromException<ModuleChangePreparation>(new InvalidOperationException("candidate rejected"));
    }
    private class StatePreparation(object state) : ModuleChangePreparation { public override object State => state; }
    private sealed class FailedResumePreparation(RunningState state, CancellationTokenSource canceled) : StatePreparation(state)
    {
        public override ValueTask SuspendAsync(CancellationToken token) { state.Running = false; canceled.Cancel(); return ValueTask.CompletedTask; }
        public override ValueTask ResumeAsync() => ValueTask.FromException(new InvalidOperationException("resume failed"));
    }
    private sealed class ReentryPreparation(string member, Action reenter) : ModuleChangePreparation
    {
        public override object State { get { if (member == "State") reenter(); return new object(); } }
        public override IReadOnlyCollection<ModuleId> AffectedModules
        {
            get
            {
                if (member == "AffectedModules") reenter();
                return member == "Enumeration" ? new ReentryCollection(reenter) : [];
            }
        }
    }
    private sealed class ReentryCollection(Action reenter) : IReadOnlyCollection<ModuleId>
    {
        public int Count => 0;
        public IEnumerator<ModuleId> GetEnumerator() { reenter(); return Enumerable.Empty<ModuleId>().GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class RunningState { public bool Running = true; }
    private class A : ModuleBase;
    private sealed class B : ModuleBase
    {
        public bool Fail;
        public override void Shutdown(ModuleShutdownContext context) { if (Fail) throw new InvalidOperationException("B failed"); }
    }
    private sealed class C : ModuleBase
    {
        public readonly byte[] Payload = new byte[1024 * 1024];
        public int Stopped;
        public override void Shutdown(ModuleShutdownContext context) => Stopped++;
    }
    private sealed class Resource;
    private sealed class ManagedB(bool declaredButUnusedDependency) : ModuleBase
    {
        public override void ConfigureServices(ModuleServiceConfigurationContext context) =>
            context.ManagedServices!.AddSingletonFactory(ModuleId.FromModuleType<ManagedB>(),
                static _ => new BadResource(), static _ => throw new InvalidOperationException("service release failed"),
                dependencies: declaredButUnusedDependency ? [typeof(GoodResource)] : []);
    }
    private sealed class ManagedC : ModuleBase
    {
        private readonly byte[] _payload = new byte[1024 * 1024];
        public override void ConfigureServices(ModuleServiceConfigurationContext context) =>
            context.ManagedServices!.AddSingletonFactory(ModuleId.FromModuleType<ManagedC>(),
                _ => new GoodResource(_payload.Length), static _ => ValueTask.CompletedTask);
    }
    private sealed class ManagedRejectingParticipant : IModuleChangeParticipant
    {
        public WeakReference? Failed;
        public ValueTask<ModuleChangePreparation> PrepareAsync(ModuleChangeContext context, CancellationToken cancellationToken)
        {
            using (var bad = context.ManagedServices!.Root.Borrow<BadResource>()) Failed = new(bad.Value);
            using (context.ManagedServices!.Root.Borrow<GoodResource>()) { }
            throw new InvalidOperationException("reject initialized services");
        }
    }
    private sealed class BadResource;
    private sealed record GoodResource(int Size);
}
