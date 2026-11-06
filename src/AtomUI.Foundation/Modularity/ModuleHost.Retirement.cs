using AtomUI.Modularity.Services;

namespace AtomUI.Modularity;

public sealed partial class ModuleHost
{
    private ModuleChangeRetirement StartTrackedCleanup(ModuleRetirementPlan plan)
    {
        var completion = NewCompletion();
        var retirement = new ModuleChangeRetirement(completion.Task, plan.Resources);
        lock (_stateLock) _retirements.Add(retirement);
        _ = CompleteTrackedCleanupAsync(plan, completion, retirement);
        return retirement;
    }

    private async Task CompleteTrackedCleanupAsync(ModuleRetirementPlan plan, TaskCompletionSource<ModuleHostResult> completion,
        ModuleChangeRetirement retirement)
    {
        ModuleHostResult result;
        try { result = CreateResult((await RunRetirementAsync(plan, retirement).ConfigureAwait(false)).Diagnostics); }
        catch (Exception exception) { result = CreateResult([FailureDiagnostic(exception, "Retirement")]); }
        if (result.IsSuccess)
        {
            retirement.ForgetReleasedResources();
            lock (_stateLock) _retirements.Remove(retirement);
        }
        completion.TrySetResult(result);
    }

    /// <summary>
    /// The single retirement scheduler shared by committed changes, withdrawn candidates and final
    /// shutdown. Both Stop paths finish before the drain starts; domain cleanup may return a module
    /// lease or wait for a module/service ticket, so it starts before awaiting any module's reader drain.
    /// </summary>
    private async Task<ModuleHostResult> RunRetirementAsync(ModuleRetirementPlan plan, ModuleChangeRetirement? receipt)
    {
        var diagnostics = plan.Stop();
        if (receipt is not null) receipt.ManagedRetirement = plan.ManagedRetirement();
        diagnostics.AddRange(plan.StopDomain());
        var domainCompletion = plan.CompleteDomain();
        var moduleCompletion = CleanupEntriesAsync(plan.Modules, plan.Registry);
        var serviceCompletion = CompleteServicesAfterModulesAsync(moduleCompletion, plan.CompleteServices);
        var results = await Task.WhenAll(domainCompletion, moduleCompletion, serviceCompletion).ConfigureAwait(false);
        // Keep actual failed modules and their Required dependencies, rather than
        // a whole historical composition and all of its registration factories.
        receipt?.RetainResources(new ModuleRetirementResources(FailedModuleClosure(plan.Modules), null, null,
            results[2].IsSuccess ? null : receipt.ManagedRetirement, results[0].IsSuccess ? null : plan.Domain));
        diagnostics.AddRange(results.SelectMany(result => result.Diagnostics));
        return new ModuleHostResult(diagnostics);
    }

    private static IReadOnlyList<ModuleEntry> FailedModuleClosure(ModuleEntry[] modules)
    {
        var indexed = modules.ToDictionary(entry => entry.Descriptor.Id);
        var retained = new HashSet<ModuleEntry>();
        var pending = new Stack<ModuleEntry>(modules.Where(entry =>
            entry.ShutdownOperation is not { IsCompletedSuccessfully: true } || !entry.ShutdownOperation.Result.IsSuccess));
        while (pending.TryPop(out var entry))
        {
            if (!retained.Add(entry)) continue;
            foreach (var dependency in entry.Descriptor.Dependencies)
                if (indexed.TryGetValue(dependency.ModuleId, out var prerequisite)) pending.Push(prerequisite);
        }
        return retained.ToArray();
    }

    private static async Task<ModuleHostResult> CompleteServicesAfterModulesAsync(Task<ModuleHostResult> moduleCompletion,
        Func<Task<ModuleHostResult>> completeServices)
    {
        await moduleCompletion.ConfigureAwait(false);
        return await completeServices().ConfigureAwait(false);
    }

    /// <summary>Plan that retires the current generation, including its published domain state, at final shutdown.</summary>
    private ModuleRetirementPlan CurrentRetirementPlan()
    {
        var current = Current;
        var publisher = current.Publisher;
        return new ModuleRetirementPlan(current.Composition.Modules.ToArray(), current.Services,
            Stop: () =>
            {
                var diagnostics = new List<ModuleDiagnostic>();
                try { InvokeCallback(() => { _options.Lifetime?.Stop(); return true; }); }
                catch (Exception exception) { diagnostics.Add(FailureDiagnostic(exception, "Stop")); }

                RecoveryFailure? recovery;
                lock (_stateLock) { recovery = _recoveryFailure; _recoveryFailure = null; }
                if (recovery is not null)
                {
                    diagnostics.AddRange(recovery.Diagnostics);
                    // Global Stop has closed runtime admission. It is now safe to return
                    // the recoverable suspension without admitting new service work.
                    recovery.Services?.Dispose();
                }
                return diagnostics;
            },
            StopDomain: () =>
            {
                var diagnostics = new List<ModuleDiagnostic>();
                try { if (publisher is not null) InvokeCallback(() => { publisher.StopCurrent(); return true; }); }
                catch (Exception exception) { diagnostics.Add(FailureDiagnostic(exception, "StopCurrentDomain")); }
                return diagnostics;
            },
            ManagedRetirement: static () => null,
            CompleteDomain: () => CompleteCurrentDomainAsync(publisher),
            CompleteServices: CompleteLifetimeShutdownAsync,
            Domain: publisher,
            Resources: null);
    }

    private async Task<ModuleHostResult> CompleteLifetimeShutdownAsync()
    {
        if (_options.Lifetime is null) return ModuleHostResult.Success;
        try
        {
            var result = await InvokeCallbackAsync(() => _options.Lifetime.CompleteShutdownAsync()).ConfigureAwait(false);
            return new ModuleHostResult(result.Diagnostics);
        }
        catch (Exception exception) { return new ModuleHostResult([FailureDiagnostic(exception, "CompleteShutdown")]); }
    }

    private async Task<ModuleHostResult> CompleteCurrentDomainAsync(ModuleChangePreparation? publisher)
    {
        if (publisher is null) return ModuleHostResult.Success;
        try { return await InvokeCallbackAsync(() => publisher.CompleteCurrentAsync()).ConfigureAwait(false); }
        catch (Exception exception) { return new ModuleHostResult([FailureDiagnostic(exception, "RetireCurrentDomain")]); }
    }

    private async Task<ModuleHostResult> CompleteCandidateServicesAsync(ModuleServiceBindings? services)
    {
        if (services is null) return ModuleHostResult.Success;
        try
        {
            var result = await services.CompleteRetirement().FinalCompletion.ConfigureAwait(false);
            return CreateResult(ModuleServiceRuntime.ToHostResult(result).Diagnostics);
        }
        catch (Exception exception) { return CreateResult([FailureDiagnostic(exception, "AbortServices")]); }
    }

    private async Task<ModuleHostResult> AbortDomainAsync(ModuleChangePreparation? preparation)
    {
        if (preparation is null) return ModuleHostResult.Success;
        try
        {
            await InvokeCallbackAsync(async () => { await preparation.AbortAsync().ConfigureAwait(false); return true; }).ConfigureAwait(false);
            return ModuleHostResult.Success;
        }
        catch (Exception exception) { return CreateResult([FailureDiagnostic(exception, "AbortDomain")]); }
    }

    private async Task<ModuleHostResult> CompletePreviousServicesAsync(ModuleServiceTransition? services)
    {
        if (services is null) return ModuleHostResult.Success;
        try
        {
            var result = await services.CompleteRetirement().FinalCompletion.ConfigureAwait(false);
            return CreateResult(ModuleServiceRuntime.ToHostResult(result).Diagnostics);
        }
        catch (Exception exception) { return CreateResult([FailureDiagnostic(exception, "RetireServices")]); }
    }

    private async Task<ModuleHostResult> CompletePreviousDomainAsync(ModuleChangePreparation? preparation)
    {
        if (preparation is null) return ModuleHostResult.Success;
        try { return await InvokeCallbackAsync(() => preparation.CompletePreviousAsync()).ConfigureAwait(false); }
        catch (Exception exception) { return CreateResult([FailureDiagnostic(exception, "RetireDomain")]); }
    }
}
