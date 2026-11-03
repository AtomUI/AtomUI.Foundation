using AtomUI.Modularity.Services;

namespace AtomUI.Modularity;

/// <summary>Exposes the same service authority used by a custom host lifetime adapter.</summary>
public interface IModuleHostReconfigurationLifetime : IModuleHostLifetime
{
    ModuleServiceRuntime ServiceRuntime { get; }
}
