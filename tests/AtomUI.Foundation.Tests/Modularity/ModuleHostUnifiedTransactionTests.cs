using AtomUI.Modularity;
using AtomUI.Modularity.Services;
using Xunit;

namespace AtomUI.Foundation.Tests.Modularity;

/// <summary>
/// P1 acceptance for the 2026-11-05 optimization proposal: startup, reconfiguration and final
/// shutdown are one composition change protocol, and final shutdown retires the current
/// generation's published domain state (audit R01).
/// </summary>
public sealed class ModuleHostUnifiedTransactionTests
{
    [Fact]
    public async Task Final_shutdown_lets_the_current_domain_state_return_its_module_lease()
    {
        var events = new List<string>();
        var runtime = new ModuleServiceRuntime();
        var owner = ModuleId.FromModuleType<B>();
        runtime.AddSingletonFactory(owner, _ => new Service(),
            _ => { lock (events) events.Add("service.release"); return ValueTask.CompletedTask; },
            _ => { lock (events) events.Add("service.stop"); });
        var b = new B(events);
        var ra = ModuleRegistration.For(ModuleDescriptor.For<A>("A"), () => new A());
        var rb = ModuleRegistration.For(ModuleDescriptor.For<B>("B"), () => b);
        var host = new ModuleHost([ra, rb], new ModuleHostOptions([typeof(A), typeof(B)]) { Lifetime = runtime });
        var token = TestContext.Current.CancellationToken;
        Assert.True((await host.InitializeAsync(token)).IsSuccess);
        runtime.Root.Borrow<Service>().Dispose();

        // The published state holds a real reader of B. Before this protocol, final shutdown waited
        // for B's readers before any domain hook could run, so the lease could never be returned.
        var state = new LeaseState(host.AcquireView([owner]));
        var preparation = new CurrentStatePreparation(state, events);
        var change = await host.ChangeAsync(new(host.Current, [ra, rb], new ModuleHostOptions([typeof(A), typeof(B)]),
            participant: new Participant(preparation)), token);
        Assert.True(change.IsCommitted);
        Assert.True((await change.Retirement.FinalCompletion).IsSuccess);

        var shutdown = await host.ShutdownAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);

        Assert.True(shutdown.IsSuccess);
        Assert.Equal(ModuleHostPhase.Shutdown, host.Phase);
        Assert.Equal(1, preparation.Stops);
        Assert.Equal(1, b.Shutdowns);
        Assert.True(events.IndexOf("service.stop") < events.IndexOf("domain.stop-current"));
        Assert.True(events.IndexOf("domain.stop-current") < events.IndexOf("domain.complete-current"));
        Assert.True(events.IndexOf("domain.complete-current") < events.IndexOf("B.shutdown"));
        Assert.True(events.IndexOf("B.shutdown") < events.IndexOf("service.release"));
    }

    [Fact]
    public async Task Only_the_state_still_current_is_retired_by_its_publisher_at_shutdown()
    {
        var events = new List<string>();
        var ra = ModuleRegistration.For(ModuleDescriptor.For<A>("A"), () => new A());
        var host = new ModuleHost([ra], new ModuleHostOptions([typeof(A)]));
        var token = TestContext.Current.CancellationToken;
        Assert.True((await host.InitializeAsync(token)).IsSuccess);
        var first = new CurrentStatePreparation(new LeaseState(null), events, "first");
        var second = new CurrentStatePreparation(new LeaseState(null), events, "second");

        await (await host.ChangeAsync(new(host.Current, [ra], new ModuleHostOptions([typeof(A)]), participant: new Participant(first)), token))
            .Retirement.FinalCompletion;
        await (await host.ChangeAsync(new(host.Current, [ra], new ModuleHostOptions([typeof(A)]), participant: new Participant(second)), token))
            .Retirement.FinalCompletion;
        Assert.True((await host.ShutdownAsync(token)).IsSuccess);

        Assert.Equal(0, first.Stops);
        Assert.Equal(1, second.Stops);
        Assert.Equal(["second.stop-current", "second.complete-current"], events);
    }

    [Fact]
    public async Task Current_domain_cleanup_failure_is_reported_without_skipping_module_shutdown()
    {
        var a = new A();
        var ra = ModuleRegistration.For(ModuleDescriptor.For<A>("A"), () => a);
        var host = new ModuleHost([ra], new ModuleHostOptions([typeof(A)]));
        var token = TestContext.Current.CancellationToken;
        Assert.True((await host.InitializeAsync(token)).IsSuccess);
        await (await host.ChangeAsync(new(host.Current, [ra], new ModuleHostOptions([typeof(A)]),
            participant: new Participant(new FailingCurrentPreparation())), token)).Retirement.FinalCompletion;

        var shutdown = await host.ShutdownAsync(token);

        Assert.False(shutdown.IsSuccess);
        Assert.Contains(shutdown.Diagnostics, diagnostic => diagnostic.Stage == "StopCurrentDomain");
        Assert.Contains(shutdown.Diagnostics, diagnostic => diagnostic.Stage == "RetireCurrentDomain");
        Assert.Equal(1, a.Shutdowns);
        Assert.Equal(ModuleHostPhase.Shutdown, host.Phase);
    }

    [Fact]
    public async Task Startup_publishes_generation_one_and_manual_stages_match_one_shot_initialize()
    {
        var token = TestContext.Current.CancellationToken;
        var oneShotFactories = 0;
        var oneShot = new ModuleHost([ModuleRegistration.For(ModuleDescriptor.For<A>("A"), () => { oneShotFactories++; return new A(); })],
            new ModuleHostOptions([typeof(A)]));
        Assert.Equal(0, oneShot.Current.Generation);
        Assert.True((await oneShot.InitializeAsync(token)).IsSuccess);
        Assert.Equal(1, oneShot.Current.Generation);

        var manualFactories = 0;
        var manual = new ModuleHost([ModuleRegistration.For(ModuleDescriptor.For<A>("A"), () => { manualFactories++; return new A(); })],
            new ModuleHostOptions([typeof(A)]));
        Assert.True(manual.ResolveModules().IsSuccess);
        Assert.True((await manual.CreateModulesAsync(token)).IsSuccess);
        var created = Assert.Single(manual.EnabledModules).Module;
        Assert.Equal(0, manual.Current.Generation);
        Assert.True((await manual.PreConfigureServicesAsync(token)).IsSuccess);
        Assert.True((await manual.InitializeAsync(token)).IsSuccess);

        Assert.Equal(1, manual.Current.Generation);
        Assert.Same(created, Assert.Single(manual.Current.Modules).Module);
        Assert.Equal(oneShotFactories, manualFactories);
        Assert.Equal(1, manualFactories);
        Assert.True((await oneShot.ShutdownAsync(token)).IsSuccess);
        Assert.True((await manual.ShutdownAsync(token)).IsSuccess);
    }

    private sealed record LeaseState(ModuleHostLease? Lease);

    private sealed class Participant(ModuleChangePreparation preparation) : IModuleChangeParticipant
    {
        public ValueTask<ModuleChangePreparation> PrepareAsync(ModuleChangeContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(preparation);
    }

    private sealed class CurrentStatePreparation(LeaseState state, List<string> events, string name = "domain") : ModuleChangePreparation
    {
        public int Stops;
        public override object State => state;
        public override void StopCurrent()
        {
            Stops++;
            lock (events) events.Add($"{name}.stop-current");
        }

        public override ValueTask<ModuleHostResult> CompleteCurrentAsync()
        {
            lock (events) events.Add($"{name}.complete-current");
            state.Lease?.Dispose();
            return ValueTask.FromResult(ModuleHostResult.Success);
        }
    }

    private sealed class FailingCurrentPreparation : ModuleChangePreparation
    {
        public override object State => "failing";
        public override void StopCurrent() => throw new InvalidOperationException("stop-current");
        public override ValueTask<ModuleHostResult> CompleteCurrentAsync() => throw new InvalidOperationException("complete-current");
    }

    private sealed class A : ModuleBase
    {
        public int Shutdowns;
        public override void Shutdown(ModuleShutdownContext context) => Shutdowns++;
    }

    private sealed class B(List<string> events) : ModuleBase
    {
        public int Shutdowns;
        public override void Shutdown(ModuleShutdownContext context)
        {
            Shutdowns++;
            lock (events) events.Add("B.shutdown");
        }
    }

    private sealed class Service;
}
