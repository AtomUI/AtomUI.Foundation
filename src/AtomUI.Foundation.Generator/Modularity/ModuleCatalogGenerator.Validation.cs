using AtomUI.Modularity.Generators.Diagnostics;

namespace AtomUI.Modularity.Generators;

public sealed partial class ModuleCatalogGenerator
{
    private static IReadOnlyList<ModuleGeneratorDiagnostic> ValidateModules(IReadOnlyList<ModuleModel> modules)
    {
        var diagnostics = new List<ModuleGeneratorDiagnostic>();
        var duplicateId = modules
            .GroupBy(module => module.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateId is not null)
        {
            foreach (var duplicate in duplicateId.Skip(1))
            {
                diagnostics.Add(new ModuleGeneratorDiagnostic(
                    ModuleGeneratorDiagnostics.DuplicateModuleId,
                    duplicate.Location,
                    duplicate.Id));
            }

            return diagnostics;
        }

        foreach (var module in modules)
        {
            if (!module.IsModule)
            {
                diagnostics.Add(new ModuleGeneratorDiagnostic(
                    ModuleGeneratorDiagnostics.ModuleTypeDoesNotImplementModule,
                    module.Location,
                    module.TypeName));
                continue;
            }

            if (module.UnsupportedShape is not null)
            {
                diagnostics.Add(new ModuleGeneratorDiagnostic(
                    ModuleGeneratorDiagnostics.UnsupportedModuleType,
                    module.TypeLocation,
                    module.TypeName,
                    module.UnsupportedShape));
                continue;
            }

            if (!module.HasAccessibleParameterlessConstructor)
            {
                diagnostics.Add(new ModuleGeneratorDiagnostic(
                    ModuleGeneratorDiagnostics.ModuleFactoryConstructorMissing,
                    module.Location,
                    module.TypeName));
            }
            else if (!module.SatisfiesRequiredMembers)
            {
                diagnostics.Add(new ModuleGeneratorDiagnostic(
                    ModuleGeneratorDiagnostics.ModuleRequiredMembersNotSatisfied,
                    module.TypeLocation,
                    module.TypeName));
            }
        }

        if (HasErrors(diagnostics))
        {
            return diagnostics;
        }

        foreach (var module in modules)
        {
            foreach (var dependency in module.Dependencies)
            {
                if (dependency.HasOpenTypeParameters)
                {
                    diagnostics.Add(new ModuleGeneratorDiagnostic(
                        ModuleGeneratorDiagnostics.UnsupportedDependencyType,
                        dependency.Location ?? module.Location,
                        module.TypeName,
                        dependency.TypeName));
                    continue;
                }

                if (!dependency.IsModule)
                {
                    diagnostics.Add(new ModuleGeneratorDiagnostic(
                        ModuleGeneratorDiagnostics.DependencyTypeIsNotModule,
                        dependency.Location ?? module.Location,
                        module.TypeName,
                        dependency.TypeName));
                    continue;
                }

                if (!dependency.HasModuleAttribute)
                {
                    diagnostics.Add(new ModuleGeneratorDiagnostic(
                        ModuleGeneratorDiagnostics.MissingRequiredDependency,
                        dependency.Location ?? module.Location,
                        module.TypeName,
                        dependency.TypeName));
                }
                else if (dependency.CodeTypeName is null)
                {
                    diagnostics.Add(new ModuleGeneratorDiagnostic(
                        ModuleGeneratorDiagnostics.DependencyReferenceIsAmbiguous,
                        dependency.Location ?? module.Location,
                        module.TypeName,
                        dependency.TypeName,
                        dependency.AssemblyIdentity!));
                }
            }
        }

        if (HasErrors(diagnostics))
        {
            return diagnostics;
        }

        var modulesByKey = CreateModuleMap(modules);
        var states = new Dictionary<string, VisitState>(StringComparer.Ordinal);
        var path = new Stack<ModuleModel>();

        foreach (var module in modules)
        {
            VisitForCycle(module, modulesByKey, states, path, diagnostics);
            if (HasErrors(diagnostics))
            {
                return diagnostics;
            }
        }

        return diagnostics;
    }

    private static IReadOnlyList<ModuleModel> OrderModules(IReadOnlyList<ModuleModel> modules)
    {
        var modulesByKey = CreateModuleMap(modules);
        var ordered = new List<ModuleModel>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var module in modules)
        {
            Visit(module);
        }

        return ordered;

        // Validation has already rejected cycles, so a depth-first visit yields dependency-first order.
        void Visit(ModuleModel module)
        {
            if (!visited.Add(module.Key))
            {
                return;
            }

            foreach (var dependency in module.Dependencies)
            {
                if (dependency.Key is not null && modulesByKey.TryGetValue(dependency.Key, out var dependencyModule))
                {
                    Visit(dependencyModule);
                }
            }

            ordered.Add(module);
        }
    }

    private static void VisitForCycle(
        ModuleModel module,
        IReadOnlyDictionary<string, ModuleModel> modulesByKey,
        IDictionary<string, VisitState> states,
        Stack<ModuleModel> path,
        ICollection<ModuleGeneratorDiagnostic> diagnostics)
    {
        if (states.TryGetValue(module.Key, out var state))
        {
            if (state == VisitState.Visited)
            {
                return;
            }

            diagnostics.Add(new ModuleGeneratorDiagnostic(
                ModuleGeneratorDiagnostics.DependencyCycle,
                module.Location,
                CreateCyclePath(path, module)));
            return;
        }

        states.Add(module.Key, VisitState.Visiting);
        path.Push(module);

        foreach (var dependency in module.Dependencies)
        {
            if (dependency.Key is null || !modulesByKey.TryGetValue(dependency.Key, out var dependencyModule))
            {
                continue;
            }

            VisitForCycle(dependencyModule, modulesByKey, states, path, diagnostics);
            if (HasErrors(diagnostics))
            {
                return;
            }
        }

        path.Pop();
        states[module.Key] = VisitState.Visited;
    }

    private static Dictionary<string, ModuleModel> CreateModuleMap(IEnumerable<ModuleModel> modules)
    {
        var map = new Dictionary<string, ModuleModel>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            map.Add(module.Key, module);
        }

        return map;
    }

    private static string CreateCyclePath(IEnumerable<ModuleModel> path, ModuleModel repeatedModule)
    {
        var orderedPath = path.Reverse().ToArray();
        var cycleStart = Array.FindIndex(orderedPath,
            module => string.Equals(module.Key, repeatedModule.Key, StringComparison.Ordinal));
        return string.Join(" -> ", orderedPath.Skip(Math.Max(0, cycleStart))
            .Append(repeatedModule).Select(module => module.TypeName));
    }
}
