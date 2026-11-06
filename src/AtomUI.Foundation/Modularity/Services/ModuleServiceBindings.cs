using AtomUI.Lifetimes;

namespace AtomUI.Modularity.Services;

/// <summary>An independently frozen service registration view within one runtime.</summary>
public sealed class ModuleServiceBindings
{
    private readonly Dictionary<Type, ServiceRegistration> _registrations;
    private readonly HashSet<ServiceRegistration> _newRegistrations;
    private readonly bool _candidate;
    internal readonly object Identity = new();
    private bool _frozen;
    private bool _retiring;
    private LifetimeRetirement? _retirement;
    private LifetimeRetirement? _retirementBatch;
    private readonly List<ModuleServiceScope.ScopeState> _ownedScopes = [];

    internal ModuleServiceBindings(ModuleServiceRuntime runtime)
    {
        Runtime = runtime;
        _registrations = [];
        _newRegistrations = [];
        Root = new ModuleServiceScope(this, runtime.Lifetimes.CreateScope(), isRoot: true);
    }

    internal ModuleServiceBindings(ModuleServiceBindings baseline, HashSet<ModuleId> removed)
    {
        Runtime = baseline.Runtime;
        lock (Runtime.Gate)
        {
            if (!baseline._frozen) throw new InvalidOperationException("Baseline bindings are not frozen.");
            _candidate = true;
            _registrations = baseline._registrations.Where(pair => !removed.Contains(pair.Value.Owner)).ToDictionary();
            _newRegistrations = [];
            Root = new ModuleServiceScope(this, baseline.Root);
        }
    }

    public ModuleServiceRuntime Runtime { get; }
    public ModuleServiceScope Root { get; }
    public LifetimeRetirement? Retirement { get { lock (Runtime.Gate) return _retirement; } }
    internal IEnumerable<ServiceRegistration> Registrations => _registrations.Values;
    internal bool Frozen => _frozen;
    internal bool IsNew(ServiceRegistration registration) => _candidate && _newRegistrations.Contains(registration);

    public void AddSingletonFactory<T>(ModuleId owner, Func<ModuleServiceActivationContext, T> factory,
        Func<T, ValueTask> release, Action<T>? stop = null, params Type[] dependencies) where T : class =>
        Register(owner, ModuleServiceLifetime.Singleton, factory, release, stop, dependencies);
    public void AddScopedFactory<T>(ModuleId owner, Func<ModuleServiceActivationContext, T> factory,
        Func<T, ValueTask> release, Action<T>? stop = null, params Type[] dependencies) where T : class =>
        Register(owner, ModuleServiceLifetime.Scoped, factory, release, stop, dependencies);
    public void AddTransientFactory<T>(ModuleId owner, Func<ModuleServiceActivationContext, T> factory,
        Func<T, ValueTask> release, Action<T>? stop = null, params Type[] dependencies) where T : class =>
        Register(owner, ModuleServiceLifetime.Transient, factory, release, stop, dependencies);

    public void Freeze()
    {
        lock (Runtime.Gate)
        {
            if (_frozen) return;
            ServiceRegistrationGraph.Validate(_registrations);
            foreach (var registration in _newRegistrations) registration.BindDependencies(_registrations);
            _frozen = true;
        }
    }

    public ModuleServiceScope BindScope(ModuleServiceScope source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!ReferenceEquals(source.Runtime, Runtime)) throw new ArgumentException("Scope belongs to another runtime.", nameof(source));
        return new ModuleServiceScope(this, source);
    }

    /// <summary>Closes candidate admission and dispatches Stop without releasing owners.</summary>
    public void Stop()
    {
        var ticket = PrepareRetirement();
        if (!ReferenceEquals(ticket, Runtime.ShutdownRetirement)) Runtime.Lifetimes.DispatchRetirement(ticket);
    }

    /// <summary>Allows the candidate's previously stopped owners to release.</summary>
    public LifetimeRetirement CompleteRetirement()
    {
        var ticket = PrepareRetirement();
        if (ReferenceEquals(ticket, Runtime.ShutdownRetirement)) Runtime.Lifetimes.CompleteRetirement();
        else Runtime.Lifetimes.CompleteRetirement(ticket);
        return Retirement!;
    }

    /// <summary>Stops and completes retirement of candidate-owned owners.</summary>
    public LifetimeRetirement BeginRetirement() => CompleteRetirement();

    private LifetimeRetirement PrepareRetirement()
    {
        ModuleServiceScope.ScopeState[] scopes;
        LifetimeRetirement[] sources;
        TaskCompletionSource<LifetimeRetirement[]> source;
        LifetimeRetirement batch;
        lock (Runtime.Gate)
        {
            while (_retiring && _retirement is null) Monitor.Wait(Runtime.Gate);
            if (_retirement is not null) return _retirementBatch!;
            _retiring = true;
            foreach (var registration in _newRegistrations) registration.Blocks++;
            foreach (var scope in _ownedScopes) scope.Blocks++;
            while (_newRegistrations.Any(registration => registration.Pending != 0) || _ownedScopes.Any(scope => scope.Pending != 0))
                Monitor.Wait(Runtime.Gate);
            scopes = _ownedScopes.ToArray();
            lock (Runtime.Lifetimes.Gate)
                batch = _retirementBatch = Runtime.ShutdownRetirement ??
                    Runtime.Lifetimes.PrepareRetirement(_newRegistrations.SelectMany(registration => registration.Owners)
                        .Concat(scopes.SelectMany(scope => scope.Owners.Owners)).Distinct());
            sources = scopes.Select(scope => scope.PrepareRetirement()).Append(batch).ToArray();
            source = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _retirement = LifetimeRetirement.Aggregate(source.Task, result =>
            {
                if (result.IsSuccess)
                    lock (Runtime.Gate) Runtime.CandidateRetirements.Remove(_retirement!);
            });
            Runtime.CandidateRetirements.Add(_retirement, batch);
            Monitor.PulseAll(Runtime.Gate);
        }
        foreach (var scope in scopes) scope.StartRetirement();
        source.SetResult(sources);
        return batch;
    }

    internal ServiceRegistration GetRegistration(Type type)
    {
        lock (Runtime.Gate)
        {
            if (!_frozen) throw new InvalidOperationException("Managed service registrations are not frozen.");
            if (_retiring) throw new InvalidOperationException("Managed service bindings are retiring.");
            return _registrations.TryGetValue(type, out var registration) ? registration :
                throw new InvalidOperationException($"Managed service '{type.FullName}' is not registered.");
        }
    }

    internal ModuleServiceScope CreateScope()
    {
        lock (Runtime.Gate)
        {
            if (_retiring) throw new InvalidOperationException("Managed service bindings are retiring.");
            var scope = new ModuleServiceScope(this, Runtime.Lifetimes.CreateScope());
            scope.State.Registration = new ModuleServiceScope.ScopeRegistration(Runtime.Gate, _ownedScopes, Runtime.LiveScopes, scope.State);
            _ownedScopes.Add(scope.State);
            Runtime.LiveScopes.Add(scope.State);
            return scope;
        }
    }

    internal bool Selects(ServiceRegistration registration)
    {
        lock (Runtime.Gate)
            return _frozen && !_retiring && _registrations.TryGetValue(registration.ServiceType, out var selected) &&
                ReferenceEquals(selected, registration);
    }

    private void Register<T>(ModuleId owner, ModuleServiceLifetime lifetime, Func<ModuleServiceActivationContext, T> factory,
        Func<T, ValueTask> release, Action<T>? stop, Type[] dependencies) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(release); ArgumentNullException.ThrowIfNull(dependencies);
        if (string.IsNullOrWhiteSpace(owner.Value)) throw new ArgumentException("A valid module owner is required.", nameof(owner));
        if (dependencies.Any(type => type is null || type.ContainsGenericParameters || type.IsByRef || type.IsPointer))
            throw new ArgumentException("Dependencies must be closed service types.", nameof(dependencies));
        var registration = new ServiceRegistration<T>(owner, lifetime, factory, release, stop, dependencies.Distinct().ToArray());
        lock (Runtime.Gate)
        {
            if (_frozen || _retiring) throw new InvalidOperationException("Managed service registrations are frozen or retiring.");
            if (!_registrations.TryAdd(typeof(T), registration)) throw new InvalidOperationException($"Managed service '{typeof(T).FullName}' already has a registration.");
            _newRegistrations.Add(registration);
        }
    }
}
