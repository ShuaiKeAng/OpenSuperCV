using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SuperCV;

public partial class TextEntryBackgroundWindow : Window, INotifyPropertyChanged
{
    private const double PreviewReferenceWidth = 268;
    private const double PreviewReferenceHeight = 98;
    private Point? _dragStart;
    private double _dragStartOffsetX;
    private double _dragStartOffsetY;
    private string _imagePath;
    private int _opacityPercent;
    private double _scale;
    private double _offsetX;
    private double _offsetY;
    private ImageSource? _previewImageSource;

    public TextEntryBackgroundWindow(SettingViewModel settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _imagePath = settings.TextEntryBackgroundImagePath;
        _opacityPercent = settings.TextEntryBackgroundOpacityPercent;
        _scale = settings.TextEntryBackgroundScale;
        _offsetX = settings.TextEntryBackgroundOffsetX;
        _offsetY = settings.TextEntryBackgroundOffsetY;
        InitializeComponent();
        DataContext = this;
        LoadPreviewImage();
        LocalizationService.Current.LanguageChanged += Localization_LanguageChanged;
        Loaded += (_, _) =>
        {
            UpdatePreviewFrameClip();
            UpdatePreviewTransform();
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? Applied;

    protected override void OnClosed(EventArgs e)
    {
        LocalizationService.Current.LanguageChanged -= Localization_LanguageChanged;
        base.OnClosed(e);
    }

    public string ImagePath
    {
        get => _imagePath;
        private set
        {
            if (string.Equals(_imagePath, value, StringComparison.Ordinal))
            {
                return;
            }

            _imagePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ImageFileName));
        }
    }

    public string ImageFileName => string.IsNullOrWhiteSpace(ImagePath)
        ? LocalizationService.Current.T("尚未选择图片")
        : Path.GetFileName(ImagePath);

    public ImageSource? PreviewImageSource
    {
        get => _previewImageSource;
        private set
        {
            if (ReferenceEquals(_previewImageSource, value))
            {
                return;
            }

            _previewImageSource = value;
            OnPropertyChanged();

            UpdateArtwork();
        }
    }

    public int OpacityLevelIndex
    {
        get => Math.Clamp((int)Math.Round(_opacityPercent / 5.0, MidpointRounding.AwayFromZero), 0, 15);
        set
        {
            int percent = Math.Clamp(value, 0, 15) * 5;
            if (_opacityPercent != percent)
            {
                _opacityPercent = percent;
                OnPropertyChanged();
                OnPropertyChanged(nameof(OpacityPercentDisplay));
                UpdateArtwork();
            }
        }
    }

    public string SelectedImagePath => ImagePath;

    public int SelectedOpacityPercent => _opacityPercent;

    public string OpacityPercentDisplay => $"{_opacityPercent}%";

    public double SelectedScale => _scale;

    public double SelectedOffsetX => _offsetX;

    public double SelectedOffsetY => _offsetY;

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // The pointer may be released between the event check and DragMove.
            }
        }
    }

    private void Localization_LanguageChanged(object? sender, EventArgs e) =>
        OnPropertyChanged(nameof(ImageFileName));

    private void Minimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void ChooseImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = LocalizationService.Current.T("选择文字条目背景图片"),
            Filter = $"{LocalizationService.Current.T("图片文件")}|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|{LocalizationService.Current.T("所有文件")}|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ImagePath = dialog.FileName;
        _scale = 1.0;
        _offsetX = 0.0;
        _offsetY = 0.0;
        LoadPreviewImage();
        UpdatePreviewTransform();
    }

    private void RemoveImageButton_Click(object sender, RoutedEventArgs e)
    {
        ImagePath = string.Empty;
        PreviewImageSource = null;
    }

    private void PreviewSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (PreviewImageSource is null)
        {
            return;
        }

        _dragStart = e.GetPosition(PreviewSurface);
        _dragStartOffsetX = _offsetX;
        _dragStartOffsetY = _offsetY;
        _ = PreviewSurface.CaptureMouse();
        e.Handled = true;
    }

    private void PreviewSurface_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not Point start || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point point = e.GetPosition(PreviewSurface);
        double width = PreviewReferenceWidth;
        double height = PreviewReferenceHeight;
        _offsetX = _dragStartOffsetX + ((point.X - start.X) / width);
        _offsetY = _dragStartOffsetY + ((point.Y - start.Y) / height);
        ConstrainPreviewOffsets();
        UpdatePreviewTransform();
    }

    private void PreviewSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        if (ReferenceEquals(Mouse.Captured, PreviewSurface))
        {
            Mouse.Capture(null);
        }
    }

    private void PreviewFrame_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePreviewFrameClip();
        UpdatePreviewTransform();
    }

    private void PreviewSurface_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (PreviewImageSource is null || e.Delta == 0)
        {
            return;
        }

        _scale = Math.Clamp(_scale + (e.Delta / 120.0 * 0.1), 1.0, 3.0);
        ConstrainPreviewOffsets();
        UpdatePreviewTransform();
        e.Handled = true;
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        Applied?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void LoadPreviewImage()
    {
        if (string.IsNullOrWhiteSpace(ImagePath) || !File.Exists(ImagePath))
        {
            PreviewImageSource = null;
            return;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(ImagePath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            PreviewImageSource = image;
        }
        catch (Exception)
        {
            PreviewImageSource = null;
        }
    }

    private void UpdatePreviewTransform()
    {
        if (!IsLoaded || PreviewViewport is null)
        {
            return;
        }

        PositionPreviewElements();
        ConstrainPreviewOffsets();
        UpdateArtwork();
    }

    /// <summary>
    /// Keeps the artwork over both text-region references.  The outer photo frame does not
    /// affect either its source fit or its draggable range.
    /// </summary>
    private void ConstrainPreviewOffsets()
    {
        if (PreviewImageSource is null ||
            PreviewImageSource.Width <= 0 ||
            PreviewImageSource.Height <= 0)
        {
            return;
        }

        double baseScale = Math.Max(
            PreviewReferenceWidth / PreviewImageSource.Width,
            PreviewReferenceHeight / PreviewImageSource.Height);
        double imageWidth = PreviewImageSource.Width * baseScale;
        double imageHeight = PreviewImageSource.Height * baseScale;
        double baseLeft = PreviewReferenceWidth - imageWidth;
        double baseTop = (PreviewReferenceHeight - imageHeight) / 2.0;
        double scaledLeft = (PreviewReferenceWidth / 2.0) + (_scale * (baseLeft - (PreviewReferenceWidth / 2.0)));
        double scaledTop = (PreviewReferenceHeight / 2.0) + (_scale * (baseTop - (PreviewReferenceHeight / 2.0)));
        double scaledRight = scaledLeft + (imageWidth * _scale);
        double scaledBottom = scaledTop + (imageHeight * _scale);

        double minimumOffsetX = (PreviewReferenceWidth - scaledRight) / PreviewReferenceWidth;
        double maximumOffsetX = -scaledLeft / PreviewReferenceWidth;
        double minimumOffsetY = (PreviewReferenceHeight - scaledBottom) / PreviewReferenceHeight;
        double maximumOffsetY = -scaledTop / PreviewReferenceHeight;

        _offsetX = Math.Clamp(_offsetX, minimumOffsetX, maximumOffsetX);
        _offsetY = Math.Clamp(_offsetY, minimumOffsetY, maximumOffsetY);
    }

    private void UpdatePreviewFrameClip()
    {
        PreviewFrameClip.Rect = new Rect(
            0,
            0,
            Math.Max(0, PreviewSurface.ActualWidth),
            Math.Max(0, PreviewSurface.ActualHeight));
    }

    private void PositionPreviewElements()
    {
        double surfaceWidth = Math.Max(0, PreviewSurface.ActualWidth);
        double surfaceHeight = Math.Max(0, PreviewSurface.ActualHeight);
        double hiddenLeft = (surfaceWidth - PreviewReferenceWidth) / 2.0;
        double hiddenTop = (surfaceHeight - PreviewReferenceHeight) / 2.0;

        Canvas.SetLeft(PreviewViewport, hiddenLeft);
        Canvas.SetTop(PreviewViewport, hiddenTop);
    }


    private void UpdateArtwork()
    {
        if (PreviewImageSource is null ||
            PreviewImageSource.Width <= 0 ||
            PreviewImageSource.Height <= 0 ||
            PreviewSurface.ActualWidth <= 0 ||
            PreviewSurface.ActualHeight <= 0)
        {
            PreviewArtwork.Visibility = Visibility.Collapsed;
            return;
        }

        double baseScale = Math.Max(
            PreviewReferenceWidth / PreviewImageSource.Width,
            PreviewReferenceHeight / PreviewImageSource.Height);
        double imageWidth = PreviewImageSource.Width * baseScale;
        double imageHeight = PreviewImageSource.Height * baseScale;
        double referenceLeft = (PreviewSurface.ActualWidth - PreviewReferenceWidth) / 2.0;
        double referenceTop = (PreviewSurface.ActualHeight - PreviewReferenceHeight) / 2.0;
        double imageLeft = referenceLeft + PreviewReferenceWidth - imageWidth;
        double imageTop = referenceTop + ((PreviewReferenceHeight - imageHeight) / 2.0);

        PreviewArtwork.Width = imageWidth;
        PreviewArtwork.Height = imageHeight;
        PreviewArtwork.Opacity = _opacityPercent / 100.0;
        PreviewArtwork.RenderTransform = new TransformGroup
        {
            Children =
            {
                new ScaleTransform(
                    _scale,
                    _scale,
                    (referenceLeft + (PreviewReferenceWidth / 2.0)) - imageLeft,
                    (referenceTop + (PreviewReferenceHeight / 2.0)) - imageTop),
                new TranslateTransform(
                    _offsetX * PreviewReferenceWidth,
                    _offsetY * PreviewReferenceHeight),
            },
        };
        Canvas.SetLeft(PreviewArtwork, imageLeft);
        Canvas.SetTop(PreviewArtwork, imageTop);
        PreviewArtwork.Visibility = Visibility.Visible;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
