using System.Windows.Threading;

namespace SuperCV;

internal sealed class RelativeTimeTicker
{
    private readonly DispatcherTimer _timer;
    private EventHandler? _subscribers;

    private RelativeTimeTicker()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(1),
        };
        _timer.Tick += OnTimerTick;
    }

    public static RelativeTimeTicker Current { get; } = new();

    public event EventHandler MinuteTick
    {
        add
        {
            _subscribers += value;
            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }
        }
        remove
        {
            _subscribers -= value;
            if (_subscribers is null)
            {
                _timer.Stop();
            }
        }
    }

    private void OnTimerTick(object? sender, EventArgs e) =>
        _subscribers?.Invoke(this, EventArgs.Empty);
}
