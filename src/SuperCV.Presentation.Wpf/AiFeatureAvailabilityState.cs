using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using SuperCV.Application.Settings;

namespace SuperCV;

public sealed class AiFeatureAvailabilityState : INotifyPropertyChanged, IDisposable
{
    private SettingsService? _settings;
    private Dispatcher? _dispatcher;
    private bool _isEnabled = true;

    private AiFeatureAvailabilityState()
    {
    }

    public static AiFeatureAvailabilityState Current { get; } = new();

    public bool IsEnabled
    {
        get => _isEnabled;
        private set
        {
            if (_isEnabled == value)
            {
                return;
            }

            _isEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EnabledVisibility));
            OnPropertyChanged(nameof(DisabledVisibility));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public Visibility EnabledVisibility =>
        IsEnabled ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DisabledVisibility =>
        IsEnabled ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? Changed;

    internal void Initialize(SettingsService settings, Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (_settings is not null)
        {
            throw new InvalidOperationException("AI feature availability is already initialized.");
        }

        _settings = settings;
        _dispatcher = dispatcher;
        IsEnabled = IsAiConfigurationAvailable(settings.Snapshot.EnableAiFeatures, settings.ApiKey, settings.Snapshot.AiBaseUrl);
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
            IsEnabled = IsAiConfigurationAvailable(
                e.Settings.EnableAiFeatures,
                _settings?.ApiKey,
                e.Settings.AiBaseUrl);
            return;
        }

        _ = dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(() => IsEnabled = IsAiConfigurationAvailable(
                e.Settings.EnableAiFeatures,
                _settings?.ApiKey,
                e.Settings.AiBaseUrl)));
    }

    private static bool IsAiConfigurationAvailable(
        bool aiFeaturesEnabled,
        string? apiKey,
        string? baseUrl) =>
        aiFeaturesEnabled &&
        (!string.IsNullOrWhiteSpace(apiKey) || AIProviderDefaults.AllowsEmptyApiKey(baseUrl));

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
