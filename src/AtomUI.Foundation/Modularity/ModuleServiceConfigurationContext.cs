using AtomUI.Modularity.Services;

namespace AtomUI.Modularity;

public sealed class ModuleServiceConfigurationContext
{
    internal ModuleServiceConfigurationContext(ModuleDescriptor descriptor, ModuleServiceCollection services,
        ModuleServiceBindings? managedServices = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(services);

        Descriptor = descriptor;
        Services = services;
        ManagedServices = managedServices;
    }

    public ModuleDescriptor Descriptor { get; }

    public ModuleServiceCollection Services { get; }
    public ModuleServiceBindings? ManagedServices { get; }
}
