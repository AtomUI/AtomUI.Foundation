namespace AtomUI.Lifetimes;

public sealed class LifetimeSuspension : IDisposable
{
    private readonly LifetimeDomain _domain;

    internal LifetimeSuspension(LifetimeDomain domain, LifetimeOwner[] owners)
    {
        _domain = domain;
        Members = owners;
        Owners = Array.AsReadOnly(owners);
    }

    internal LifetimeOwner[] Members { get; }
    internal bool Stopped;
    internal bool NotificationsComplete;
    internal bool Completed;
    internal bool Disposed;
    public LifetimeRetirement? Retirement { get; internal set; }

    public IReadOnlyList<LifetimeOwner> Owners { get; }
    // Calls from a Stop callback can reenter without waiting for their own dispatch.
    // In that case the retirement's Completion signals the notification barrier.
    public void Stop() => _domain.Stop(this);
    public LifetimeRetirement CompleteRetirement() => _domain.CompleteRetirement(this);
    public void Dispose() => _domain.Resume(this);
}
