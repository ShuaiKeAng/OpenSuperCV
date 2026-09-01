using System.Buffers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace SuperCV.Infrastructure.Windows;

internal sealed record SseEvent(string Data, string? EventType, string? LastEventId);

internal static class SseParser
{
    internal const int DefaultMaximumLineBytes = 1024 * 1024;
    internal const int DefaultMaximumEventCharacters = 2 * 1024 * 1024;
    internal const int DefaultMaximumStreamCharacters = 16 * 1024 * 1024;
    private const int ReadBufferSize = 8 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static async IAsyncEnumerable<SseEvent> ReadEventsAsync(
        Stream stream,
        int maximumLineBytes = DefaultMaximumLineBytes,
        int maximumEventCharacters = DefaultMaximumEventCharacters,
        int maximumStreamCharacters = DefaultMaximumStreamCharacters,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLineBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEventCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumStreamCharacters);

        var dataLines = new List<string>();
        string? eventType = null;
        string? lastEventId = null;
        int eventCharacters = 0;
        long streamCharacters = 0;

        await foreach (string line in ReadLinesAsync(stream, maximumLineBytes, cancellationToken)
                           .ConfigureAwait(false))
        {
            long lineCharacters = (long)line.Length + 1;
            if (streamCharacters > maximumStreamCharacters - lineCharacters)
            {
                throw new InvalidDataException("The SSE stream exceeded the configured size limit.");
            }

            streamCharacters += lineCharacters;
            if (line.Length == 0)
            {
                if (dataLines.Count > 0)
                {
                    yield return new SseEvent(string.Join('\n', dataLines), eventType, lastEventId);
                }

                dataLines.Clear();
                eventCharacters = 0;
                eventType = null;
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            int separatorIndex = line.IndexOf(':');
            string field;
            string value;
            if (separatorIndex < 0)
            {
                field = line;
                value = string.Empty;
            }
            else
            {
                field = line[..separatorIndex];
                value = line[(separatorIndex + 1)..];
                if (value.StartsWith(' '))
                {
                    value = value[1..];
                }
            }

            switch (field)
            {
                case "data":
                    int separatorCharacters = dataLines.Count == 0 ? 0 : 1;
                    if (eventCharacters > maximumEventCharacters - value.Length - separatorCharacters)
                    {
                        throw new InvalidDataException(
                            "An SSE event exceeded the configured size limit.");
                    }

                    dataLines.Add(value);
                    eventCharacters += value.Length + separatorCharacters;
                    break;
                case "event":
                    eventType = value;
                    break;
                case "id" when !value.Contains('\0'):
                    lastEventId = value;
                    break;
                case "retry":
                    // Reconnection is deliberately owned by the caller. Parsing retry here but
                    // acting on it would risk replaying a non-idempotent completion request.
                    break;
            }
        }

        if (dataLines.Count > 0)
        {
            yield return new SseEvent(string.Join('\n', dataLines), eventType, lastEventId);
        }
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(
        Stream stream,
        int maximumLineBytes,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        byte[] readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        var lineBuffer = new ArrayBufferWriter<byte>();
        bool firstLine = true;

        try
        {
            while (true)
            {
                int bytesRead = await stream.ReadAsync(
                        readBuffer.AsMemory(0, ReadBufferSize),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    break;
                }

                int segmentStart = 0;
                for (int index = 0; index < bytesRead; index++)
                {
                    if (readBuffer[index] != (byte)'\n')
                    {
                        continue;
                    }

                    Append(lineBuffer, readBuffer.AsSpan(segmentStart, index - segmentStart), maximumLineBytes);
                    string line = DecodeLine(lineBuffer.WrittenSpan, firstLine);
                    firstLine = false;
                    lineBuffer.Clear();
                    segmentStart = index + 1;
                    yield return line;
                }

                Append(
                    lineBuffer,
                    readBuffer.AsSpan(segmentStart, bytesRead - segmentStart),
                    maximumLineBytes);
            }

            if (lineBuffer.WrittenCount > 0)
            {
                yield return DecodeLine(lineBuffer.WrittenSpan, firstLine);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(readBuffer);
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    private static void Append(
        ArrayBufferWriter<byte> destination,
        ReadOnlySpan<byte> source,
        int maximumLineBytes)
    {
        if (destination.WrittenCount > maximumLineBytes - source.Length)
        {
            throw new InvalidDataException("An SSE line exceeded the configured size limit.");
        }

        source.CopyTo(destination.GetSpan(source.Length));
        destination.Advance(source.Length);
    }

    private static string DecodeLine(ReadOnlySpan<byte> bytes, bool firstLine)
    {
        if (!bytes.IsEmpty && bytes[^1] == (byte)'\r')
        {
            bytes = bytes[..^1];
        }

        string line = StrictUtf8.GetString(bytes);
        if (firstLine && line.Length > 0 && line[0] == '\uFEFF')
        {
            line = line[1..];
        }

        return line;
    }
}
