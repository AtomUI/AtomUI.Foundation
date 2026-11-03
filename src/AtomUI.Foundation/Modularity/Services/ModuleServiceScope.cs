using AtomUI.Lifetimes;

namespace AtomUI.Modularity.Services;

public sealed class ModuleServiceScope : IAsyncDisposable
{
    internal sealed class ScopeState(LifetimeScope owners, bool isRoot, object origin)
    {
        internal readonly object Gate = new();
        internal readonly Dictionary<ServiceRegistration, Lazy<LifetimeOwner>> Instances = [];
        internal bool Closing;
        internal int Blocks;
        internal int Pending;
        internal LifetimeScope Owners = owners;
        internal bool IsRoot = isRoot;
        internal object Origin = origin;
    }
    private readonly ScopeState _state;
    private readonly ModuleServiceBindings _bindings;
    private object _gate => _state.Gate;
    private Dictionary<ServiceRegistration, Lazy<LifetimeOwner>> _instances => _state.Instances;
    private bool _isRoot => _state.IsRoot;
    internal ModuleServiceRuntime Runtime => _bindings.Runtime;
    internal ScopeState State => _state;

    internal ModuleServiceScope(ModuleServiceBindings bindings, LifetimeScope owners, bool isRoot = false)
    {
        _bindings = bindings;
        _state = new ScopeState(owners, isRoot, bindings.Identity);
    }

    internal ModuleServiceScope(ModuleServiceBindings bindings, ModuleServiceScope source)
    {
        _bindings = bindings;
        _state = source._state;
    }

    internal LifetimeScope Owners => _state.Owners;

    public LifetimeLease<T> Borrow<T>() where T : class
    {
        var owner = ResolveOwner<T>();
        lock (_gate)
        {
            EnsureOpen();
            return owner.AcquireLease();
        }
    }

    public ModuleServiceScope CreateScope()
    {
        lock (_gate)
        {
            EnsureOpen();
            lock (Runtime.Gate)
                if (_state.Blocks != 0) throw new InvalidOperationException("Scope admission is suspended.");
            return _bindings.CreateScope();
        }
    }

    public void ShareFrom<T>(ModuleServiceScope source) where T : class
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!ReferenceEquals(Runtime, source.Runtime) || ReferenceEquals(_state, source._state))
            throw new ArgumentException("Shared services require a different scope of the same runtime.", nameof(source));
        if (_isRoot)
            throw new InvalidOperationException("The runtime root cannot share a service from a shorter-lived scope.");
        var registration = _bindings.GetRegistration(typeof(T));
        if (!ReferenceEquals(registration, source._bindings.GetRegistration(typeof(T))))
            throw new InvalidOperationException("Shared scopes must use the same service registration.");
        if (_bindings.IsNew(registration) && !ReferenceEquals(_state.Origin, _bindings.Identity) && !Runtime.Bindings.Selects(registration))
            throw new InvalidOperationException("Candidate services cannot be shared into an existing scope; create a candidate scope.");
        var shared = new Lazy<LifetimeOwner>(() =>
        {
            var owner = source.ResolveOwner<T>();
            LifetimeOwner? linkOwner = null;
            Owners.Create<ScopeLink>(registration.Owner, link =>
            {
                lock (Runtime.Gate)
                {
                    if (_state.Blocks != 0) throw new InvalidOperationException("Scope admission is suspended.");
                    Runtime.ScopeLinks.Add(link, _state);
                    try { link.Capture(owner); }
                    catch { Runtime.ScopeLinks.Remove(link); throw; }
                    linkOwner = link;
                }
                return new ScopeLink();
            }, _ =>
            {
                lock (Runtime.Gate) Runtime.ScopeLinks.Remove(linkOwner!);
                RemoveCached(registration);
                return ValueTask.CompletedTask;
            }, _ => BeginRetirement());
            return owner;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
        lock (_gate)
        {
            EnsureOpen();
            if (!_instances.TryAdd(registration, shared))
                throw new InvalidOperationException($"Service '{typeof(T).FullName}' is already bound in this scope.");
        }
        try { _ = shared.Value; }
        catch
        {
            lock (_gate)
                if (_instances.TryGetValue(registration, out var current) && ReferenceEquals(current, shared))
                    _instances.Remove(registration);
            throw;
        }
    }

    public LifetimeRetirement BeginRetirement()
    {
        lock (_gate) _state.Closing = true;
        return _isRoot ? Runtime.Lifetimes.BeginRetirement() : Owners.BeginRetirement();
    }

    public async ValueTask DisposeAsync()
    {
        var result = await BeginRetirement().FinalCompletion.ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new LifetimeCleanupException(result);
    }

    internal LifetimeOwner<T> ResolveOwner<T>() where T : class
    {
        return ResolveOwner<T>(_bindings.GetRegistration(typeof(T)));
    }

    internal LifetimeOwner<T> ResolveOwner<T>(ServiceRegistration registration) where T : class
    {
        lock (Runtime.Gate)
            if (registration.Blocks != 0 || _state.Blocks != 0) throw new InvalidOperationException("Service admission is suspended.");
        Lazy<LifetimeOwner>? existing;
        lock (_gate)
        {
            EnsureOpen();
            _instances.TryGetValue(registration, out existing);
        }
        if (existing is not null) return ReadCached<T>(registration, existing);
        if (registration.Lifetime == ModuleServiceLifetime.Singleton && !_isRoot)
            return _bindings.Root.ResolveOwner<T>(registration);
        if (registration.Lifetime == ModuleServiceLifetime.Transient)
            return (LifetimeOwner<T>)registration.Create(this);
        lock (_gate)
        {
            EnsureOpen();
            if (!_instances.TryGetValue(registration, out existing))
            {
                existing = new Lazy<LifetimeOwner>(() => registration.Create(this), LazyThreadSafetyMode.ExecutionAndPublication);
                _instances.Add(registration, existing);
            }
        }
        return ReadCached<T>(registration, existing);
    }

    private LifetimeOwner<T> ReadCached<T>(ServiceRegistration registration, Lazy<LifetimeOwner> value) where T : class
    {
        try { return (LifetimeOwner<T>)value.Value; }
        catch
        {
            lock (_gate)
                if (_instances.TryGetValue(registration, out var current) && ReferenceEquals(current, value))
                    _instances.Remove(registration);
            throw;
        }
    }

    internal void RemoveCached(ServiceRegistration registration)
    {
        lock (_gate) _instances.Remove(registration);
    }

    private void EnsureOpen() => ObjectDisposedException.ThrowIf(_state.Closing || Owners.IsRetiring, this);
    private sealed class ScopeLink;
}
