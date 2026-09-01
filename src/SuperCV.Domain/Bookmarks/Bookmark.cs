using SuperCV.Domain.Clipboard;

namespace SuperCV.Domain.Bookmarks;

public sealed record Bookmark
{
    public Bookmark(Guid id, string title, ClipboardPayload payload, DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A bookmark id cannot be empty.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.IsEmpty)
        {
            throw new ArgumentException("A bookmark payload cannot be empty.", nameof(payload));
        }

        Id = id;
        Title = title.Trim();
        Payload = payload;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
    }

    public Guid Id { get; init; }

    public string Title { get; init; }

    public ClipboardPayload Payload { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }
}
