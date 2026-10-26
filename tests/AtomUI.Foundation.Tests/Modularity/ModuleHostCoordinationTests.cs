using AtomUI.Modularity;
using Xunit;

namespace AtomUI.Foundation.Tests.Modularity;

public sealed class ModuleHostCoordinationTests
{
    [Fact]
    public async Task CreationCancellation_RollsBackEveryReturnedInstance()
    {
        using var cancellation = new CancellationTokenSource();
        var events = new List<string>();
        var host = Host(
            () => new First { OnShutdown = _ => { events.Add("first.shutdown"); return ValueTask.CompletedTask; } },
            () => { cancellation.Cancel(); return new Second { OnShutdown = _ => { events.Add("second.shutdown"); return ValueTask.CompletedTask; } }; });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.InitializeAsync(cancellation.Token).AsTask());

        Assert.Equal(new[] { "second.shutdown", "first.shutdown" }, events);
        Assert.Equal(ModuleHostPhase.Failed, host.Phase);
        Assert.False((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);
    }

    [Fact]
    public async Task NullFactory_RollsBackPreviouslyCreatedInstances()
    {
        var shutdowns = 0;
        var host = Host(() => new First { OnShutdown = _ => { shutdowns++; return ValueTask.CompletedTask; } }, () => null!);

        var result = await host.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(1, shutdowns);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Stage == "CreateModule" && diagnostic.ModuleId == ModuleId.FromModuleType<Second>());
    }

    [Fact]
    public async Task InitializationCancellation_CleansPartialModulesBeforeReportingCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var events = new List<string>();
        var host = Host(
            () => new First { OnInitialize = token => { Assert.Equal(cancellation.Token, token); cancellation.Cancel(); token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }, OnShutdown = token => { Assert.False(token.IsCancellationRequested); events.Add("first"); return ValueTask.CompletedTask; } },
            () => new Second { OnShutdown = _ => { events.Add("second"); return ValueTask.CompletedTask; } });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.InitializeAsync(cancellation.Token).AsTask());

        Assert.Equal(new[] { "second", "first" }, events);
        Assert.Equal(ModuleHostPhase.Failed, host.Phase);
    }

    [Fact]
    public async Task InternalOperationCanceledExceptionWithoutCallerCancellationIsReportedAsFailure()
    {
        var host = Host(() => new First
        {
            OnInitialize = _ => throw new OperationCanceledException("module canceled its own operation")
        });

        var result = await host.InitializeAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ModuleDiagnosticCodes.ModuleLifecycleFailed, diagnostic.Code);
        Assert.Equal("Initialize", diagnostic.Stage);
        Assert.Equal(ModuleId.FromModuleType<First>(), diagnostic.ModuleId);
        Assert.Equal(typeof(OperationCanceledException).FullName, diagnostic.ExceptionType);
        Assert.Equal(ModuleHostPhase.Failed, host.Phase);
    }

    [Fact]
    public async Task ConcurrentInitialization_SharesExecutionAndOnlyCancelsJoinerWait()
    {
        using var original = new CancellationTokenSource();
        using var joiner = new CancellationTokenSource();
        var entered = Signal();
        var release = Signal();
        var factoryCalls = 0;
        var tokenSeen = default(CancellationToken);
        var host = Host(() => { factoryCalls++; return new First { OnInitialize = async token => { tokenSeen = token; entered.SetResult(); await release.Task; } }; });
        var first = host.InitializeAsync(original.Token).AsTask();
        await entered.Task;
        var second = host.InitializeAsync(joiner.Token).AsTask();
        joiner.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            Assert.False(first.IsCompleted);
        }
        finally { release.SetResult(); }

        Assert.True((await first).IsSuccess);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(original.Token, tokenSeen);
    }

    [Fact]
    public async Task ShutdownOverlap_WaitsCurrentCallbackAndPreventsFurtherInitialization()
    {
        var entered = Signal();
        var release = Signal();
        var events = new List<string>();
        var host = Host(
            () => new First { OnInitialize = async _ => { events.Add("first.initialize"); entered.SetResult(); await release.Task; }, OnShutdown = _ => { events.Add("first.shutdown"); return ValueTask.CompletedTask; } },
            () => new Second { OnInitialize = _ => { events.Add("second.initialize"); return ValueTask.CompletedTask; }, OnShutdown = _ => { events.Add("second.shutdown"); return ValueTask.CompletedTask; } });
        var initialization = host.InitializeAsync(TestContext.Current.CancellationToken).AsTask();
        await entered.Task;
        var shutdown = host.ShutdownAsync(TestContext.Current.CancellationToken).AsTask();
        try { Assert.False(shutdown.IsCompleted); }
        finally { release.SetResult(); }

        Assert.False((await initialization).IsSuccess);
        Assert.True((await shutdown).IsSuccess);
        Assert.Equal(new[] { "first.initialize", "second.shutdown", "first.shutdown" }, events);
        Assert.Equal(ModuleHostPhase.Shutdown, host.Phase);
    }

    [Fact]
    public async Task CallbackReentry_IsRejectedWithoutWaitingOnItself()
    {
        ModuleHost host = null!;
        ModuleHostResult? initializeResult = null;
        ModuleHostResult? shutdownResult = null;
        host = Host(() => new First { OnInitialize = async _ => { initializeResult = await host.InitializeAsync(TestContext.Current.CancellationToken); shutdownResult = await host.ShutdownAsync(TestContext.Current.CancellationToken); } });

        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);

        Assert.False(initializeResult!.IsSuccess);
        Assert.False(shutdownResult!.IsSuccess);
        Assert.All(initializeResult.Diagnostics.Concat(shutdownResult.Diagnostics), diagnostic => Assert.Equal(ModuleDiagnosticCodes.InvalidLifecycleTransition, diagnostic.Code));
        Assert.Equal(ModuleHostPhase.Initialized, host.Phase);
    }

    [Fact]
    public async Task ShutdownFailure_IsCachedAndModulesAreNeverRetried()
    {
        var shutdowns = 0;
        var host = Host(() => new First { OnShutdown = _ => { shutdowns++; throw new InvalidOperationException("shutdown failed"); } });
        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);

        var first = await host.ShutdownAsync(TestContext.Current.CancellationToken);
        var second = await host.ShutdownAsync(TestContext.Current.CancellationToken);

        Assert.False(first.IsSuccess);
        Assert.Same(first, second);
        Assert.Equal(1, shutdowns);
    }

    [Fact]
    public async Task ObserverFailure_DoesNotMaskModuleFailureOrPreventRollback()
    {
        var shutdowns = 0;
        var options = new ModuleHostOptionsBuilder().Enable<First>().UseDiagnostics(new ThrowingDiagnostics()).Build();
        var host = new ModuleHost([Register(() => new First { OnInitialize = _ => throw new InvalidOperationException("original"), OnShutdown = _ => { shutdowns++; return ValueTask.CompletedTask; } })], options);

        var result = await host.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(1, shutdowns);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("original", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.ExceptionType == typeof(ObserverException).FullName);
    }

    [Fact]
    public async Task InitializeAfterManualCreation_ResumesWithoutRebuildingModules()
    {
        var factoryCalls = 0;
        var events = new List<string>();
        var host = Host(() => { factoryCalls++; return new First { OnPreConfigure = _ => { events.Add("pre"); return ValueTask.CompletedTask; }, OnInitialize = _ => { events.Add("initialize"); return ValueTask.CompletedTask; } }; });
        Assert.True(host.ResolveModules().IsSuccess);
        Assert.True(host.CreateModules().IsSuccess);
        Assert.True((await host.PreConfigureServicesAsync(TestContext.Current.CancellationToken)).IsSuccess);

        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(new[] { "pre", "initialize" }, events);
    }

    [Fact]
    public async Task CustomPhaseWithoutRollback_PreservesPhaseAndCancellationSemantics()
    {
        var shutdowns = 0;
        var host = Host(() => new First { OnShutdown = _ => { shutdowns++; return ValueTask.CompletedTask; } });
        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);
        var result = await host.RunModulePhaseAsync(ModulePhaseId.FromType<CustomPhase>(), ModuleHostPhase.Initialized, ModulePhaseExecutionOrder.Forward, (_, _) => throw new InvalidOperationException("domain failure"), cancellationToken: TestContext.Current.CancellationToken, rollbackOnFailure: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.RunModulePhaseAsync(ModulePhaseId.FromType<CustomPhase>(), ModuleHostPhase.Initialized, ModulePhaseExecutionOrder.Forward, (_, _) => ValueTask.CompletedTask, cancellation.Token, rollbackOnFailure: false).AsTask());

        Assert.False(result.IsSuccess);
        Assert.Equal(ModuleHostPhase.Initialized, host.Phase);
        Assert.Equal(0, shutdowns);
        Assert.True((await host.ShutdownAsync(TestContext.Current.CancellationToken)).IsSuccess);
    }

    [Fact]
    public async Task WorkScheduledByCallback_CanRequestShutdownAfterCallbackHasExited()
    {
        var exit = Signal();
        Task<ModuleHostResult>? scheduled = null;
        ModuleHost host = null!;
        host = Host(() => new First { OnInitialize = _ => { scheduled = Task.Run(async () => { await exit.Task; return await host.ShutdownAsync(TestContext.Current.CancellationToken); }, TestContext.Current.CancellationToken); return ValueTask.CompletedTask; } });
        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);
        exit.SetResult();

        Assert.True((await scheduled!).IsSuccess);
        Assert.Equal(ModuleHostPhase.Shutdown, host.Phase);
    }

    [Fact]
    public async Task RepeatedInitialization_WithCanceledTokenPreservesInitializedHost()
    {
        var shutdowns = 0;
        var host = Host(() => new First { OnShutdown = _ => { shutdowns++; return ValueTask.CompletedTask; } });
        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        Assert.True((await host.InitializeAsync(canceled.Token)).IsSuccess);

        Assert.Equal(ModuleHostPhase.Initialized, host.Phase);
        Assert.Equal(0, shutdowns);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static ModuleHost Host(Func<First> first, Func<Second>? second = null)
    {
        var registrations = new List<ModuleRegistration> { Register(first) };
        var builder = new ModuleHostOptionsBuilder().Enable<First>();
        if (second is not null) { registrations.Add(Register(second)); builder.Enable<Second>(); }
        return new ModuleHost(registrations, builder.Build());
    }

    internal static ModuleRegistration Register<T>(Func<T> factory) where T : IModule => ModuleRegistration.For(ModuleDescriptor.For<T>(typeof(T).Name), factory);

    internal class ProbeModule : ModuleBase
    {
        public Func<CancellationToken, ValueTask>? OnPreConfigure { get; init; }
        public Func<CancellationToken, ValueTask>? OnInitialize { get; init; }
        public Func<CancellationToken, ValueTask>? OnShutdown { get; init; }
        public override ValueTask PreConfigureServicesAsync(ModuleServiceConfigurationContext context, CancellationToken cancellationToken = default) => OnPreConfigure?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;
        public override ValueTask InitializeAsync(ModuleInitializationContext context, CancellationToken cancellationToken = default) => OnInitialize?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;
        public override ValueTask ShutdownAsync(ModuleShutdownContext context, CancellationToken cancellationToken = default) => OnShutdown?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;
    }

    internal sealed class First : ProbeModule;
    internal sealed class Second : ProbeModule;
    private sealed class CustomPhase;
    private sealed class ObserverException : Exception;
    private sealed class ThrowingDiagnostics : IModuleDiagnostics { public void Report(ModuleDiagnostic diagnostic) => throw new ObserverException(); }
}
