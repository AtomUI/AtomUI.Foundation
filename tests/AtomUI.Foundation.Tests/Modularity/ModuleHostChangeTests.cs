using AtomUI.Modularity;
using Xunit;

namespace AtomUI.Foundation.Tests.Modularity;

public sealed class ModuleHostChangeTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("cycle")]
    [InlineData("duplicate")]
    public async Task Invalid_candidate_graph_rejects_before_business_factories(string kind)
    {
        var a = new A(); var created = 0;
        var host = new ModuleHost([Register(() => a)], Options(typeof(A)));
        await host.InitializeAsync(TestContext.Current.CancellationToken);
        var baseline = host.Current;
        var b = ModuleRegistration.For(ModuleDescriptor.For<B>("B", kind == "missing"
            ? [ModuleDependencyDescriptor.Required<D>()]
            : kind == "cycle" ? [ModuleDependencyDescriptor.Required<C>()] : []), () => { created++; return new B(); });
        var c = ModuleRegistration.For(ModuleDescriptor.For<C>("C", [ModuleDependencyDescriptor.Required<B>()]), () => { created++; return new C(); });
        ModuleRegistration[] registrations = kind == "duplicate" ? [b, b] : kind == "cycle" ? [b, c] : [b];
        var result = await host.ChangeAsync(new ModuleChangeRequest(baseline, registrations, Options(typeof(B))), TestContext.Current.CancellationToken);
        Assert.Equal(ModuleChangeStatus.Rejected, result.Status);
        Assert.Equal(0, created);
        Assert.Same(baseline, host.Current);
        Assert.Equal(0, a.Stopped);
        Assert.NotEmpty(result.Diagnostics);
        await host.ShutdownAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Replacement_factory_cannot_transfer_an_already_owned_module_again()
    {
        var a = new A(); var ra = Register(() => a);
        var host = new ModuleHost([ra], Options(typeof(A)));
        await host.InitializeAsync(TestContext.Current.CancellationToken);
        var baseline = host.Current;
        var result = await host.ChangeAsync(new ModuleChangeRequest(baseline, [Register(() => a)], Options(typeof(A)),
            replacedModules: [ModuleId.FromModuleType<A>()]), TestContext.Current.CancellationToken);
        Assert.Equal(ModuleChangeStatus.Rejected, result.Status);
        Assert.Same(baseline, host.Current);
        Assert.Equal(0, a.Stopped);
        Assert.Equal(1, a.Initialized);
        await host.ShutdownAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Adding_module_preserves_original_instance_and_never_creates_unselected_module()
    {
        var a = new A();
        var bCreated = 0;
        var dCreated = 0;
        var registration = Register(() => a);
        var host = new ModuleHost([registration], Options(typeof(A)));
        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);
        var baseline = host.Current;

        var result = await host.ChangeAsync(new ModuleChangeRequest(baseline,
            [registration, Register(() => { bCreated++; return new B(); }), Register(() => { dCreated++; return new D(); })],
            Options(typeof(A), typeof(B))), TestContext.Current.CancellationToken);

        Assert.Equal(ModuleChangeStatus.Committed, result.Status);
        Assert.Equal(2, host.EnabledModules.Count);
        Assert.Same(a, host.EnabledModules.Single(entry => entry.Module is A).Module);
        Assert.Equal(1, a.Configured);
        Assert.Equal(1, a.Initialized);
        Assert.Equal(1, bCreated);
        Assert.Equal(0, dCreated);
        Assert.Same(result.View, host.Current);
        Assert.NotSame(baseline, host.Current);
        Assert.True((await result.Retirement.FinalCompletion).IsSuccess);
        Assert.True((await host.ShutdownAsync(TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(1, a.Stopped);
    }

    [Fact]
    public async Task Unchanged_request_does_not_replay_lifecycle()
    {
        var a = new A();
        var registration = Register(() => a);
        var host = new ModuleHost([registration], Options(typeof(A)));
        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);
        var baseline = host.Current;
        var result = await host.ChangeAsync(new ModuleChangeRequest(baseline, [registration], Options(typeof(A))), TestContext.Current.CancellationToken);
        Assert.Equal(ModuleChangeStatus.NoChange, result.Status);
        Assert.Same(baseline, host.Current);
        Assert.Equal(1, a.Initialized);
        await host.ShutdownAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Candidate_initialization_failure_cleans_only_candidate_and_keeps_current_host_serving()
    {
        var a = new A();
        var b = new B { FailInitialization = true };
        var registration = Register(() => a);
        var host = new ModuleHost([registration], Options(typeof(A)));
        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);
        var baseline = host.Current;
        var result = await host.ChangeAsync(new ModuleChangeRequest(baseline,
            [registration, Register(() => b)], Options(typeof(A), typeof(B))), TestContext.Current.CancellationToken);
        Assert.Equal(ModuleChangeStatus.Rejected, result.Status);
        Assert.Same(baseline, host.Current);
        Assert.Equal(ModuleHostPhase.Initialized, host.Phase);
        Assert.Equal(0, a.Stopped);
        Assert.Equal(1, b.Stopped);
        using (var lease = host.AcquireView()) Assert.Same(baseline, lease.View);
        Assert.True((await host.ShutdownAsync(TestContext.Current.CancellationToken)).IsSuccess);
    }

    [Fact]
    public async Task Disabling_required_module_rejects_before_any_candidate_factory()
    {
        var created = 0;
        var b = Register(() => new B());
        var c = ModuleRegistration.For(ModuleDescriptor.For<C>("C", [ModuleDependencyDescriptor.Required<B>()]), () => new C());
        var host = new ModuleHost([b, c], Options(typeof(C)));
        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);
        var baseline = host.Current;
        var result = await host.ChangeAsync(new ModuleChangeRequest(baseline,
            [b, c, Register(() => { created++; return new D(); })], Options(typeof(C), typeof(D)),
            disabledModules: [ModuleId.FromModuleType<B>()]), TestContext.Current.CancellationToken);
        Assert.Equal(ModuleChangeStatus.Rejected, result.Status);
        Assert.Equal(0, created);
        Assert.Same(baseline, host.Current);
        await host.ShutdownAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Removing_module_waits_for_original_view_reader_without_rebuilding_survivor()
    {
        var a = new A();
        var b = new B();
        var ra = Register(() => a);
        var rb = Register(() => b);
        var host = new ModuleHost([ra, rb], Options(typeof(A), typeof(B)));
        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);
        var lease = host.AcquireView();
        var result = await host.ChangeAsync(new ModuleChangeRequest(host.Current, [ra, rb], Options(typeof(A))), TestContext.Current.CancellationToken);
        Assert.Equal(ModuleChangeStatus.Committed, result.Status);
        Assert.Same(a, Assert.Single(host.EnabledModules).Module);
        Assert.False(result.Retirement.FinalCompletion.IsCompleted);
        Assert.Equal(0, b.Stopped);
        Assert.Equal(2, lease.View.Modules.Count);
        lease.Dispose();
        Assert.True((await result.Retirement.FinalCompletion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(1, b.Stopped);
        await host.ShutdownAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, a.Stopped);
    }

    private static ModuleHostOptions Options(params Type[] types) => new(types);
    private static ModuleRegistration Register<T>(Func<T> factory) where T : IModule =>
        ModuleRegistration.For(ModuleDescriptor.For<T>(typeof(T).Name), factory);

    private class Probe : ModuleBase
    {
        public int Configured;
        public int Initialized;
        public int Stopped;
        public bool FailInitialization;
        public override void ConfigureServices(ModuleServiceConfigurationContext context) => Configured++;
        public override void Initialize(ModuleInitializationContext context)
        {
            Initialized++;
            if (FailInitialization) throw new InvalidOperationException("candidate initialization");
        }
        public override void Shutdown(ModuleShutdownContext context)
        {
            Stopped++;
        }
    }
    private sealed class A : Probe;
    private sealed class B : Probe;
    private sealed class C : Probe;
    private sealed class D : Probe;
}
