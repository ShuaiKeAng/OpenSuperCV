namespace SuperCV.Application.Animation;

/// <summary>
/// Timing data for one animation update accepted by <see cref="AdaptiveFramePacer"/>.
/// </summary>
public readonly record struct AnimationFrameTiming(
    double DeltaSeconds,
    double TargetFramesPerSecond,
    double MeasuredRenderCallbackFramesPerSecond,
    double MeasuredAnimationFramesPerSecond);

/// <summary>
/// A rolling, measured view of the render callback and accepted animation rates.
/// </summary>
public readonly record struct AnimationFrameRateSnapshot(
    double TargetFramesPerSecond,
    double MeasuredRenderCallbackFramesPerSecond,
    double MeasuredAnimationFramesPerSecond,
    int RenderSampleCount,
    int AnimationSampleCount);

/// <summary>
/// Converts real UI-thread pulse timestamps into a paced animation stream. The target follows the
/// current display refresh rate, is capped at 120 FPS, and is continuously corrected using the
/// separately measured compositor and accepted-frame rates rather than assuming a timer is accurate.
/// </summary>
public sealed class AdaptiveFramePacer
{
    public const double MaximumFramesPerSecond = 120.0;

    private const double DefaultFramesPerSecond = 60.0;
    private const double MinimumPlausibleRefreshRate = 24.0;
    private const double MaximumPlausibleRefreshRate = 1000.0;
    private const double MaximumFeedbackCorrectionRatio = 0.005;
    private const int MinimumFeedbackSamples = 8;

    private readonly SlidingFrameRateEstimator _renderRate = new();
    private readonly SlidingFrameRateEstimator _animationRate = new();
    private long? _lastRenderCallbackTicks;
    private long? _lastPulseTicks;
    private long? _lastAnimationTicks;
    private double _nominalRefreshRate;
    private double _frameCredit;

    public AdaptiveFramePacer(double nominalRefreshRate = DefaultFramesPerSecond)
    {
        SetNominalRefreshRate(nominalRefreshRate);
    }

    public AnimationFrameRateSnapshot Snapshot => new(
        TargetFramesPerSecond,
        _renderRate.FramesPerSecond,
        _animationRate.FramesPerSecond,
        _renderRate.SampleCount,
        _animationRate.SampleCount);

    public double TargetFramesPerSecond => Math.Min(
        MaximumFramesPerSecond,
        _nominalRefreshRate);

    public void SetNominalRefreshRate(double refreshRate)
    {
        double normalized = double.IsFinite(refreshRate) &&
                            refreshRate >= MinimumPlausibleRefreshRate &&
                            refreshRate <= MaximumPlausibleRefreshRate
            ? refreshRate
            : DefaultFramesPerSecond;

        if (Math.Abs(_nominalRefreshRate - normalized) < 0.01)
        {
            return;
        }

        _nominalRefreshRate = normalized;
        _frameCredit = 0;
    }

    /// <summary>
    /// Restarts animation timing after an idle period without discarding rolling diagnostics.
    /// </summary>
    public void Restart()
    {
        _lastPulseTicks = null;
        _lastAnimationTicks = null;
        _frameCredit = 0;
    }

    /// <summary>
    /// Records one real WPF composition callback for diagnostics. Calling this method never
    /// creates an animation frame or changes the display-derived target.
    /// </summary>
    public void RecordRenderCallback(TimeSpan timestamp)
    {
        long nowTicks = timestamp.Ticks;
        if (_lastRenderCallbackTicks is not long previousRenderTicks)
        {
            _lastRenderCallbackTicks = nowTicks;
            _renderRate.Add(nowTicks);
            return;
        }

        long elapsedTicks = nowTicks - previousRenderTicks;
        if (elapsedTicks == 0)
        {
            // WPF can raise multiple Rendering notifications for one composition timestamp.
            // They are one real frame and must not inflate metrics or reset pacing state.
            return;
        }

        if (elapsedTicks < 0 || elapsedTicks > TimeSpan.TicksPerSecond * 5L)
        {
            // RenderingTime can reset when the composition target is recreated. Treat that as a
            // new clock epoch so a stale timestamp cannot create an animation jump.
            _renderRate.Reset();
            _lastRenderCallbackTicks = nowTicks;
            _renderRate.Add(nowTicks);
            return;
        }

        _lastRenderCallbackTicks = nowTicks;
        _renderRate.Add(nowTicks);
    }

    /// <summary>
    /// Offers one real UI-thread scheduling opportunity. A frame is accepted only when the
    /// measured wall-clock phase reaches the adaptive target.
    /// </summary>
    public bool TryAdvance(TimeSpan timestamp, out AnimationFrameTiming timing)
    {
        long nowTicks = timestamp.Ticks;
        if (_lastPulseTicks is not long previousPulseTicks)
        {
            _lastPulseTicks = nowTicks;
            timing = default;
            return false;
        }

        long elapsedTicks = nowTicks - previousPulseTicks;
        if (elapsedTicks == 0)
        {
            timing = default;
            return false;
        }

        if (elapsedTicks < 0 || elapsedTicks > TimeSpan.TicksPerSecond * 5L)
        {
            _animationRate.Reset();
            Restart();
            _lastPulseTicks = nowTicks;
            timing = default;
            return false;
        }

        _lastPulseTicks = nowTicks;

        double targetRate = TargetFramesPerSecond;
        double measuredRenderRate = _renderRate.FramesPerSecond;
        double measuredAnimationRate = _animationRate.FramesPerSecond;
        double regulatedRate = targetRate;

        // The timestamp accumulator already produces the correct long-term cadence. This small
        // measured-rate correction removes residual quantization bias on refresh rates such as
        // 59.94/119.88 without allowing feedback to overshoot visibly.
        if (_animationRate.SampleCount >= MinimumFeedbackSamples)
        {
            double errorRatio = (targetRate - measuredAnimationRate) / targetRate;
            double correctionRatio = Math.Clamp(
                errorRatio * 0.08,
                -MaximumFeedbackCorrectionRatio,
                MaximumFeedbackCorrectionRatio);
            regulatedRate *= 1.0 + correctionRatio;
        }

        double elapsedSeconds = elapsedTicks / (double)TimeSpan.TicksPerSecond;
        _frameCredit = Math.Min(2.0, _frameCredit + (elapsedSeconds * regulatedRate));
        if (_frameCredit < 1.0)
        {
            timing = default;
            return false;
        }

        _frameCredit -= 1.0;
        long animationElapsedTicks = _lastAnimationTicks is long previousAnimationTicks
            ? nowTicks - previousAnimationTicks
            : elapsedTicks;
        _lastAnimationTicks = nowTicks;
        _animationRate.Add(nowTicks);

        timing = new AnimationFrameTiming(
            animationElapsedTicks / (double)TimeSpan.TicksPerSecond,
            targetRate,
            measuredRenderRate,
            _animationRate.FramesPerSecond);
        return true;
    }

    private sealed class SlidingFrameRateEstimator
    {
        private const long MeasurementWindowTicks = TimeSpan.TicksPerSecond;
        private const int Capacity = 2048;

        private readonly long[] _timestamps = new long[Capacity];
        private int _head;
        private int _count;

        internal int SampleCount => _count;

        internal double FramesPerSecond
        {
            get
            {
                if (_count < 2)
                {
                    return 0;
                }

                long oldest = _timestamps[_head];
                long newest = _timestamps[(_head + _count - 1) % Capacity];
                long duration = newest - oldest;
                return duration <= 0
                    ? 0
                    : (_count - 1) * (double)TimeSpan.TicksPerSecond / duration;
            }
        }

        internal void Add(long timestamp)
        {
            if (_count > 0)
            {
                long newest = _timestamps[(_head + _count - 1) % Capacity];
                if (timestamp <= newest)
                {
                    Reset();
                }
            }

            while (_count > 0 && timestamp - _timestamps[_head] > MeasurementWindowTicks)
            {
                _head = (_head + 1) % Capacity;
                _count--;
            }

            if (_count == Capacity)
            {
                _head = (_head + 1) % Capacity;
                _count--;
            }

            _timestamps[(_head + _count) % Capacity] = timestamp;
            _count++;
        }

        internal void Reset()
        {
            _head = 0;
            _count = 0;
        }
    }
}
