using AtomUI.Modularity.Services;

namespace AtomUI.Modularity;

public sealed partial class ModuleHost
{
    private ModuleChangeRetirement StartTrackedCleanup(Func<ModuleChangeRetirement, Task<ModuleHostResult>> cleanup, object resources)
    {
        var completion = NewCompletion();
        var retirement = new ModuleChangeRetirement(completion.Task, resources);
        lock (_stateLock) _retirements.Add(retirement);
        _ = CompleteTrackedCleanupAsync(cleanup, completion, retirement);
        return retirement;
    }

    private async Task CompleteTrackedCleanupAsync(Func<ModuleChangeRetirement, Task<ModuleHostResult>> cleanup, TaskCompletionSource<ModuleHostResult> completion,
        ModuleChangeRetirement retirement)
    {
        ModuleHostResult result;
        try { result = await cleanup(retirement).ConfigureAwait(false); }
        catch (Exception exception) { result = CreateResult([FailureDiagnostic(exception, "Retirement")]); }
        if (result.IsSuccess)
        {
            retirement.ForgetReleasedResources();
            lock (_stateLock) _retirements.Remove(retirement);
        }
        completion.TrySetResult(result);
    }

    private async Task<ModuleHostResult> RetirePreviousAsync(ModuleEntry[] removed, ModuleServiceRegistry registry,
        ModuleServiceTransition? services, ModuleChangePreparation? preparation, ModuleChangeRetirement receipt)
    {
        var diagnostics = new List<ModuleDiagnostic>();
        try { if (services is not null) InvokeCallback(() => { services.Stop(); return true; }); }
        catch (Exception exception) { diagnostics.Add(FailureDiagnostic(exception, "StopPreviousServices")); }
        receipt.ManagedRetirement = services?.Retirement;
        try { if (preparation is not null) InvokeCallback(() => { preparation.StopPrevious(); return true; }); }
        catch (Exception exception) { diagnostics.Add(FailureDiagnostic(exception, "StopPreviousDomain")); }
        var result = await DrainRetirementAsync(removed, registry,
            () => CompletePreviousDomainAsync(preparation),
            () => CompletePreviousServicesAsync(services)).ConfigureAwait(false);
        diagnostics.AddRange(result.Diagnostics);
        return CreateResult(diagnostics);
    }

    private async Task<ModuleHostResult> AbortCandidateAsync(List<ModuleEntry> created, ModuleComposition candidate,
        ModuleChangePreparation? preparation, ModuleChangeRetirement receipt)
    {
        var diagnostics = new List<ModuleDiagnostic>();
        try { if (candidate.ManagedServices is not null) InvokeCallback(() => { candidate.ManagedServices.Stop(); return true; }); }
        catch (Exception exception) { diagnostics.Add(FailureDiagnostic(exception, "StopCandidateServices")); }
        receipt.ManagedRetirement = candidate.ManagedServices?.Retirement;
        var result = await DrainRetirementAsync(created, candidate.Registry,
            () => AbortDomainAsync(preparation),
            () => CompleteCandidateServicesAsync(candidate.ManagedServices)).ConfigureAwait(false);
        diagnostics.AddRange(result.Diagnostics);
        return CreateResult(diagnostics);
    }

    private async Task<ModuleHostResult> DrainRetirementAsync(IEnumerable<ModuleEntry> entries, ModuleServiceRegistry registry,
        Func<Task<ModuleHostResult>> completeDomain, Func<Task<ModuleHostResult>> completeServices)
    {
        // Both Stop paths have finished before this scheduler is entered. Domain
        // cleanup may return a module lease, or wait for a module/service ticket;
        // it must be started before awaiting any module's final reader drain.
        var domainCompletion = completeDomain();
        var moduleCompletion = CleanupEntriesAsync(entries, registry);
        var serviceCompletion = CompleteServicesAfterModulesAsync(moduleCompletion, completeServices);
        var results = await Task.WhenAll(domainCompletion, moduleCompletion, serviceCompletion).ConfigureAwait(false);
        return new ModuleHostResult(results.SelectMany(result => result.Diagnostics));
    }

    private static async Task<ModuleHostResult> CompleteServicesAfterModulesAsync(Task<ModuleHostResult> moduleCompletion,
        Func<Task<ModuleHostResult>> completeServices)
    {
        await moduleCompletion.ConfigureAwait(false);
        return await completeServices().ConfigureAwait(false);
    }

    private async Task<ModuleHostResult> CompleteCandidateServicesAsync(ModuleServiceBindings? services)
    {
        if (services is null) return ModuleHostResult.Success;
        try
        {
            var result = await services.CompleteRetirement().FinalCompletion.ConfigureAwait(false);
            return CreateResult(result.Failures.Select(failure => FailureDiagnostic(failure.Exception, failure.Stage, failure.ModuleId)));
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
            return CreateResult(result.Failures.Select(failure => FailureDiagnostic(failure.Exception, failure.Stage, failure.ModuleId)));
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
