using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SuperCV.Application.Ports;
using SuperCV.Domain.Instructions;

namespace SuperCV.Infrastructure.Windows;

public sealed class MarkdownInstructionRepository : IInstructionRepository
{
    private const int MaximumFileNameLabelLength = 48;
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private readonly V2Paths _paths;

    public MarkdownInstructionRepository()
        : this(V2Paths.ForCurrentUser())
    {
    }

    public MarkdownInstructionRepository(string rootDirectory)
        : this(V2Paths.FromRootDirectory(rootDirectory))
    {
    }

    internal MarkdownInstructionRepository(V2Paths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async ValueTask<IReadOnlyList<CustomInstruction>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        SemaphoreSlim gate = FileOperationLocks.ForPath(_paths.InstructionsDirectoryPath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            DeleteLegacyJsonStorage();
            if (!Directory.Exists(_paths.InstructionsDirectoryPath))
            {
                return Array.Empty<CustomInstruction>();
            }

            var instructions = new List<CustomInstruction>();
            foreach (string path in EnumerateManagedMarkdownFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string markdown = await File.ReadAllTextAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                _ = TryGetIdFromFileName(path, out Guid fileId);
                CustomInstruction instruction;
                bool requiresRepair = false;
                try
                {
                    instruction = Parse(markdown);
                    if (instruction.Id != fileId)
                    {
                        instruction = new CustomInstruction(
                            fileId,
                            instruction.Label,
                            instruction.Prompt,
                            instruction.CreatedAtUtc);
                        requiresRepair = true;
                    }
                }
                catch (Exception exception) when (IsRecoverableFormatException(exception))
                {
                    instruction = Recover(
                        markdown,
                        fileId,
                        CreateFallbackLabel(path, fileId),
                        GetFallbackCreatedAtUtc(path),
                        preserveExpectedMetadata: false);
                    requiresRepair = true;
                }

                if (requiresRepair)
                {
                    await WriteDocumentAsync(path, instruction, cancellationToken)
                        .ConfigureAwait(false);
                }

                instructions.Add(instruction);
            }

            return Array.AsReadOnly(instructions.ToArray());
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<CustomInstruction?> RepairDocumentAsync(
        CustomInstruction expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);

        SemaphoreSlim gate = FileOperationLocks.ForPath(_paths.InstructionsDirectoryPath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            string? path = GetDocumentPath(expected.Id);
            if (path is null || !File.Exists(path))
            {
                return null;
            }

            string markdown = await File.ReadAllTextAsync(path, cancellationToken)
                .ConfigureAwait(false);
            CustomInstruction repaired;
            try
            {
                CustomInstruction parsed = Parse(markdown);
                repaired = new CustomInstruction(
                    expected.Id,
                    expected.Label,
                    parsed.Prompt,
                    expected.CreatedAtUtc);
            }
            catch (Exception exception) when (IsRecoverableFormatException(exception))
            {
                repaired = Recover(
                    markdown,
                    expected.Id,
                    expected.Label,
                    expected.CreatedAtUtc,
                    preserveExpectedMetadata: true);
            }

            string canonicalMarkdown = Serialize(repaired);
            if (!string.Equals(markdown, canonicalMarkdown, StringComparison.Ordinal))
            {
                await WriteDocumentAsync(path, repaired, cancellationToken)
                    .ConfigureAwait(false);
            }

            return repaired;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask SaveAsync(
        IReadOnlyList<CustomInstruction> instructions,
        CancellationToken cancellationToken = default)
    {
        CustomInstruction[] snapshot = RepositoryValidation.CreateSnapshot(
            instructions,
            static instruction => instruction.Id,
            nameof(instructions));

        SemaphoreSlim gate = FileOperationLocks.ForPath(_paths.InstructionsDirectoryPath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            DeleteLegacyJsonStorage();
            if (snapshot.Length > 0)
            {
                Directory.CreateDirectory(_paths.InstructionsDirectoryPath);
            }

            var retainedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CustomInstruction instruction in snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = GetFilePath(instruction);
                retainedPaths.Add(Path.GetFullPath(path));
                await WriteDocumentAsync(path, instruction, cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (string existingPath in EnumerateManagedMarkdownFiles())
            {
                if (!retainedPaths.Contains(Path.GetFullPath(existingPath)))
                {
                    File.Delete(existingPath);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public string? GetDocumentPath(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An instruction id cannot be empty.", nameof(id));
        }

        if (!Directory.Exists(_paths.InstructionsDirectoryPath))
        {
            return null;
        }

        string searchPattern = $"*-{id:N}.md";
        return Directory
            .EnumerateFiles(
                _paths.InstructionsDirectoryPath,
                searchPattern,
                SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    internal static string Serialize(CustomInstruction instruction)
    {
        ArgumentNullException.ThrowIfNull(instruction);

        string name = JsonSerializer.Serialize(instruction.Label, MetadataJsonOptions);
        string heading = CreateHeading(instruction.Label);
        return
            "---\n" +
            $"name: {name}\n" +
            $"id: {instruction.Id:D}\n" +
            $"created_at_utc: {instruction.CreatedAtUtc:O}\n" +
            "---\n\n" +
            $"# {heading}\n\n" +
            instruction.Prompt;
    }

    internal static CustomInstruction Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        int position = 0;
        if (markdown.Length > 0 && markdown[0] == '\uFEFF')
        {
            position++;
        }

        string firstLine = ReadRequiredLine(markdown, ref position);
        if (!string.Equals(firstLine, "---", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The document must start with YAML front matter.");
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        while (true)
        {
            string line = ReadRequiredLine(markdown, ref position);
            if (string.Equals(line, "---", StringComparison.Ordinal))
            {
                break;
            }

            int separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
            {
                throw new InvalidDataException("Each metadata line must contain a key and value.");
            }

            string key = line[..separatorIndex].Trim();
            string value = line[(separatorIndex + 1)..].Trim();
            if (!metadata.TryAdd(key, value))
            {
                throw new InvalidDataException($"Metadata key '{key}' is duplicated.");
            }
        }

        string nameValue = GetRequiredMetadata(metadata, "name");
        string name = JsonSerializer.Deserialize<string>(nameValue)
            ?? throw new InvalidDataException("The instruction name cannot be null.");

        string idValue = GetRequiredMetadata(metadata, "id");
        if (!Guid.TryParseExact(idValue, "D", out Guid id) || id == Guid.Empty)
        {
            throw new FormatException("The instruction id is invalid.");
        }

        string createdAtValue = GetRequiredMetadata(metadata, "created_at_utc");
        if (!DateTimeOffset.TryParseExact(
                createdAtValue,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset createdAtUtc))
        {
            throw new FormatException("The instruction creation timestamp is invalid.");
        }

        if (ReadRequiredLine(markdown, ref position).Length != 0)
        {
            throw new InvalidDataException("A blank line must follow the front matter.");
        }

        string expectedHeading = $"# {CreateHeading(name)}";
        string heading = ReadRequiredLine(markdown, ref position);
        if (!string.Equals(heading, expectedHeading, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Markdown title must match the instruction name.");
        }

        if (ReadRequiredLine(markdown, ref position).Length != 0)
        {
            throw new InvalidDataException("A blank line must follow the Markdown title.");
        }

        string prompt = position >= markdown.Length ? string.Empty : markdown[position..];
        return new CustomInstruction(id, name, prompt, createdAtUtc);
    }

    internal static CustomInstruction Recover(
        string markdown,
        Guid fileId,
        string fallbackLabel,
        DateTimeOffset fallbackCreatedAtUtc,
        bool preserveExpectedMetadata)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (fileId == Guid.Empty)
        {
            throw new ArgumentException("An instruction id cannot be empty.", nameof(fileId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackLabel);

        IReadOnlyDictionary<string, string> metadata = ReadLenientMetadata(
            markdown,
            out int contentStart,
            out bool frontMatterWasOpened);
        string prompt = ExtractPrompt(
            markdown,
            contentStart,
            frontMatterWasOpened,
            out string? headingLabel);

        string label = fallbackLabel;
        DateTimeOffset createdAtUtc = fallbackCreatedAtUtc;
        if (!preserveExpectedMetadata)
        {
            string? metadataLabel = TryReadMetadataName(metadata);
            label = !string.IsNullOrWhiteSpace(metadataLabel)
                ? metadataLabel
                : !string.IsNullOrWhiteSpace(headingLabel)
                    ? headingLabel
                    : fallbackLabel;

            if (metadata.TryGetValue("created_at_utc", out string? createdAtValue) &&
                DateTimeOffset.TryParse(
                    createdAtValue,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset parsedCreatedAtUtc))
            {
                createdAtUtc = parsedCreatedAtUtc;
            }
        }

        return new CustomInstruction(fileId, label, prompt, createdAtUtc);
    }

    private static IReadOnlyDictionary<string, string> ReadLenientMetadata(
        string markdown,
        out int contentStart,
        out bool frontMatterWasOpened)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        int position = markdown.Length > 0 && markdown[0] == '\uFEFF' ? 1 : 0;
        int documentStart = position;
        frontMatterWasOpened = false;
        contentStart = documentStart;

        if (!TryReadLine(markdown, ref position, out string firstLine) ||
            !string.Equals(firstLine, "---", StringComparison.Ordinal))
        {
            return metadata;
        }

        frontMatterWasOpened = true;
        while (TryReadLine(markdown, ref position, out string line))
        {
            if (string.Equals(line, "---", StringComparison.Ordinal))
            {
                contentStart = position;
                return metadata;
            }

            int separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
            {
                continue;
            }

            string key = line[..separatorIndex].Trim();
            string value = line[(separatorIndex + 1)..].Trim();
            if (key.Length > 0)
            {
                metadata.TryAdd(key, value);
            }
        }

        contentStart = documentStart;
        return metadata;
    }

    private static string ExtractPrompt(
        string markdown,
        int contentStart,
        bool frontMatterWasOpened,
        out string? headingLabel)
    {
        headingLabel = null;
        int candidateStart = contentStart;
        SkipBlankLines(markdown, ref candidateStart);

        int afterCandidate = candidateStart;
        if (TryReadLine(markdown, ref afterCandidate, out string candidateLine) &&
            candidateLine.StartsWith("# ", StringComparison.Ordinal))
        {
            headingLabel = candidateLine[2..].Trim();
            SkipBlankLines(markdown, ref afterCandidate);
            return afterCandidate >= markdown.Length ? string.Empty : markdown[afterCandidate..];
        }

        if (frontMatterWasOpened && contentStart <= 1)
        {
            int searchPosition = contentStart;
            while (TryReadLine(markdown, ref searchPosition, out string line))
            {
                if (!line.StartsWith("# ", StringComparison.Ordinal))
                {
                    continue;
                }

                headingLabel = line[2..].Trim();
                SkipBlankLines(markdown, ref searchPosition);
                return searchPosition >= markdown.Length
                    ? string.Empty
                    : markdown[searchPosition..];
            }
        }

        return candidateStart >= markdown.Length ? string.Empty : markdown[candidateStart..];
    }

    private static void SkipBlankLines(string text, ref int position)
    {
        while (position < text.Length)
        {
            int lineStart = position;
            if (!TryReadLine(text, ref position, out string line))
            {
                return;
            }

            if (line.Length == 0)
            {
                continue;
            }

            position = lineStart;
            return;
        }
    }

    private static string? TryReadMetadataName(IReadOnlyDictionary<string, string> metadata)
    {
        if (!metadata.TryGetValue("name", out string? value) ||
            string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            string? jsonName = JsonSerializer.Deserialize<string>(value);
            if (!string.IsNullOrWhiteSpace(jsonName))
            {
                return jsonName.Trim();
            }
        }
        catch (JsonException)
        {
        }

        string plainName = value.Trim().Trim('"', '\'').Trim();
        return string.IsNullOrWhiteSpace(plainName) ? null : plainName;
    }

    private IEnumerable<string> EnumerateManagedMarkdownFiles()
    {
        if (!Directory.Exists(_paths.InstructionsDirectoryPath))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(
                _paths.InstructionsDirectoryPath,
                "*.md",
                SearchOption.TopDirectoryOnly)
            .Where(static path => TryGetIdFromFileName(path, out _))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private string GetFilePath(CustomInstruction instruction)
    {
        string label = CreateFileNameLabel(instruction.Label);
        return Path.Combine(
            _paths.InstructionsDirectoryPath,
            $"{label}-{instruction.Id:N}.md");
    }

    private static async Task WriteDocumentAsync(
        string path,
        CustomInstruction instruction,
        CancellationToken cancellationToken)
    {
        byte[] contents = Utf8WithoutBom.GetBytes(Serialize(instruction));
        await DurableFileCommitter.WriteBytesAtomicallyAsync(
                path,
                contents,
                createBackup: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string CreateFallbackLabel(string path, Guid id)
    {
        string fileName = Path.GetFileNameWithoutExtension(path);
        string suffix = $"-{id:N}";
        string label = fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^suffix.Length]
            : fileName;
        label = label.Replace('-', ' ').Trim();
        return string.IsNullOrWhiteSpace(label) ? "未命名指令" : label;
    }

    private static DateTimeOffset GetFallbackCreatedAtUtc(string path)
    {
        DateTime createdAtUtc = File.GetCreationTimeUtc(path);
        return createdAtUtc.Year >= 2000
            ? new DateTimeOffset(createdAtUtc)
            : DateTimeOffset.UtcNow;
    }

    private static bool IsRecoverableFormatException(Exception exception) =>
        exception is InvalidDataException or
            FormatException or
            JsonException or
            ArgumentException;

    private void DeleteLegacyJsonStorage()
    {
        File.Delete(_paths.LegacyInstructionsFilePath);
        File.Delete(DurableFileCommitter.GetBackupPath(_paths.LegacyInstructionsFilePath));

        if (!Directory.Exists(_paths.RootDirectory))
        {
            return;
        }

        foreach (string temporaryPath in Directory.EnumerateFiles(
                     _paths.RootDirectory,
                     $"{V2Paths.LegacyInstructionsFileName}.*.tmp",
                     SearchOption.TopDirectoryOnly))
        {
            File.Delete(temporaryPath);
        }
    }

    private static string GetRequiredMetadata(
        IReadOnlyDictionary<string, string> metadata,
        string key)
    {
        if (!metadata.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Required metadata '{key}' is missing.");
        }

        return value;
    }

    private static string ReadRequiredLine(string text, ref int position)
    {
        if (position >= text.Length)
        {
            throw new InvalidDataException("The Markdown document ended unexpectedly.");
        }

        int lineFeedIndex = text.IndexOf('\n', position);
        if (lineFeedIndex < 0)
        {
            string finalLine = text[position..];
            position = text.Length;
            return finalLine.EndsWith('\r') ? finalLine[..^1] : finalLine;
        }

        int lineEnd = lineFeedIndex;
        if (lineEnd > position && text[lineEnd - 1] == '\r')
        {
            lineEnd--;
        }

        string line = text[position..lineEnd];
        position = lineFeedIndex + 1;
        return line;
    }

    private static bool TryReadLine(string text, ref int position, out string line)
    {
        if (position >= text.Length)
        {
            line = string.Empty;
            return false;
        }

        int lineFeedIndex = text.IndexOf('\n', position);
        if (lineFeedIndex < 0)
        {
            line = text[position..];
            position = text.Length;
        }
        else
        {
            int lineEnd = lineFeedIndex;
            if (lineEnd > position && text[lineEnd - 1] == '\r')
            {
                lineEnd--;
            }

            line = text[position..lineEnd];
            position = lineFeedIndex + 1;
        }

        if (line.EndsWith('\r'))
        {
            line = line[..^1];
        }

        return true;
    }

    private static string CreateHeading(string label)
    {
        string[] words = label
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', words);
    }

    private static string CreateFileNameLabel(string label)
    {
        var builder = new StringBuilder();
        bool previousWasSeparator = false;

        foreach (char character in label.Trim())
        {
            bool isAllowed = char.IsLetterOrDigit(character) || character is '-' or '_';
            if (isAllowed)
            {
                if (builder.Length >= MaximumFileNameLabelLength)
                {
                    break;
                }

                builder.Append(char.ToLowerInvariant(character));
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator && builder.Length > 0)
            {
                builder.Append('-');
                previousWasSeparator = true;
            }
        }

        string result = builder.ToString().Trim('-', '_');
        return string.IsNullOrEmpty(result) ? "instruction" : result;
    }

    private static bool TryGetIdFromFileName(string path, out Guid id)
    {
        id = Guid.Empty;
        string name = Path.GetFileNameWithoutExtension(path);
        int separatorIndex = name.LastIndexOf('-');
        return separatorIndex >= 0 &&
               Guid.TryParseExact(name[(separatorIndex + 1)..], "N", out id) &&
               id != Guid.Empty;
    }
}
