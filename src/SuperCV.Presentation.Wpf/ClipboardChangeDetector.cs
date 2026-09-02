using SuperCV.Domain.Clipboard;

namespace SuperCV;

public sealed class ClipboardChangeDetector
{
    private static readonly TimeSpan FormatUpdateDebounceWindow =
        TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan NewEntryThrottleWindow =
        TimeSpan.FromMilliseconds(500);

    private readonly object _gate = new();
    private string? _lastFingerprint;
    private string? _lastAcceptedContentKey;
    private DateTimeOffset _lastAcceptedAtUtc;
    private DateTimeOffset _lastNewEntryAcceptedAtUtc;

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

        string fingerprint = payload.ComputeContentFingerprint();
        string? contentKey = payload.IsEmpty ? null : fingerprint;
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
            _lastNewEntryAcceptedAtUtc = default;
        }
    }

    /// <summary>
    /// Prevents clipboard producers that publish an item in several rapid updates from creating
    /// more than one history entry. This intentionally applies to both text and images.
    /// </summary>
    public bool IsNewEntryThrottled(DateTimeOffset observedAtUtc)
    {
        lock (_gate)
        {
            if (_lastNewEntryAcceptedAtUtc == default)
            {
                return false;
            }

            TimeSpan elapsed = observedAtUtc - _lastNewEntryAcceptedAtUtc;
            return elapsed < TimeSpan.Zero || elapsed < NewEntryThrottleWindow;
        }
    }

    /// <summary>
    /// Records an entry only after it has actually been retained by history, so an ignored or
    /// duplicate entry never starts the throttle window.
    /// </summary>
    public void RecordNewEntryAccepted(DateTimeOffset acceptedAtUtc)
    {
        lock (_gate)
        {
            _lastNewEntryAcceptedAtUtc = acceptedAtUtc;
        }
    }

}

public sealed record ClipboardChangeDetection(
    bool Changed,
    bool IsFormatUpdateBurst,
    string Fingerprint);
