using AtomUI.Modularity;
using System.Runtime.CompilerServices;

namespace AtomUI.Lifetimes;

public sealed class LifetimeDomain : IAsyncDisposable
{
    internal readonly object Gate = new();
    private readonly List<LifetimeScope> _scopes = [];
    private readonly List<LifetimeOwner> _owners = [];
    private readonly List<LifetimeRetirement> _retirements = [];
    private readonly Queue<LifetimeOwner> _progressQueue = [];
    private readonly HashSet<LifetimeOwner> _queuedOwners = [];
    private readonly HashSet<LifetimeOwner> _ownersToPrune = [];
    private static long _nextOwnerId;
    private static readonly ConditionalWeakTable<object, LifetimeOwner> Ownership = new();
    private static readonly object OwnershipGate = new();
    private bool _stopped;
    private bool _releaseEnabled;
    private int _notificationBatches;
    private LifetimeRetirement? _retirement;
    private bool _progressing;
    private bool _progressRequested;

    public LifetimeScope CreateScope()
    {
        lock (Gate)
        {
            if (_stopped) throw new InvalidOperationException("The lifetime domain is retiring.");
            var scope = new LifetimeScope(this);
            _scopes.Add(scope);
            return scope;
        }
    }

    internal LifetimeOwner<T> Own<T>(LifetimeScope scope, ModuleId moduleId, T value,
        Func<T, ValueTask> release, Action<T>? stop) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(release);
        lock (Gate)
        {
            if (_stopped || scope.Retiring) throw new InvalidOperationException("The lifetime scope is retiring.");
            var owner = new LifetimeOwner<T>(this, Interlocked.Increment(ref _nextOwnerId), moduleId, value, release, stop);
            AdoptUnsafe(value, owner);
            owner.HandoffCompleted = true;
            scope.Owners.Add(owner);
            _owners.Add(owner);
            return owner;
        }
    }

    private static void AdoptUnsafe(object value, LifetimeOwner owner)
    {
        lock (OwnershipGate)
        {
            if (Ownership.TryGetValue(value, out _))
                throw new InvalidOperationException("The instance already has a lifetime owner.");
            Ownership.Add(value, owner);
        }
    }

    internal LifetimeOwner<T> Create<T>(LifetimeScope scope, ModuleId moduleId, Func<LifetimeOwner<T>, T> factory,
        Func<T, ValueTask> release, Action<T>? stop) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(release);
        LifetimeOwner<T> owner;
        lock (Gate)
        {
            if (_stopped || scope.Retiring) throw new InvalidOperationException("The lifetime scope is retiring.");
            owner = new LifetimeOwner<T>(this, Interlocked.Increment(ref _nextOwnerId), moduleId, null, release, stop)
            {
                Constructing = true
            };
            // Publish the reservation before user construction so retirement can always find it.
            scope.Owners.Add(owner);
            _owners.Add(owner);
        }

        List<LifetimeOwner> notifications = [];
        try
        {
            var value = factory(owner) ?? throw new InvalidOperationException("The lifetime factory returned null.");
            lock (Gate)
            {
                AdoptUnsafe(value, owner);
                owner.SetValueUnsafe(value);
                owner.HandoffCompleted = true;
                owner.Constructing = false;
                if (owner.CurrentState is LifetimeOwnerState.Retiring or LifetimeOwnerState.RetainedByFailedOwner)
                {
                    owner.StopInvoked = true;
                    notifications.Add(owner);
                }
                QueueProgressUnsafe(owner);
                // The late Stop must be dispatched before this owner can become ready.
                _notificationBatches++;
            }
        }
        catch (Exception exception)
        {
            lock (Gate)
            {
                owner.Constructing = false;
                // Preserve the failed activation in scope diagnostics until its retirement is
                // observed. The factory caller still owns cleanup of any local, unhanded value.
                owner.CurrentState = LifetimeOwnerState.CleanupFailed;
                RecordFailureUnsafe(owner, "Factory", exception);
                foreach (var capture in owner.Captures.ToArray()) RemoveCaptureUnsafe(capture);
                QueueProgressUnsafe(owner);
                RetainFailureClosureUnsafe(owner);
                owner.ForgetValue();
            }
            Progress();
            throw;
        }
        Dispatch(notifications);
        return owner;
    }

    internal LifetimeLease<T> Capture<T>(LifetimeOwner consumer, LifetimeOwner<T> dependency) where T : class
    {
        ArgumentNullException.ThrowIfNull(dependency);
        LifetimeLease<T> lease;
        var extendedFailureClosure = false;
        lock (Gate)
        {
            if (dependency.LifetimeDomain != this) throw new InvalidOperationException("Captures require the same lifetime domain.");
            if (consumer.CurrentState != LifetimeOwnerState.Active || dependency.CurrentState != LifetimeOwnerState.Active || dependency.Constructing)
                throw new InvalidOperationException("Retiring owners do not admit new captures.");
            if (ReachesUnsafe(consumer, dependency)) throw new InvalidOperationException("The capture would create a release cycle.");
            var capture = new LifetimeCapture(consumer, dependency);
            consumer.Captures.Add(capture);
            dependency.Consumers.Add(capture);
            lease = new LifetimeLease<T>(dependency, capture);
            if (consumer.BlockedByFailure)
            {
                RetainFailureClosureUnsafe(consumer);
                extendedFailureClosure = true;
            }
        }
        if (extendedFailureClosure) Progress();
        return lease;
    }

    internal void AddOrdering(LifetimeOwner owner, LifetimeOwner prerequisite)
    {
        ArgumentNullException.ThrowIfNull(prerequisite);
        var extendedFailureClosure = false;
        lock (Gate)
        {
            if (prerequisite.LifetimeDomain != this) throw new InvalidOperationException("Release ordering requires the same lifetime domain.");
            if (owner.CurrentState != LifetimeOwnerState.Active || prerequisite.CurrentState != LifetimeOwnerState.Active)
                throw new InvalidOperationException("Release ordering must be registered before retirement.");
            if (ReachesUnsafe(prerequisite, owner)) throw new InvalidOperationException("The ordering would create a release cycle.");
            owner.Prerequisites.Add(prerequisite);
            prerequisite.ReleaseDependents.Add(owner);
            if (prerequisite.BlockedByFailure)
            {
                RetainFailureClosureUnsafe(prerequisite);
                extendedFailureClosure = true;
            }
        }
        if (extendedFailureClosure) Progress();
    }

    private static bool ReachesUnsafe(LifetimeOwner start, LifetimeOwner destination)
    {
        var pending = new Stack<LifetimeOwner>();
        var visited = new HashSet<LifetimeOwner>();
        pending.Push(start);
        while (pending.TryPop(out var current))
        {
            if (current == destination) return true;
            if (!visited.Add(current)) continue;
            foreach (var prerequisite in current.Prerequisites) pending.Push(prerequisite);
            foreach (var capture in current.Consumers) pending.Push(capture.Consumer);
        }
        return false;
    }

    internal static void RemoveCaptureUnsafe(LifetimeCapture capture)
    {
        capture.Returned = true;
        capture.Consumer.Captures.Remove(capture);
        capture.Dependency.Consumers.Remove(capture);
        capture.Dependency.LifetimeDomain.QueueProgressUnsafe(capture.Dependency);
    }

    internal LifetimeLease<T> Acquire<T>(LifetimeOwner<T> owner) where T : class
    {
        lock (Gate)
        {
            if (owner.CurrentState != LifetimeOwnerState.Active || owner.Constructing)
                throw new InvalidOperationException("The lifetime owner does not admit new readers.");
            owner.Readers++;
            return new LifetimeLease<T>(owner);
        }
    }

    public void Stop()
    {
        List<LifetimeOwner> notifications;
        lock (Gate)
        {
            if (_retirement is not null) return;
            _stopped = true;
            foreach (var scope in _scopes) scope.Retiring = true;
            _retirement = PrepareRetirementUnsafe(_owners, false, out notifications);
            foreach (var scope in _scopes) scope.Retirement ??= _retirement;
        }
        Dispatch(notifications);
    }

    public LifetimeRetirement CompleteRetirement()
    {
        Stop();
        LifetimeRetirement retirement;
        lock (Gate)
        {
            _releaseEnabled = true;
            foreach (var owner in _owners)
            {
                owner.ReleaseEnabled = true;
                QueueProgressUnsafe(owner);
            }
            retirement = _retirement!;
        }
        Progress();
        return retirement;
    }

    public LifetimeRetirement BeginRetirement() => CompleteRetirement();
    public async ValueTask DisposeAsync()
    {
        var result = await BeginRetirement().FinalCompletion.ConfigureAwait(false);
        if (!result.IsSuccess) throw new LifetimeCleanupException(result);
    }

    internal LifetimeRetirement Retire(LifetimeScope scope)
    {
        List<LifetimeOwner> notifications;
        LifetimeRetirement retirement;
        lock (Gate)
        {
            if (scope.Retirement is not null) return scope.Retirement;
            scope.Retiring = true;
            scope.Retirement = retirement = PrepareRetirementUnsafe(scope.Owners, true, out notifications);
        }
        Dispatch(notifications);
        return retirement;
    }

    internal LifetimeRetirement Retire(LifetimeOwner owner)
    {
        List<LifetimeOwner> notifications;
        LifetimeRetirement retirement;
        lock (Gate)
        {
            if (owner.Retirement is not null) return owner.Retirement;
            owner.Retirement = retirement = PrepareRetirementUnsafe([owner], true, out notifications);
        }
        Dispatch(notifications);
        return retirement;
    }

    private LifetimeRetirement PrepareRetirementUnsafe(IEnumerable<LifetimeOwner> owners, bool release,
        out List<LifetimeOwner> notifications)
    {
        var affected = new HashSet<LifetimeOwner>(owners);
        var pending = new Queue<LifetimeOwner>(affected);
        while (pending.TryDequeue(out var current))
            foreach (var capture in current.Consumers)
                if (affected.Add(capture.Consumer)) pending.Enqueue(capture.Consumer);
        var members = _owners.Where(affected.Contains).ToArray();
        notifications = [];
        foreach (var owner in members)
        {
            if (owner.CurrentState == LifetimeOwnerState.Active)
                owner.CurrentState = owner.BlockedByFailure ? LifetimeOwnerState.RetainedByFailedOwner : LifetimeOwnerState.Retiring;
            owner.ReleaseEnabled |= release;
            QueueProgressUnsafe(owner);
            if (!owner.Constructing && !owner.StopInvoked && owner.CurrentState is LifetimeOwnerState.Retiring or LifetimeOwnerState.RetainedByFailedOwner)
            {
                owner.StopInvoked = true;
                notifications.Add(owner);
            }
        }

        var retirement = new LifetimeRetirement(members);
        foreach (var owner in members)
        {
            owner.Retirement ??= retirement;
            owner.Retirements.Add(retirement);
            if (!IsFinalOwnerUnsafe(owner)) retirement.PendingFinalOwners.Add(owner);
        }
        _retirements.Add(retirement);
        _notificationBatches++;
        return retirement;
    }

    private void Dispatch(List<LifetimeOwner> notifications)
    {
        foreach (var owner in notifications)
        {
            try { owner.InvokeStop(); }
            catch (Exception exception)
            {
                lock (Gate) RecordFailureUnsafe(owner, "Stop", exception);
            }
        }
        lock (Gate) _notificationBatches--;
        Progress();
    }

    internal void Progress()
    {
        lock (Gate)
        {
            if (_progressing)
            {
                _progressRequested = true;
                return;
            }
            _progressing = true;
        }

        while (true)
        {
            List<LifetimeOwner> ready = [];
            lock (Gate)
            {
                if (_notificationBatches != 0)
                {
                    _progressing = false;
                    return;
                }

                PublishInitialSnapshotsUnsafe();
                while (_progressQueue.TryDequeue(out var owner))
                {
                    _queuedOwners.Remove(owner);
                    UpdateRetirementFinalStateUnsafe(owner);
                    if ((_stopped && !_releaseEnabled) || owner.CurrentState != LifetimeOwnerState.Retiring ||
                        !owner.ReleaseEnabled || owner.Readers != 0 || owner.Constructing || owner.Consumers.Count != 0 ||
                        owner.Prerequisites.Any(prerequisite => prerequisite.CurrentState != LifetimeOwnerState.Released)) continue;
                    owner.CurrentState = LifetimeOwnerState.Releasing;
                    ready.Add(owner);
                }

                CompleteReadyRetirementsUnsafe();
                if (ready.Count == 0)
                {
                    if (_progressRequested)
                    {
                        _progressRequested = false;
                        continue;
                    }
                    PruneOwnersUnsafe();
                    RemoveCompletedScopesUnsafe();
                    _progressing = false;
                    return;
                }
                _progressRequested = false;
            }

            foreach (var owner in ready) _ = ReleaseAsync(owner);

            lock (Gate)
            {
                if (_notificationBatches != 0)
                {
                    _progressing = false;
                    return;
                }
                if (_progressRequested || _progressQueue.Count != 0)
                {
                    _progressRequested = false;
                    continue;
                }
                PruneOwnersUnsafe();
                RemoveCompletedScopesUnsafe();
                _progressing = false;
                return;
            }
        }
    }

    private async Task ReleaseAsync(LifetimeOwner owner)
    {
        var deferred = false;
        lock (Gate)
        {
            // Another release can reenter Stop after this owner was selected but before it starts.
            if (_notificationBatches != 0 || (_stopped && !_releaseEnabled))
            {
                owner.CurrentState = owner.BlockedByFailure
                    ? LifetimeOwnerState.RetainedByFailedOwner : LifetimeOwnerState.Retiring;
                QueueProgressUnsafe(owner);
                deferred = true;
            }
        }
        if (deferred)
        {
            Progress();
            return;
        }
        Exception? failure = null;
        try { await owner.InvokeReleaseAsync().ConfigureAwait(false); }
        catch (Exception exception) { failure = exception; }
        lock (Gate)
        {
            if (failure is null)
            {
                owner.CurrentState = LifetimeOwnerState.Released;
                foreach (var capture in owner.Captures.ToArray()) RemoveCaptureUnsafe(capture);
                foreach (var dependent in owner.ReleaseDependents) QueueProgressUnsafe(dependent);
                owner.ForgetValue();
                _ownersToPrune.Add(owner);
            }
            else
            {
                owner.CurrentState = LifetimeOwnerState.CleanupFailed;
                RecordFailureUnsafe(owner, "Release", failure);
                RetainFailureClosureUnsafe(owner);
            }
            QueueProgressUnsafe(owner);
        }
        Progress();
    }

    private void RetainFailureClosureUnsafe(LifetimeOwner failed)
    {
        var pending = new Queue<LifetimeOwner>();
        var visited = new HashSet<LifetimeOwner>();
        pending.Enqueue(failed);
        while (pending.TryDequeue(out var current))
        {
            if (!visited.Add(current)) continue;
            if (current != failed && current.CurrentState is not (LifetimeOwnerState.Released or LifetimeOwnerState.CleanupFailed))
            {
                current.BlockedByFailure = true;
                if (current.CurrentState == LifetimeOwnerState.Retiring)
                    current.CurrentState = LifetimeOwnerState.RetainedByFailedOwner;
                QueueProgressUnsafe(current);
            }
            foreach (var capture in current.Captures) pending.Enqueue(capture.Dependency);
            foreach (var dependent in current.ReleaseDependents) pending.Enqueue(dependent);
        }
    }

    internal void QueueProgressUnsafe(LifetimeOwner owner)
    {
        if (_queuedOwners.Add(owner)) _progressQueue.Enqueue(owner);
    }

    private static bool IsFinalOwnerUnsafe(LifetimeOwner owner) =>
        owner.Readers == 0 && !owner.Constructing && owner.CurrentState is
            LifetimeOwnerState.Released or LifetimeOwnerState.CleanupFailed or LifetimeOwnerState.RetainedByFailedOwner;

    private void PublishInitialSnapshotsUnsafe()
    {
        foreach (var retirement in _retirements)
            if (!retirement.Completion.IsCompleted)
                retirement.Initial.TrySetResult(SnapshotUnsafe(retirement));
    }

    private void UpdateRetirementFinalStateUnsafe(LifetimeOwner owner)
    {
        if (!IsFinalOwnerUnsafe(owner)) return;
        foreach (var retirement in owner.Retirements.ToArray())
            if (retirement.PendingFinalOwners.Remove(owner) && retirement.PendingFinalOwners.Count == 0)
                CompleteRetirementUnsafe(retirement);
    }

    private void CompleteReadyRetirementsUnsafe()
    {
        foreach (var retirement in _retirements.ToArray())
            if (retirement.PendingFinalOwners.Count == 0)
                CompleteRetirementUnsafe(retirement);
    }

    private void CompleteRetirementUnsafe(LifetimeRetirement retirement)
    {
        if (retirement.PendingFinalOwners.Count != 0 || retirement.FinalCompletion.IsCompleted ||
            !retirement.Completion.IsCompleted) return;

        var members = retirement.Members;
        var result = SnapshotUnsafe(retirement);
        foreach (var owner in members)
        {
            owner.Retirements.Remove(retirement);
            if (owner.CurrentState == LifetimeOwnerState.CleanupFailed && !owner.HandoffCompleted)
                _ownersToPrune.Add(owner);
        }
        _retirements.Remove(retirement);
        retirement.CompleteFinal(result);
    }

    private void PruneOwnersUnsafe()
    {
        if (_ownersToPrune.Count == 0) return;
        _owners.RemoveAll(_ownersToPrune.Contains);
        foreach (var scope in _scopes) scope.Owners.RemoveAll(_ownersToPrune.Contains);
        _ownersToPrune.Clear();
    }

    private void RemoveCompletedScopesUnsafe()
    {
        foreach (var scope in _scopes.Where(scope => scope.Retiring && scope.Retirement?.FinalCompletion.IsCompleted == true).ToArray())
        {
            // Final snapshots contain immutable owner data. A closed scope need not keep completed owners alive.
            scope.Owners.Clear();
            _scopes.Remove(scope);
        }
    }

    private void RecordFailureUnsafe(LifetimeOwner owner, string stage, Exception exception)
    {
        owner.Failure ??= exception;
        owner.Failures.Add(new LifetimeFailure(owner.Id, owner.ModuleId, stage, exception));
    }

    private LifetimeCloseResult SnapshotUnsafe(LifetimeRetirement retirement)
    {
        var ids = retirement.Members.Select(owner => owner.Id).ToHashSet();
        return new LifetimeCloseResult(Array.AsReadOnly(retirement.Members.Select(owner =>
                new LifetimeOwnerSnapshot(owner.Id, owner.ModuleId, owner.CurrentState, owner.Readers, owner.Constructing,
                    Array.AsReadOnly(owner.Captures.Select(capture => capture.Dependency.Id)
                        .Concat(owner.Prerequisites.Select(prerequisite => prerequisite.Id)).Distinct().Order().ToArray()), owner.Failure)).ToArray()),
            Array.AsReadOnly(retirement.Members.SelectMany(owner => owner.Failures).Where(failure => ids.Contains(failure.OwnerId)).ToArray()));
    }
}
