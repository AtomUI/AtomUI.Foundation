using AtomUI.Lifetimes;

namespace AtomUI.Modularity.Services;

public sealed class ModuleServiceRuntime : IModuleHostReconfigurationLifetime, IAsyncDisposable
{
    internal readonly object Gate = new();
    private ModuleServiceBindings? _initialBindings;
    private Func<ModuleServiceBindings>? _current;
    private ModuleHost? _host;
    internal readonly Dictionary<LifetimeOwner, ModuleServiceScope.ScopeState> ScopeLinks = [];
    internal readonly List<LifetimeRetirement> CandidateRetirements = [];
    internal LifetimeRetirement? ShutdownRetirement;

    public LifetimeDomain Lifetimes { get; } = new();
    public ModuleServiceRuntime ServiceRuntime => this;
    public ModuleServiceBindings Bindings => _current?.Invoke() ?? _initialBindings!;
    internal ModuleServiceBindings InitialBindings => _initialBindings ?? throw new InvalidOperationException("Managed service registrations are frozen.");
    public ModuleServiceScope Root => Bindings.Root;

    public ModuleServiceRuntime() => _initialBindings = new ModuleServiceBindings(this);

    internal void AttachHost(ModuleHost host, Func<ModuleServiceBindings> current)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(current);
        lock (Gate)
        {
            if (_host is not null && !ReferenceEquals(_host, host))
                throw new InvalidOperationException("The service runtime is already attached to a host.");
            if (_host is null) { _host = host; _current = current; }
        }
    }

    public ModuleServiceBindings CreateCandidate(ModuleServiceBindings baseline, IEnumerable<ModuleId> removedModules)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(removedModules);
        if (!ReferenceEquals(baseline.Runtime, this)) throw new ArgumentException("Bindings belong to another runtime.", nameof(baseline));
        return new ModuleServiceBindings(baseline, removedModules.ToHashSet());
    }

    public ModuleServiceTransition PrepareTransition(ModuleServiceBindings previous, ModuleServiceBindings next) => new(this, previous, next);

    public void AddSingletonFactory<T>(ModuleId owner, Func<ModuleServiceActivationContext, T> factory,
        Func<T, ValueTask> release, Action<T>? stop = null, params Type[] dependencies) where T : class =>
        InitialBindings.AddSingletonFactory(owner, factory, release, stop, dependencies);

    public void AddScopedFactory<T>(ModuleId owner, Func<ModuleServiceActivationContext, T> factory,
        Func<T, ValueTask> release, Action<T>? stop = null, params Type[] dependencies) where T : class =>
        InitialBindings.AddScopedFactory(owner, factory, release, stop, dependencies);

    public void AddTransientFactory<T>(ModuleId owner, Func<ModuleServiceActivationContext, T> factory,
        Func<T, ValueTask> release, Action<T>? stop = null, params Type[] dependencies) where T : class =>
        InitialBindings.AddTransientFactory(owner, factory, release, stop, dependencies);

    public void Freeze()
    {
        lock (Gate)
        {
            if (_initialBindings is null) return;
            _initialBindings.Freeze();
            if (_current is not null) _initialBindings = null;
        }
    }

    public void Stop() => Lifetimes.Stop();

    public async ValueTask<ModuleHostResult> CompleteShutdownAsync()
    {
        Lifetimes.Stop();
        LifetimeRetirement[] candidates;
        lock (Gate)
        {
            ShutdownRetirement ??= Lifetimes.CurrentRetirement!;
            candidates = CandidateRetirements.ToArray();
        }
        foreach (var candidate in candidates) Lifetimes.CompleteRetirement(candidate);
        var global = Lifetimes.CompleteRetirement();
        var results = await Task.WhenAll(candidates.Append(global).Select(ticket => ticket.FinalCompletion)).ConfigureAwait(false);
        var failures = results.SelectMany(result => result.Failures).Distinct().ToArray();
        var owners = results.SelectMany(result => result.Owners).GroupBy(owner => owner.Id).Select(group => group.Last()).ToArray();
        var diagnostics = failures.Select(failure => new ModuleDiagnostic(
            ModuleServiceDiagnosticCodes.CleanupFailed, failure.Exception.Message, failure.ModuleId,
            stage: failure.Stage, exceptionType: failure.Exception.GetType().FullName,
            context: new Dictionary<string, string> { ["ownerId"] = failure.OwnerId.ToString(System.Globalization.CultureInfo.InvariantCulture) })).ToList();
        var failedOwnerIds = failures.Select(failure => failure.OwnerId).ToHashSet();
        diagnostics.AddRange(owners
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

}
