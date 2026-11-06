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

public sealed class LifetimeRetirement
{
    private readonly Task<LifetimeCloseResult>? _completion;
    private readonly Task<LifetimeCloseResult>? _finalCompletion;

    internal LifetimeRetirement(IEnumerable<LifetimeOwner> owners)
    {
        Members = owners.ToArray();
    }

    internal LifetimeOwner[] Members { get; private set; }
    internal LifetimeDomain? BatchDomain;
    internal List<LifetimeOwner>? BatchNotifications;
    internal bool BatchDispatchStarted;
    internal bool BatchNotificationsComplete;
    internal bool BatchCompleted;
    internal readonly HashSet<LifetimeOwner> PendingFinalOwners = [];
    internal readonly TaskCompletionSource<LifetimeCloseResult> Initial = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource<LifetimeCloseResult> Final = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private LifetimeRetirement(Task<LifetimeRetirement[]> sources, Action<LifetimeCloseResult>? beforeFinal)
        : this(Array.Empty<LifetimeOwner>())
    {
        _completion = AggregateAsync(sources, final: false, null);
        _finalCompletion = AggregateAsync(sources, final: true, beforeFinal);
    }

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

    public Task<LifetimeCloseResult> Completion => _completion ?? Initial.Task;
    public Task<LifetimeCloseResult> FinalCompletion => _finalCompletion ?? Final.Task;

    internal bool CompleteFinal(LifetimeCloseResult result)
    {
        if (!Final.TrySetResult(result)) return false;
        Members = [];
        return true;
    }
}
