using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using AtomUI.Lifetimes;
using AtomUI.Modularity;
using AtomUI.Modularity.Services;
using Xunit;

namespace AtomUI.Foundation.Tests.Services;

public sealed class ModuleServiceRetirementRetentionTests
{
    private static readonly ModuleId Module = ModuleId.FromSerializedValue("scope-retention");

    [Fact]
    public async Task Four_thousand_successful_scopes_leave_no_candidate_index_entries()
    {
        var releases = 0;
        var runtime = CreateRuntime(_ => { releases++; return ValueTask.CompletedTask; });
        var candidate = CreateCandidate(runtime);
        var references = new List<WeakReference>();
        for (var index = 0; index < 4000; index++)
            references.Add(RetireScope(candidate));

        Assert.Equal(4000, releases);
        Assert.Equal(0, OwnedScopeCount(candidate));
        Collect();
        Assert.All(references, reference => Assert.False(reference.IsAlive));
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Rebinding_scope_shares_retirement_and_removes_original_registration()
    {
        var runtime = CreateRuntime(_ => ValueTask.CompletedTask);
        var origin = CreateCandidate(runtime);
        var view = CreateCandidate(runtime);
        var scope = origin.Root.CreateScope();
        var rebound = view.BindScope(scope);
        using (rebound.Borrow<Resource>()) { }

        var ticket = rebound.BeginRetirement();
        Assert.True((await ticket.FinalCompletion).IsSuccess);
        Assert.Same(ticket, scope.BeginRetirement());
        Assert.Equal(0, OwnedScopeCount(origin));
        Assert.Equal(0, OwnedScopeCount(view));
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Reader_and_async_release_keep_registration_until_public_final_completion()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releases = 0;
        var runtime = CreateRuntime(async _ => { releases++; entered.SetResult(); await finish.Task; });
        var origin = CreateCandidate(runtime);
        var view = CreateCandidate(runtime);
        var scope = origin.Root.CreateScope();
        var reader = scope.Borrow<Resource>();
        var ticket = view.BindScope(scope).BeginRetirement();
        Assert.False(ticket.FinalCompletion.IsCompleted);
        Assert.Equal(1, OwnedScopeCount(origin));
        Assert.Equal(0, releases);
        reader.Dispose();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(ticket.FinalCompletion.IsCompleted);
        Assert.Equal(1, OwnedScopeCount(origin));
        finish.SetResult();
        Assert.True((await ticket.FinalCompletion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(0, OwnedScopeCount(origin));
        Assert.Equal(1, releases);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Failed_locally_retired_scope_keeps_original_exception_in_candidate_receipt()
    {
        var failure = new InvalidOperationException("original scope release");
        var releases = 0;
        var runtime = CreateRuntime(_ => { releases++; return ValueTask.FromException(failure); });
        var candidate = CreateCandidate(runtime);
        var scope = candidate.Root.CreateScope();
        using (scope.Borrow<Resource>()) { }
        var local = await scope.BeginRetirement().FinalCompletion;
        Assert.False(local.IsSuccess);
        Assert.Same(failure, Assert.Single(local.Failures).Exception);
        Assert.Equal(1, OwnedScopeCount(candidate));

        var ticket = candidate.BeginRetirement();
        var result = await ticket.FinalCompletion;
        Assert.False(result.IsSuccess);
        Assert.Same(failure, Assert.Single(result.Failures).Exception);
        Assert.Equal(Assert.Single(local.Owners).Id, Assert.Single(result.Owners).Id);
        Assert.Same(ticket, candidate.BeginRetirement());
        Assert.Equal(1, CandidateReceiptCount(runtime));
        Assert.False((await runtime.CompleteShutdownAsync()).IsSuccess);
        Assert.False((await runtime.CompleteShutdownAsync()).IsSuccess);
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task Successful_candidate_receipts_are_collectible_while_runtime_is_active()
    {
        var runtime = CreateRuntime(_ => ValueTask.CompletedTask);
        var references = new List<WeakReference>();
        for (var index = 0; index < 128; index++) references.Add(RetireEmptyCandidate(runtime));
        Assert.Equal(0, CandidateReceiptCount(runtime));
        Collect();
        Assert.All(references, reference => Assert.False(reference.IsAlive));
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Global_shutdown_completes_stop_only_candidate_and_waits_for_real_reader()
    {
        var releases = 0;
        var runtime = CreateRuntime(_ => { releases++; return ValueTask.CompletedTask; });
        var candidate = CreateCandidate(runtime);
        var scope = candidate.Root.CreateScope();
        var reader = scope.Borrow<Resource>();
        candidate.Stop();
        var ticket = candidate.Retirement!;
        Assert.False(ticket.FinalCompletion.IsCompleted);
        Assert.Equal(1, CandidateReceiptCount(runtime));
        var shutdown = runtime.CompleteShutdownAsync().AsTask();
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(0, releases);
        reader.Dispose();
        Assert.True((await shutdown.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await ticket.FinalCompletion).IsSuccess);
        Assert.Equal(0, CandidateReceiptCount(runtime));
        Assert.Equal(0, OwnedScopeCount(candidate));
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task Global_shutdown_preserves_locally_retired_factory_failure_from_unstopped_candidate()
    {
        var failure = new InvalidOperationException("original failed activation");
        var releases = 0;
        var runtime = new ModuleServiceRuntime();
        runtime.AddScopedFactory<Resource>(Module, _ => throw failure,
            _ => { releases++; return ValueTask.CompletedTask; });
        runtime.Freeze();
        var candidate = CreateCandidate(runtime);
        var scope = candidate.Root.CreateScope();
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => scope.Borrow<Resource>()));
        var local = await scope.BeginRetirement().FinalCompletion;
        Assert.Same(failure, Assert.Single(local.Failures).Exception);
        Assert.Null(candidate.Retirement);

        var result = await runtime.CompleteShutdownAsync();
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message == failure.Message);
        var late = await candidate.BeginRetirement().FinalCompletion;
        Assert.False(late.IsSuccess);
        Assert.Same(failure, Assert.Single(late.Failures).Exception);
        Assert.Equal(Assert.Single(local.Owners).Id, Assert.Single(late.Owners).Id);
        Assert.False((await runtime.CompleteShutdownAsync()).IsSuccess);
        Assert.Equal(0, releases);
    }

    [Fact]
    public async Task Candidate_retired_after_global_shutdown_finishes_its_original_scope_index()
    {
        var releases = 0;
        var runtime = CreateRuntime(_ => { releases++; return ValueTask.CompletedTask; });
        var candidate = CreateCandidate(runtime);
        var scope = candidate.Root.CreateScope();
        using (scope.Borrow<Resource>()) { }
        Assert.True((await runtime.CompleteShutdownAsync()).IsSuccess);

        var ticket = candidate.BeginRetirement();
        Assert.True((await ticket.FinalCompletion).IsSuccess);
        Assert.Equal(0, OwnedScopeCount(candidate));
        Assert.Equal(0, CandidateReceiptCount(runtime));
        Assert.True((await scope.BeginRetirement().FinalCompletion).IsSuccess);
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task Candidate_and_local_retirement_share_stop_barrier_and_release_once()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finishStop = new ManualResetEventSlim();
        var stops = 0;
        var releases = 0;
        var runtime = new ModuleServiceRuntime();
        runtime.AddScopedFactory<Resource>(Module, _ => new Resource(), _ =>
        {
            Assert.Equal(2, Volatile.Read(ref stops));
            Interlocked.Increment(ref releases);
            return ValueTask.CompletedTask;
        }, _ =>
        {
            if (Interlocked.Increment(ref stops) == 1)
            {
                entered.SetResult();
                Assert.True(finishStop.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            }
        });
        runtime.Freeze();
        var candidate = CreateCandidate(runtime);
        var first = candidate.Root.CreateScope();
        var second = candidate.Root.CreateScope();
        using (first.Borrow<Resource>()) { }
        using (second.Borrow<Resource>()) { }
        var global = Task.Run(candidate.BeginRetirement, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var localEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var local = Task.Run(() =>
        {
            localEntered.SetResult();
            return first.BeginRetirement();
        }, TestContext.Current.CancellationToken);
        await localEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref releases));
        finishStop.Set();
        var globalTicket = await global.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var localTicket = await local.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True((await globalTicket.FinalCompletion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await localTicket.FinalCompletion).IsSuccess);
        Assert.Equal(2, releases);
        Assert.Equal(0, OwnedScopeCount(candidate));
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Shared_root_does_not_keep_retired_registration_factory_capture_alive()
    {
        var (host, reference, _) = await RetireHistoricalRegistration();
        Collect();
        Assert.False(reference.IsAlive);
        Assert.Equal(ModuleHostPhase.Initialized, host.Phase);
        Assert.True((await host.ShutdownAsync(TestContext.Current.CancellationToken)).IsSuccess);
    }

    [Fact]
    public async Task Rebound_live_scope_does_not_keep_historical_bindings_alive()
    {
        var (host, reference, scope) = await RetireHistoricalRegistration(keepScope: true);
        Collect();
        Assert.False(reference.IsAlive);
        await scope!.DisposeAsync();
        Assert.True((await host.ShutdownAsync(TestContext.Current.CancellationToken)).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Global_shutdown_finishes_live_scope_registrations_from_every_binding(bool candidateScope)
    {
        var releases = 0;
        var runtime = CreateRuntime(_ => { releases++; return ValueTask.CompletedTask; });
        var origin = candidateScope ? CreateCandidate(runtime) : runtime.Bindings;
        var rebound = CreateCandidate(runtime);
        var states = new List<WeakReference>();
        for (var index = 0; index < 128; index++) states.Add(CreateLiveScope(origin, rebound));
        Assert.Equal(128, OwnedScopeCount(origin));

        Assert.True(CompleteShutdownForCollection(runtime).IsSuccess);
        Assert.Equal(128, releases);
        Assert.Equal(0, OwnedScopeCount(origin));
        Assert.Equal(0, LiveScopeCount(runtime));
        Collect();
        Assert.All(states, state => Assert.False(state.IsAlive));
        Assert.True((await runtime.CompleteShutdownAsync()).IsSuccess);
        Assert.Equal(128, releases);
    }

    [Fact]
    public async Task Failed_service_keeps_its_resource_without_retaining_successful_sibling_factory()
    {
        var (runtime, ticket, failed, payload, counts) = RetireCandidateWithIndependentServices();
        Assert.Equal(1, counts.Failed);
        Assert.Equal(1, counts.Successful);
        Collect();
        Assert.True(failed.IsAlive);
        Assert.False(payload.IsAlive);
        Assert.False((await ticket.FinalCompletion).IsSuccess);
        Assert.False((await runtime.CompleteShutdownAsync()).IsSuccess);
        Assert.Equal(1, counts.Failed);
        Assert.Equal(1, counts.Successful);
        GC.KeepAlive(runtime);
        GC.KeepAlive(ticket);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateLiveScope(ModuleServiceBindings origin, ModuleServiceBindings rebound)
    {
        var scope = origin.Root.CreateScope();
        using (rebound.BindScope(scope).Borrow<Resource>()) { }
        return new WeakReference(typeof(ModuleServiceScope).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope)!);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ModuleHostResult CompleteShutdownForCollection(ModuleServiceRuntime runtime) =>
        runtime.CompleteShutdownAsync().AsTask().GetAwaiter().GetResult();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ModuleServiceRuntime, LifetimeRetirement, WeakReference, WeakReference, ReleaseCounts)
        RetireCandidateWithIndependentServices()
    {
        var runtime = new ModuleServiceRuntime();
        runtime.Freeze();
        var candidate = runtime.CreateCandidate(runtime.Bindings, []);
        var counts = new ReleaseCounts();
        RegisterFailedService(candidate, counts);
        var payload = RegisterSuccessfulService(candidate, counts);
        candidate.Freeze();
        WeakReference failed;
        using (var lease = candidate.Root.Borrow<FailedResource>()) failed = new WeakReference(lease.Value);
        using (candidate.Root.Borrow<Resource>()) { }
        var ticket = candidate.BeginRetirement();
        Assert.False(ticket.FinalCompletion.GetAwaiter().GetResult().IsSuccess);
        return (runtime, ticket, failed, payload, counts);
    }

    private static void RegisterFailedService(ModuleServiceBindings candidate, ReleaseCounts counts) =>
        candidate.AddSingletonFactory<FailedResource>(Module, static _ => new FailedResource(), _ =>
        {
            counts.Failed++;
            return ValueTask.FromException(new InvalidOperationException("failed resource cleanup"));
        });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RegisterSuccessfulService(ModuleServiceBindings candidate, ReleaseCounts counts)
    {
        var payload = new byte[65536];
        candidate.AddSingletonFactory<Resource>(Module, _ =>
        {
            GC.KeepAlive(payload);
            return new Resource();
        }, _ => { counts.Successful++; return ValueTask.CompletedTask; });
        return new WeakReference(payload);
    }

    private static ModuleServiceRuntime CreateRuntime(Func<Resource, ValueTask> release)
    {
        var runtime = new ModuleServiceRuntime();
        runtime.AddScopedFactory<Resource>(Module, _ => new Resource(), release);
        runtime.Freeze();
        return runtime;
    }

    private static ModuleServiceBindings CreateCandidate(ModuleServiceRuntime runtime)
    {
        var candidate = runtime.CreateCandidate(runtime.Bindings, []);
        candidate.Freeze();
        return candidate;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RetireScope(ModuleServiceBindings candidate)
    {
        var scope = candidate.Root.CreateScope();
        using (scope.Borrow<Resource>()) { }
        var state = typeof(ModuleServiceScope).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope)!;
        var reference = new WeakReference(state);
        Assert.True(scope.BeginRetirement().FinalCompletion.GetAwaiter().GetResult().IsSuccess);
        return reference;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RetireEmptyCandidate(ModuleServiceRuntime runtime)
    {
        var candidate = CreateCandidate(runtime);
        var ticket = candidate.BeginRetirement();
        Assert.True(ticket.FinalCompletion.GetAwaiter().GetResult().IsSuccess);
        return new WeakReference(ticket);
    }

    // White-box counters over the runtime's internal retirement ledger (see ModuleServiceRetirementLedger).
    private static object Ledger(ModuleServiceRuntime runtime) =>
        typeof(ModuleServiceRuntime).GetField("Retirements", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;

    private static int OwnedScopeCount(ModuleServiceBindings bindings)
    {
        var ledger = Ledger(bindings.Runtime);
        var scopes = (Array)ledger.GetType().GetMethod("ScopesOf", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ledger, [bindings])!;
        return scopes.Length;
    }

    private static int CandidateReceiptCount(ModuleServiceRuntime runtime)
    {
        var ledger = Ledger(runtime);
        return (int)ledger.GetType().GetProperty("CandidateCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ledger)!;
    }

    private static int LiveScopeCount(ModuleServiceRuntime runtime)
    {
        var ledger = Ledger(runtime);
        return (int)ledger.GetType().GetProperty("ScopeCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ledger)!;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(ModuleHost, WeakReference, ModuleServiceScope?)> RetireHistoricalRegistration(bool keepScope = false)
    {
        var runtime = new ModuleServiceRuntime();
        var capture = new object();
        var reference = new WeakReference(capture);
        runtime.AddSingletonFactory<Resource>(ModuleId.FromModuleType<HistoricalModule>(), _ =>
        {
            GC.KeepAlive(capture);
            return new Resource();
        }, _ => ValueTask.CompletedTask);
        var registration = ModuleRegistration.For(ModuleDescriptor.For<HistoricalModule>("history"), () => new HistoricalModule());
        var host = new ModuleHost([registration], new ModuleHostOptions([typeof(HistoricalModule)]) { Lifetime = runtime });
        Assert.True((await host.InitializeAsync(TestContext.Current.CancellationToken)).IsSuccess);
        var scope = keepScope ? runtime.Root.CreateScope() : null;
        using (runtime.Root.Borrow<Resource>()) { }
        var changed = await host.ChangeAsync(new ModuleChangeRequest(host.Current, [], new ModuleHostOptions([]) { Lifetime = runtime }), TestContext.Current.CancellationToken);
        Assert.Equal(ModuleChangeStatus.Committed, changed.Status);
        Assert.True((await changed.Retirement!.FinalCompletion).IsSuccess);
        return (host, reference, scope is null ? null : runtime.Bindings.BindScope(scope));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Collect()
    {
        for (var index = 0; index < 3; index++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private sealed class Resource;
    private sealed class FailedResource;
    private sealed class ReleaseCounts
    {
        internal int Failed;
        internal int Successful;
    }
    private sealed class HistoricalModule : ModuleBase;
}
