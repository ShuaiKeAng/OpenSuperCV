using SuperCV.Domain.Clipboard;

namespace SuperCV.Domain.History;

public sealed record ClipboardEntry
{
    public ClipboardEntry(
        Guid id,
        DateTimeOffset capturedAtUtc,
        ClipboardPayload payload,
        int tag = 0,
        bool isPinned = false)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An entry id cannot be empty.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(payload);

        if (tag is < 0 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(tag), tag, "Tag must be between 0 and 5.");
        }

        Id = id;
        CapturedAtUtc = capturedAtUtc.ToUniversalTime();
        Payload = payload;
        Tag = tag;
        IsPinned = isPinned;
    }

    public Guid Id { get; init; }

    public DateTimeOffset CapturedAtUtc { get; init; }

    public ClipboardPayload Payload { get; init; }

    public int Tag { get; init; }

    public bool IsPinned { get; init; }

    public static ClipboardEntry Create(
        ClipboardPayload payload,
        DateTimeOffset capturedAtUtc,
        int tag = 0,
        bool isPinned = false) =>
        new(Guid.NewGuid(), capturedAtUtc, payload, tag, isPinned);
}
