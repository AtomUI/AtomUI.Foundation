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
    internal LifetimeRetirement(IEnumerable<LifetimeOwner> owners)
    {
        Members = owners.ToArray();
    }

    internal LifetimeOwner[] Members { get; private set; }
    internal readonly HashSet<LifetimeOwner> PendingFinalOwners = [];
    internal readonly TaskCompletionSource<LifetimeCloseResult> Initial = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource<LifetimeCloseResult> Final = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<LifetimeCloseResult> Completion => Initial.Task;
    public Task<LifetimeCloseResult> FinalCompletion => Final.Task;

    internal bool CompleteFinal(LifetimeCloseResult result)
    {
        if (!Final.TrySetResult(result)) return false;
        Members = [];
        return true;
    }
}
