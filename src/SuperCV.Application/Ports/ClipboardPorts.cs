using SuperCV.Domain.Clipboard;

namespace SuperCV.Application.Ports;

public sealed record ClipboardSnapshot(
    ClipboardPayload Payload,
    uint SequenceNumber,
    bool IsSelfChange,
    DateTimeOffset CapturedAtUtc);

public sealed class ClipboardChangedEventArgs : EventArgs
{
    public ClipboardChangedEventArgs(ClipboardSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public ClipboardSnapshot Snapshot { get; }
}

public interface IClipboardService : IAsyncDisposable
{
    event EventHandler<ClipboardChangedEventArgs>? ClipboardChanged;

    bool IsMonitoring { get; }

    bool ImageCaptureEnabled { get; set; }

    ValueTask StartMonitoringAsync(CancellationToken cancellationToken = default);

    ValueTask StopMonitoringAsync(CancellationToken cancellationToken = default);

    ValueTask<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default);

    ValueTask WriteAsync(ClipboardPayload payload, CancellationToken cancellationToken = default);

    ValueTask PasteAsync(CancellationToken cancellationToken = default);

    ValueTask DeleteCachedImagesExceptAsync(
        IReadOnlySet<string> retainedImageLinks,
        CancellationToken cancellationToken = default);
}
