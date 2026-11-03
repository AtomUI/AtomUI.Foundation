namespace AtomUI.Modularity;

public sealed class ModuleEntry
{
    internal ModuleEntry(ModuleDescriptor descriptor, IModule module)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(module);

        Descriptor = descriptor;
        Module = module;
    }

    public ModuleDescriptor Descriptor { get; }

    public IModule Module { get; }

    public ModuleHostPhase Phase { get; internal set; } = ModuleHostPhase.ModulesCreated;

    internal int Readers;
    internal bool Closing;
    internal TaskCompletionSource? ReadersExited;
    internal Task<ModuleHostResult>? ShutdownOperation;
}
