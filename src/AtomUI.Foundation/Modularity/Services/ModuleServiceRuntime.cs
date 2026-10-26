using AtomUI.Lifetimes;

namespace AtomUI.Modularity.Services;

public sealed class ModuleServiceRuntime : IModuleHostLifetime, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, ServiceRegistration> _registrations = [];
    private bool _frozen;

    public LifetimeDomain Lifetimes { get; } = new();
    public ModuleServiceScope Root { get; }

    public ModuleServiceRuntime() => Root = new(this, Lifetimes.CreateScope(), isRoot: true);

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
        lock (_gate)
        {
            if (_frozen) return;
            ServiceRegistrationGraph.Validate(_registrations);
            _frozen = true;
        }
    }

    public void Stop() => Lifetimes.Stop();

    public async ValueTask<ModuleHostResult> CompleteShutdownAsync()
    {
        var result = await Lifetimes.CompleteRetirement().FinalCompletion.ConfigureAwait(false);
        var diagnostics = result.Failures.Select(failure => new ModuleDiagnostic(
            ModuleServiceDiagnosticCodes.CleanupFailed, failure.Exception.Message, failure.ModuleId,
            stage: failure.Stage, exceptionType: failure.Exception.GetType().FullName,
            context: new Dictionary<string, string> { ["ownerId"] = failure.OwnerId.ToString(System.Globalization.CultureInfo.InvariantCulture) })).ToList();
        var failedOwnerIds = result.Failures.Select(failure => failure.OwnerId).ToHashSet();
        diagnostics.AddRange(result.Owners
            .Where(owner => owner.State == LifetimeOwnerState.RetainedByFailedOwner && !failedOwnerIds.Contains(owner.Id))
            .Select(owner => new ModuleDiagnostic(
                ModuleServiceDiagnosticCodes.OwnerRetainedAfterCleanupFailure,
                $"Managed owner '{owner.Id}' remains retained because a dependent owner failed to release.",
                owner.ModuleId,
                stage: "RetainedAfterCleanupFailure",
                context: new Dictionary<string, string>
                {
                    ["ownerId"] = owner.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["state"] = owner.State.ToString(),
                    ["readers"] = owner.Readers.ToString(System.Globalization.CultureInfo.InvariantCulture)
                })));
        return new ModuleHostResult(diagnostics);
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        var result = await CompleteShutdownAsync().ConfigureAwait(false);
        if (!result.IsSuccess) throw new ModuleServiceShutdownException(result.Diagnostics);
    }

    internal ServiceRegistration GetRegistration(Type serviceType)
    {
        lock (_gate)
        {
            if (!_frozen) throw new InvalidOperationException("Managed service registrations are not frozen.");
            return _registrations.TryGetValue(serviceType, out var registration)
                ? registration : throw new InvalidOperationException($"Managed service '{serviceType.FullName}' is not registered.");
        }
    }

    private void Register<T>(ModuleId owner, ModuleServiceLifetime lifetime,
        Func<ModuleServiceActivationContext, T> factory, Func<T, ValueTask> release,
        Action<T>? stop, Type[] dependencies) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(dependencies);
        if (string.IsNullOrWhiteSpace(owner.Value)) throw new ArgumentException("A valid module owner is required.", nameof(owner));
        if (dependencies.Any(type => type is null || type.ContainsGenericParameters || type.IsByRef || type.IsPointer))
            throw new ArgumentException("Dependencies must be closed service types.", nameof(dependencies));
        var registration = new ServiceRegistration<T>(owner, lifetime, factory, release, stop, dependencies.Distinct().ToArray());
        lock (_gate)
        {
            if (_frozen) throw new InvalidOperationException("Managed service registrations are frozen.");
            if (!_registrations.TryAdd(typeof(T), registration))
                throw new InvalidOperationException($"Managed service '{typeof(T).FullName}' already has a registration.");
        }
    }
}
