namespace AtomUI.Modularity;

public sealed class ModuleHostOptions
{
    private readonly IReadOnlySet<ModuleId> _enabledModuleIds;

    public ModuleHostOptions(
        IEnumerable<Type>? enabledModuleTypes = null,
        IModuleDiagnostics? diagnostics = null)
        : this(CreateModuleIds(enabledModuleTypes), diagnostics)
    {
    }

    internal ModuleHostOptions(
        IEnumerable<ModuleId> enabledModuleIds,
        IModuleDiagnostics? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(enabledModuleIds);

        _enabledModuleIds = new HashSet<ModuleId>(enabledModuleIds);
        Diagnostics = diagnostics;
    }

    internal IReadOnlySet<ModuleId> EnabledModuleIds => _enabledModuleIds;

    public IModuleDiagnostics? Diagnostics { get; }

    public IModuleHostLifetime? Lifetime { get; init; }

    private static IEnumerable<ModuleId> CreateModuleIds(IEnumerable<Type>? moduleTypes)
    {
        foreach (var moduleType in moduleTypes ?? [])
        {
            ArgumentNullException.ThrowIfNull(moduleType);
            yield return ModuleId.FromModuleType(moduleType);
        }
    }
}
