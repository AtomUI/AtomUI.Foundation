using System.Collections.ObjectModel;

namespace AtomUI.Modularity;

public sealed class ModuleServiceCollection
{
    private readonly Store _store;
    private readonly ModuleId? _owner;

    public ModuleServiceCollection() : this(new Store(), null) { }
    private ModuleServiceCollection(Store store, ModuleId? owner) { _store = store; _owner = owner; }

    public bool IsFrozen => _store.IsFrozen;
    public IReadOnlyList<ModuleServiceDescriptor> Descriptors => _store.View;

    public void AddSingleton<TService>(TService service) where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(service);
        Add(new ModuleServiceDescriptor(typeof(TService), ModuleServiceLifetime.Singleton, instance: service));
    }

    public void AddSingleton<TService, TImplementation>() where TService : notnull where TImplementation : TService =>
        AddType<TService, TImplementation>(ModuleServiceLifetime.Singleton);
    public void AddScoped<TService, TImplementation>() where TService : notnull where TImplementation : TService =>
        AddType<TService, TImplementation>(ModuleServiceLifetime.Scoped);
    public void AddTransient<TService, TImplementation>() where TService : notnull where TImplementation : TService =>
        AddType<TService, TImplementation>(ModuleServiceLifetime.Transient);
    public void ReplaceSingleton<TService>(TService service) where TService : notnull => AddSingleton(service);

    public bool Remove<TService>() where TService : notnull
    {
        ThrowIfFrozen();
        var existed = _store.Descriptors.Any(item => item.ServiceType == typeof(TService));
        _store.Apply(new Operation(_owner, typeof(TService), null));
        return existed;
    }

    public void Clear() { ThrowIfFrozen(); _store.Apply(new Operation(_owner, null, null)); }

    public TService GetRequiredService<TService>() where TService : notnull =>
        _store.Instances.TryGetValue(typeof(TService), out var service)
            ? (TService)service
            : throw new InvalidOperationException($"Service '{typeof(TService).FullName}' is not registered.");

    public void Freeze() => _store.IsFrozen = true;

    internal ModuleServiceCollection ForModule(ModuleId owner) => new(_store, owner);

    internal ModuleServiceCollection Fork(IReadOnlySet<ModuleId> removed)
    {
        var result = new ModuleServiceCollection();
        foreach (var operation in _store.Operations)
            if (operation.Owner is null || !removed.Contains(operation.Owner.Value)) result._store.Apply(operation);
        return result;
    }

    private void Add(ModuleServiceDescriptor descriptor)
    {
        ThrowIfFrozen();
        _store.Apply(new Operation(_owner, descriptor.ServiceType, descriptor));
    }

    private void AddType<TService, TImplementation>(ModuleServiceLifetime lifetime)
        where TService : notnull where TImplementation : TService =>
        Add(new ModuleServiceDescriptor(typeof(TService), lifetime, typeof(TImplementation)));

    private void ThrowIfFrozen()
    {
        if (IsFrozen) throw new InvalidOperationException("Module service collection is frozen.");
    }

    private sealed record Operation(ModuleId? Owner, Type? ServiceType, ModuleServiceDescriptor? Descriptor);

    // One provenance ledger supports both initial composition and filtered candidate snapshots.
    // Hidden registrations remain available when a later module's overriding contribution retires.
    private sealed class Store
    {
        internal readonly List<Operation> Operations = [];
        internal readonly List<ModuleServiceDescriptor> Descriptors = [];
        internal readonly Dictionary<Type, object> Instances = [];
        internal readonly ReadOnlyCollection<ModuleServiceDescriptor> View;
        internal bool IsFrozen;
        internal Store() => View = Descriptors.AsReadOnly();

        internal void Apply(Operation operation)
        {
            // Only the latest operation for one contributor/key can affect any
            // future filtered composition. Keep other contributors' shadowed
            // bindings so removing an override restores the surviving value.
            Operations.RemoveAll(previous => previous.Owner == operation.Owner &&
                (operation.ServiceType is null || previous.ServiceType == operation.ServiceType));
            Operations.Add(operation);
            if (operation.ServiceType is null) { Descriptors.Clear(); Instances.Clear(); return; }
            Descriptors.RemoveAll(item => item.ServiceType == operation.ServiceType);
            Instances.Remove(operation.ServiceType);
            if (operation.Descriptor is not { } descriptor) return;
            Descriptors.Add(descriptor);
            if (descriptor.Instance is { } instance) Instances.Add(operation.ServiceType, instance);
        }
    }
}
