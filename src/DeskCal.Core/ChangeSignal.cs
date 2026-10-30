namespace DeskCal.Core;

public sealed class ChangeSignal : IDisposable
{
    private const string EVENT_NAME = @"Local\DeskCal.DataChanged";
    private readonly EventWaitHandle _signal = new(false, EventResetMode.AutoReset, EVENT_NAME);

    public WaitHandle Handle => _signal;

    public static void Notify()
    {
        try
        {
            using var notification = new EventWaitHandle(false, EventResetMode.AutoReset, EVENT_NAME);
            notification.Set();
        }
        catch (Exception)
        {
            // A best-effort refresh signal must never turn a committed write into a failure.
        }
    }

    public void Dispose() => _signal.Dispose();
}
