using AtomUI.Lifetimes;

namespace AtomUI.Modularity.Services;

internal abstract class ServiceRegistration(Type serviceType, ModuleId owner, ModuleServiceLifetime lifetime, Type[] dependencies)
{
    internal Type ServiceType { get; } = serviceType;
    internal ModuleId Owner { get; } = owner;
    internal ModuleServiceLifetime Lifetime { get; } = lifetime;
    internal IReadOnlyList<Type> Dependencies { get; } = Array.AsReadOnly(dependencies);
    internal abstract LifetimeOwner Create(ModuleServiceScope scope);
}

internal sealed class ServiceRegistration<T>(ModuleId owner, ModuleServiceLifetime lifetime,
    Func<ModuleServiceActivationContext, T> factory, Func<T, ValueTask> release,
    Action<T>? stop, Type[] dependencies) : ServiceRegistration(typeof(T), owner, lifetime, dependencies) where T : class
{
    internal override LifetimeOwner Create(ModuleServiceScope scope) => scope.Owners.Create<T>(Owner, current =>
    {
        var context = new ModuleServiceActivationContext(scope, Dependencies, new ServiceCapture<T>(current));
        try
        {
            return factory(context) ?? throw new InvalidOperationException($"Factory for '{typeof(T).FullName}' returned null.");
        }
        finally
        {
            context.Close();
        }
    }, release, stop);
}

internal interface IServiceCapture
{
    T Capture<T>(LifetimeOwner<T> dependency) where T : class;
}

internal sealed class ServiceCapture<TService>(LifetimeOwner<TService> owner) : IServiceCapture where TService : class
{
    public T Capture<T>(LifetimeOwner<T> dependency) where T : class => owner.Capture(dependency).Value;
}
