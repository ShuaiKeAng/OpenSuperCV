using System.Buffers;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuperCV.Domain.Clipboard;

/// <summary>
/// Immutable clipboard content. Long text is stored as Brotli-compressed UTF-8 and expanded only
/// by the operation that needs it. History, bookmarks, and persistence therefore share one memory policy.
/// </summary>
[JsonConverter(typeof(ClipboardPayloadJsonConverter))]
public sealed class ClipboardPayload : IEquatable<ClipboardPayload>
{
    public const int MaximumTextCharactersPerFormat = 1_000_000;
    private const int CompressionThresholdBytes = 4 * 1024;
    private const int FingerprintBufferSize = 16 * 1024;
    private static readonly IReadOnlyDictionary<ClipboardFormat, StoredText> EmptyFormats =
        new ReadOnlyDictionary<ClipboardFormat, StoredText>(new Dictionary<ClipboardFormat, StoredText>());

    private readonly IReadOnlyDictionary<ClipboardFormat, StoredText> _formats;
    private readonly bool _requiresSerializationMigration;
    private readonly Func<IReadOnlyDictionary<ClipboardFormat, string>>? _deferredFormatsProvider;
    private readonly IReadOnlyCollection<ClipboardFormat>? _deferredFormatKeys;

    public ClipboardPayload(IReadOnlyDictionary<ClipboardFormat, string>? formats)
    {
        if (formats is null || formats.Count == 0)
        {
            _formats = EmptyFormats;
            return;
        }

        var copy = new Dictionary<ClipboardFormat, StoredText>();
        foreach ((ClipboardFormat format, string? value) in formats)
        {
            if (!string.IsNullOrEmpty(value))
            {
                copy[format] = StoredText.FromText(format == ClipboardFormat.Image ? value : LimitText(value));
            }
        }

        NormalizeMixedContent(copy);
        _formats = copy.Count == 0 ? EmptyFormats : new ReadOnlyDictionary<ClipboardFormat, StoredText>(copy);
    }

    private ClipboardPayload(IReadOnlyDictionary<ClipboardFormat, StoredText> formats, bool requiresSerializationMigration = false)
    {
        var copy = new Dictionary<ClipboardFormat, StoredText>(formats);
        NormalizeMixedContent(copy);
        _formats = copy.Count == 0 ? EmptyFormats : new ReadOnlyDictionary<ClipboardFormat, StoredText>(copy);
        _requiresSerializationMigration = requiresSerializationMigration;
    }

    private ClipboardPayload(
        Func<IReadOnlyDictionary<ClipboardFormat, string>> deferredFormatsProvider,
        IReadOnlyCollection<ClipboardFormat> formatKeys)
    {
        _deferredFormatsProvider = deferredFormatsProvider;
        _deferredFormatKeys = formatKeys;
        _formats = EmptyFormats;
    }

    public static ClipboardPayload Empty { get; } = new(EmptyFormats);

    /// <summary>Compatibility view for legacy consumers. Prefer <see cref="TryGetText"/> for one format.</summary>
    public IReadOnlyDictionary<ClipboardFormat, string> Formats => DeferredPayload?.Formats ??
        new ReadOnlyDictionary<ClipboardFormat, string>(
            _formats.ToDictionary(pair => pair.Key, pair => pair.Value.GetText()));

    public IReadOnlyCollection<ClipboardFormat> FormatKeys =>
        _deferredFormatKeys ?? _formats.Keys.ToArray();

    public bool IsDeferred => _deferredFormatsProvider is not null;

    public bool IsEmpty => _deferredFormatKeys?.Count == 0 || (_deferredFormatKeys is null && _formats.Count == 0);
    public bool IsImage => _deferredFormatKeys is { Count: 1 }
        ? _deferredFormatKeys.Contains(ClipboardFormat.Image)
        : _formats.Count == 1 && _formats.ContainsKey(ClipboardFormat.Image);
    public bool IsText => !IsEmpty && !IsImage;
    public bool RequiresSerializationMigration => _requiresSerializationMigration;
    public string ImageLink => TryGetText(ClipboardFormat.Image, out string imageLink) ? imageLink : string.Empty;

    public bool TryGetText(ClipboardFormat format, out string text)
    {
        if (DeferredPayload is { } deferred)
        {
            return deferred.TryGetText(format, out text);
        }

        if (_formats.TryGetValue(format, out StoredText? stored))
        {
            text = stored.GetText();
            return true;
        }

        text = string.Empty;
        return false;
    }

    public string PrimaryText
    {
        get
        {
            if (DeferredPayload is { } deferred) return deferred.PrimaryText;
            if (TryGetText(ClipboardFormat.UnicodeText, out string unicodeText)) return unicodeText;
            if (TryGetText(ClipboardFormat.Text, out string text)) return text;
            return string.Join(Environment.NewLine, _formats.Where(pair => pair.Key != ClipboardFormat.Image)
                .OrderBy(pair => pair.Key).Select(pair => pair.Value.GetText())
                .Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal));
        }
    }

    public string ComputeFingerprint()
    {
        if (DeferredPayload is { } deferred) return deferred.ComputeFingerprint();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> integerBuffer = stackalloc byte[sizeof(int)];
        foreach ((ClipboardFormat format, StoredText value) in _formats.OrderBy(pair => pair.Key))
        {
            BinaryPrimitives.WriteInt32LittleEndian(integerBuffer, (int)format);
            hash.AppendData(integerBuffer);
            BinaryPrimitives.WriteInt32LittleEndian(integerBuffer, value.Utf8Length);
            hash.AppendData(integerBuffer);
            value.AppendUtf8To(hash);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public bool Equals(ClipboardPayload? other)
    {
        if (DeferredPayload is { } deferred) return deferred.Equals(other);
        if (other?.DeferredPayload is { } deferredOther) return Equals(deferredOther);
        if (ReferenceEquals(this, other)) return true;
        if (other is null || _formats.Count != other._formats.Count) return false;
        return _formats.All(pair => other._formats.TryGetValue(pair.Key, out StoredText? right) && pair.Value.ContentEquals(right));
    }

    public override bool Equals(object? obj) => Equals(obj as ClipboardPayload);

    public override int GetHashCode()
    {
        if (DeferredPayload is { } deferred) return deferred.GetHashCode();
        var hash = new HashCode();
        foreach ((ClipboardFormat format, StoredText value) in _formats.OrderBy(pair => pair.Key))
        {
            hash.Add(format);
            hash.Add(value.GetText(), StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public IReadOnlyList<ClipboardPayloadStorageFormat> GetStorageFormats() => DeferredPayload?.GetStorageFormats() ?? _formats.OrderBy(pair => pair.Key)
        .Select(pair => pair.Value.ToStorageFormat(pair.Key)).ToArray();

    /// <summary>
    /// Creates a disk-backed payload. The provider must return a fresh immutable format map and
    /// is intentionally invoked per access so scrolling never retains non-visible bodies.
    /// </summary>
    public static ClipboardPayload FromDeferredFormats(
        Func<IReadOnlyDictionary<ClipboardFormat, string>> deferredFormatsProvider,
        IReadOnlyCollection<ClipboardFormat> formatKeys)
    {
        ArgumentNullException.ThrowIfNull(deferredFormatsProvider);
        ArgumentNullException.ThrowIfNull(formatKeys);
        ClipboardFormat[] keys = formatKeys.Distinct().ToArray();
        return keys.Length == 0 ? Empty : new ClipboardPayload(deferredFormatsProvider, keys);
    }

    public static ClipboardPayload FromStorageFormats(
        IEnumerable<ClipboardPayloadStorageFormat>? formats,
        bool requiresSerializationMigration = false)
    {
        if (formats is null) return Empty;
        var values = new Dictionary<ClipboardFormat, StoredText>();
        foreach (ClipboardPayloadStorageFormat format in formats)
        {
            if (format is null) continue;
            StoredText value = format.Brotli is { Length: > 0 }
                ? StoredText.FromCompressed(format.Brotli, format.Utf8Length, format.CharacterLength)
                : StoredText.FromText(format.Format == ClipboardFormat.Image ? format.Text ?? string.Empty : LimitText(format.Text ?? string.Empty));
            if (!value.IsEmpty) values[format.Format] = value;
        }

        return values.Count == 0 ? Empty : new ClipboardPayload(values, requiresSerializationMigration);
    }

    private static string LimitText(string value)
    {
        if (value.Length <= MaximumTextCharactersPerFormat) return value;
        int length = MaximumTextCharactersPerFormat;
        if (char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length])) length--;
        return value[..length];
    }

    private static void NormalizeMixedContent(Dictionary<ClipboardFormat, StoredText> formats)
    {
        if (formats.ContainsKey(ClipboardFormat.Image) && formats.Keys.Any(format => format != ClipboardFormat.Image))
            formats.Remove(ClipboardFormat.Image);
    }

    private ClipboardPayload? DeferredPayload => _deferredFormatsProvider is null
        ? null
        : new ClipboardPayload(_deferredFormatsProvider());

    private sealed class StoredText
    {
        private readonly string? _text;
        private readonly byte[]? _brotli;

        private StoredText(string text)
        {
            _text = text;
            Utf8Length = Encoding.UTF8.GetByteCount(text);
            CharacterLength = text.Length;
        }

        private StoredText(byte[] brotli, int utf8Length, int characterLength)
        {
            _brotli = brotli;
            Utf8Length = utf8Length;
            CharacterLength = characterLength;
        }

        public int Utf8Length { get; }
        public int CharacterLength { get; }
        public bool IsEmpty => Utf8Length == 0;

        public static StoredText FromText(string text)
        {
            int utf8Length = Encoding.UTF8.GetByteCount(text);
            if (utf8Length < CompressionThresholdBytes) return new StoredText(text);

            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            try
            {
                using var output = new MemoryStream();
                using (var stream = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
                    stream.Write(utf8, 0, utf8.Length);
                return new StoredText(output.ToArray(), utf8.Length, text.Length);
            }
            finally
            {
                Array.Clear(utf8, 0, utf8.Length);
            }
        }

        public static StoredText FromCompressed(byte[] brotli, int utf8Length, int characterLength)
        {
            if (utf8Length <= 0 || characterLength < 0) return new StoredText(string.Empty);
            if (characterLength > MaximumTextCharactersPerFormat ||
                utf8Length > MaximumTextCharactersPerFormat * 3)
                throw new InvalidDataException("Compressed clipboard text exceeds the supported limit.");
            return new StoredText(brotli.ToArray(), utf8Length, characterLength);
        }

        public string GetText()
        {
            if (_text is not null) return _text;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(Utf8Length);
            try
            {
                int offset = 0;
                using var source = new MemoryStream(_brotli!, writable: false);
                using var stream = new BrotliStream(source, CompressionMode.Decompress);
                while (offset < Utf8Length)
                {
                    int read = stream.Read(buffer, offset, Utf8Length - offset);
                    if (read == 0) break;
                    offset += read;
                }

                if (offset != Utf8Length || stream.ReadByte() != -1)
                    throw new InvalidDataException("The compressed clipboard text is corrupted.");
                return Encoding.UTF8.GetString(buffer, 0, offset);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }

        public void AppendUtf8To(IncrementalHash hash)
        {
            if (_text is not null)
            {
                AppendTextUtf8(hash, _text);
                return;
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Min(Utf8Length, FingerprintBufferSize));
            try
            {
                using var source = new MemoryStream(_brotli!, writable: false);
                using var stream = new BrotliStream(source, CompressionMode.Decompress);
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer.AsSpan(0, read));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }

        public bool ContentEquals(StoredText other) => Utf8Length == other.Utf8Length && string.Equals(GetText(), other.GetText(), StringComparison.Ordinal);
        public ClipboardPayloadStorageFormat ToStorageFormat(ClipboardFormat format) => new(format, _text, _brotli, Utf8Length, CharacterLength);

        private static void AppendTextUtf8(IncrementalHash hash, string value)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(FingerprintBufferSize);
            try
            {
                Encoder encoder = Encoding.UTF8.GetEncoder();
                ReadOnlySpan<char> remaining = value.AsSpan();
                bool completed;
                do
                {
                    encoder.Convert(remaining, buffer, flush: true, out int usedChars, out int usedBytes, out completed);
                    if (usedBytes > 0) hash.AppendData(buffer.AsSpan(0, usedBytes));
                    remaining = remaining[usedChars..];
                } while (!completed);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }
    }
}

public sealed record ClipboardPayloadStorageFormat(ClipboardFormat Format, string? Text, byte[]? Brotli, int Utf8Length, int CharacterLength);

public sealed class ClipboardPayloadJsonConverter : JsonConverter<ClipboardPayload>
{
    public override ClipboardPayload? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;
        JsonElement formats = root.TryGetProperty("formats", out JsonElement wrapped) ? wrapped : root;
        if (formats.ValueKind != JsonValueKind.Object) throw new JsonException("Clipboard payload formats must be an object.");
        var values = new List<ClipboardPayloadStorageFormat>();
        bool legacy = false;
        foreach (JsonProperty property in formats.EnumerateObject())
        {
            if (!Enum.TryParse(property.Name, ignoreCase: true, out ClipboardFormat format)) continue;
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                legacy = true;
                values.Add(new ClipboardPayloadStorageFormat(format, property.Value.GetString(), null, 0, 0));
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                string? text = property.Value.TryGetProperty("text", out JsonElement textElement) ? textElement.GetString() : null;
                byte[]? brotli = property.Value.TryGetProperty("brotli", out JsonElement brotliElement) ? brotliElement.GetBytesFromBase64() : null;
                int utf8Length = property.Value.TryGetProperty("utf8Length", out JsonElement bytes) ? bytes.GetInt32() : 0;
                int chars = property.Value.TryGetProperty("characterLength", out JsonElement charLength) ? charLength.GetInt32() : 0;
                values.Add(new ClipboardPayloadStorageFormat(format, text, brotli, utf8Length, chars));
            }
        }

        return ClipboardPayload.FromStorageFormats(values, legacy);
    }

    public override void Write(Utf8JsonWriter writer, ClipboardPayload value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("formats");
        writer.WriteStartObject();
        foreach (ClipboardPayloadStorageFormat format in value.GetStorageFormats())
        {
            writer.WritePropertyName(char.ToLowerInvariant(format.Format.ToString()[0]) + format.Format.ToString()[1..]);
            writer.WriteStartObject();
            if (format.Brotli is null)
            {
                writer.WriteString("text", format.Text);
            }
            else
            {
                writer.WriteBase64String("brotli", format.Brotli);
                writer.WriteNumber("utf8Length", format.Utf8Length);
                writer.WriteNumber("characterLength", format.CharacterLength);
            }
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
