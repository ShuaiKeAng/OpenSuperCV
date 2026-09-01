using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SuperCV.Infrastructure.Windows.Platform;

internal static class ClipboardMemory
{
    internal const int DefaultMaximumFormatBytes = 16 * 1024 * 1024;

    private static readonly Encoding AnsiEncoding;
    private static readonly Encoding OemEncoding;
    private static readonly Encoding Utf8Encoding = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false);

    static ClipboardMemory()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        AnsiEncoding = Encoding.GetEncoding(
            checked((int)NativeMethods.GetACP()),
            EncoderFallback.ReplacementFallback,
            DecoderFallback.ReplacementFallback);
        OemEncoding = Encoding.GetEncoding(
            checked((int)NativeMethods.GetOEMCP()),
            EncoderFallback.ReplacementFallback,
            DecoderFallback.ReplacementFallback);
    }

    internal static string ReadText(
        nint handle,
        uint format,
        uint htmlFormat,
        uint rtfFormat,
        int maximumBytes = DefaultMaximumFormatBytes)
    {
        if (handle == nint.Zero)
        {
            return string.Empty;
        }

        nuint nativeSize = NativeMethods.GlobalSize(handle);
        if (nativeSize == 0 || nativeSize > checked((nuint)maximumBytes))
        {
            return string.Empty;
        }

        int size = checked((int)nativeSize);
        nint pointer = NativeMethods.GlobalLock(handle);
        if (pointer == nint.Zero)
        {
            return string.Empty;
        }

        try
        {
            byte[] bytes = GC.AllocateUninitializedArray<byte>(size);
            Marshal.Copy(pointer, bytes, 0, size);

            if (format == NativeMethods.CfUnicodeText)
            {
                int length = FindUnicodeTerminator(bytes);
                return Encoding.Unicode.GetString(bytes, 0, length);
            }

            int byteLength = Array.IndexOf(bytes, (byte)0);
            if (byteLength < 0)
            {
                byteLength = bytes.Length;
            }

            Encoding encoding = format switch
            {
                NativeMethods.CfText => AnsiEncoding,
                NativeMethods.CfOemText => OemEncoding,
                _ when format == htmlFormat => Utf8Encoding,
                _ when format == rtfFormat => AnsiEncoding,
                _ => Utf8Encoding,
            };

            return encoding.GetString(bytes, 0, byteLength);
        }
        finally
        {
            _ = NativeMethods.GlobalUnlock(handle);
        }
    }

    internal static byte[] ReadBytes(
        nint handle,
        int maximumBytes = DefaultMaximumFormatBytes)
    {
        if (handle == nint.Zero)
        {
            return [];
        }

        nuint nativeSize = NativeMethods.GlobalSize(handle);
        if (nativeSize == 0 || nativeSize > checked((nuint)maximumBytes))
        {
            return [];
        }

        int size = checked((int)nativeSize);
        nint pointer = NativeMethods.GlobalLock(handle);
        if (pointer == nint.Zero)
        {
            return [];
        }

        try
        {
            byte[] bytes = GC.AllocateUninitializedArray<byte>(size);
            Marshal.Copy(pointer, bytes, 0, size);
            return bytes;
        }
        finally
        {
            _ = NativeMethods.GlobalUnlock(handle);
        }
    }

    internal static GlobalMemory AllocateText(
        string text,
        uint format,
        uint htmlFormat,
        uint rtfFormat,
        int maximumBytes = DefaultMaximumFormatBytes)
    {
        ArgumentNullException.ThrowIfNull(text);

        Encoding encoding;
        int terminatorBytes;
        if (format == NativeMethods.CfUnicodeText)
        {
            encoding = Encoding.Unicode;
            terminatorBytes = 2;
        }
        else
        {
            encoding = format switch
            {
                NativeMethods.CfText => AnsiEncoding,
                NativeMethods.CfOemText => OemEncoding,
                _ when format == htmlFormat => Utf8Encoding,
                _ when format == rtfFormat => AnsiEncoding,
                _ => Utf8Encoding,
            };
            terminatorBytes = 1;
        }

        byte[] payload = encoding.GetBytes(text);
        int allocationSize = checked(payload.Length + terminatorBytes);
        if (allocationSize > maximumBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(text),
                $"Clipboard payload exceeds the {maximumBytes}-byte per-format limit.");
        }

        nint handle = NativeMethods.GlobalAlloc(
            NativeMethods.GmemMoveable | NativeMethods.GmemZeroInit,
            checked((nuint)allocationSize));
        if (handle == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to allocate clipboard memory.");
        }

        var memory = new GlobalMemory(handle);
        nint pointer = NativeMethods.GlobalLock(handle);
        if (pointer == nint.Zero)
        {
            memory.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to lock clipboard memory.");
        }

        try
        {
            Marshal.Copy(payload, 0, pointer, payload.Length);
        }
        finally
        {
            _ = NativeMethods.GlobalUnlock(handle);
        }

        return memory;
    }

    internal static GlobalMemory AllocateBytes(
        ReadOnlySpan<byte> bytes,
        int maximumBytes = DefaultMaximumFormatBytes)
    {
        if (bytes.IsEmpty)
        {
            throw new ArgumentException("Clipboard binary data cannot be empty.", nameof(bytes));
        }

        if (bytes.Length > maximumBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bytes),
                $"Clipboard payload exceeds the {maximumBytes}-byte per-format limit.");
        }

        nint handle = NativeMethods.GlobalAlloc(
            NativeMethods.GmemMoveable | NativeMethods.GmemZeroInit,
            checked((nuint)bytes.Length));
        if (handle == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to allocate clipboard memory.");
        }

        var memory = new GlobalMemory(handle);
        nint pointer = NativeMethods.GlobalLock(handle);
        if (pointer == nint.Zero)
        {
            memory.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to lock clipboard memory.");
        }

        try
        {
            byte[] copy = bytes.ToArray();
            Marshal.Copy(copy, 0, pointer, copy.Length);
        }
        finally
        {
            _ = NativeMethods.GlobalUnlock(handle);
        }

        return memory;
    }

    private static int FindUnicodeTerminator(byte[] bytes)
    {
        int evenLength = bytes.Length - (bytes.Length % 2);
        for (int index = 0; index + 1 < evenLength; index += 2)
        {
            if (bytes[index] == 0 && bytes[index + 1] == 0)
            {
                return index;
            }
        }

        return evenLength;
    }
}

internal sealed class GlobalMemory : IDisposable
{
    private nint _handle;

    internal GlobalMemory(nint handle)
    {
        _handle = handle;
    }

    internal nint Handle => _handle;

    internal nint TransferOwnership()
    {
        return Interlocked.Exchange(ref _handle, nint.Zero);
    }

    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref _handle, nint.Zero);
        if (handle != nint.Zero)
        {
            _ = NativeMethods.GlobalFree(handle);
        }
    }
}
