namespace AtomUI.Modularity.Services;

internal static class ServiceRegistrationGraph
{
    internal static void Validate(IReadOnlyDictionary<Type, ServiceRegistration> registrations)
    {
        var errors = new List<ModuleDiagnostic>();
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
        }
        if (errors.Count != 0) throw new ModuleServiceValidationException(errors);

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
