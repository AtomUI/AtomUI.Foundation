using AtomUI.Modularity;

namespace AtomUI.Lifetimes;

public abstract class LifetimeOwner
{
    internal LifetimeOwner(LifetimeDomain domain, long id, ModuleId moduleId)
    {
        LifetimeDomain = domain;
        Id = id;
        ModuleId = moduleId;
    }

    internal LifetimeOwnerState CurrentState;
    internal int Readers;
    internal bool StopInvoked;
    internal bool ReleaseEnabled;
    internal bool Constructing;
    internal bool HandoffCompleted;
    internal bool BlockedByFailure;
    internal Exception? Failure;
    internal readonly List<LifetimeFailure> Failures = [];
    internal LifetimeRetirement? Retirement;
    internal readonly List<LifetimeRetirement> Retirements = [];
    internal readonly List<LifetimeCapture> Captures = [];
    internal readonly List<LifetimeCapture> Consumers = [];
    internal readonly HashSet<LifetimeOwner> Prerequisites = [];
    internal readonly HashSet<LifetimeOwner> ReleaseDependents = [];
    internal abstract void InvokeStop();
    internal abstract ValueTask InvokeReleaseAsync();
    internal abstract void ForgetValue();

    public long Id { get; }
    public ModuleId ModuleId { get; }
    public LifetimeDomain LifetimeDomain { get; }
    public LifetimeOwnerState State { get { lock (LifetimeDomain.Gate) return CurrentState; } }
    public void WaitForRelease(LifetimeOwner prerequisite) => LifetimeDomain.AddOrdering(this, prerequisite);
    public LifetimeRetirement BeginRetirement() => LifetimeDomain.Retire(this);
}

public sealed class LifetimeOwner<T> : LifetimeOwner where T : class
{
    private T? _value;
    private Func<T, ValueTask>? _release;
    private Action<T>? _stop;

    internal LifetimeOwner(LifetimeDomain domain, long id, ModuleId moduleId, T? value,
        Func<T, ValueTask> release, Action<T>? stop) : base(domain, id, moduleId)
    {
        _value = value;
        _release = release;
        _stop = stop;
    }

    internal T ValueUnsafe => _value ?? throw new InvalidOperationException("The owner has no available instance.");
    internal void SetValueUnsafe(T value) => _value = value;
    internal override void InvokeStop() => _stop?.Invoke(ValueUnsafe);
    internal override ValueTask InvokeReleaseAsync() => _release!(ValueUnsafe);
    internal override void ForgetValue() { _value = null; _release = null; _stop = null; }

    public LifetimeLease<T> AcquireLease() => LifetimeDomain.Acquire(this);
    public LifetimeLease<TDependency> Capture<TDependency>(LifetimeOwner<TDependency> dependency)
        where TDependency : class => LifetimeDomain.Capture(this, dependency);
}

internal sealed class LifetimeCapture(LifetimeOwner consumer, LifetimeOwner dependency)
{
    internal LifetimeOwner Consumer { get; } = consumer;
    internal LifetimeOwner Dependency { get; } = dependency;
    internal bool Returned;
}

public sealed class LifetimeLease<T> : IDisposable where T : class
{
    private readonly LifetimeOwner<T> _owner;
    private readonly LifetimeCapture? _capture;
    private bool _returned;

    internal LifetimeLease(LifetimeOwner<T> owner, LifetimeCapture? capture = null)
    {
        _owner = owner;
        _capture = capture;
    }

    public T Value
    {
        get
        {
            lock (_owner.LifetimeDomain.Gate)
            {
                ObjectDisposedException.ThrowIf(_returned || _capture?.Returned == true, this);
                return _owner.ValueUnsafe;
            }
        }
    }

    public void Dispose()
    {
        lock (_owner.LifetimeDomain.Gate)
        {
            if (_returned) return;
            _returned = true;
            if (_capture is null)
            {
                _owner.Readers--;
                _owner.LifetimeDomain.QueueProgressUnsafe(_owner);
            }
            else
            {
                _capture.Returned = true;
                // Release may return a dependency and then throw. Keep its pin until the outcome is known.
                if (_capture.Consumer.CurrentState is not (LifetimeOwnerState.Releasing or
                        LifetimeOwnerState.CleanupFailed or LifetimeOwnerState.RetainedByFailedOwner))
                    LifetimeDomain.RemoveCaptureUnsafe(_capture);
            }
        }
        _owner.LifetimeDomain.Progress();
    }
}
