using System.Runtime.InteropServices;

namespace SuperCV;

/// <summary>
/// Requests 1 ms timer granularity only while an entry animation is active. The lease is balanced
/// and short-lived, avoiding a permanent system-wide power cost while allowing 120 Hz UI pulses.
/// </summary>
internal sealed class HighResolutionTimerPeriodLease : IDisposable
{
    private const uint PeriodMilliseconds = 1;
    private bool _acquired;

    private HighResolutionTimerPeriodLease()
    {
        _acquired = TimeBeginPeriod(PeriodMilliseconds) == 0;
    }

    internal static HighResolutionTimerPeriodLease Acquire() => new();

    public void Dispose()
    {
        if (!_acquired)
        {
            return;
        }

        _acquired = false;
        _ = TimeEndPeriod(PeriodMilliseconds);
    }

    [DllImport("Winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint periodMilliseconds);

    [DllImport("Winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint periodMilliseconds);
}
