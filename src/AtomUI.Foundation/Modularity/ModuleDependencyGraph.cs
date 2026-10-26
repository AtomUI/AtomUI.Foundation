namespace AtomUI.Modularity;

public sealed class ModuleDependencyGraph
{
    private ModuleDependencyGraph(IReadOnlyList<ModuleDescriptor> modules)
    {
        Modules = Array.AsReadOnly(modules.ToArray());
    }

    public IReadOnlyList<ModuleDescriptor> Modules { get; }

    public static ModuleDependencyGraph Resolve(IEnumerable<ModuleDescriptor> modules)
    {
        var result = TryResolve(modules);
        result.ThrowIfFailed();
        return new ModuleDependencyGraph(result.Modules);
    }

    public static ModuleDependencyGraph Resolve(ModuleCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return Resolve(catalog.Modules);
    }

    public static ModuleResolutionResult TryResolve(ModuleCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return TryResolve(catalog.Modules);
    }

    public static ModuleResolutionResult TryResolve(IEnumerable<ModuleDescriptor> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var moduleArray = modules.ToArray();
        var duplicate = moduleArray.GroupBy(module => module.Id).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            return new ModuleResolutionResult(diagnostics: [new ModuleDiagnostic(
                ModuleDiagnosticCodes.DuplicateModuleId,
                $"Module id '{duplicate.Key}' is declared more than once.", duplicate.Key,
                dependencyPath: [duplicate.Key, duplicate.Key])]);
        }

        var moduleMap = moduleArray.ToDictionary(module => module.Id);
        var dependencies = moduleArray.SelectMany(module => module.Dependencies).Select(dependency => dependency.ModuleId).ToHashSet();
        var states = new Dictionary<ModuleId, VisitState>();
        var ordered = new List<ModuleDescriptor>();
        var diagnostics = new List<ModuleDiagnostic>();
        var path = new List<ModuleId>();

        // Start with consumer roots even when registrations arrived in dependency-first order,
        // so missing-dependency diagnostics retain the complete originating path.
        foreach (var module in moduleArray.Where(module => !dependencies.Contains(module.Id)).Concat(moduleArray))
        {
            Visit(module);
        }
        return diagnostics.Count == 0 ? new ModuleResolutionResult(ordered) : new ModuleResolutionResult(diagnostics: diagnostics);

        void Visit(ModuleDescriptor module)
        {
            if (states.TryGetValue(module.Id, out var state))
            {
                if (state == VisitState.Visiting)
                {
                    var cyclePath = path.Append(module.Id).ToArray();
                    diagnostics.Add(new ModuleDiagnostic(ModuleDiagnosticCodes.DependencyCycle,
                        $"Module dependency graph contains a cycle: {string.Join(" -> ", cyclePath)}.",
                        module.Id, dependencyPath: cyclePath));
                }
                return;
            }

            states.Add(module.Id, VisitState.Visiting);
            path.Add(module.Id);
            foreach (var dependency in module.Dependencies)
            {
                if (moduleMap.TryGetValue(dependency.ModuleId, out var dependencyModule))
                {
                    Visit(dependencyModule);
                }
                else
                {
                    diagnostics.Add(new ModuleDiagnostic(ModuleDiagnosticCodes.RequiredDependencyMissing,
                        $"Module '{module.Id}' depends on missing module '{dependency.ModuleId}'.",
                        module.Id, dependency.ModuleId, dependencyPath: path.Append(dependency.ModuleId).ToArray()));
                }
            }
            path.RemoveAt(path.Count - 1);
            states[module.Id] = VisitState.Visited;
            ordered.Add(module);
        }
    }

    private enum VisitState
    {
        Visiting,
        Visited
    }
}
