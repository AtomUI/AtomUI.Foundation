using AtomUI.Lifetimes;
using AtomUI.Modularity;
using AtomUI.Modularity.Services;
using Xunit;

namespace AtomUI.Foundation.Tests.Optimization;

/// <summary>
/// P0 red baseline for the 2026-11-05 architecture optimization proposal
/// (docs/architecture/2026-11-05-foundation-architecture-optimization-proposal.md).
/// Each test asserts the approved target contract and fails against the current
/// implementation. The category trait identifies them until the owning phase turns
/// them green; it must not be used to skip them in release verification. The trait is applied per
/// test and removed as each owning phase lands (O02 and O16 turned green in P2, O05 and O07 in P3).
/// </summary>
public sealed class P0RedBaselineTests
{
    private static readonly ModuleId Owner = ModuleId.FromSerializedValue("Tests.Optimization");

    // O02 / W2: a Transient instance is a request owner. Once its last lease returns and
    // nothing captures it, it must release without waiting for the scope to close.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task O02_transient_releases_after_last_lease_without_scope_retirement(bool fromRoot)
    {
        var runtime = new ModuleServiceRuntime();
        var releases = 0;
        runtime.AddTransientFactory<TransientResource>(Owner, _ => new(),
            _ => { Interlocked.Increment(ref releases); return ValueTask.CompletedTask; });
        runtime.Freeze();
        var scope = fromRoot ? runtime.Root : runtime.Root.CreateScope();

        for (var index = 0; index < 8; index++)
            using (scope.Borrow<TransientResource>()) { }

        try
        {
            Assert.Equal(8, Volatile.Read(ref releases));
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    // O05 / W4: reentrancy detection must follow the asynchronous call context. A Stop
    // callback that hands the reentrant Stop to another thread of the same logical flow
    // must be treated as reentrant, not wait for the dispatch that is waiting on it.
    [Fact]
    public async Task O05_stop_reentry_from_flowed_context_is_not_a_self_wait()
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        LifetimeSuspension? suspension = null;
        var reentryReturned = false;
        var owner = scope.Own(Owner, new TransientResource(), _ => ValueTask.CompletedTask, _ =>
        {
            var reentry = Task.Run(() => suspension!.Stop());
            reentryReturned = reentry.Wait(TimeSpan.FromSeconds(2));
        });
        suspension = domain.Suspend([owner]);

        suspension.Stop();

        Assert.True(reentryReturned, "A reentrant Stop on a flowed context must not wait for its own dispatch.");
        await suspension.CompleteRetirement().FinalCompletion;
        await domain.DisposeAsync();
    }

    // O07 / W4: returning one unrelated lease must not cost work proportional to the number
    // of pending retirements in the domain. Allocation is a deterministic proxy for the
    // full scans in the current progress loop.
    [Fact]
    public async Task O07_lease_return_cost_does_not_grow_with_pending_retirements()
    {
        var small = await MeasureLeaseReturnAllocationAsync(64);
        var large = await MeasureLeaseReturnAllocationAsync(4096);

        Assert.True(large <= small * 4 + 1024,
            $"Lease return allocated {small} bytes with 64 pending retirements and {large} bytes with 4096.");
    }

    private static async Task<long> MeasureLeaseReturnAllocationAsync(int pendingRetirements)
    {
        var domain = new LifetimeDomain();
        var scope = domain.CreateScope();
        var held = new List<LifetimeLease<TransientResource>>(pendingRetirements);
        for (var index = 0; index < pendingRetirements; index++)
        {
            var owner = scope.Own(Owner, new TransientResource(), _ => ValueTask.CompletedTask);
            held.Add(owner.AcquireLease());
            owner.BeginRetirement();
        }

        var probe = scope.Own(Owner, new TransientResource(), _ => ValueTask.CompletedTask);
        for (var warmup = 0; warmup < 4; warmup++)
            probe.AcquireLease().Dispose();

        const int samples = 16;
        var leases = new LifetimeLease<TransientResource>[samples];
        for (var index = 0; index < samples; index++) leases[index] = probe.AcquireLease();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < samples; index++) leases[index].Dispose();
        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / samples;

        foreach (var lease in held) lease.Dispose();
        await domain.DisposeAsync();
        return allocated;
    }

    // O16 (found by the P0 concurrency skeleton): an admitted Scoped factory that resolves a
    // Singleton dependency while its scope starts retiring must complete. Today the dependency
    // lookup re-checks the closing scope, throws ObjectDisposedException inside the factory, and
    // the scope's retirement reports a spurious Factory cleanup failure.
    [Fact]
    public async Task O16_admitted_factory_completes_when_scope_retires_during_construction()
    {
        var runtime = new ModuleServiceRuntime();
        using var entered = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        var creates = 0;
        var releases = 0;
        runtime.AddSingletonFactory<SharedResource>(Owner, _ => new(), _ => ValueTask.CompletedTask);
        runtime.AddScopedFactory<ScopedResource>(Owner, context =>
        {
            entered.Set();
            proceed.Wait(TimeSpan.FromSeconds(10));
            Interlocked.Increment(ref creates);
            return new(context.GetRequiredService<SharedResource>());
        }, _ => { Interlocked.Increment(ref releases); return ValueTask.CompletedTask; }, dependencies: [typeof(SharedResource)]);
        runtime.Freeze();
        var scope = runtime.Root.CreateScope();

        var borrow = Task.Run(() =>
        {
            try { scope.Borrow<ScopedResource>().Dispose(); }
            catch (InvalidOperationException) { }
        }, TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var retirement = scope.BeginRetirement();
        proceed.Set();
        await borrow;

        var result = await retirement.FinalCompletion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Empty(result.Failures);
        Assert.True(result.IsSuccess);
        Assert.Equal(1, creates);
        Assert.Equal(1, releases);
        await runtime.DisposeAsync();
    }

    private sealed class TransientResource;
    private sealed class SharedResource;
    private sealed class ScopedResource(SharedResource dependency)
    {
        public SharedResource Dependency { get; } = dependency;
    }
}
