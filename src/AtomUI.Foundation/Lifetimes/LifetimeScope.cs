using AtomUI.Modularity;

namespace AtomUI.Lifetimes;

public sealed class LifetimeScope : IAsyncDisposable
{
    internal LifetimeScope(LifetimeDomain domain) => LifetimeDomain = domain;

    internal readonly List<LifetimeOwner> Owners = [];
    internal bool Retiring;
    internal LifetimeRetirement? Retirement;

    public LifetimeDomain LifetimeDomain { get; }
    public bool IsRetiring { get { lock (LifetimeDomain.Gate) return Retiring; } }
    public LifetimeOwner<T> Own<T>(ModuleId moduleId, T value, Func<T, ValueTask> release, Action<T>? stop = null)
        where T : class => LifetimeDomain.Own(this, moduleId, value, release, stop);
    public LifetimeOwner<T> Create<T>(ModuleId moduleId, Func<LifetimeOwner<T>, T> factory, Func<T, ValueTask> release, Action<T>? stop = null)
        where T : class => LifetimeDomain.Create(this, moduleId, factory, release, stop);
    public LifetimeRetirement BeginRetirement() => LifetimeDomain.Retire(this);
    public async ValueTask DisposeAsync()
    {
        var result = await BeginRetirement().FinalCompletion.ConfigureAwait(false);
        if (!result.IsSuccess) throw new LifetimeCleanupException(result);
    }
}
