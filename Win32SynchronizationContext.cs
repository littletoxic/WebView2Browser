using System.Collections.Concurrent;
using Windows.Win32.Foundation;
using static Windows.Win32.PInvoke;

internal sealed class Win32SynchronizationContext(HWND hwnd) : SynchronizationContext
{
    internal const uint ContinuationMessage = 0x8001;

    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

    public override void Post(SendOrPostCallback d, object? state)
    {
        _queue.Enqueue((d, state));
        PostMessage(hwnd, ContinuationMessage, default, default);
    }

    internal void DispatchPending()
    {
        while (_queue.TryDequeue(out (SendOrPostCallback Callback, object? State) work))
        {
            work.Callback(work.State);
        }
    }
}
