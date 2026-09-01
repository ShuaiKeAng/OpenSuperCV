using System.IO;
using System.Globalization;
using System.Text;

namespace SuperCV;

public static class FileNameValidator
{
    private static readonly HashSet<string> ReservedNames = new(
        new[]
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
            "CLOCK$",
        },
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<char> InvalidChars = new(
        Enumerable.Range(0, 32).Select(value => (char)value)
            .Concat(new[] { '"', '<', '>', '|', ':', '*', '?', '\\', '/' }));

    public static bool IsValidFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255)
        {
            return false;
        }

        if (fileName.All(character => character == '.')
            || fileName[0] is ' ' or '.'
            || fileName[^1] is ' ' or '.')
        {
            return false;
        }

        if (fileName.Any(character => character != ' ' && InvalidChars.Contains(character)))
        {
            return false;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        return !ReservedNames.Contains(stem);
    }
}

public enum AIProvider
{
    DeepSeek = 0,
    ChatGLM = 1,
    QianWen = 2,
    Minimax = 3,
    Custom = 4,
}

public static class WordBasedTokenEstimator
{
    private const int UnitsPerToken = 100;
    private const long MaximumTokenUnits = (long)int.MaxValue * UnitsPerToken;

    private enum TextElementKind
    {
        AsciiLetter,
        AsciiDigit,
        HorizontalWhitespace,
        NewLine,
        Han,
        KanaOrHangul,
        OtherWord,
        Punctuation,
        Symbol,
        Other,
    }

    public static int EstimateTokenCount(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int[] elementStarts = StringInfo.ParseCombiningCharacters(text);
        long tokenUnits = 0;
        int elementIndex = 0;
        while (elementIndex < elementStarts.Length)
        {
            TextElementKind kind = GetKind(text, elementStarts[elementIndex]);
            int runEnd = FindRunEnd(text, elementStarts, elementIndex, kind);
            tokenUnits += EstimateRunTokenUnits(text, elementStarts, elementIndex, runEnd, kind);
            if (tokenUnits >= MaximumTokenUnits)
            {
                return int.MaxValue;
            }

            elementIndex = runEnd;
        }

        return ConvertUnitsToTokenCount(tokenUnits);
    }

    public static string TruncateToTokenCount(string? text, int maxTokens = 512)
    {
        if (maxTokens < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTokens),
                maxTokens,
                "Token limit cannot be negative.");
        }

        if (string.IsNullOrEmpty(text) || maxTokens == 0)
        {
            return string.Empty;
        }

        int[] elementStarts = StringInfo.ParseCombiningCharacters(text);
        long tokenUnits = 0;
        int elementIndex = 0;
        while (elementIndex < elementStarts.Length)
        {
            TextElementKind kind = GetKind(text, elementStarts[elementIndex]);
            int runEnd = FindRunEnd(text, elementStarts, elementIndex, kind);
            long runTokenUnits = EstimateRunTokenUnits(
                text,
                elementStarts,
                elementIndex,
                runEnd,
                kind);

            if (ConvertUnitsToTokenCount(tokenUnits + runTokenUnits) <= maxTokens)
            {
                tokenUnits += runTokenUnits;
                elementIndex = runEnd;
                continue;
            }

            int acceptedRunEnd = FindAcceptedRunEnd(
                text,
                elementStarts,
                elementIndex,
                runEnd,
                kind,
                tokenUnits,
                maxTokens);
            int charEnd = acceptedRunEnd < elementStarts.Length
                ? elementStarts[acceptedRunEnd]
                : text.Length;
            return text[..charEnd];
        }

        return text;
    }

    private static int FindAcceptedRunEnd(
        string text,
        int[] elementStarts,
        int runStart,
        int runEnd,
        TextElementKind kind,
        long currentTokenUnits,
        int maxTokens)
    {
        int acceptedEnd = runStart;
        for (int candidateEnd = runStart + 1; candidateEnd <= runEnd; candidateEnd++)
        {
            long candidateTokenUnits = EstimateRunTokenUnits(
                text,
                elementStarts,
                runStart,
                candidateEnd,
                kind);
            if (ConvertUnitsToTokenCount(currentTokenUnits + candidateTokenUnits) > maxTokens)
            {
                break;
            }

            acceptedEnd = candidateEnd;
        }

        return acceptedEnd;
    }

    private static int FindRunEnd(
        string text,
        int[] elementStarts,
        int runStart,
        TextElementKind kind)
    {
        int runEnd = runStart + 1;
        while (runEnd < elementStarts.Length && GetKind(text, elementStarts[runEnd]) == kind)
        {
            runEnd++;
        }

        return runEnd;
    }

    private static long EstimateRunTokenUnits(
        string text,
        int[] elementStarts,
        int runStart,
        int runEnd,
        TextElementKind kind)
    {
        int elementCount = runEnd - runStart;
        int charStart = elementStarts[runStart];
        int charEnd = runEnd < elementStarts.Length ? elementStarts[runEnd] : text.Length;
        int utf8ByteCount = Encoding.UTF8.GetByteCount(text.AsSpan(charStart, charEnd - charStart));

        return kind switch
        {
            // Common short words are normally a single BPE token; longer and uncommon
            // words approach the widely used four-characters-per-token heuristic. The
            // product calibration intentionally applies a two-thirds factor to English.
            TextElementKind.AsciiLetter =>
                DivideRounded(
                    Math.Max(1, (elementCount + 2) / 4) * UnitsPerToken * 2L,
                    3),
            TextElementKind.AsciiDigit =>
                Math.Max(1, (elementCount + 2) / 3) * UnitsPerToken,
            // A single separating space is usually merged into the following token.
            TextElementKind.HorizontalWhitespace => Math.Max(0, elementCount - 1) * 12L,
            TextElementKind.NewLine =>
                CountNewLineTokens(text.AsSpan(charStart, charEnd - charStart)) * UnitsPerToken,
            // Modern multilingual BPE vocabularies contain many common two-character
            // Han tokens, so counting every ideograph as one token materially overstates
            // normal Chinese prose. Kana and Hangul generally merge less aggressively.
            TextElementKind.Han => elementCount * 60L,
            TextElementKind.KanaOrHangul => elementCount * 75L,
            TextElementKind.OtherWord =>
                Math.Max(UnitsPerToken, DivideRounded(utf8ByteCount * 100L, 3)),
            TextElementKind.Punctuation =>
                Math.Max(1, (elementCount + 1) / 2) * UnitsPerToken,
            // Emoji and pictographs commonly occupy multiple byte-pair tokens.
            TextElementKind.Symbol => Math.Max(UnitsPerToken, utf8ByteCount * 40L),
            _ => Math.Max(UnitsPerToken, utf8ByteCount * 25L),
        };
    }

    private static int ConvertUnitsToTokenCount(long tokenUnits)
    {
        if (tokenUnits >= MaximumTokenUnits)
        {
            return int.MaxValue;
        }

        long rounded = (tokenUnits + (UnitsPerToken / 2)) / UnitsPerToken;
        return Math.Max(1, (int)rounded);
    }

    private static long DivideRounded(long dividend, int divisor) =>
        (dividend + (divisor / 2)) / divisor;

    private static int CountNewLineTokens(ReadOnlySpan<char> text)
    {
        int count = 0;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
            }

            count++;
        }

        return count;
    }

    private static TextElementKind GetKind(string text, int charIndex)
    {
        Rune rune = Rune.GetRuneAt(text, charIndex);
        if (rune.Value is '\r' or '\n')
        {
            return TextElementKind.NewLine;
        }

        if (Rune.IsWhiteSpace(rune))
        {
            return TextElementKind.HorizontalWhitespace;
        }

        if (IsHan(rune.Value))
        {
            return TextElementKind.Han;
        }

        if (IsKanaOrHangul(rune.Value))
        {
            return TextElementKind.KanaOrHangul;
        }

        if (rune.IsAscii && Rune.IsLetter(rune))
        {
            return TextElementKind.AsciiLetter;
        }

        if (rune.IsAscii && Rune.IsDigit(rune))
        {
            return TextElementKind.AsciiDigit;
        }

        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        return category switch
        {
            UnicodeCategory.UppercaseLetter or
            UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or
            UnicodeCategory.ModifierLetter or
            UnicodeCategory.OtherLetter or
            UnicodeCategory.DecimalDigitNumber or
            UnicodeCategory.LetterNumber or
            UnicodeCategory.OtherNumber or
            UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.EnclosingMark => TextElementKind.OtherWord,

            UnicodeCategory.ConnectorPunctuation or
            UnicodeCategory.DashPunctuation or
            UnicodeCategory.OpenPunctuation or
            UnicodeCategory.ClosePunctuation or
            UnicodeCategory.InitialQuotePunctuation or
            UnicodeCategory.FinalQuotePunctuation or
            UnicodeCategory.OtherPunctuation => TextElementKind.Punctuation,

            UnicodeCategory.MathSymbol or
            UnicodeCategory.CurrencySymbol or
            UnicodeCategory.ModifierSymbol or
            UnicodeCategory.OtherSymbol => TextElementKind.Symbol,

            _ => TextElementKind.Other,
        };
    }

    private static bool IsHan(int value) =>
        value is >= 0x3400 and <= 0x4DBF or
            >= 0x4E00 and <= 0x9FFF or
            >= 0xF900 and <= 0xFAFF or
            >= 0x20000 and <= 0x2FA1F;

    private static bool IsKanaOrHangul(int value) =>
        value is >= 0x3040 and <= 0x30FF or
            >= 0x31F0 and <= 0x31FF or
            >= 0xAC00 and <= 0xD7AF;
}


