using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SuperCV.Infrastructure.Windows.Platform;

internal sealed class WindowsClipboardImageCache
{
    internal const int MaximumImageBytes = 64 * 1024 * 1024;
    private const long MaximumDecodedPixels = 40_000_000;
    private const int VisualHashBufferBytes = 1024 * 1024;

    private readonly string _cacheDirectory;
    private readonly string _cacheDirectoryPrefix;

    internal WindowsClipboardImageCache(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _cacheDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheDirectory));
        _cacheDirectoryPrefix = _cacheDirectory + Path.DirectorySeparatorChar;
    }

    internal string Save(byte[] sourceBytes, bool sourceIsPng)
    {
        ArgumentNullException.ThrowIfNull(sourceBytes);
        if (sourceBytes.Length == 0 || sourceBytes.Length > MaximumImageBytes)
        {
            throw new InvalidDataException("The clipboard image is empty or exceeds the supported size.");
        }

        byte[] pngBytes;
        if (sourceIsPng)
        {
            if (!HasPngSignature(sourceBytes))
            {
                throw new InvalidDataException("The clipboard PNG data is invalid.");
            }

            pngBytes = sourceBytes;
        }
        else
        {
            pngBytes = ConvertDibToPng(sourceBytes);
        }

        // ComputeVisualHash also validates the decoded frame, so a PNG is decoded only once.
        string hash = ComputeVisualHash(pngBytes);
        Directory.CreateDirectory(_cacheDirectory);
        string destination = Path.Combine(_cacheDirectory, $"{hash}.png");
        if (File.Exists(destination))
        {
            return destination;
        }

        string temporary = Path.Combine(_cacheDirectory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(pngBytes);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporary, destination);
            }
            catch (IOException) when (File.Exists(destination))
            {
                File.Delete(temporary);
            }

            return destination;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal bool TrySaveFile(string sourcePath, out string imageLink)
    {
        imageLink = string.Empty;
        try
        {
            var file = new FileInfo(sourcePath);
            if (!file.Exists ||
                file.Length == 0 ||
                file.Length > MaximumImageBytes)
            {
                return false;
            }

            byte[] sourceBytes = File.ReadAllBytes(file.FullName);
            if (sourceBytes.Length == 0 ||
                sourceBytes.Length > MaximumImageBytes)
            {
                return false;
            }

            if (HasPngSignature(sourceBytes))
            {
                imageLink = Save(sourceBytes, sourceIsPng: true);
                return true;
            }

            using var input = new MemoryStream(sourceBytes, writable: false);
            BitmapDecoder decoder = BitmapDecoder.Create(
                input,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnDemand);
            ValidateFrame(decoder);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));
            using var output = new MemoryStream();
            encoder.Save(output);
            if (output.Length > MaximumImageBytes)
            {
                return false;
            }

            imageLink = Save(output.ToArray(), sourceIsPng: true);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                FormatException or
                InvalidOperationException)
        {
            return false;
        }
    }

    internal (byte[] Png, byte[] Dib) ReadClipboardFormats(string imageLink)
    {
        string path = ResolveExistingPath(imageLink);
        byte[] png = File.ReadAllBytes(path);
        _ = ValidatePng(png);

        using var input = new MemoryStream(png, writable: false);
        BitmapDecoder decoder = BitmapDecoder.Create(
            input,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnDemand);
        ValidateFrame(decoder);
        var encoder = new BmpBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));
        using var bmp = new MemoryStream();
        encoder.Save(bmp);
        byte[] bmpBytes = bmp.ToArray();
        if (bmpBytes.Length <= 14 || bmpBytes[0] != (byte)'B' || bmpBytes[1] != (byte)'M')
        {
            throw new InvalidDataException("The cached image could not be converted to a bitmap.");
        }

        return (png, bmpBytes[14..]);
    }

    internal ValueTask DeleteExceptAsync(
        IReadOnlySet<string> retainedImageLinks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retainedImageLinks);
        if (!Directory.Exists(_cacheDirectory))
        {
            return ValueTask.CompletedTask;
        }

        var retained = new HashSet<string>(
            retainedImageLinks
                .Where(link => !string.IsNullOrWhiteSpace(link))
                .Select(TryResolvePath)
                .OfType<string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(_cacheDirectory, "*.png"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!retained.Contains(Path.GetFullPath(file)))
            {
                File.Delete(file);
            }
        }

        return ValueTask.CompletedTask;
    }

    private static byte[] ValidatePng(byte[] bytes)
    {
        if (!HasPngSignature(bytes))
        {
            throw new InvalidDataException("The clipboard PNG data is invalid.");
        }

        using var stream = new MemoryStream(bytes, writable: false);
        BitmapDecoder decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnDemand);
        ValidateFrame(decoder);

        return bytes;
    }

    private static bool HasPngSignature(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        return bytes.Length >= signature.Length &&
               bytes[..signature.Length].SequenceEqual(signature);
    }

    internal static string ComputeVisualHash(byte[] pngBytes)
    {
        using var stream = new MemoryStream(pngBytes, writable: false);
        BitmapDecoder decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnDemand);
        ValidateFrame(decoder);

        BitmapSource pixels = decoder.Frames[0];
        if (pixels.Format != PixelFormats.Bgra32)
        {
            pixels = new FormatConvertedBitmap(
                pixels,
                PixelFormats.Bgra32,
                destinationPalette: null,
                alphaThreshold: 0);
        }

        int stride = checked(pixels.PixelWidth * 4);
        long decodedBytes = checked((long)stride * pixels.PixelHeight);
        int requestedBufferSize = checked((int)Math.Min(
            VisualHashBufferBytes,
            decodedBytes));
        byte[] pixelBytes = ArrayPool<byte>.Shared.Rent(requestedBufferSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> dimensions = stackalloc byte[sizeof(int) * 2];
            BinaryPrimitives.WriteInt32LittleEndian(dimensions, pixels.PixelWidth);
            BinaryPrimitives.WriteInt32LittleEndian(
                dimensions[sizeof(int)..],
                pixels.PixelHeight);
            hash.AppendData(dimensions);

            int bufferCapacity = Math.Min(pixelBytes.Length, VisualHashBufferBytes);
            if (stride <= bufferCapacity)
            {
                int rowsPerChunk = Math.Max(1, bufferCapacity / stride);
                for (int y = 0; y < pixels.PixelHeight; y += rowsPerChunk)
                {
                    int rowCount = Math.Min(rowsPerChunk, pixels.PixelHeight - y);
                    int byteCount = checked(stride * rowCount);
                    pixels.CopyPixels(
                        new System.Windows.Int32Rect(
                            0,
                            y,
                            pixels.PixelWidth,
                            rowCount),
                        pixelBytes,
                        stride,
                        offset: 0);
                    AppendOpaquePixels(hash, pixelBytes, byteCount);
                }
            }
            else
            {
                int pixelsPerChunk = Math.Max(1, bufferCapacity / 4);
                for (int y = 0; y < pixels.PixelHeight; y++)
                {
                    for (int x = 0; x < pixels.PixelWidth; x += pixelsPerChunk)
                    {
                        int pixelCount = Math.Min(pixelsPerChunk, pixels.PixelWidth - x);
                        int byteCount = checked(pixelCount * 4);
                        pixels.CopyPixels(
                            new System.Windows.Int32Rect(x, y, pixelCount, 1),
                            pixelBytes,
                            byteCount,
                            offset: 0);
                        AppendOpaquePixels(hash, pixelBytes, byteCount);
                    }
                }
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixelBytes);
        }
    }

    private static void AppendOpaquePixels(
        IncrementalHash hash,
        byte[] pixelBytes,
        int byteCount)
    {
        // Classic CF_DIB producers frequently discard or normalize alpha while exposing the
        // same RGB pixels as their PNG format. Alpha therefore cannot be part of the stable
        // cross-format clipboard identity; the cached file still retains its original alpha.
        for (int alphaIndex = 3; alphaIndex < byteCount; alphaIndex += 4)
        {
            pixelBytes[alphaIndex] = byte.MaxValue;
        }

        hash.AppendData(pixelBytes.AsSpan(0, byteCount));
    }

    private static byte[] ConvertDibToPng(byte[] dib)
    {
        byte[] bmp = WrapDibAsBitmap(dib);
        using var input = new MemoryStream(bmp, writable: false);
        BitmapDecoder decoder = BitmapDecoder.Create(
            input,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnDemand);
        ValidateFrame(decoder);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static void ValidateFrame(BitmapDecoder decoder)
    {
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException("The clipboard image contains no usable frame.");
        }

        BitmapFrame frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 ||
            frame.PixelHeight <= 0 ||
            (long)frame.PixelWidth * frame.PixelHeight > MaximumDecodedPixels)
        {
            throw new InvalidDataException(
                "The clipboard image dimensions are invalid or exceed the supported limit.");
        }
    }

    private static byte[] WrapDibAsBitmap(byte[] dib)
    {
        if (dib.Length < 40)
        {
            throw new InvalidDataException("The clipboard DIB header is incomplete.");
        }

        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(0, 4));
        if (headerSize < 40 || headerSize > dib.Length)
        {
            throw new InvalidDataException("The clipboard DIB header size is invalid.");
        }

        ushort bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14, 2));
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16, 4));
        uint colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32, 4));
        int masks = headerSize == 40
            ? compression switch
            {
                3 => 12,
                6 => 16,
                _ => 0,
            }
            : 0;
        long paletteEntries = colorsUsed != 0
            ? colorsUsed
            : bitsPerPixel <= 8
                ? 1L << bitsPerPixel
                : 0;
        long pixelOffset = 14L + headerSize + masks + (paletteEntries * 4L);
        long fileSize = 14L + dib.Length;
        if (pixelOffset > fileSize || fileSize > int.MaxValue)
        {
            throw new InvalidDataException("The clipboard DIB layout is invalid.");
        }

        byte[] bmp = GC.AllocateUninitializedArray<byte>(checked((int)fileSize));
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2, 4), checked((uint)fileSize));
        bmp.AsSpan(6, 8).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10, 4), checked((uint)pixelOffset));
        dib.CopyTo(bmp, 14);
        return bmp;
    }

    private string ResolveExistingPath(string imageLink)
    {
        string? path = TryResolvePath(imageLink);
        if (path is null || !File.Exists(path))
        {
            throw new FileNotFoundException("The cached clipboard image is unavailable.", imageLink);
        }

        return path;
    }

    private string? TryResolvePath(string imageLink)
    {
        if (string.IsNullOrWhiteSpace(imageLink))
        {
            return null;
        }

        try
        {
            string path = Path.GetFullPath(imageLink);
            return path.StartsWith(_cacheDirectoryPrefix, StringComparison.OrdinalIgnoreCase)
                ? path
                : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
