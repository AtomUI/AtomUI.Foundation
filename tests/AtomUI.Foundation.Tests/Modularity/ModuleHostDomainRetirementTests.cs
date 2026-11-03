using AtomUI.Modularity;
using AtomUI.Modularity.Services;
using Xunit;

namespace AtomUI.Foundation.Tests.Modularity;

public sealed class ModuleHostDomainRetirementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Published_domain_can_return_old_module_lease_from_its_final_cleanup(bool domainFails)
    {
        var events = new List<string>();
        var runtime = new ModuleServiceRuntime();
        var owner = ModuleId.FromModuleType<B>();
        runtime.AddSingletonFactory(owner, _ => new Service(),
            _ => { events.Add("service.release"); return ValueTask.CompletedTask; }, _ => events.Add("service.stop"));
        runtime.AddSingletonFactory(owner, _ => new OtherService(),
            _ => { events.Add("other.release"); return ValueTask.CompletedTask; }, _ => events.Add("other.stop"));
        var a = new A();
        var b = new B(events);
        var ra = ModuleRegistration.For(ModuleDescriptor.For<A>("A"), () => a);
        var rb = ModuleRegistration.For(ModuleDescriptor.For<B>("B"), () => b);
        var host = new ModuleHost([ra, rb], new ModuleHostOptions([typeof(A), typeof(B)]) { Lifetime = runtime });
        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);
        runtime.Root.Borrow<Service>().Dispose();
        runtime.Root.Borrow<OtherService>().Dispose();

        // Preserve the original history: publish the actual lease holder as
        // the current domain state before asking a second change to remove B.
        var lease = host.AcquireView([owner]);
        var oldState = new DomainState(lease);
        var first = await host.ChangeAsync(new ModuleChangeRequest(host.Current, [ra, rb],
            new ModuleHostOptions([typeof(A), typeof(B)]), participant: new Participant(new InitialPreparation(oldState))),
            TestContext.Current.CancellationToken);
        Assert.True(first.IsCommitted);
        Assert.Same(oldState, host.Current.State);
        Assert.True((await first.Retirement.FinalCompletion).IsSuccess);

        var allowReturn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparation = new RetiringPreparation(oldState, events, allowReturn, domainFails);
        var result = await host.ChangeAsync(new ModuleChangeRequest(host.Current, [ra],
            new ModuleHostOptions([typeof(A)]), participant: new Participant(preparation)), TestContext.Current.CancellationToken);
        Assert.True(result.IsCommitted);
        Assert.Same(a, Assert.Single(host.Current.Modules).Module);

        try
        {
            await preparation.CompleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(4, events.Count);
            Assert.Equal("domain.complete", events[^1]);
            Assert.Equal(0, b.Shutdowns);
            Assert.False(result.Retirement.FinalCompletion.IsCompleted);
            Assert.Equal(2, lease.View.Modules.Count);
            allowReturn.SetResult();

            var final = await result.Retirement.FinalCompletion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(!domainFails, final.IsSuccess);
            if (domainFails) Assert.Contains(final.Diagnostics, diagnostic => diagnostic.Code == "DOMAIN-CLEANUP-FAILED");
            Assert.Equal(1, b.Shutdowns);
            Assert.True(events.IndexOf("B.shutdown") < events.IndexOf("service.release"));
            Assert.True(events.IndexOf("B.shutdown") < events.IndexOf("other.release"));
            Assert.Same(result.View, host.Current);
        }
        finally
        {
            // On RED only, break the known cycle so the test leaves no work
            // behind. All passing assertions above precede this fallback.
            allowReturn.TrySetResult();
            lease.Dispose();
            await result.Retirement.FinalCompletion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await host.ShutdownAsync(TestContext.Current.CancellationToken);
        }
        Assert.Equal(1, a.Shutdowns);
    }

    private sealed record DomainState(ModuleHostLease Lease);
    private sealed class Participant(ModuleChangePreparation preparation) : IModuleChangeParticipant
    {
        public ValueTask<ModuleChangePreparation> PrepareAsync(ModuleChangeContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(preparation);
    }
    private sealed class InitialPreparation(DomainState state) : ModuleChangePreparation { public override object State => state; }
    private sealed class RetiringPreparation(DomainState oldState, List<string> events, TaskCompletionSource allowReturn, bool fail) : ModuleChangePreparation
    {
        public override object State => "new";
        public TaskCompletionSource CompleteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void StopPrevious() => events.Add("domain.stop");
        public override async ValueTask<ModuleHostResult> CompletePreviousAsync()
        {
            Assert.Contains("service.stop", events);
            Assert.Contains("other.stop", events);
            Assert.Contains("domain.stop", events);
            Assert.DoesNotContain("service.release", events);
            events.Add("domain.complete");
            CompleteEntered.SetResult();
            await allowReturn.Task;
            oldState.Lease.Dispose();
            return fail ? new ModuleHostResult([new ModuleDiagnostic("DOMAIN-CLEANUP-FAILED", "domain cleanup failed after returning its lease")]) : ModuleHostResult.Success;
        }
    }
    private sealed class A : ModuleBase
    {
        public int Shutdowns;
        public override void Shutdown(ModuleShutdownContext context) => Shutdowns++;
    }
    private sealed class B(List<string> events) : ModuleBase
    {
        public int Shutdowns;
        public override void Shutdown(ModuleShutdownContext context) { Shutdowns++; events.Add("B.shutdown"); }
    }
    private sealed class Service;
    private sealed class OtherService;
}
