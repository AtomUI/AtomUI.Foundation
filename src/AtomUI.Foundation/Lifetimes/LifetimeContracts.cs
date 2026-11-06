using AtomUI.Modularity;

namespace AtomUI.Lifetimes;

public enum LifetimeOwnerState
{
    Active,
    Retiring,
    Releasing,
    Released,
    CleanupFailed,
    RetainedByFailedOwner
}

public sealed record LifetimeFailure(long OwnerId, ModuleId ModuleId, string Stage, Exception Exception);

public sealed record LifetimeOwnerSnapshot(long Id, ModuleId ModuleId, LifetimeOwnerState State,
    int Readers, bool IsConstructing, IReadOnlyList<long> Dependencies, Exception? Failure);

public sealed class LifetimeCleanupException : InvalidOperationException
{
    public LifetimeCleanupException(LifetimeCloseResult result)
        : base("Lifetime retirement completed with cleanup failures or retained owners.")
    {
        ArgumentNullException.ThrowIfNull(result);
        Result = result;
    }

    public LifetimeCloseResult Result { get; }
}

public sealed class LifetimeCloseResult
{
    internal LifetimeCloseResult(IReadOnlyList<LifetimeOwnerSnapshot> owners, IReadOnlyList<LifetimeFailure> failures)
    {
        Owners = owners;
        Failures = failures;
    }

    public IReadOnlyList<LifetimeOwnerSnapshot> Owners { get; }
    public IReadOnlyList<LifetimeFailure> Failures { get; }
    public bool IsSuccess => Failures.Count == 0 && Owners.All(owner => owner.State == LifetimeOwnerState.Released);
}

/// <summary>
/// The domain's unit of release progress for one retirement: fixed members, owners still pending a
/// final state, Stop dispatch state and the two completion sources. Only the domain mutates it;
/// callers observe it through its public <see cref="LifetimeRetirement"/> ticket.
/// </summary>
internal sealed class LifetimeRetirementBatch
{
    internal LifetimeRetirementBatch(IEnumerable<LifetimeOwner> owners)
    {
        Members = owners.ToArray();
        Ticket = new LifetimeRetirement(this);
    }

    internal LifetimeOwner[] Members { get; private set; }
    internal LifetimeRetirement Ticket { get; }

    /// <summary>Set for an explicitly dispatched batch whose release opens only on completion.</summary>
    internal LifetimeDomain? Domain;
    internal List<LifetimeOwner>? Notifications;
    internal bool DispatchStarted;
    internal bool NotificationsComplete;
    internal bool Completed;
    internal readonly HashSet<LifetimeOwner> PendingFinalOwners = [];
    /// <summary>Scopes whose retirement this batch is; they leave the domain when it completes.</summary>
    internal List<LifetimeScope>? Scopes;
    internal readonly TaskCompletionSource<LifetimeCloseResult> Initial = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource<LifetimeCloseResult> Final = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task<LifetimeCloseResult> Completion => Initial.Task;
    internal Task<LifetimeCloseResult> FinalCompletion => Final.Task;

    internal bool CompleteFinal(LifetimeCloseResult result)
    {
        if (!Final.TrySetResult(result)) return false;
        Members = [];
        return true;
    }
}

/// <summary>
/// A public retirement receipt. It either observes exactly one domain batch or aggregates several
/// receipts; it is never a release authority of its own.
/// </summary>
public sealed class LifetimeRetirement
{
    private readonly Task<LifetimeCloseResult> _completion;
    private readonly Task<LifetimeCloseResult> _finalCompletion;

    internal LifetimeRetirement(LifetimeRetirementBatch batch)
    {
        Batch = batch;
        _completion = batch.Completion;
        _finalCompletion = batch.FinalCompletion;
    }

    private LifetimeRetirement(Task<LifetimeRetirement[]> sources, Action<LifetimeCloseResult>? beforeFinal)
    {
        _completion = AggregateAsync(sources, final: false, null);
        _finalCompletion = AggregateAsync(sources, final: true, beforeFinal);
    }

    /// <summary>The observed batch, or null for an aggregate receipt.</summary>
    internal LifetimeRetirementBatch? Batch { get; }

    // The returned tasks own aggregation and final bookkeeping. Domain batches remain the
    // only release authority; callers must connect sources outside lifetime/service locks.
    internal static LifetimeRetirement Aggregate(Task<LifetimeRetirement[]> sources,
        Action<LifetimeCloseResult>? beforeFinal = null) => new(sources, beforeFinal);

    internal static LifetimeCloseResult Combine(IEnumerable<LifetimeCloseResult> results)
    {
        var snapshots = results.ToArray();
        return new LifetimeCloseResult(
            Array.AsReadOnly(snapshots.SelectMany(result => result.Owners).GroupBy(owner => owner.Id)
                .Select(group => group.Last()).ToArray()),
            Array.AsReadOnly(snapshots.SelectMany(result => result.Failures).Distinct().ToArray()));
    }

    private static async Task<LifetimeCloseResult> AggregateAsync(Task<LifetimeRetirement[]> sources,
        bool final, Action<LifetimeCloseResult>? beforeFinal)
    {
        var tickets = await sources.ConfigureAwait(false);
        var results = await Task.WhenAll(tickets.Select(ticket => final ? ticket.FinalCompletion : ticket.Completion))
            .ConfigureAwait(false);
        var result = Combine(results);
        beforeFinal?.Invoke(result);
        beforeFinal = null;
        return result;
    }

    public Task<LifetimeCloseResult> Completion => _completion;
    public Task<LifetimeCloseResult> FinalCompletion => _finalCompletion;
}
