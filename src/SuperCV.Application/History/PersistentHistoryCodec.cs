using SuperCV.Domain.Clipboard;

namespace SuperCV.Application.History;

public static class PersistentHistoryCodec
{
    public const string ImagePrefix = "\u001ESuperCV.Image\u001F";

    public static string Encode(ClipboardPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return payload.IsImage
            ? ImagePrefix + payload.ImageLink
            : payload.PrimaryText;
    }

    public static bool TryDecodeImage(string? storedValue, out string imageLink)
    {
        string value = storedValue ?? string.Empty;
        if (value.StartsWith(ImagePrefix, StringComparison.Ordinal))
        {
            imageLink = value[ImagePrefix.Length..];
            return !string.IsNullOrWhiteSpace(imageLink);
        }

        imageLink = string.Empty;
        return false;
    }

    public static string DecodeDisplayValue(string? storedValue) =>
        TryDecodeImage(storedValue, out string imageLink)
            ? imageLink
            : storedValue ?? string.Empty;
}
