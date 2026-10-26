using AtomUI.Lifetimes;

namespace AtomUI.Modularity.Services;

public sealed class ModuleServiceScope : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, Lazy<LifetimeOwner>> _instances = [];
    private readonly ModuleServiceRuntime _runtime;
    private readonly bool _isRoot;
    private bool _closing;

    internal ModuleServiceScope(ModuleServiceRuntime runtime, LifetimeScope owners, bool isRoot = false)
    {
        _runtime = runtime;
        Owners = owners;
        _isRoot = isRoot;
    }

    internal LifetimeScope Owners { get; }

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
            return new ModuleServiceScope(_runtime, _runtime.Lifetimes.CreateScope());
        }
    }

    public void ShareFrom<T>(ModuleServiceScope source) where T : class
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!ReferenceEquals(_runtime, source._runtime) || ReferenceEquals(this, source))
            throw new ArgumentException("Shared services require a different scope of the same runtime.", nameof(source));
        if (_isRoot)
            throw new InvalidOperationException("The runtime root cannot share a service from a shorter-lived scope.");
        var registration = _runtime.GetRegistration(typeof(T));
        var shared = new Lazy<LifetimeOwner>(() =>
        {
            var owner = source.ResolveOwner<T>();
            Owners.Create<ScopeLink>(registration.Owner, link =>
            {
                link.Capture(owner);
                return new ScopeLink();
            }, static _ => ValueTask.CompletedTask, _ => BeginRetirement());
            return owner;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
        lock (_gate)
        {
            EnsureOpen();
            if (!_instances.TryAdd(typeof(T), shared))
                throw new InvalidOperationException($"Service '{typeof(T).FullName}' is already bound in this scope.");
        }
        _ = shared.Value;
    }

    public LifetimeRetirement BeginRetirement()
    {
        lock (_gate) _closing = true;
        return _isRoot ? _runtime.Lifetimes.BeginRetirement() : Owners.BeginRetirement();
    }

    public async ValueTask DisposeAsync()
    {
        var result = await BeginRetirement().FinalCompletion.ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new LifetimeCleanupException(result);
    }

    internal LifetimeOwner<T> ResolveOwner<T>() where T : class
    {
        var registration = _runtime.GetRegistration(typeof(T));
        Lazy<LifetimeOwner>? existing;
        lock (_gate)
        {
            EnsureOpen();
            _instances.TryGetValue(typeof(T), out existing);
        }
        if (existing is not null) return (LifetimeOwner<T>)existing.Value;
        if (registration.Lifetime == ModuleServiceLifetime.Singleton && !_isRoot)
            return _runtime.Root.ResolveOwner<T>();
        if (registration.Lifetime == ModuleServiceLifetime.Transient)
            return (LifetimeOwner<T>)registration.Create(this);
        lock (_gate)
        {
            EnsureOpen();
            if (!_instances.TryGetValue(typeof(T), out existing))
            {
                existing = new Lazy<LifetimeOwner>(() => registration.Create(this), LazyThreadSafetyMode.ExecutionAndPublication);
                _instances.Add(typeof(T), existing);
            }
        }
        return (LifetimeOwner<T>)existing.Value;
    }

    private void EnsureOpen() => ObjectDisposedException.ThrowIf(_closing || Owners.IsRetiring, this);
    private sealed class ScopeLink;
}
