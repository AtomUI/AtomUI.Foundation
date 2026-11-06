namespace AtomUI.Modularity.Services;

internal static class ServiceRegistrationGraph
{
    /// <summary>Throws for invalid graphs; returns informational notes keyed by the Singleton they concern.</summary>
    internal static IReadOnlyList<(ServiceRegistration Singleton, ModuleDiagnostic Note)> Validate(IReadOnlyDictionary<Type, ServiceRegistration> registrations)
    {
        var errors = new List<ModuleDiagnostic>();
        var notes = new List<(ServiceRegistration, ModuleDiagnostic)>();
        foreach (var registration in registrations.Values)
        foreach (var dependency in registration.Dependencies)
            if (!registrations.ContainsKey(dependency))
                errors.Add(Error(ModuleServiceDiagnosticCodes.RequiredDependencyMissing, $"Service '{registration.ServiceType.FullName}' requires unregistered '{dependency.FullName}'.", registration));
        if (errors.Count != 0) throw new ModuleServiceValidationException(errors);

        var visited = new HashSet<Type>();
        var path = new List<Type>();
        foreach (var registration in registrations.Values)
            Visit(registration, false);
        if (errors.Count != 0) throw new ModuleServiceValidationException(errors);

        foreach (var singleton in registrations.Values.Where(registration => registration.Lifetime == ModuleServiceLifetime.Singleton))
        {
            visited.Clear();
            Visit(singleton, true);
            // A captured Transient lives as long as the Singleton that captures it. That is legal, but
            // it is no longer released per request, so the promotion is reported.
            foreach (var type in visited)
                if (registrations[type].Lifetime == ModuleServiceLifetime.Transient)
                    notes.Add((singleton, new ModuleDiagnostic(ModuleServiceDiagnosticCodes.SingletonCapturesTransient,
                        $"Singleton service '{singleton.ServiceType.FullName}' captures transient '{type.FullName}', which then lives as long as the singleton.",
                        singleton.Owner, severity: ModuleDiagnosticSeverity.Info, stage: "FreezeManagedServices",
                        context: new Dictionary<string, string>
                        {
                            ["serviceType"] = singleton.ServiceType.FullName ?? singleton.ServiceType.Name,
                            ["transientType"] = type.FullName ?? type.Name
                        })));
        }
        if (errors.Count != 0) throw new ModuleServiceValidationException(errors);
        return notes;

        void Visit(ServiceRegistration current, bool singletonGraph)
        {
            if (path.Contains(current.ServiceType))
            {
                errors.Add(Error(ModuleServiceDiagnosticCodes.DependencyCycle, $"Service dependency cycle: {string.Join(" -> ", path.Append(current.ServiceType).Select(type => type.FullName))}.", current));
                return;
            }
            if (!visited.Add(current.ServiceType)) return;
            if (singletonGraph && current.Lifetime == ModuleServiceLifetime.Scoped)
                errors.Add(Error(ModuleServiceDiagnosticCodes.SingletonCapturesScoped, $"Singleton service graph captures scoped '{current.ServiceType.FullName}'.", current));
            path.Add(current.ServiceType);
            foreach (var dependency in current.Dependencies) Visit(registrations[dependency], singletonGraph);
            path.RemoveAt(path.Count - 1);
        }
    }

    private static ModuleDiagnostic Error(string code, string message, ServiceRegistration registration) =>
        new(code, message, registration.Owner, stage: "FreezeManagedServices",
            context: new Dictionary<string, string> { ["serviceType"] = registration.ServiceType.FullName ?? registration.ServiceType.Name });
}
