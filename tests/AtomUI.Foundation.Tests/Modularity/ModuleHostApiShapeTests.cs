using AtomUI.Modularity;
using AtomUI.Modularity.Services;
using Xunit;

namespace AtomUI.Foundation.Tests.Modularity;

/// <summary>P5 acceptance: non-throwing admission probes and structured change diagnostics.</summary>
public sealed class ModuleHostApiShapeTests
{
    [Fact]
    public async Task TryAcquireView_reports_unavailability_without_throwing()
    {
        var token = TestContext.Current.CancellationToken;
        var a = Register(() => new A());
        var host = new ModuleHost([a], new ModuleHostOptions([typeof(A)]));

        Assert.False(host.TryAcquireView(null, out var early));
        Assert.Null(early);

        Assert.True((await host.InitializeAsync(token)).IsSuccess);
        Assert.True(host.TryAcquireView([ModuleId.FromModuleType<A>()], out var lease));
        Assert.Same(host.Current, lease!.View);
        lease.Dispose();
        Assert.Throws<ArgumentException>(() => host.TryAcquireView([ModuleId.FromModuleType<B>()], out _));

        Assert.True((await host.ShutdownAsync(token)).IsSuccess);
        Assert.False(host.TryAcquireView(null, out var late));
        Assert.Null(late);
    }

    [Fact]
    public async Task TryAcquireView_returns_false_for_a_module_suspended_by_a_change()
    {
        var token = TestContext.Current.CancellationToken;
        var a = Register(() => new A());
        var b = Register(() => new B());
        var host = new ModuleHost([a, b], new ModuleHostOptions([typeof(A), typeof(B)]));
        Assert.True((await host.InitializeAsync(token)).IsSuccess);
        var preparation = new BlockingPreparation();
        var change = host.ChangeAsync(new(host.Current, [a, b], new ModuleHostOptions([typeof(A)]),
            participant: new Participant(preparation)), token).AsTask();
        await preparation.Suspended.Task.WaitAsync(TimeSpan.FromSeconds(5), token);

        Assert.False(host.TryAcquireView([ModuleId.FromModuleType<B>()], out var suspended));
        Assert.Null(suspended);
        Assert.True(host.TryAcquireView([ModuleId.FromModuleType<A>()], out var independent));
        independent!.Dispose();

        preparation.Proceed.SetResult();
        var result = await change;
        Assert.True(result.IsCommitted);
        await result.Retirement.FinalCompletion;
        Assert.True((await host.ShutdownAsync(token)).IsSuccess);
    }

    [Fact]
    public async Task TryBorrow_reports_closed_or_retiring_admission_without_throwing()
    {
        var runtime = new ModuleServiceRuntime();
        var owner = ModuleId.FromSerializedValue("Tests.ApiShape");
        runtime.AddScopedFactory<Resource>(owner, _ => new(), _ => ValueTask.CompletedTask);
        runtime.Freeze();

        var scope = runtime.Root.CreateScope();
        Assert.True(scope.TryBorrow<Resource>(out var lease));
        lease!.Dispose();
        Assert.Throws<InvalidOperationException>(() => scope.TryBorrow<Unregistered>(out _));

        var retirement = scope.BeginRetirement();
        Assert.False(scope.TryBorrow<Resource>(out var closed));
        Assert.Null(closed);
        Assert.True((await retirement.FinalCompletion).IsSuccess);

        var candidate = runtime.CreateCandidate(runtime.Bindings, []);
        candidate.Freeze();
        var candidateScope = candidate.Root.CreateScope();
        candidate.Stop();
        Assert.False(candidateScope.TryBorrow<Resource>(out var retiring));
        Assert.Null(retiring);
        Assert.True((await candidate.CompleteRetirement().FinalCompletion).IsSuccess);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Rejected_change_keeps_the_candidate_stage_diagnostics()
    {
        var token = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = Register(() => new A());
        var b = Register(() => new B(entered, proceed));
        var host = new ModuleHost([a, b], new ModuleHostOptions([typeof(A)]));
        Assert.True((await host.InitializeAsync(token)).IsSuccess);

        var change = host.ChangeAsync(new(host.Current, [a, b], new ModuleHostOptions([typeof(A), typeof(B)])), token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        var shutdown = host.ShutdownAsync(token).AsTask();
        proceed.SetResult();
        var result = await change;

        Assert.Equal(ModuleChangeStatus.Rejected, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Stage == "ChangeModules");
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == ModuleDiagnosticCodes.InvalidLifecycleTransition && diagnostic.Stage == "ConfigureServices");
        await result.Retirement.FinalCompletion;
        Assert.True((await shutdown).IsSuccess);
    }

    [Fact]
    public async Task Rejected_change_uses_the_public_change_code()
    {
        var token = TestContext.Current.CancellationToken;
        var a = Register(() => new A());
        var host = new ModuleHost([a], new ModuleHostOptions([typeof(A)]));
        Assert.True((await host.InitializeAsync(token)).IsSuccess);
        var replacement = Register(() => new A());

        var result = await host.ChangeAsync(new(host.Current, [replacement], new ModuleHostOptions([typeof(A)])), token);

        Assert.Equal(ModuleChangeStatus.Rejected, result.Status);
        Assert.Equal(ModuleDiagnosticCodes.ModuleChangeRejected, Assert.Single(result.Diagnostics).Code);
        Assert.True((await host.ShutdownAsync(token)).IsSuccess);
    }

    private static ModuleRegistration Register<T>(Func<T> factory) where T : IModule =>
        ModuleRegistration.For(ModuleDescriptor.For<T>(typeof(T).Name), factory);

    private sealed class A : ModuleBase;

    private sealed class B : ModuleBase
    {
        private readonly TaskCompletionSource? _entered;
        private readonly TaskCompletionSource? _proceed;
        public B() { }
        public B(TaskCompletionSource entered, TaskCompletionSource proceed) { _entered = entered; _proceed = proceed; }

        public override async ValueTask PreConfigureServicesAsync(ModuleServiceConfigurationContext context, CancellationToken cancellationToken = default)
        {
            if (_entered is null) return;
            _entered.SetResult();
            await _proceed!.Task.ConfigureAwait(false);
        }
    }

    private sealed class Resource;
    private sealed class Unregistered;

    private sealed class Participant(ModuleChangePreparation preparation) : IModuleChangeParticipant
    {
        public ValueTask<ModuleChangePreparation> PrepareAsync(ModuleChangeContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(preparation);
    }

    private sealed class BlockingPreparation : ModuleChangePreparation
    {
        public TaskCompletionSource Suspended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Proceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override object? State => null;

        public override async ValueTask SuspendAsync(CancellationToken cancellationToken)
        {
            Suspended.SetResult();
            await Proceed.Task.ConfigureAwait(false);
        }
    }
}
