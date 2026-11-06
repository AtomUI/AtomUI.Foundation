using AtomUI.Lifetimes;

namespace AtomUI.Modularity.Services;

/// <summary>Recoverable admission suspension for the actual old binding closure.</summary>
public sealed class ModuleServiceTransition : IDisposable
{
    private readonly ModuleServiceRuntime _runtime;
    private readonly ServiceRegistration[] _removed;
    private readonly LifetimeSuspension _suspension;
    private readonly LifetimeOwner[] _owners;
    private LifetimeRetirement? _retirement;
    private readonly HashSet<ModuleServiceScope.ScopeState> _scopes = [];
    private bool _stopped;
    private bool _disposed;

    internal ModuleServiceTransition(ModuleServiceRuntime runtime, ModuleServiceBindings previous, ModuleServiceBindings next)
    {
        ArgumentNullException.ThrowIfNull(previous); ArgumentNullException.ThrowIfNull(next);
        if (!ReferenceEquals(previous.Runtime, runtime) || !ReferenceEquals(next.Runtime, runtime))
            throw new ArgumentException("Bindings belong to another runtime.");
        _runtime = runtime;
        lock (runtime.Gate)
        {
            if (!previous.Frozen || !next.Frozen) throw new InvalidOperationException("Bindings must be frozen.");
            var retained = next.Registrations.ToHashSet();
            _removed = previous.Registrations.Where(registration => !retained.Contains(registration)).ToArray();
            if (previous.Registrations.Where(retained.Contains)
                .Any(registration => registration.BoundDependencies.Values.Any(dependency => !retained.Contains(dependency))))
                throw new InvalidOperationException("A retained service depends on a changed registration; explicitly replace its registration.");
            foreach (var registration in _removed) registration.Blocks++;
            try
            {
                LifetimeSuspension suspension;
                lock (runtime.Lifetimes.Gate)
                    suspension = runtime.Lifetimes.Suspend(_removed.SelectMany(registration => registration.Owners)
                        .Where(owner => owner.CurrentState == LifetimeOwnerState.Active));
                try
                {
                    HashSet<LifetimeOwner> affected;
                    while (true)
                    {
                        ModuleServiceScope.ScopeState[] added;
                        lock (runtime.Lifetimes.Gate)
                        {
                            affected = ConsumerClosure(_removed.SelectMany(registration => registration.Owners)
                                .Concat(_scopes.SelectMany(scope => scope.Owners.Owners)));
                            added = affected.Where(runtime.ScopeLinks.ContainsKey)
                                .Select(owner => runtime.ScopeLinks[owner]).Where(scope => _scopes.Add(scope)).ToArray();
                            foreach (var scope in added) scope.Blocks++;
                        }
                        if (added.Length == 0) break;
                        lock (runtime.Lifetimes.Gate)
                        {
                            var seeds = suspension.Owners.Concat(_scopes.SelectMany(scope => scope.Owners.Owners))
                                .Where(owner => owner.CurrentState == LifetimeOwnerState.Active).ToArray();
                            var expanded = runtime.Lifetimes.Suspend(seeds);
                            suspension.Dispose();
                            suspension = expanded;
                        }
                    }
                    if (retained.Any(registration => registration.Owners.Any(affected.Contains)))
                        throw new InvalidOperationException("A retained service captures a retiring registration; explicitly replace its registration.");
                    _owners = affected.ToArray();
                    _suspension = suspension;
                    AffectedModules = _removed.Select(registration => registration.Owner)
                        .Concat(affected.Select(owner => owner.ModuleId)).ToHashSet();
                }
                catch { suspension.Dispose(); throw; }
            }
            catch
            {
                foreach (var registration in _removed) registration.Blocks--;
                foreach (var scope in _scopes) scope.Blocks--;
                throw;
            }
        }
    }

    private static HashSet<LifetimeOwner> ConsumerClosure(IEnumerable<LifetimeOwner> seeds)
    {
        var affected = seeds.ToHashSet();
        var pending = new Queue<LifetimeOwner>(affected);
        while (pending.TryDequeue(out var owner))
            foreach (var capture in owner.Consumers)
                if (affected.Add(capture.Consumer)) pending.Enqueue(capture.Consumer);
        return affected;
    }

    public IReadOnlySet<ModuleId> AffectedModules { get; }
    public LifetimeRetirement? Retirement { get { lock (_runtime.Gate) return _retirement; } }
    public void Stop()
    {
        LifetimeRetirement ticket;
        lock (_runtime.Gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_stopped)
            {
                _retirement = _runtime.Lifetimes.PrepareRetirement(_owners);
                _suspension.Dispose();
                _stopped = true;
            }
            ticket = _retirement!;
        }
        _runtime.Lifetimes.DispatchRetirement(ticket);
    }
    public LifetimeRetirement CompleteRetirement()
    {
        Stop();
        return _runtime.Lifetimes.CompleteRetirement(Retirement!);
    }
    public void Dispose()
    {
        lock (_runtime.Gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_stopped) return;
            _suspension.Dispose();
            foreach (var registration in _removed) registration.Blocks--;
            foreach (var scope in _scopes) scope.Blocks--;
        }
    }
}
