using AtomUI.Modularity.Services;

namespace AtomUI.Modularity;

public sealed partial class ModuleHost
{
    private readonly HashSet<ModuleId> _suspendedModules = [];
    private readonly HashSet<ModuleChangeRetirement> _retirements = [];
    private RecoveryFailure? _recoveryFailure;

    private sealed record RecoveryFailure(ModuleServiceTransition? Services, IReadOnlyList<ModuleDiagnostic> Diagnostics);

    public ModuleHostLease AcquireView(IEnumerable<ModuleId>? modules = null)
    {
        var requested = modules?.ToHashSet();
        lock (_stateLock)
        {
            if (_phase != ModuleHostPhase.Initialized || _shutdownRequested)
                throw new InvalidOperationException("Only a running host admits composition readers.");
            var view = Current;
            var entries = requested is null ? view.Modules.ToArray() : requested.Select(id =>
                view.Composition.ModulesById.TryGetValue(id, out var entry) ? entry :
                    throw new ArgumentException("The requested modules are not enabled.", nameof(modules))).ToArray();
            if (entries.Any(entry => entry.Closing || _suspendedModules.Contains(entry.Descriptor.Id)))
                throw new InvalidOperationException("The requested modules do not admit new work.");
            foreach (var entry in entries)
            {
                if (entry.Readers++ == 0) entry.ReadersExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            return new ModuleHostLease(this, view, entries);
        }
    }

    internal void ReturnView(IEnumerable<ModuleEntry> modules)
    {
        lock (_stateLock)
            foreach (var entry in modules)
                if (--entry.Readers == 0) entry.ReadersExited!.TrySetResult();
    }

    public ValueTask<ModuleChangeResult> ChangeAsync(ModuleChangeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        TaskCompletionSource<ModuleHostResult> operation;
        TaskCompletionSource<ModuleChangeResult> completion;
        lock (_stateLock)
        {
            if (InCallback || _shutdownRequested || _recoveryFailure is not null ||
                _phase != ModuleHostPhase.Initialized || _activeOperation is not null)
                return ValueTask.FromResult(RejectedChange("The host is not available for a module change."));
            if (!ReferenceEquals(request.Baseline, Current) || !ReferenceEquals(request.Baseline.Host, this))
                return ValueTask.FromResult(new ModuleChangeResult(ModuleChangeStatus.StaleBaseline, Current));
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromResult(new ModuleChangeResult(ModuleChangeStatus.Canceled, Current));
            operation = NewCompletion();
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeOperation = operation.Task;
            _activeStage = "ChangeModules";
        }
        _ = CompleteChangeOperationAsync(request, cancellationToken, operation, completion);
        return new ValueTask<ModuleChangeResult>(completion.Task);
    }

    private async Task CompleteChangeOperationAsync(ModuleChangeRequest request, CancellationToken cancellationToken,
        TaskCompletionSource<ModuleHostResult> operation, TaskCompletionSource<ModuleChangeResult> completion)
    {
        ModuleChangeResult result;
        try { result = await ChangeCoreAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) { result = RejectedChange(exception.Message, exception); }
        lock (_stateLock) { _activeOperation = null; _activeStage = null; }
        operation.TrySetResult(ModuleHostResult.Success);
        completion.TrySetResult(result);
    }

    private ModuleChangeResult RejectedChange(string message, Exception? exception = null) =>
        new(ModuleChangeStatus.Rejected, Current, [new ModuleDiagnostic("ATOMUICHANGE001", message,
            stage: "ChangeModules", exceptionType: exception?.GetType().FullName)]);

    private async ValueTask<ModuleChangeResult> ChangeCoreAsync(ModuleChangeRequest request, CancellationToken token)
    {
        var baseline = request.Baseline;
        if (_options.Lifetime is not null && _options.Lifetime is not IModuleHostReconfigurationLifetime)
            return RejectedChange("The configured lifetime does not support partial reconfiguration.");
        if (request.Options.Lifetime is not null && !ReferenceEquals(request.Options.Lifetime, _options.Lifetime))
            return RejectedChange("A module change cannot replace the host lifetime authority.");

        var roots = request.Options.EnabledModuleIds.ToHashSet();
        roots.ExceptWith(request.DisabledModules);
        var candidate = new ModuleComposition(request.Registrations, new ModuleHostOptions(roots));
        var registrationDiagnostics = ValidateRegistrations(candidate);
        if (registrationDiagnostics.Count != 0) return new ModuleChangeResult(ModuleChangeStatus.Rejected, baseline, registrationDiagnostics);
        if (request.DisabledModules.Count != 0)
        {
            var map = BuildRegistrationMap(candidate);
            foreach (var root in roots.ToArray())
            {
                var pending = new Stack<ModuleId>();
                var visited = new HashSet<ModuleId>();
                pending.Push(root);
                var forbidden = false;
                while (pending.TryPop(out var id))
                {
                    if (!visited.Add(id)) continue;
                    if (request.DisabledModules.Contains(id)) { forbidden = true; break; }
                    if (map.TryGetValue(id, out var registration))
                        foreach (var dependency in registration.Descriptor.Dependencies) pending.Push(dependency.ModuleId);
                }
                if (!forbidden) continue;
                if (!request.Cascade) return RejectedChange("An enabled module requires a module explicitly disabled by this change.");
                roots.Remove(root);
            }
            candidate = new ModuleComposition(request.Registrations, new ModuleHostOptions(roots));
        }
        var graph = ResolveGraph(candidate);
        if (!graph.IsSuccess) return new ModuleChangeResult(ModuleChangeStatus.Rejected, baseline, graph.Diagnostics);
        candidate.Resolved = graph.Modules;
        var previousRegistrations = baseline.Registrations.ToDictionary(registration => registration.Descriptor.Id);
        foreach (var registration in candidate.Registrations)
            if (previousRegistrations.TryGetValue(registration.Descriptor.Id, out var previous) &&
                !ReferenceEquals(previous, registration) && !request.ReplacedModules.Contains(registration.Descriptor.Id))
                return RejectedChange("Replacing an existing registration requires an explicit replaced module identity.");
        if (request.ReplacedModules.Any(id => !previousRegistrations.ContainsKey(id) || !candidate.Registrations.Any(registration => registration.Descriptor.Id == id)))
            return RejectedChange("A replacement must identify an existing and a target registration.");

        var selected = graph.Modules.Select(descriptor => descriptor.Id).ToHashSet();
        var removed = baseline.Modules.Where(entry => !selected.Contains(entry.Descriptor.Id) || request.ReplacedModules.Contains(entry.Descriptor.Id)).ToArray();
        var removedIds = removed.Select(entry => entry.Descriptor.Id).ToHashSet();
        var unchanged = request.Participant is null && removed.Length == 0 &&
            candidate.Registrations.SequenceEqual(baseline.Registrations) &&
            roots.SetEquals(baseline.Composition.Options.EnabledModuleIds);
        if (unchanged) return new ModuleChangeResult(ModuleChangeStatus.NoChange, baseline);
        if (baseline.State is not null && request.Participant is null)
            return RejectedChange("Changing a composition with domain state requires its domain participant.");

        candidate = new ModuleComposition(candidate.Registrations, candidate.Options, baseline.Composition.Services.Fork(removedIds))
        { Resolved = graph.Modules };
        if (baseline.ManagedServices is { } bindings)
            candidate.ManagedServices = bindings.Runtime.CreateCandidate(bindings, removedIds);
        var retained = baseline.Modules.Where(entry => !removedIds.Contains(entry.Descriptor.Id)).ToDictionary(entry => entry.Descriptor.Id);
        var transaction = new ModuleChangeTransaction(this, baseline, candidate, initial: false, retained, removed, request.Participant);
        try
        {
            EnsureCandidateStage(transaction.CreateEntries("ChangeModules", token));
            EnsureCandidateStage(await transaction.ConfigureAsync("PreConfigureServices", ModuleHostPhase.PreConfiguredServices,
                static (module, context, cancellation) => module.PreConfigureServicesAsync(context, cancellation), token).ConfigureAwait(false));
            EnsureCandidateStage(await transaction.ConfigureAsync("ConfigureServices", ModuleHostPhase.ConfiguredServices,
                static (module, context, cancellation) => module.ConfigureServicesAsync(context, cancellation), token).ConfigureAwait(false));
            EnsureCandidateStage(await transaction.ConfigureAsync("PostConfigureServices", ModuleHostPhase.PostConfiguredServices,
                static (module, context, cancellation) => module.PostConfigureServicesAsync(context, cancellation), token).ConfigureAwait(false));
            await transaction.ConfigureDomainAsync(token).ConfigureAwait(false);
            var freezeNotes = transaction.Freeze();
            await transaction.PrepareDomainAsync(token).ConfigureAwait(false);
            EnsureCandidateStage(await transaction.InitializeAsync(token).ConfigureAwait(false));
            token.ThrowIfCancellationRequested();
            await transaction.SuspendAsync(token).ConfigureAwait(false);
            var (state, publisher) = transaction.SealState();
            ModuleHostView next;
            lock (_stateLock)
            {
                token.ThrowIfCancellationRequested();
                if (_shutdownRequested) throw new InvalidOperationException("Host shutdown was requested.");
                if (!ReferenceEquals(Current, baseline)) throw new InvalidOperationException("The composition baseline is stale.");
                next = transaction.Publish(state, publisher);
                Volatile.Write(ref _current, next);
            }
            transaction.MarkRetiring();
            var retirement = StartTrackedCleanup(transaction.PreviousRetirementPlan());
            return new ModuleChangeResult(ModuleChangeStatus.Committed, next,
                freezeNotes.Count == 0 ? null : CreateResult(freezeNotes).Diagnostics, retirement);
        }
        catch (Exception exception)
        {
            var diagnostics = new List<ModuleDiagnostic> { FailureDiagnostic(exception, "ChangeModules") };
            // Only proven recovery may reopen admission. Failed recovery remains
            // isolated under this coordinator until global shutdown takes over.
            var recovered = true;
            if (transaction.Suspended)
            {
                try
                {
                    if (transaction.Preparation is { } preparation)
                        await InvokeCallbackAsync(async () => { await preparation.ResumeAsync().ConfigureAwait(false); return true; }).ConfigureAwait(false);
                }
                catch (Exception resume)
                {
                    recovered = false;
                    var failure = FailureDiagnostic(resume, "ResumeModules");
                    diagnostics.Add(failure);
                    lock (_stateLock) _recoveryFailure = new RecoveryFailure(transaction.Services, [failure]);
                }
                if (recovered) lock (_stateLock) _suspendedModules.Clear();
            }
            if (recovered) transaction.Services?.Dispose();
            transaction.MarkAborted();
            var cleanup = StartTrackedCleanup(transaction.AbortPlan());
            var canceled = exception is OperationCanceledException && token.IsCancellationRequested;
            return new ModuleChangeResult(canceled ? ModuleChangeStatus.Canceled : ModuleChangeStatus.Rejected,
                baseline, diagnostics, cleanup);
        }
    }

    private static void EnsureCandidateStage(ModuleHostResult result)
    {
        if (!result.IsSuccess) throw new InvalidOperationException(string.Join("; ", result.Diagnostics.Select(item => item.Message)));
    }

}
