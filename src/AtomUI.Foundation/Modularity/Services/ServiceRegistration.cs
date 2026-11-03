using AtomUI.Lifetimes;

namespace AtomUI.Modularity.Services;

internal abstract class ServiceRegistration(Type serviceType, ModuleId owner, ModuleServiceLifetime lifetime, Type[] dependencies)
{
    internal Type ServiceType { get; } = serviceType;
    internal ModuleId Owner { get; } = owner;
    internal ModuleServiceLifetime Lifetime { get; } = lifetime;
    internal IReadOnlyList<Type> Dependencies { get; } = Array.AsReadOnly(dependencies);
    internal IReadOnlyDictionary<Type, ServiceRegistration> BoundDependencies { get; private set; } = new Dictionary<Type, ServiceRegistration>();
    internal void BindDependencies(IReadOnlyDictionary<Type, ServiceRegistration> registrations) =>
        BoundDependencies = Dependencies.ToDictionary(type => type, type => registrations[type]);
    internal readonly List<LifetimeOwner> Owners = [];
    internal int Pending;
    internal int Blocks;
    internal abstract LifetimeOwner Create(ModuleServiceScope scope);
}

internal sealed class ServiceRegistration<T>(ModuleId owner, ModuleServiceLifetime lifetime,
    Func<ModuleServiceActivationContext, T> factory, Func<T, ValueTask> release,
    Action<T>? stop, Type[] dependencies) : ServiceRegistration(typeof(T), owner, lifetime, dependencies) where T : class
{
    internal override LifetimeOwner Create(ModuleServiceScope scope)
    {
        var runtime = scope.Runtime;
        lock (runtime.Gate)
        {
            if (Blocks != 0 || scope.State.Blocks != 0) throw new InvalidOperationException("Service admission is suspended.");
            Pending++;
            scope.State.Pending++;
        }
        var reserved = false;
        LifetimeOwner? reservation = null;
        try
        {
            return scope.Owners.Create<T>(Owner, current =>
            {
                lock (runtime.Gate)
                {
                    reservation = current;
                    Owners.Add(current);
                    Pending--;
                    scope.State.Pending--;
                    reserved = true;
                    Monitor.PulseAll(runtime.Gate);
                }
                var context = new ModuleServiceActivationContext(scope, BoundDependencies, new ServiceCapture<T>(current));
                try
                {
                    return factory(context) ?? throw new InvalidOperationException($"Factory for '{typeof(T).FullName}' returned null.");
                }
                finally
                {
                    context.Close();
                }
            }, async value =>
            {
                await release(value).ConfigureAwait(false);
                lock (runtime.Gate) Owners.Remove(reservation!);
                scope.RemoveCached(this);
            }, stop);
        }
        finally
        {
            if (!reserved) lock (runtime.Gate) { Pending--; scope.State.Pending--; Monitor.PulseAll(runtime.Gate); }
        }
    }
}

internal interface IServiceCapture
{
    T Capture<T>(LifetimeOwner<T> dependency) where T : class;
}

internal sealed class ServiceCapture<TService>(LifetimeOwner<TService> owner) : IServiceCapture where TService : class
{
    public T Capture<T>(LifetimeOwner<T> dependency) where T : class => owner.Capture(dependency).Value;
}
