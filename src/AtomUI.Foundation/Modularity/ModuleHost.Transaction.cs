using AtomUI.Modularity.Services;

namespace AtomUI.Modularity;

/// <summary>Coarse progress of one composition change transaction.</summary>
internal enum ModuleChangeStage
{
    Validated,
    Created,
    Configured,
    Prepared,
    Suspended,
    Published,
    Retiring,
    Completed,
    Aborted
}

public sealed partial class ModuleHost
{
    /// <summary>
    /// One composition change from a baseline view to a candidate composition. Initial startup is
    /// the change from the empty generation-0 view to the initial composition; reconfiguration is
    /// a change between two published generations; final shutdown retires the current generation
    /// through the same retirement scheduler. Each stage has exactly one failure exit: the caller
    /// aborts the transaction, which retires only what this transaction created.
    /// </summary>
    private sealed class ModuleChangeTransaction
    {
        private readonly ModuleHost _host;
        private readonly IReadOnlyDictionary<ModuleId, ModuleEntry> _retained;

        internal ModuleChangeTransaction(ModuleHost host, ModuleHostView baseline, ModuleComposition candidate,
            bool initial, IReadOnlyDictionary<ModuleId, ModuleEntry>? retained = null, ModuleEntry[]? removed = null,
            IModuleChangeParticipant? participant = null)
        {
            _host = host;
            Baseline = baseline;
            Candidate = candidate;
            IsInitial = initial;
            _retained = retained ?? new Dictionary<ModuleId, ModuleEntry>();
            Removed = removed ?? [];
            RemovedIds = Removed.Select(entry => entry.Descriptor.Id).ToHashSet();
            Participant = participant;
        }

        internal ModuleHostView Baseline { get; }
        internal ModuleComposition Candidate { get; }
        internal bool IsInitial { get; }
        internal ModuleChangeStage Stage { get; private set; } = ModuleChangeStage.Validated;
        internal List<ModuleEntry> Created { get; } = [];
        internal ModuleEntry[] Removed { get; }
        internal HashSet<ModuleId> RemovedIds { get; }
        internal IModuleChangeParticipant? Participant { get; }
        internal ModuleChangePreparation? Preparation { get; private set; }
        internal ModuleServiceTransition? Services { get; private set; }
        internal bool Suspended { get; private set; }

        /// <summary>Creates every candidate module not retained from the baseline, in graph order.</summary>
        internal ModuleHostResult CreateEntries(string stage, CancellationToken token)
        {
            var registrations = new Dictionary<ModuleId, ModuleRegistration>();
            foreach (var registration in Candidate.Registrations) registrations.TryAdd(registration.Descriptor.Id, registration);
            foreach (var descriptor in Candidate.Resolved!)
            {
                // Startup reports a shutdown race as a phase result; a candidate rejects it.
                if (!IsInitial) token.ThrowIfCancellationRequested();
                var admission = _host.CheckAdmission(stage);
                if (!admission.IsSuccess) return admission;
                token.ThrowIfCancellationRequested();
                if (!_retained.TryGetValue(descriptor.Id, out var entry))
                {
                    // Transfer ownership before checking cancellation or advancing to another factory.
                    entry = _host.CreateModuleEntry(descriptor, registrations[descriptor.Id], token);
                    Created.Add(entry);
                }
                lock (_host._stateLock) Candidate.AddModule(entry);
                if (IsInitial) token.ThrowIfCancellationRequested();
            }
            token.ThrowIfCancellationRequested();
            Stage = ModuleChangeStage.Created;
            return ModuleHostResult.Success;
        }

        internal ValueTask<ModuleHostResult> ConfigureAsync(string stage, ModuleHostPhase nextPhase,
            Func<IModule, ModuleServiceConfigurationContext, CancellationToken, ValueTask> action, CancellationToken token) =>
            _host.ConfigureEntriesAsync(Candidate, Created, stage, nextPhase, action, token);

        internal async ValueTask ConfigureDomainAsync(CancellationToken token)
        {
            if (Participant is null) return;
            var context = new ModuleChangeContext(Baseline, Candidate);
            await _host.InvokeCallbackAsync(async () =>
            {
                await Participant.ConfigureAsync(context, token).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }

        /// <summary>Freezes neutral and managed registrations of this candidate only.</summary>
        internal void Freeze()
        {
            if (IsInitial)
            {
                try { _host.InvokeCallback(() => { _host._options.Lifetime?.Freeze(); return true; }); }
                catch (Exception exception) { throw new ModuleLifecycleExecutionException(null, "FreezeServices", exception); }
            }
            else
            {
                Candidate.ManagedServices?.Freeze();
            }
            Candidate.Services.Freeze();
            foreach (var entry in Created) entry.Phase = ModuleHostPhase.ServicesFrozen;
            Stage = ModuleChangeStage.Configured;
        }

        internal async ValueTask PrepareDomainAsync(CancellationToken token)
        {
            if (Participant is null) return;
            var context = new ModuleChangeContext(Baseline, Candidate);
            Preparation = await _host.InvokeCallbackAsync(() => Participant.PrepareAsync(context, token)).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The domain participant returned no preparation.");
        }

        internal async ValueTask<ModuleHostResult> InitializeAsync(CancellationToken token)
        {
            var result = await _host.InitializeEntriesAsync(Candidate, Created, token).ConfigureAwait(false);
            if (result.IsSuccess) Stage = ModuleChangeStage.Prepared;
            return result;
        }

        /// <summary>
        /// Computes the complete affected set and closes its admission. Domain getters are user code and
        /// are read once, outside the state lock, under the callback boundary.
        /// </summary>
        internal async ValueTask SuspendAsync(CancellationToken token)
        {
            if (Baseline.ManagedServices is not null)
                Services = Baseline.ManagedServices.Runtime.PrepareTransition(Baseline.ManagedServices, Candidate.ManagedServices!);
            var affected = RemovedIds.Concat(Services?.AffectedModules ?? Enumerable.Empty<ModuleId>()).ToHashSet();
            if (Preparation is not null)
            {
                var domainAffected = _host.InvokeCallback(() => Preparation.AffectedModules.ToArray());
                if (domainAffected.Any(id => !Baseline.Composition.ModulesById.ContainsKey(id)))
                    throw new InvalidOperationException("Domain impact must identify modules in the current composition.");
                affected.UnionWith(domainAffected);
            }
            lock (_host._stateLock)
            {
                if (_host._shutdownRequested) throw new InvalidOperationException("Host shutdown was requested.");
                _host._suspendedModules.UnionWith(affected);
                Suspended = true;
            }
            Stage = ModuleChangeStage.Suspended;
            if (Preparation is not null)
                await _host.InvokeCallbackAsync(async () =>
                {
                    await Preparation.SuspendAsync(token).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
        }

        /// <summary>Seals the published state outside the lock; the caller publishes it under the lock.</summary>
        internal (object? State, ModuleChangePreparation? Publisher) SealState() =>
            Preparation is null
                ? (Baseline.State, Baseline.Publisher)
                : (_host.InvokeCallback(() => Preparation.State), Preparation);

        /// <summary>Creates the next coherent view. Called under the state lock at the single decision point.</summary>
        internal ModuleHostView Publish(object? state, ModuleChangePreparation? publisher)
        {
            foreach (var entry in Removed) entry.Closing = true;
            if (Suspended)
            {
                _host._suspendedModules.Clear();
                Suspended = false;
            }
            Stage = ModuleChangeStage.Published;
            return new ModuleHostView(_host, checked(Baseline.Generation + 1), Candidate, state, publisher);
        }

        /// <summary>Plan that retires the baseline's removed and affected resources after publication.</summary>
        internal ModuleRetirementPlan PreviousRetirementPlan()
        {
            var services = Services;
            var preparation = Preparation;
            return new ModuleRetirementPlan(Removed, Baseline.Services,
                Stop: () =>
                {
                    var diagnostics = new List<ModuleDiagnostic>();
                    try { if (services is not null) _host.InvokeCallback(() => { services.Stop(); return true; }); }
                    catch (Exception exception) { diagnostics.Add(_host.FailureDiagnostic(exception, "StopPreviousServices")); }
                    return diagnostics;
                },
                StopDomain: () =>
                {
                    var diagnostics = new List<ModuleDiagnostic>();
                    try { if (preparation is not null) _host.InvokeCallback(() => { preparation.StopPrevious(); return true; }); }
                    catch (Exception exception) { diagnostics.Add(_host.FailureDiagnostic(exception, "StopPreviousDomain")); }
                    return diagnostics;
                },
                ManagedRetirement: () => services?.Retirement,
                CompleteDomain: () => _host.CompletePreviousDomainAsync(preparation),
                CompleteServices: () => _host.CompletePreviousServicesAsync(services),
                Domain: preparation,
                Resources: new ModuleRetirementResources(Removed, services, null, null, preparation));
        }

        /// <summary>Plan that withdraws everything this unpublished candidate created.</summary>
        internal ModuleRetirementPlan AbortPlan()
        {
            var bindings = Candidate.ManagedServices;
            var preparation = Preparation;
            var created = Created.ToArray();
            return new ModuleRetirementPlan(created, Candidate.Registry,
                Stop: () =>
                {
                    var diagnostics = new List<ModuleDiagnostic>();
                    try { if (bindings is not null) _host.InvokeCallback(() => { bindings.Stop(); return true; }); }
                    catch (Exception exception) { diagnostics.Add(_host.FailureDiagnostic(exception, "StopCandidateServices")); }
                    return diagnostics;
                },
                StopDomain: static () => [],
                ManagedRetirement: () => bindings?.Retirement,
                CompleteDomain: () => _host.AbortDomainAsync(preparation),
                CompleteServices: () => _host.CompleteCandidateServicesAsync(bindings),
                Domain: preparation,
                Resources: new ModuleRetirementResources(created, null, bindings, null, preparation));
        }

        internal void MarkRetiring() => Stage = ModuleChangeStage.Retiring;
        internal void MarkAborted() => Stage = ModuleChangeStage.Aborted;
    }

    /// <summary>
    /// One retirement in the shared scheduler. Service and domain Stop callbacks run first, in that
    /// order; then domain completion and the module reader drain start together, and managed service
    /// completion waits for module callbacks. Every delegate already owns its diagnostics.
    /// </summary>
    private sealed record ModuleRetirementPlan(
        ModuleEntry[] Modules,
        ModuleServiceRegistry Registry,
        Func<List<ModuleDiagnostic>> Stop,
        Func<List<ModuleDiagnostic>> StopDomain,
        Func<Lifetimes.LifetimeRetirement?> ManagedRetirement,
        Func<Task<ModuleHostResult>> CompleteDomain,
        Func<Task<ModuleHostResult>> CompleteServices,
        ModuleChangePreparation? Domain,
        ModuleRetirementResources? Resources);
}
