using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SuperCV.Application.Settings;
using DomainAppSettings = SuperCV.Domain.Settings.AppSettings;

namespace SuperCV;

/// <summary>
/// Shared, settings-backed artwork for text-entry cards.  Keeping the source in one place makes
/// every live card update together and avoids each card independently decoding the same file.
/// </summary>
public sealed class TextEntryBackgroundAppearanceState : INotifyPropertyChanged, IDisposable
{
    // A card is roughly 268 × 98 logical pixels.  1024 px preserves sharpness on high-DPI
    // displays while avoiding a full-resolution photo being retained for every live card.
    private const int RuntimeImageMaximumDimension = 1024;
    private const int RuntimeJpegQuality = 85;
    private SettingsService? _settings;
    private Dispatcher? _dispatcher;
    private ImageSource? _backgroundImageSource;
    private double _backgroundOpacity;
    private double _backgroundScale = 1.0;
    private double _backgroundOffsetX;
    private double _backgroundOffsetY;
    private BitmapSource? _runtimeImage;
    private string? _runtimeImagePath;
    private DateTime _runtimeImageLastWriteUtc;
    private bool _isEnabled;

    private TextEntryBackgroundAppearanceState()
    {
    }

    public static TextEntryBackgroundAppearanceState Current { get; } = new();

    public ImageSource? BackgroundImageSource
    {
        get => _backgroundImageSource;
        private set
        {
            if (ReferenceEquals(_backgroundImageSource, value))
            {
                return;
            }

            _backgroundImageSource = value;
            OnPropertyChanged();
        }
    }

    public double BackgroundOpacity
    {
        get => _backgroundOpacity;
        private set => SetValue(ref _backgroundOpacity, value);
    }

    public double BackgroundScale
    {
        get => _backgroundScale;
        private set => SetValue(ref _backgroundScale, value);
    }

    public double BackgroundOffsetX
    {
        get => _backgroundOffsetX;
        private set => SetValue(ref _backgroundOffsetX, value);
    }

    public double BackgroundOffsetY
    {
        get => _backgroundOffsetY;
        private set => SetValue(ref _backgroundOffsetY, value);
    }

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
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Initialize(SettingsService settings, Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (_settings is not null)
        {
            throw new InvalidOperationException("Text-entry background appearance is already initialized.");
        }

        _settings = settings;
        _dispatcher = dispatcher;
        Apply(settings.Snapshot);
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
        _runtimeImage = null;
        _runtimeImagePath = null;
        _runtimeImageLastWriteUtc = default;
        ClearBackground();
        IsEnabled = false;
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        Dispatcher? dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply(e.Settings);
            return;
        }

        _ = dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(() => Apply(e.Settings)));
    }

    private void Apply(DomainAppSettings settings)
    {
        string path = settings.TextEntryBackgroundImagePath;
        if (string.IsNullOrWhiteSpace(path) ||
            settings.TextEntryBackgroundOpacityPercent <= 0 ||
            !File.Exists(path))
        {
            ClearBackground();
            IsEnabled = false;
            return;
        }

        try
        {
            BitmapSource image = GetRuntimeImage(path);

            BackgroundImageSource = image;
            BackgroundOpacity = settings.TextEntryBackgroundOpacityPercent / 100.0;
            BackgroundScale = settings.TextEntryBackgroundScale;
            BackgroundOffsetX = settings.TextEntryBackgroundOffsetX;
            BackgroundOffsetY = settings.TextEntryBackgroundOffsetY;
            IsEnabled = true;
        }
        catch (Exception)
        {
            // A missing, unsupported, or unreadable user file should simply leave cards plain.
            ClearBackground();
            IsEnabled = false;
        }
    }

    /// <summary>
    /// Creates one reduced, JPEG-compressed in-memory source for the live cards.  The original
    /// user-selected file remains untouched and the configuration preview continues to use it.
    /// </summary>
    private static BitmapSource LoadCompressedRuntimeImage(string path)
    {
        var original = new BitmapImage();
        original.BeginInit();
        original.CacheOption = BitmapCacheOption.OnLoad;
        original.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        original.UriSource = new Uri(path, UriKind.Absolute);
        original.EndInit();
        original.Freeze();

        double resizeScale = Math.Min(
            1.0,
            RuntimeImageMaximumDimension / (double)Math.Max(original.PixelWidth, original.PixelHeight));
        BitmapSource frame = original;
        if (resizeScale < 1.0)
        {
            var scaled = new TransformedBitmap(
                original,
                new ScaleTransform(resizeScale, resizeScale));
            scaled.Freeze();
            frame = scaled;
        }

        try
        {
            bool preservesTransparency = HasTransparency(frame);
            var compatibleFrame = new FormatConvertedBitmap();
            compatibleFrame.BeginInit();
            compatibleFrame.Source = frame;
            compatibleFrame.DestinationFormat = preservesTransparency
                ? PixelFormats.Bgra32
                : PixelFormats.Bgr32;
            compatibleFrame.EndInit();
            compatibleFrame.Freeze();

            // JPEG offers the best size/performance trade-off for opaque artwork.  For images
            // with visible transparency, retain alpha through a resized PNG instead of turning
            // transparent pixels black.
            BitmapEncoder encoder = preservesTransparency
                ? new PngBitmapEncoder()
                : new JpegBitmapEncoder { QualityLevel = RuntimeJpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(compatibleFrame));
            using var encoded = new MemoryStream();
            encoder.Save(encoded);
            encoded.Position = 0;

            var compressed = new BitmapImage();
            compressed.BeginInit();
            compressed.CacheOption = BitmapCacheOption.OnLoad;
            compressed.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            compressed.StreamSource = encoded;
            compressed.EndInit();
            compressed.Freeze();
            return compressed;
        }
        catch (Exception)
        {
            // Rendering availability takes precedence over compression for an unusual decoder
            // format.  The normal path above remains compressed for supported images.
            return frame;
        }
    }

    private BitmapSource GetRuntimeImage(string path)
    {
        DateTime lastWriteUtc;
        try
        {
            lastWriteUtc = File.GetLastWriteTimeUtc(path);
        }
        catch when (_runtimeImage is not null &&
                    string.Equals(_runtimeImagePath, path, StringComparison.OrdinalIgnoreCase))
        {
            return _runtimeImage;
        }

        if (_runtimeImage is not null &&
            string.Equals(_runtimeImagePath, path, StringComparison.OrdinalIgnoreCase) &&
            _runtimeImageLastWriteUtc == lastWriteUtc)
        {
            return _runtimeImage;
        }

        try
        {
            BitmapSource compressed = LoadCompressedRuntimeImage(path);
            _runtimeImage = compressed;
            _runtimeImagePath = path;
            _runtimeImageLastWriteUtc = lastWriteUtc;
            return compressed;
        }
        catch when (_runtimeImage is not null &&
                    string.Equals(_runtimeImagePath, path, StringComparison.OrdinalIgnoreCase))
        {
            // An editor can replace the selected image through a short-lived temporary file.
            // Keep the last known-good texture visible until it becomes readable again.
            return _runtimeImage;
        }
    }

    private static bool HasTransparency(BitmapSource source)
    {
        var pixels = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = checked(pixels.PixelWidth * 4);
        var buffer = new byte[checked(stride * pixels.PixelHeight)];
        pixels.CopyPixels(buffer, stride, 0);
        for (int offset = 3; offset < buffer.Length; offset += 4)
        {
            if (buffer[offset] != byte.MaxValue)
            {
                return true;
            }
        }

        return false;
    }

    private void ClearBackground()
    {
        BackgroundImageSource = null;
        BackgroundOpacity = 0;
        BackgroundScale = 1.0;
        BackgroundOffsetX = 0;
        BackgroundOffsetY = 0;
    }

    private void SetValue<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        where T : IEquatable<T>
    {
        if (field.Equals(value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
