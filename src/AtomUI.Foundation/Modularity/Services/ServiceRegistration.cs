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
        var releaseState = new ReleaseState(runtime.Gate, scope.State, this, release);
        try
        {
            Func<LifetimeOwner<T>, T> construct = current =>
            {
                lock (runtime.Gate)
                {
                    releaseState.Owner = current;
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
            };
            // A Transient instance belongs to the request that activated it, not to the scope's lifetime.
            return Lifetime == ModuleServiceLifetime.Transient
                ? scope.Owners.CreateRequest(Owner, construct, releaseState.ReleaseAsync, stop)
                : scope.Owners.Create(Owner, construct, releaseState.ReleaseAsync, stop);
        }
        finally
        {
            if (!reserved) lock (runtime.Gate) { Pending--; scope.State.Pending--; Monitor.PulseAll(runtime.Gate); }
        }
    }

    // A separate method-group target prevents the long-lived Release delegate from sharing
    // the factory lambda's closure, which necessarily includes the complete binding wrapper.
    private sealed class ReleaseState(object gate, ModuleServiceScope.ScopeState scope,
        ServiceRegistration registration, Func<T, ValueTask> release)
    {
        private ServiceRegistration? _registration = registration;
        private readonly List<LifetimeOwner> _owners = registration.Owners;
        internal LifetimeOwner? Owner;

        internal async ValueTask ReleaseAsync(T value)
        {
            var released = false;
            try
            {
                await release(value).ConfigureAwait(false);
                released = true;
            }
            finally
            {
                if (released) lock (gate) _owners.Remove(Owner!);
                // Admission is already closed. Cache/registration metadata is not
                // a second retention authority for a failed real owner, and may
                // contain declared factory dependencies that were never captured.
                scope.RemoveCached(_registration!);
                _registration = null;
            }
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
