namespace AtomUI.Lifetimes;

/// <summary>
/// One active user-callback invocation, tracked through the asynchronous call context. Work that a
/// callback hands to another thread of the same logical flow still observes the frame, so it is
/// recognized as reentry; work that outlives the callback observes an inactive frame. The host and
/// the lifetime domain use this one mechanism for reentry detection.
/// </summary>
internal sealed class CallbackFrame
{
    private volatile bool _active = true;

    internal static bool IsInside(AsyncLocal<CallbackFrame?> slot) => slot.Value?._active == true;

    /// <summary>Marks the current flow as inside a callback until the returned scope is disposed.</summary>
    internal static Scope Enter(AsyncLocal<CallbackFrame?> slot)
    {
        var previous = slot.Value;
        var frame = new CallbackFrame();
        slot.Value = frame;
        return new Scope(slot, previous, frame);
    }

    internal readonly struct Scope(AsyncLocal<CallbackFrame?> slot, CallbackFrame? previous, CallbackFrame frame) : IDisposable
    {
        public void Dispose()
        {
            frame._active = false;
            slot.Value = previous;
        }
    }
}
