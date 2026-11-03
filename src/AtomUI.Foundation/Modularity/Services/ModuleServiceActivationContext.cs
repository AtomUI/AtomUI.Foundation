namespace AtomUI.Modularity.Services;

/// <summary>Resolves declared, owner-pinned dependencies during a synchronous service factory.</summary>
public sealed class ModuleServiceActivationContext
{
    private readonly ModuleServiceScope _scope;
    private readonly IReadOnlyDictionary<Type, ServiceRegistration> _dependencies;
    private readonly IServiceCapture _capture;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private int _closed;

    internal ModuleServiceActivationContext(ModuleServiceScope scope, IReadOnlyDictionary<Type, ServiceRegistration> dependencies, IServiceCapture capture)
    {
        _scope = scope;
        _dependencies = dependencies;
        _capture = capture;
    }

    public T GetRequiredService<T>() where T : class
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("Service factory contexts are confined to their synchronous factory thread.");
        if (!_dependencies.TryGetValue(typeof(T), out var registration))
            throw new InvalidOperationException($"Factory dependency '{typeof(T).FullName}' was not declared.");
        return _capture.Capture(_scope.ResolveOwner<T>(registration));
    }

    internal void Close() => Interlocked.Exchange(ref _closed, 1);
}
