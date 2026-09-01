using System.Windows;
using SuperCV.Application.Ports;
using SuperCV.Domain.Clipboard;

namespace SuperCV;

public enum TextFormat
{
    Text = 0,
    UnicodeText = 1,
    Html = 2,
    Rtf = 3,
    Image = 4,
}

/// <summary>
/// Keeps the existing presentation contract while delegating every Windows operation to the
/// instance-scoped clipboard service created by <see cref="App"/>.
/// </summary>
public static class MyClipboard
{
    private static readonly TimeSpan PasteMonitoringResumeDelay =
        TimeSpan.FromMilliseconds(100);
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim ClipboardWriteGate = new(1, 1);
    private static readonly SemaphoreSlim PasteGate = new(1, 1);
    private static IClipboardService? _service;
    private static Dictionary<TextFormat, string> _lastFormats = new();
    private static bool _disposed;

    public static event EventHandler<ClipBoardChangeArgs>? ClipboardChanged;

    public static bool MonitoringStatus
    {
        get
        {
            lock (Gate)
            {
                return _service?.IsMonitoring == true;
            }
        }
        set
        {
            if (value)
            {
                StartMonitoring();
            }
            else
            {
                StopMonitoring();
            }
        }
    }

    internal static void Initialize(IClipboardService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        lock (Gate)
        {
            if (_service is not null)
            {
                throw new InvalidOperationException("Clipboard adapter is already initialized.");
            }

            _service = service;
            _service.ClipboardChanged += OnClipboardChanged;
            _disposed = false;
        }
    }

    public static void Init()
    {
        _ = GetService();
    }

    public static void StartMonitoring()
    {
        IClipboardService service = GetService();
        service.StartMonitoringAsync().AsTask().GetAwaiter().GetResult();
    }

    public static void StopMonitoring()
    {
        IClipboardService? service;
        lock (Gate)
        {
            service = _service;
        }

        service?.StopMonitoringAsync().AsTask().GetAwaiter().GetResult();
    }

    public static Dictionary<TextFormat, string> GetAllTextFormats()
    {
        lock (Gate)
        {
            if (_lastFormats.Count > 0)
            {
                return new Dictionary<TextFormat, string>(_lastFormats);
            }
        }

        ClipboardSnapshot snapshot = GetService().ReadAsync().AsTask().GetAwaiter().GetResult();
        Dictionary<TextFormat, string> formats = ToLegacy(snapshot.Payload);
        lock (Gate)
        {
            _lastFormats = formats;
            return new Dictionary<TextFormat, string>(_lastFormats);
        }
    }

    public static List<TextFormat> GetAvailableTextFormats() =>
        GetAllTextFormats().Keys.OrderBy(format => format).ToList();

    public static string GetText(TextFormat format = TextFormat.Text)
    {
        Dictionary<TextFormat, string> formats = GetAllTextFormats();
        return formats.TryGetValue(format, out string? value) ? value : string.Empty;
    }

    public static void SetMultipleTextFormats(Dictionary<TextFormat, string> formatTexts)
    {
        ArgumentNullException.ThrowIfNull(formatTexts);
        var payload = new ClipboardPayload(
            formatTexts.ToDictionary(pair => ToDomain(pair.Key), pair => pair.Value));
        if (payload.IsEmpty)
        {
            return;
        }

        ClipboardWriteGate.Wait();
        try
        {
            IClipboardService service = GetService();
            bool restartMonitoring = service.IsMonitoring;
            try
            {
                if (restartMonitoring)
                {
                    service.StopMonitoringAsync().AsTask().GetAwaiter().GetResult();
                }

                service.WriteAsync(payload).AsTask().GetAwaiter().GetResult();
                lock (Gate)
                {
                    _lastFormats = ToLegacy(payload);
                }
            }
            finally
            {
                if (restartMonitoring)
                {
                    service.StartMonitoringAsync().AsTask().GetAwaiter().GetResult();
                }
            }
        }
        finally
        {
            ClipboardWriteGate.Release();
        }
    }

    public static async Task SetTextAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var payload = new ClipboardPayload(
            new Dictionary<ClipboardFormat, string>
            {
                [ClipboardFormat.UnicodeText] = text,
            });

        await ClipboardWriteGate
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            IClipboardService service = GetService();
            // The native service records this write as a self-change, so monitoring can remain
            // active without feeding the copied AI response back into clipboard history.
            await service
                .WriteAsync(payload, cancellationToken)
                .ConfigureAwait(false);
            lock (Gate)
            {
                _lastFormats = ToLegacy(payload);
            }
        }
        finally
        {
            ClipboardWriteGate.Release();
        }
    }

    public static void Paste() =>
        GetService().PasteAsync().AsTask().GetAwaiter().GetResult();

    public static async Task PasteWithMonitoringSuspendedAsync()
    {
        await PasteGate.WaitAsync().ConfigureAwait(true);
        bool restartMonitoring = false;
        try
        {
            restartMonitoring = MonitoringStatus;
            if (restartMonitoring)
            {
                StopMonitoring();
            }

            Paste();
            await Task.Delay(PasteMonitoringResumeDelay).ConfigureAwait(true);
        }
        finally
        {
            try
            {
                if (restartMonitoring)
                {
                    StartMonitoring();
                }
            }
            finally
            {
                PasteGate.Release();
            }
        }
    }

    public static void Dispose()
    {
        IClipboardService? service;
        lock (Gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            service = _service;
            _service = null;
            _lastFormats = new Dictionary<TextFormat, string>();
        }

        if (service is null)
        {
            return;
        }

        service.ClipboardChanged -= OnClipboardChanged;
    }

    private static IClipboardService GetService()
    {
        lock (Gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(MyClipboard));
            }

            return _service
                ?? throw new InvalidOperationException("Clipboard service has not been composed.");
        }
    }

    private static void OnClipboardChanged(object? sender, ClipboardChangedEventArgs e)
    {
        Dictionary<TextFormat, string> formats = ToLegacy(e.Snapshot.Payload);
        lock (Gate)
        {
            if (_disposed)
            {
                return;
            }

            _lastFormats = formats;
        }

        var eventArgs = new ClipBoardChangeArgs(
            e.Snapshot.IsSelfChange,
            new Dictionary<TextFormat, string>(formats),
            e.Snapshot.SequenceNumber,
            e.Snapshot.CapturedAtUtc);

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ClipboardChanged?.Invoke(null, eventArgs);
            return;
        }

        _ = dispatcher.BeginInvoke(() => ClipboardChanged?.Invoke(null, eventArgs));
    }

    private static ClipboardFormat ToDomain(TextFormat format) => format switch
    {
        TextFormat.Text => ClipboardFormat.Text,
        TextFormat.UnicodeText => ClipboardFormat.UnicodeText,
        TextFormat.Html => ClipboardFormat.Html,
        TextFormat.Rtf => ClipboardFormat.Rtf,
        TextFormat.Image => ClipboardFormat.Image,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    private static TextFormat ToLegacy(ClipboardFormat format) => format switch
    {
        ClipboardFormat.Text => TextFormat.Text,
        ClipboardFormat.UnicodeText => TextFormat.UnicodeText,
        ClipboardFormat.Html => TextFormat.Html,
        ClipboardFormat.Rtf => TextFormat.Rtf,
        ClipboardFormat.Image => TextFormat.Image,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    private static Dictionary<TextFormat, string> ToLegacy(ClipboardPayload payload) =>
        payload.Formats.ToDictionary(pair => ToLegacy(pair.Key), pair => pair.Value);
}

public sealed class ClipBoardChangeArgs : EventArgs
{
    public ClipBoardChangeArgs(
        bool onMySelf,
        IReadOnlyDictionary<TextFormat, string> formats,
        uint sequenceNumber,
        DateTimeOffset capturedAtUtc)
    {
        OnMySelf = onMySelf;
        Formats = formats ?? throw new ArgumentNullException(nameof(formats));
        SequenceNumber = sequenceNumber;
        CapturedAtUtc = capturedAtUtc;
    }

    public bool OnMySelf { get; }

    public IReadOnlyDictionary<TextFormat, string> Formats { get; }

    public uint SequenceNumber { get; }

    public DateTimeOffset CapturedAtUtc { get; }
}
