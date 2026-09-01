using SuperCV.Domain.Clipboard;

namespace SuperCV;

public sealed class ClipboardChangeDetector
{
    private static readonly TimeSpan FormatUpdateDebounceWindow =
        TimeSpan.FromMilliseconds(1500);

    private readonly object _gate = new();
    private string? _lastFingerprint;
    private string? _lastAcceptedContentKey;
    private DateTimeOffset _lastAcceptedAtUtc;

    public ClipboardChangeDetection DetectChange(
        Dictionary<TextFormat, string> formats,
        DateTimeOffset capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(formats);

        var payload = new ClipboardPayload(
            formats.ToDictionary(
                pair => pair.Key switch
                {
                    TextFormat.Text => ClipboardFormat.Text,
                    TextFormat.UnicodeText => ClipboardFormat.UnicodeText,
                    TextFormat.Html => ClipboardFormat.Html,
                    TextFormat.Rtf => ClipboardFormat.Rtf,
                    TextFormat.Image => ClipboardFormat.Image,
                    _ => throw new ArgumentOutOfRangeException(nameof(formats)),
                },
                pair => pair.Value));

        string fingerprint = payload.ComputeFingerprint();
        string? contentKey = CreateContentKey(payload);
        lock (_gate)
        {
            bool isFormatUpdateBurst = contentKey is not null &&
                                       string.Equals(
                                           _lastAcceptedContentKey,
                                           contentKey,
                                           StringComparison.Ordinal) &&
                                       capturedAtUtc >= _lastAcceptedAtUtc &&
                                       capturedAtUtc - _lastAcceptedAtUtc <=
                                           FormatUpdateDebounceWindow;
            bool changed = !string.Equals(
                _lastFingerprint,
                fingerprint,
                StringComparison.Ordinal);
            _lastFingerprint = fingerprint;

            if (!isFormatUpdateBurst && changed)
            {
                _lastAcceptedContentKey = contentKey;
                _lastAcceptedAtUtc = capturedAtUtc;
            }

            return new ClipboardChangeDetection(
                Changed: changed,
                IsFormatUpdateBurst: isFormatUpdateBurst,
                Fingerprint: fingerprint);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _lastFingerprint = null;
            _lastAcceptedContentKey = null;
            _lastAcceptedAtUtc = default;
        }
    }

    private static string? CreateContentKey(ClipboardPayload payload)
    {
        if (payload.IsImage)
        {
            return string.IsNullOrEmpty(payload.ImageLink)
                ? null
                : $"image:{payload.ImageLink}";
        }

        string primaryText = payload.PrimaryText;
        if (string.IsNullOrEmpty(primaryText))
        {
            return null;
        }

        string normalizedText = primaryText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd('\n');
        return normalizedText.Length == 0 ? null : $"text:{normalizedText}";
    }
}

public sealed record ClipboardChangeDetection(
    bool Changed,
    bool IsFormatUpdateBurst,
    string Fingerprint);
