using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using SuperCV.Application.Settings;

namespace SuperCV;

public sealed class MainSurfaceAppearanceState : INotifyPropertyChanged, IDisposable
{
    private SettingsService? _settings;
    private Dispatcher? _dispatcher;
    private double _opacity = 1.0;

    private MainSurfaceAppearanceState()
    {
    }

    public static MainSurfaceAppearanceState Current { get; } = new();

    public double Opacity
    {
        get => _opacity;
        private set
        {
            if (Math.Abs(_opacity - value) < 0.0001)
            {
                return;
            }

            _opacity = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Initialize(SettingsService settings, Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (_settings is not null)
        {
            throw new InvalidOperationException("Main-surface appearance is already initialized.");
        }

        _settings = settings;
        _dispatcher = dispatcher;
        Opacity = settings.Snapshot.MainSurfaceOpacity;
        settings.Changed += OnSettingsChanged;
    }

    public void Dispose()
    {
        if (_settings is not null)
        {
            _settings.Changed -= OnSettingsChanged;
            _settings = null;
        }

        _dispatcher = null;
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        Dispatcher? dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Opacity = e.Settings.MainSurfaceOpacity;
            return;
        }

        _ = dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(() => Opacity = e.Settings.MainSurfaceOpacity));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
