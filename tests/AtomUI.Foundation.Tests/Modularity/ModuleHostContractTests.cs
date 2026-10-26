using AtomUI.Modularity;
using Xunit;
using static AtomUI.Foundation.Tests.Modularity.ModuleHostCoordinationTests;

namespace AtomUI.Foundation.Tests.Modularity;

public sealed class ModuleHostContractTests
{
    [Fact]
    public void Validate_MissingDependencyLeavesFactoriesAndHostStateUntouched()
    {
        var factories = 0;
        var host = new ModuleHost([ModuleRegistration.For(ModuleDescriptor.For<First>("first", [ModuleDependencyDescriptor.Required<Second>()]), () => { factories++; return new First(); })], new ModuleHostOptions([typeof(First)]));

        var validation = host.Validate();
        var resolution = host.ResolveModules();

        Assert.False(validation.IsSuccess);
        Assert.False(resolution.IsSuccess);
        Assert.Equal(new[] { ModuleId.FromModuleType<First>(), ModuleId.FromModuleType<Second>() }, Assert.Single(validation.Diagnostics).DependencyPath);
        Assert.Equal(0, factories);
        Assert.Empty(host.EnabledModules);
        Assert.Equal(ModuleHostPhase.Created, host.Phase);
    }

    [Fact]
    public async Task Validate_RequiredClosureAndExplicitEnablementDoNotActivateDisabledModules()
    {
        var factories = new List<string>();
        var host = new ModuleHost([
            Register(() => { factories.Add("disabled"); return new Disabled(); }),
            Register(() => { factories.Add("dependency"); return new First(); }),
            ModuleRegistration.For(ModuleDescriptor.For<Second>("second", [ModuleDependencyDescriptor.Required<First>()]), () => { factories.Add("root"); return new Second(); })], new ModuleHostOptions([typeof(Second)]));

        Assert.True(host.Validate().IsSuccess);
        Assert.Empty(factories);
        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(new[] { "dependency", "root" }, factories);
        Assert.Equal(new[] { ModuleId.FromModuleType<First>(), ModuleId.FromModuleType<Second>() }, host.EnabledModules.Select(entry => entry.Descriptor.Id));
    }

    [Fact]
    public async Task EmptyEnablement_RunsNoFactories()
    {
        var factories = 0;
        var host = new ModuleHost([Register(() => { factories++; return new First(); })]);

        Assert.True(host.Validate().IsSuccess);
        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Empty(host.EnabledModules);
        Assert.Equal(0, factories);
    }

    [Theory]
    [InlineData("Create")]
    [InlineData("PreConfigure")]
    [InlineData("Configure")]
    [InlineData("PostConfigure")]
    [InlineData("Initialize")]
    public async Task EmptyHost_CanceledLifecycleTokenDoesNotAdvancePhase(string stage)
    {
        var host = new ModuleHost([]);
        await PrepareEmptyHostForStage(host, stage);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunStage(host, stage, cancellation.Token));

        Assert.Equal(ModuleHostPhase.Failed, host.Phase);
    }

    [Fact]
    public void Validate_DuplicateAndUnknownRootDiagnosticsHaveStructuredPaths()
    {
        var duplicate = new ModuleHost([Register(() => new First()), Register(() => new First())]);
        var unknownRoot = new ModuleHost([], new ModuleHostOptions([typeof(Second)]));

        Assert.Equal(new[] { ModuleId.FromModuleType<First>(), ModuleId.FromModuleType<First>() }, Assert.Single(duplicate.Validate().Diagnostics).DependencyPath);
        Assert.Equal(new[] { ModuleId.FromModuleType<Second>() }, Assert.Single(unknownRoot.Validate().Diagnostics).DependencyPath);
    }

    [Fact]
    public async Task AsyncCreationCancellation_RegistersReturnedModuleBeforeCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var events = new List<string>();
        var host = Host(() => { cancellation.Cancel(); return new First { OnShutdown = _ => { events.Add("shutdown"); return ValueTask.CompletedTask; } }; }, () => { events.Add("second.factory"); return new Second(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.CreateModulesAsync(cancellation.Token).AsTask());

        Assert.Equal(new[] { "shutdown" }, events);
        Assert.Single(host.EnabledModules);
        Assert.Equal(ModuleHostPhase.Failed, host.Phase);
    }

    [Fact]
    public async Task InitiatingCancellation_WaitsForCallbackExitAndRollbackBeforeCancelingAllWaiters()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = Signal();
        var exit = Signal();
        var shutdownEntered = Signal();
        var shutdownExit = Signal();
        var host = Host(() => new First { OnInitialize = async token => { Assert.Equal(cancellation.Token, token); entered.SetResult(); await exit.Task; }, OnShutdown = async token => { Assert.Equal(CancellationToken.None, token); shutdownEntered.SetResult(); await shutdownExit.Task; } });
        var original = host.InitializeAsync(cancellation.Token).AsTask();
        await entered.Task;
        var joiner = host.InitializeAsync(TestContext.Current.CancellationToken).AsTask();
        cancellation.Cancel();
        try { Assert.False(original.IsCompleted); Assert.False(joiner.IsCompleted); }
        finally { exit.SetResult(); }
        await shutdownEntered.Task;
        try { Assert.False(original.IsCompleted); Assert.False(joiner.IsCompleted); }
        finally { shutdownExit.SetResult(); }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joiner);
        Assert.Equal(ModuleHostPhase.Failed, host.Phase);
    }

    [Theory]
    [InlineData(ModulePhaseExecutionOrder.Forward, "first", "second")]
    [InlineData(ModulePhaseExecutionOrder.Reverse, "second", "first")]
    public async Task CustomPhase_DispatchesInRequestedOrderWithoutChangingCompletedPhase(ModulePhaseExecutionOrder order, string first, string second)
    {
        var host = Host(() => new First(), () => new Second());
        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);
        var events = new List<string>();

        var result = await host.RunModulePhaseAsync(ModulePhaseId.FromType<DomainPhase>(), ModuleHostPhase.Initialized, order, (context, token) => { Assert.Equal(TestContext.Current.CancellationToken, token); events.Add(context.Descriptor.Id == ModuleId.FromModuleType<First>() ? "first" : "second"); return ValueTask.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { first, second }, events);
        Assert.Equal(ModuleHostPhase.Initialized, host.Phase);
    }

    [Fact]
    public async Task ConflictingManualStage_DoesNotInterruptRunningInitialization()
    {
        var entered = Signal();
        var exit = Signal();
        var host = Host(() => new First { OnInitialize = async _ => { entered.SetResult(); await exit.Task; } });
        var initialization = host.InitializeAsync(TestContext.Current.CancellationToken).AsTask();
        await entered.Task;
        try
        {
            Assert.False(host.ResolveModules().IsSuccess);
            Assert.False((await host.ConfigureServicesAsync(TestContext.Current.CancellationToken)).IsSuccess);
            Assert.False((await host.CreateModulesAsync(TestContext.Current.CancellationToken)).IsSuccess);
        }
        finally { exit.SetResult(); }

        Assert.True((await initialization).IsSuccess);
        Assert.Single(host.EnabledModules);
    }

    [Fact]
    public async Task TerminalState_RejectsEveryMutatingStage()
    {
        var host = Host(() => new First());
        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await host.ShutdownAsync(TestContext.Current.CancellationToken)).IsSuccess);

        var results = new[] { host.ResolveModules(), host.CreateModules(), await host.CreateModulesAsync(TestContext.Current.CancellationToken), await host.PreConfigureServicesAsync(TestContext.Current.CancellationToken), await host.ConfigureServicesAsync(TestContext.Current.CancellationToken), await host.PostConfigureServicesAsync(TestContext.Current.CancellationToken), host.FreezeServices(), await host.InitializeModulesAsync(TestContext.Current.CancellationToken), await host.InitializeAsync(TestContext.Current.CancellationToken), await host.RunModulePhaseAsync(ModulePhaseId.FromType<DomainPhase>(), ModuleHostPhase.Shutdown, ModulePhaseExecutionOrder.Forward, (_, _) => ValueTask.CompletedTask, TestContext.Current.CancellationToken) };

        Assert.All(results, result => Assert.Equal(ModuleDiagnosticCodes.InvalidLifecycleTransition, Assert.Single(result.Diagnostics).Code));
        Assert.Equal(ModuleHostPhase.Shutdown, host.Phase);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task PrepareEmptyHostForStage(ModuleHost host, string stage)
    {
        if (stage == "Create") return;
        Assert.True((await host.CreateModulesAsync(TestContext.Current.CancellationToken)).IsSuccess);
        if (stage == "PreConfigure") return;
        Assert.True((await host.PreConfigureServicesAsync(TestContext.Current.CancellationToken)).IsSuccess);
        if (stage == "Configure") return;
        Assert.True((await host.ConfigureServicesAsync(TestContext.Current.CancellationToken)).IsSuccess);
        if (stage == "PostConfigure") return;
        Assert.True((await host.PostConfigureServicesAsync(TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True(host.FreezeServices().IsSuccess);
    }

    private static Task RunStage(ModuleHost host, string stage, CancellationToken cancellationToken) => stage switch
    {
        "Create" => host.CreateModulesAsync(cancellationToken).AsTask(),
        "PreConfigure" => host.PreConfigureServicesAsync(cancellationToken).AsTask(),
        "Configure" => host.ConfigureServicesAsync(cancellationToken).AsTask(),
        "PostConfigure" => host.PostConfigureServicesAsync(cancellationToken).AsTask(),
        "Initialize" => host.InitializeModulesAsync(cancellationToken).AsTask(),
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown test stage.")
    };

    private sealed class Disabled : ModuleBase;
    private sealed class DomainPhase;
}
