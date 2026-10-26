namespace AtomUI.Modularity;

/// <summary>Connects an explicitly selected lifetime runtime to the module host.</summary>
public interface IModuleHostLifetime
{
    void Freeze();

    void Stop();

    ValueTask<ModuleHostResult> CompleteShutdownAsync();
}
