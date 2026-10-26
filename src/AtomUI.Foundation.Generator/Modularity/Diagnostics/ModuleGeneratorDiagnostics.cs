using Microsoft.CodeAnalysis;

namespace AtomUI.Modularity.Generators.Diagnostics;

internal static class ModuleGeneratorDiagnostics
{
    public static readonly DiagnosticDescriptor DuplicateModuleId = new(
        "ATOMUIMODGEN001",
        "Duplicate module id",
        "Module id '{0}' is declared more than once",
        "AtomUI.Modularity",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MissingRequiredDependency = new(
        "ATOMUIMODGEN002",
        "Missing module dependency metadata",
        "Module '{0}' depends on module type '{1}', but the dependency is not declared with ModuleAttribute",
        "AtomUI.Modularity",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DependencyCycle = new(
        "ATOMUIMODGEN003",
        "Module dependency cycle",
        "Module dependency graph contains a cycle: {0}",
        "AtomUI.Modularity",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DependencyTypeIsNotModule = new(
        "ATOMUIMODGEN004",
        "Dependency type is not a module",
        "Module '{0}' depends on type '{1}', but the dependency type does not implement IModule",
        "AtomUI.Modularity",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedDependencyType = new(
        "ATOMUIMODGEN011",
        "Module dependency type cannot be referenced",
        "Module '{0}' depends on '{1}', which is not a closed module type supported by generated catalogs",
        "AtomUI.Modularity",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ModuleTypeDoesNotImplementModule = new(
        "ATOMUIMODGEN005",
        "Module type does not implement IModule",
        "Type '{0}' is declared as a module but does not implement IModule",
        "AtomUI.Modularity",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ModuleFactoryConstructorMissing = new(
        "ATOMUIMODGEN006",
        "Module factory constructor is missing",
        "Module type '{0}' must declare an accessible parameterless constructor for generated AOT factory",
        "AtomUI.Modularity",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedModuleType = new(
        "ATOMUIMODGEN007",
        "Module type cannot be referenced or constructed",
        "Module type '{0}' cannot have a generated factory: {1}",
        "AtomUI.Modularity",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ModuleRequiredMembersNotSatisfied = new(
        "ATOMUIMODGEN008",
        "Module required members are not satisfied",
        "Module type '{0}' has required members; its parameterless constructor must declare SetsRequiredMembers for a generated factory",
        "AtomUI.Modularity",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidCatalogOption = new(
        "ATOMUIMODGEN009",
        "Invalid generated catalog option",
        "Build property '{0}' has invalid value '{1}' and will fall back to '{2}'",
        "AtomUI.Modularity",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor SerializedModuleIdUsage = new(
        "ATOMUIMODGEN010",
        "Serialized modularity id used in handwritten source",
        "Use type-first modularity id APIs instead of handwritten FromSerializedValue('{0}') outside generated or compatibility code",
        "AtomUI.Modularity",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

}
