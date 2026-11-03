using AtomUI.Modularity.Services;

namespace AtomUI.Modularity;

/// <summary>The private preparation state behind one published composition.</summary>
internal sealed class ModuleComposition
{
    internal ModuleComposition(IEnumerable<ModuleRegistration> registrations, ModuleHostOptions options,
        ModuleServiceCollection? services = null)
    {
        Registrations = Array.AsReadOnly(registrations.ToArray());
        Options = options;
        Services = services ?? new ModuleServiceCollection();
        Registry = new ModuleServiceRegistry(Services);
        ModuleView = Modules.AsReadOnly();
    }

    internal IReadOnlyList<ModuleRegistration> Registrations { get; }
    internal ModuleHostOptions Options { get; }
    internal ModuleServiceCollection Services { get; }
    internal ModuleServiceRegistry Registry { get; }
    internal List<ModuleEntry> Modules { get; } = [];
    internal Dictionary<ModuleId, ModuleEntry> ModulesById { get; } = [];
    internal IReadOnlyList<ModuleEntry> ModuleView { get; }
    internal IReadOnlyList<ModuleDescriptor>? Resolved;
    internal ModuleServiceBindings? ManagedServices;

    internal void AddModule(ModuleEntry entry)
    {
        ModulesById.Add(entry.Descriptor.Id, entry);
        Modules.Add(entry);
    }
}
