namespace AtomUI.Modularity;

public sealed partial class ModuleHost
{
    private async ValueTask<ModuleHostResult> InitializeCoreAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var admission = CheckAdmission("Initialize");
            if (!admission.IsSuccess) { return admission; }
            var phase = Phase;
            if (phase == ModuleHostPhase.Initialized) { return ModuleHostResult.Success; }
            cancellationToken.ThrowIfCancellationRequested();
            var result = phase switch
            {
                ModuleHostPhase.Created => ResolveModulesCore(),
                ModuleHostPhase.Resolved => await CreateModulesCoreAsync(cancellationToken).ConfigureAwait(false),
                ModuleHostPhase.ModulesCreated => await RunServicePhaseCoreAsync("PreConfigureServices", ModuleHostPhase.ModulesCreated, ModuleHostPhase.PreConfiguredServices, (module, context, token) => module.PreConfigureServicesAsync(context, token), cancellationToken).ConfigureAwait(false),
                ModuleHostPhase.PreConfiguredServices => await RunServicePhaseCoreAsync("ConfigureServices", ModuleHostPhase.PreConfiguredServices, ModuleHostPhase.ConfiguredServices, (module, context, token) => module.ConfigureServicesAsync(context, token), cancellationToken).ConfigureAwait(false),
                ModuleHostPhase.ConfiguredServices => await RunServicePhaseCoreAsync("PostConfigureServices", ModuleHostPhase.ConfiguredServices, ModuleHostPhase.PostConfiguredServices, (module, context, token) => module.PostConfigureServicesAsync(context, token), cancellationToken).ConfigureAwait(false),
                ModuleHostPhase.PostConfiguredServices => FreezeServicesCore(),
                ModuleHostPhase.ServicesFrozen => await InitializeModulesCoreAsync(cancellationToken).ConfigureAwait(false),
                ModuleHostPhase.Initialized => ModuleHostResult.Success,
                _ => InvalidTransition("Initialize", "a non-terminal phase")
            };
            if (!result.IsSuccess || phase == ModuleHostPhase.Initialized) { return result; }
        }
    }

    private ModuleHostResult ResolveModulesCore()
    {
        if (_resolvedModules is not null) { return ModuleHostResult.Success; }
        var result = ResolveGraph();
        if (!result.IsSuccess) { return CreateResult(result.Diagnostics); }
        _resolvedModules = result.Modules;
        return CompletePhase("ResolveModules", ModuleHostPhase.Resolved);
    }

    private ValueTask<ModuleHostResult> CreateModulesCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = ResolveModulesCore();
        if (!resolved.IsSuccess) { return ValueTask.FromResult(resolved); }
        if (Phase == ModuleHostPhase.ModulesCreated) { return ValueTask.FromResult(ModuleHostResult.Success); }
        var phaseResult = RequirePhase(ModuleHostPhase.Resolved, "CreateModules");
        if (!phaseResult.IsSuccess) { return ValueTask.FromResult(phaseResult); }

        foreach (var descriptor in _resolvedModules!)
        {
            var admission = CheckAdmission("CreateModules");
            if (!admission.IsSuccess) { return ValueTask.FromResult(admission); }
            cancellationToken.ThrowIfCancellationRequested();
            var registration = _registrations.First(item => item.Descriptor.Id == descriptor.Id);
            // Transfer ownership before checking cancellation or advancing to another factory.
            var entry = CreateModuleEntry(descriptor, registration, cancellationToken);
            lock (_stateLock) { Current.Composition.AddModule(entry); }
            cancellationToken.ThrowIfCancellationRequested();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CompletePhase("CreateModules", ModuleHostPhase.ModulesCreated));
    }

    private async ValueTask<ModuleHostResult> RunServicePhaseCoreAsync(
        string stage,
        ModuleHostPhase expectedPhase,
        ModuleHostPhase nextPhase,
        Func<IModule, ModuleServiceConfigurationContext, CancellationToken, ValueTask> action,
        CancellationToken cancellationToken)
    {
        var required = RequirePhase(expectedPhase, stage);
        if (!required.IsSuccess) { return required; }
        cancellationToken.ThrowIfCancellationRequested();
        var result = await ConfigureEntriesAsync(Current.Composition, _enabledModules, stage, nextPhase, action, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? CompletePhase(stage, nextPhase) : result;
    }

    private ModuleEntry CreateModuleEntry(ModuleDescriptor descriptor, ModuleRegistration registration, CancellationToken cancellationToken)
    {
        try
        {
            var module = InvokeCallback(registration.Factory)
                ?? throw new InvalidOperationException("The module factory returned null.");
            lock (_stateLock)
            {
                if (_moduleOwnership.TryGetValue(module, out _))
                    throw new InvalidOperationException("The module instance already belongs to this host.");
                var entry = new ModuleEntry(descriptor, module);
                _moduleOwnership.Add(module, entry);
                return entry;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { throw new ModuleLifecycleExecutionException(descriptor.Id, "CreateModule", exception); }
    }

    private async ValueTask<ModuleHostResult> ConfigureEntriesAsync(ModuleComposition composition, IEnumerable<ModuleEntry> entries,
        string stage, ModuleHostPhase nextPhase,
        Func<IModule, ModuleServiceConfigurationContext, CancellationToken, ValueTask> action, CancellationToken cancellationToken)
    {
        foreach (var entry in entries)
        {
            var admission = CheckAdmission(stage);
            if (!admission.IsSuccess) return admission;
            cancellationToken.ThrowIfCancellationRequested();
            var context = new ModuleServiceConfigurationContext(entry.Descriptor,
                composition.Services.ForModule(entry.Descriptor.Id), composition.ManagedServices);
            await InvokeModuleAsync(entry, stage, () => action(entry.Module, context, cancellationToken), cancellationToken).ConfigureAwait(false);
            entry.Phase = nextPhase;
            cancellationToken.ThrowIfCancellationRequested();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ModuleHostResult.Success;
    }

    private ModuleHostResult FreezeServicesCore()
    {
        var required = RequirePhase(ModuleHostPhase.PostConfiguredServices, "FreezeServices");
        if (!required.IsSuccess) { return required; }
        try { InvokeCallback(() => { _options.Lifetime?.Freeze(); return true; }); }
        catch (Exception exception) { throw new ModuleLifecycleExecutionException(null, "FreezeServices", exception); }
        _services.Freeze();
        foreach (var entry in _enabledModules) { entry.Phase = ModuleHostPhase.ServicesFrozen; }
        return CompletePhase("FreezeServices", ModuleHostPhase.ServicesFrozen);
    }

    private async ValueTask<ModuleHostResult> InitializeModulesCoreAsync(CancellationToken cancellationToken)
    {
        var required = RequirePhase(ModuleHostPhase.ServicesFrozen, "Initialize");
        if (!required.IsSuccess) { return required; }
        cancellationToken.ThrowIfCancellationRequested();
        var result = await InitializeEntriesAsync(Current.Composition, _enabledModules, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? CompletePhase("Initialize", ModuleHostPhase.Initialized) : result;
    }

    private async ValueTask<ModuleHostResult> InitializeEntriesAsync(ModuleComposition composition, IEnumerable<ModuleEntry> entries, CancellationToken cancellationToken)
    {
        foreach (var entry in entries)
        {
            var admission = CheckAdmission("Initialize");
            if (!admission.IsSuccess) return admission;
            cancellationToken.ThrowIfCancellationRequested();
            var context = new ModuleInitializationContext(entry.Descriptor, composition.Registry);
            await InvokeModuleAsync(entry, "Initialize", () => entry.Module.InitializeAsync(context, cancellationToken), cancellationToken).ConfigureAwait(false);
            entry.Phase = ModuleHostPhase.Initialized;
            cancellationToken.ThrowIfCancellationRequested();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ModuleHostResult.Success;
    }

    private async ValueTask<ModuleHostResult> RunModulePhaseCoreAsync(
        string stage,
        ModuleHostPhase requiredPhase,
        ModulePhaseExecutionOrder executionOrder,
        Func<ModulePhaseContext, CancellationToken, ValueTask> action,
        CancellationToken cancellationToken)
    {
        var required = RequirePhase(requiredPhase, stage);
        if (!required.IsSuccess) { return required; }
        cancellationToken.ThrowIfCancellationRequested();
        var entries = executionOrder == ModulePhaseExecutionOrder.Forward ? _enabledModules : _enabledModules.AsEnumerable().Reverse();
        foreach (var entry in entries)
        {
            var admission = CheckAdmission(stage);
            if (!admission.IsSuccess) { return admission; }
            cancellationToken.ThrowIfCancellationRequested();
            var context = new ModulePhaseContext(entry.Descriptor, Services);
            await InvokeModuleAsync(entry, stage, () => action(context, cancellationToken), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return CheckAdmission(stage);
    }

    private ModuleHostResult CheckAdmission(string stage)
    {
        bool allowed;
        lock (_stateLock) { allowed = !_shutdownRequested; }
        return allowed ? ModuleHostResult.Success : InvalidTransition(stage, "no shutdown request");
    }

    private ModuleHostResult RequirePhase(ModuleHostPhase expectedPhase, string stage)
    {
        var admission = CheckAdmission(stage);
        if (!admission.IsSuccess) { return admission; }
        return Phase == expectedPhase ? ModuleHostResult.Success : InvalidTransition(stage, expectedPhase.ToString());
    }

    private ModuleHostResult CompletePhase(string stage, ModuleHostPhase phase)
    {
        lock (_stateLock)
        {
            if (!_shutdownRequested)
            {
                _phase = phase;
                if (phase == ModuleHostPhase.Initialized && Current.Generation == 0)
                    Volatile.Write(ref _current, new ModuleHostView(this, 1, Current.Composition));
                return ModuleHostResult.Success;
            }
        }
        return InvalidTransition(stage, "no shutdown request");
    }

}
