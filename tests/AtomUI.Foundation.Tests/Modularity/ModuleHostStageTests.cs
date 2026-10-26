using System.Collections.Concurrent;
using AtomUI.Modularity;
using Xunit;

namespace AtomUI.Foundation.Tests.Modularity;

public sealed class ModuleHostStageTests
{
    [Fact]
    public async Task Configuration_UsesCommonBarriersAndDependencyOrder()
    {
        var events = new List<string>();
        var host = CreateHost((name, stage, _) => { events.Add($"{name}.{stage}"); return ValueTask.CompletedTask; });

        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await host.ShutdownAsync(TestContext.Current.CancellationToken)).IsSuccess);

        Assert.Equal(new[] { "first.pre", "second.pre", "first.configure", "second.configure", "first.post", "second.post", "first.initialize", "second.initialize", "second.shutdown", "first.shutdown" }, events);
    }

    [Theory]
    [InlineData("pre", "PreConfigureServices")]
    [InlineData("configure", "ConfigureServices")]
    [InlineData("post", "PostConfigureServices")]
    [InlineData("initialize", "Initialize")]
    public async Task LifecycleFailure_RollsBackAllCreatedModules(string failingStage, string diagnosticStage)
    {
        var shutdowns = new List<string>();
        var host = CreateHost((name, stage, _) =>
        {
            if (stage == "shutdown") { shutdowns.Add(name); }
            if (name == "second" && stage == failingStage) { throw new InvalidOperationException("stage failure"); }
            return ValueTask.CompletedTask;
        });

        var result = await host.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(new[] { "second", "first" }, shutdowns);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(diagnosticStage, diagnostic.Stage);
        Assert.Equal(ModuleId.FromModuleType<Second>(), diagnostic.ModuleId);
        Assert.Equal(typeof(InvalidOperationException).FullName, diagnostic.ExceptionType);
        Assert.Equal(ModuleHostPhase.Failed, host.Phase);
    }

    [Theory]
    [InlineData("pre")]
    [InlineData("configure")]
    [InlineData("post")]
    [InlineData("initialize")]
    public async Task LifecycleCancellation_PreservesInitiatingTokenAndCleansAllCreatedModules(string canceledStage)
    {
        using var cancellation = new CancellationTokenSource();
        var shutdowns = new List<string>();
        var host = CreateHost((name, stage, token) =>
        {
            if (stage == "shutdown") { Assert.Equal(CancellationToken.None, token); shutdowns.Add(name); }
            else { Assert.Equal(cancellation.Token, token); }
            if (name == "second" && stage == canceledStage) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            return ValueTask.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.InitializeAsync(cancellation.Token).AsTask());

        Assert.Equal(new[] { "second", "first" }, shutdowns);
        Assert.Equal(ModuleHostPhase.Failed, host.Phase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task Initialize_ContinuesEveryCompletedManualStage(int manualStages)
    {
        var events = new List<string>();
        var factories = 0;
        var host = new ModuleHost([ModuleRegistration.For(ModuleDescriptor.For<First>("first"), () => { factories++; return new First((_, stage, _) => { events.Add(stage); return ValueTask.CompletedTask; }); })], new ModuleHostOptions([typeof(First)]));
        if (manualStages >= 1) { Assert.True(host.ResolveModules().IsSuccess); }
        if (manualStages >= 2) { Assert.True((await host.CreateModulesAsync(TestContext.Current.CancellationToken)).IsSuccess); }
        if (manualStages >= 3) { Assert.True((await host.PreConfigureServicesAsync(TestContext.Current.CancellationToken)).IsSuccess); }
        if (manualStages >= 4) { Assert.True((await host.ConfigureServicesAsync(TestContext.Current.CancellationToken)).IsSuccess); }
        if (manualStages >= 5) { Assert.True((await host.PostConfigureServicesAsync(TestContext.Current.CancellationToken)).IsSuccess); }
        if (manualStages >= 6) { Assert.True(host.FreezeServices().IsSuccess); }
        if (manualStages >= 7) { Assert.True((await host.InitializeModulesAsync(TestContext.Current.CancellationToken)).IsSuccess); }

        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);

        Assert.Equal(1, factories);
        Assert.Equal(new[] { "pre", "configure", "post", "initialize" }, events);
        Assert.Equal(ModuleHostPhase.Initialized, host.Phase);
    }

    [Fact]
    public async Task AsyncCreationFailure_AllowsShutdownCallbackToResumeOnSingleThreadContext()
    {
        var finished = new TaskCompletionSource<ModuleHostResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackResumed = false;
        var thread = new Thread(() =>
        {
            using var context = new PumpContext();
            SynchronizationContext.SetSynchronizationContext(context);
            var host = new ModuleHost([
                ModuleRegistration.For(ModuleDescriptor.For<First>("first"), () => new First(async (_, stage, _) => { if (stage == "shutdown") { await Task.Yield(); callbackResumed = true; } })),
                ModuleRegistration.For<Second>(ModuleDescriptor.For<Second>("second", [ModuleDependencyDescriptor.Required<First>()]), () => throw new InvalidOperationException("factory failed"))], new ModuleHostOptions([typeof(Second)]));
            var creation = host.CreateModulesAsync(TestContext.Current.CancellationToken).AsTask();
            creation.ContinueWith(task => { if (task.IsCompletedSuccessfully) { finished.TrySetResult(task.Result); } else { finished.TrySetException(task.Exception!); } context.Finish(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            context.Run();
        })
        { IsBackground = true };
        thread.Start();

        var result = await finished.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.True(callbackResumed);
        thread.Join();
    }

    private static ModuleHost CreateHost(Func<string, string, CancellationToken, ValueTask> action)
    {
        return new ModuleHost([
            ModuleRegistration.For(ModuleDescriptor.For<Second>("second", [ModuleDependencyDescriptor.Required<First>()]), () => new Second(action)),
            ModuleRegistration.For(ModuleDescriptor.For<First>("first"), () => new First(action))], new ModuleHostOptions([typeof(Second)]));
    }

    private abstract class StageModule(string name, Func<string, string, CancellationToken, ValueTask> action) : ModuleBase
    {
        public override ValueTask PreConfigureServicesAsync(ModuleServiceConfigurationContext context, CancellationToken cancellationToken = default) => action(name, "pre", cancellationToken);
        public override ValueTask ConfigureServicesAsync(ModuleServiceConfigurationContext context, CancellationToken cancellationToken = default) => action(name, "configure", cancellationToken);
        public override ValueTask PostConfigureServicesAsync(ModuleServiceConfigurationContext context, CancellationToken cancellationToken = default) => action(name, "post", cancellationToken);
        public override ValueTask InitializeAsync(ModuleInitializationContext context, CancellationToken cancellationToken = default) => action(name, "initialize", cancellationToken);
        public override ValueTask ShutdownAsync(ModuleShutdownContext context, CancellationToken cancellationToken = default) => action(name, "shutdown", cancellationToken);
    }

    private sealed class First(Func<string, string, CancellationToken, ValueTask> action) : StageModule("first", action);
    private sealed class Second(Func<string, string, CancellationToken, ValueTask> action) : StageModule("second", action);

    private sealed class PumpContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Add((callback, state));
        public void Finish() => _queue.CompleteAdding();
        public void Run() { foreach (var item in _queue.GetConsumingEnumerable()) { item.Callback(item.State); } }
        public void Dispose() => _queue.Dispose();
    }
}
