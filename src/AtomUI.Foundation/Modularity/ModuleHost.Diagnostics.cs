namespace AtomUI.Modularity;

public sealed partial class ModuleHost
{
    private ModuleHostResult InvalidTransition(string stage, string expectedPhase)
    {
        var phase = Phase;
        return CreateResult([new ModuleDiagnostic(
            ModuleDiagnosticCodes.InvalidLifecycleTransition,
            $"Module host cannot run '{stage}' while current phase is '{phase}'. Expected '{expectedPhase}'.",
            stage: stage,
            context: new Dictionary<string, string> { ["currentPhase"] = phase.ToString(), ["expectedPhase"] = expectedPhase })]);
    }

    private ModuleDiagnostic FailureDiagnostic(Exception exception, string stage, ModuleId? moduleId = null)
    {
        if (exception is ModuleLifecycleExecutionException lifecycle)
        {
            moduleId = lifecycle.ModuleId;
            stage = lifecycle.Stage;
            exception = lifecycle.InnerException!;
        }
        var descriptor = _registrations.Select(registration => registration.Descriptor).FirstOrDefault(item => item.Id == moduleId);
        return new ModuleDiagnostic(ModuleDiagnosticCodes.ModuleLifecycleFailed,
            $"{(moduleId is null ? "Module host" : $"Module '{moduleId}'")} failed during '{stage}': {exception.Message}",
            moduleId: moduleId, stage: stage, sourceAssemblyName: descriptor?.SourceAssemblyName, exceptionType: exception.GetType().FullName);
    }

    private static IReadOnlyList<ModuleDiagnostic> FindServiceValidationDiagnostics(Exception exception)
    {
        if (exception is ModuleLifecycleExecutionException { InnerException: AtomUI.Modularity.Services.ModuleServiceValidationException validation })
        {
            return validation.Diagnostics;
        }

        return [];
    }

    private ModuleHostResult CreateResult(IEnumerable<ModuleDiagnostic> diagnostics)
    {
        var result = diagnostics.ToList();
        if (_options.Diagnostics is not null && _callbackFrame.Value?.IsActive != true)
        {
            foreach (var diagnostic in result.ToArray())
            {
                try { InvokeCallback(() => { _options.Diagnostics.Report(diagnostic); return true; }); }
                catch (Exception exception)
                {
                    result.Add(new ModuleDiagnostic(ModuleDiagnosticCodes.DiagnosticsObserverFailed,
                        $"Module diagnostics observer failed: {exception.Message}", severity: ModuleDiagnosticSeverity.Warning,
                        stage: "ReportDiagnostic", exceptionType: exception.GetType().FullName));
                }
            }
        }
        return result.Count == 0 ? ModuleHostResult.Success : new ModuleHostResult(result);
    }

    private ModuleResolutionResult ResolveGraph()
    {
        var diagnostics = ValidateRegistrations();
        return diagnostics.Count != 0 ? new ModuleResolutionResult(diagnostics: diagnostics) : ModuleDependencyGraph.TryResolve(SelectEnabledDescriptors());
    }

    private IReadOnlyList<ModuleDescriptor> SelectEnabledDescriptors()
    {
        var selected = new Dictionary<ModuleId, ModuleDescriptor>();
        var registrationsById = BuildRegistrationMap();
        foreach (var registration in _registrations)
        {
            if (_options.EnabledModuleIds.Contains(registration.Descriptor.Id)) { AddWithRequiredDependencies(registration.Descriptor); }
        }
        return Array.AsReadOnly(selected.Values.ToArray());

        void AddWithRequiredDependencies(ModuleDescriptor descriptor)
        {
            if (!selected.TryAdd(descriptor.Id, descriptor)) { return; }
            foreach (var dependency in descriptor.Dependencies)
            {
                if (registrationsById.TryGetValue(dependency.ModuleId, out var registration)) { AddWithRequiredDependencies(registration.Descriptor); }
            }
        }
    }

    private IReadOnlyList<ModuleDiagnostic> ValidateRegistrations()
    {
        var duplicate = _registrations.Select(registration => registration.Descriptor.Id).GroupBy(moduleId => moduleId).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            return [new ModuleDiagnostic(ModuleDiagnosticCodes.DuplicateModuleId,
                $"Module id '{duplicate.Key}' is registered more than once.", duplicate.Key, dependencyPath: [duplicate.Key, duplicate.Key])];
        }
        var registered = BuildRegistrationMap();
        foreach (var enabled in _options.EnabledModuleIds)
        {
            if (!registered.ContainsKey(enabled))
            {
                return [new ModuleDiagnostic(ModuleDiagnosticCodes.RequiredDependencyMissing,
                    $"Enabled module '{enabled}' is not registered.", enabled, dependencyPath: [enabled])];
            }
        }
        return [];
    }

    private IReadOnlyDictionary<ModuleId, ModuleRegistration> BuildRegistrationMap()
    {
        var result = new Dictionary<ModuleId, ModuleRegistration>();
        foreach (var registration in _registrations) { result.TryAdd(registration.Descriptor.Id, registration); }
        return result;
    }

    private sealed class ModuleLifecycleExecutionException(ModuleId? moduleId, string stage, Exception innerException)
        : Exception(innerException.Message, innerException)
    {
        public ModuleId? ModuleId { get; } = moduleId;
        public string Stage { get; } = stage;
    }
}
