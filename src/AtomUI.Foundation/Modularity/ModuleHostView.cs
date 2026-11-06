using AtomUI.Modularity.Services;

namespace AtomUI.Modularity;

/// <summary>A coherent composition identity. Retained resource access requires a host lease.</summary>
public sealed class ModuleHostView
{
    internal ModuleHostView(ModuleHost host, long generation, ModuleComposition composition, object? state = null,
        ModuleChangePreparation? publisher = null)
    {
        Host = host;
        Generation = generation;
        Composition = composition;
        State = state;
        Publisher = publisher;
    }

    internal ModuleHost Host { get; }
    internal ModuleComposition Composition { get; }
    /// <summary>The preparation that published <see cref="State"/>; it retires that state at final shutdown.</summary>
    internal ModuleChangePreparation? Publisher { get; }
    public long Generation { get; }
    public IReadOnlyList<ModuleRegistration> Registrations => Composition.Registrations;
    public IReadOnlyList<ModuleEntry> Modules => Composition.ModuleView;
    public ModuleServiceRegistry Services => Composition.Registry;
    public ModuleServiceBindings? ManagedServices => Composition.ManagedServices;
    public object? State { get; }
}

/// <summary>Pins the modules actually used by a reader of a captured composition.</summary>
public sealed class ModuleHostLease : IDisposable
{
    private ModuleHost? _host;
    private readonly ModuleEntry[] _modules;
    internal ModuleHostLease(ModuleHost host, ModuleHostView view, ModuleEntry[] modules)
    {
        _host = host;
        View = view;
        _modules = modules;
    }

    public ModuleHostView View { get; }
    public void Dispose() => Interlocked.Exchange(ref _host, null)?.ReturnView(_modules);
}
