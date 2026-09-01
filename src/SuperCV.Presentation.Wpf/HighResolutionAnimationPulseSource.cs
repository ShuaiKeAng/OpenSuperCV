using System.Diagnostics;
using System.Windows.Threading;

namespace SuperCV;

/// <summary>
/// Supplies high-resolution UI-thread scheduling opportunities while an animation is active.
/// Requests are coalesced, so a busy dispatcher never accumulates stale animation work.
/// </summary>
internal sealed class HighResolutionAnimationPulseSource : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action _pulse;
    private readonly Action _dispatchPulseAction;
    private System.Threading.Timer? _timer;
    private HighResolutionTimerPeriodLease? _timerPeriodLease;
    private long _intervalStopwatchTicks;
    private long _nextPulseTimestamp;
    private int _pulseQueued;
    private int _running;
    private bool _disposed;

    internal HighResolutionAnimationPulseSource(Dispatcher dispatcher, Action pulse)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _pulse = pulse ?? throw new ArgumentNullException(nameof(pulse));
        _dispatchPulseAction = DispatchPulse;
    }

    internal void Start(TimeSpan interval)
    {
        _dispatcher.VerifyAccess();
        ThrowIfDisposed();

        long intervalTicks = NormalizeInterval(interval);
        Volatile.Write(ref _intervalStopwatchTicks, intervalTicks);
        Volatile.Write(
            ref _nextPulseTimestamp,
            Stopwatch.GetTimestamp() + intervalTicks);
        if (_timer is not null)
        {
            Volatile.Write(ref _running, 1);
            ArmNextPulse();
            return;
        }

        _timerPeriodLease = HighResolutionTimerPeriodLease.Acquire();
        Volatile.Write(ref _running, 1);
        _timer = new System.Threading.Timer(
            QueuePulse,
            state: null,
            dueTime: Timeout.Infinite,
            period: Timeout.Infinite);
        _ = _timer.Change(0, Timeout.Infinite);
    }

    internal void UpdateInterval(TimeSpan interval)
    {
        _dispatcher.VerifyAccess();
        if (_timer is null)
        {
            return;
        }

        long intervalTicks = NormalizeInterval(interval);
        Volatile.Write(ref _intervalStopwatchTicks, intervalTicks);
        Volatile.Write(
            ref _nextPulseTimestamp,
            Stopwatch.GetTimestamp() + intervalTicks);
        ArmNextPulse();
    }

    internal void Stop()
    {
        _dispatcher.VerifyAccess();
        Volatile.Write(ref _running, 0);
        System.Threading.Timer? timer = _timer;
        _timer = null;
        timer?.Dispose();
        _timerPeriodLease?.Dispose();
        _timerPeriodLease = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private void QueuePulse(object? state)
    {
        if (Volatile.Read(ref _running) == 0)
        {
            return;
        }

        // System.Threading.Timer accepts only whole-millisecond due times. Re-arm it against an
        // absolute Stopwatch phase so 8.333 ms and similar intervals become a stable 8/9 ms
        // sequence instead of a permanently fast 8 ms periodic timer.
        ArmNextPulse();
        if (Interlocked.Exchange(ref _pulseQueued, 1) != 0)
        {
            return;
        }

        try
        {
            _ = _dispatcher.BeginInvoke(
                DispatcherPriority.Render,
                _dispatchPulseAction);
        }
        catch (InvalidOperationException)
        {
            Volatile.Write(ref _pulseQueued, 0);
        }
    }

    private void DispatchPulse()
    {
        Volatile.Write(ref _pulseQueued, 0);
        if (Volatile.Read(ref _running) != 0)
        {
            _pulse();
        }
    }

    private void ArmNextPulse()
    {
        long intervalTicks = Volatile.Read(ref _intervalStopwatchTicks);
        long nextTimestamp = Volatile.Read(ref _nextPulseTimestamp);
        long now = Stopwatch.GetTimestamp();
        if (nextTimestamp <= now)
        {
            long missedIntervals = ((now - nextTimestamp) / intervalTicks) + 1;
            nextTimestamp += missedIntervals * intervalTicks;
            Volatile.Write(ref _nextPulseTimestamp, nextTimestamp);
        }

        long remainingTicks = Math.Max(1, nextTimestamp - now);
        int dueMilliseconds = Math.Max(
            1,
            (int)Math.Ceiling(
                remainingTicks * 1000.0 / Stopwatch.Frequency));
        try
        {
            _ = _timer?.Change(dueMilliseconds, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Stop can race a final ThreadPool callback during application shutdown.
        }
    }

    private static long NormalizeInterval(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        return Math.Max(
            1,
            (long)Math.Round(
                interval.TotalSeconds * Stopwatch.Frequency,
                MidpointRounding.AwayFromZero));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
