using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace SuperCV;

/// <summary>
/// A validated, file-backed collection of color themes. Only JSON color tokens are accepted;
/// XAML, styles, and executable content are intentionally out of scope for external themes.
/// </summary>
internal sealed class ThemeCatalog
{
    internal const string DefaultThemeId = "default";
    internal const string ThemesDirectoryName = "Themes";
    private static readonly string[] PresetFileNames =
    [
        "default.json",
        "ocean.json",
        "plum.json",
        "ember.json",
        "aurora.json",
        "terminal.json",
        "monolith.json",
    ];

    private static readonly string[] TokenKeys =
    [
        "Color.Shadow.Surface", "Color.Shadow.Content", "Color.Shadow.Icon",
        "Color.Toggle.TrackOff", "Color.Toggle.TrackOn", "Color.Toggle.TrackOnHover", "Color.Toggle.Thumb", "Color.Toggle.ThumbHover",
        "Color.AiAccent.Start", "Color.AiAccent.Middle", "Color.AiAccent.End",
        "Brush.Surface.Window", "Brush.Surface.Subtle", "Brush.Surface.Raised", "Brush.Text.OnAccent", "Brush.Icon.OnAccent", "Brush.Border.Raised",
        "Brush.Control.FillSubtle", "Brush.Control.FillMuted", "Brush.Control.HoverSubtle", "Brush.Control.Hover", "Brush.Control.FocusSubtle", "Brush.Control.PressedSubtle", "Brush.Control.Pressed", "Brush.Control.Highlight", "Brush.Selection.Range",
        "Brush.Border.Control", "Brush.Border.Soft", "Brush.Border.Muted", "Brush.Border.Focus", "Brush.Separator.Default", "Brush.Separator.Strong", "Brush.ScrollBar.Thumb", "Brush.ScrollBar.ThumbSubtle",
        "Brush.Overlay.Action", "Brush.Overlay.ActionHover",
        "Brush.Text.Primary", "Brush.Text.Secondary", "Brush.Text.Muted", "Brush.Text.Selection", "Brush.Text.Caret", "Brush.Icon.Primary", "Brush.Icon.Muted", "Brush.Icon.Subtle", "Brush.Border.Default", "Brush.Border.Subtle", "Brush.Ai.ProgressTrack",
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    private static readonly Lazy<string> DefaultJson = new(ReadEmbeddedDefaultJson);
    private static readonly Lazy<ThemeDefinition> BuiltInDefault = new(() =>
        ParseDefinition(DefaultJson.Value) ?? throw new InvalidOperationException("内置默认主题无效。"));

    private ThemeCatalog(
        string? directoryPath,
        IReadOnlyList<ThemeDefinition> definitions,
        bool wasRecreatedFromEmpty)
    {
        DirectoryPath = directoryPath;
        Definitions = definitions;
        WasRecreatedFromEmpty = wasRecreatedFromEmpty;
    }

    internal string? DirectoryPath { get; }

    internal IReadOnlyList<ThemeDefinition> Definitions { get; }

    /// <summary>
    /// Indicates that the folder contained no themes and the bundled presets were restored.
    /// Callers must reset any previous selection to the default theme.
    /// </summary>
    internal bool WasRecreatedFromEmpty { get; }

    internal ThemeDefinition Resolve(string? themeId) =>
        Definitions.FirstOrDefault(item => string.Equals(item.Id, themeId, StringComparison.OrdinalIgnoreCase))
        ?? Definitions.First(item => item.Id == DefaultThemeId);

    internal static ThemeCatalog BuiltIn() => new(null, [BuiltInDefault.Value], wasRecreatedFromEmpty: false);

    internal static ThemeCatalog Load(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        string directoryPath = Path.Combine(dataRoot, ThemesDirectoryName);
        bool wasRecreatedFromEmpty = EnsurePresetFiles(directoryPath);
        var definitions = new List<ThemeDefinition> { BuiltInDefault.Value };
        var indexesById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [DefaultThemeId] = 0,
        };

        try
        {
            foreach (string path in Directory.EnumerateFiles(directoryPath, "*.json")
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                ThemeDefinition? definition = TryReadDefinition(path);
                if (definition is null)
                {
                    continue;
                }

                if (indexesById.TryGetValue(definition.Id, out int existingIndex))
                {
                    if (definition.Id == DefaultThemeId &&
                        string.Equals(Path.GetFileName(path), "default.json", StringComparison.OrdinalIgnoreCase))
                    {
                        definitions[existingIndex] = definition;
                    }

                    continue;
                }

                indexesById.Add(definition.Id, definitions.Count);
                definitions.Add(definition);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return new ThemeCatalog(directoryPath, definitions, wasRecreatedFromEmpty);
    }

    private static bool EnsurePresetFiles(string directoryPath)
    {
        try
        {
            bool directoryExisted = Directory.Exists(directoryPath);
            Directory.CreateDirectory(directoryPath);
            bool containedNoThemeFiles = !Directory.EnumerateFiles(directoryPath, "*.json").Any();
            bool wasRecreatedFromEmpty = !directoryExisted || containedNoThemeFiles;
            if (!containedNoThemeFiles)
            {
                return false;
            }

            foreach (string fileName in PresetFileNames)
            {
                File.WriteAllText(Path.Combine(directoryPath, fileName), ReadEmbeddedThemeJson(fileName));
            }

            return wasRecreatedFromEmpty;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }

    private static ThemeDefinition? TryReadDefinition(string path)
    {
        try
        {
            return ParseDefinition(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ThemeDefinition? ParseDefinition(string json)
    {
        try
        {
            ThemeFile? file = JsonSerializer.Deserialize<ThemeFile>(json, SerializerOptions);
            if (file is null || file.SchemaVersion != 1 || !IsValidId(file.Id) ||
                string.IsNullOrWhiteSpace(file.DisplayName?.ZhCn) ||
                string.IsNullOrWhiteSpace(file.DisplayName.EnUs) ||
                !ValidateTokens(file.Light) || !ValidateTokens(file.Dark))
            {
                return null;
            }

            return new ThemeDefinition(
                file.Id.Trim(),
                file.DisplayName.ZhCn.Trim(),
                file.DisplayName.EnUs.Trim(),
                new Dictionary<string, string>(file.Light!, StringComparer.Ordinal),
                new Dictionary<string, string>(file.Dark!, StringComparer.Ordinal));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ValidateTokens(Dictionary<string, string>? tokens)
    {
        if (tokens is null || tokens.Count != TokenKeys.Length ||
            tokens.Keys.Any(key => !TokenKeys.Contains(key, StringComparer.Ordinal)))
        {
            return false;
        }

        foreach (string key in TokenKeys)
        {
            if (!tokens.TryGetValue(key, out string? value) || !TryParseColor(value, out _))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool TryParseColor(string? value, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            if (ColorConverter.ConvertFromString(value.Trim()) is Color parsed)
            {
                color = parsed;
                return true;
            }
        }
        catch (FormatException)
        {
        }

        return false;
    }

    private static bool IsValidId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
        {
            return false;
        }

        string value = id.Trim();
        if (!char.IsAsciiLetterLower(value[0]) && !char.IsDigit(value[0]))
        {
            return false;
        }

        return value.All(character => char.IsAsciiLetterLower(character) || char.IsDigit(character) || character is '-' or '_');
    }

    private static string ReadEmbeddedDefaultJson() => ReadEmbeddedThemeJson("default.json");

    private static string ReadEmbeddedThemeJson(string fileName)
    {
        string resourceName = $"SuperCV.Themes.{Path.GetFileNameWithoutExtension(fileName)}.json";
        using Stream stream = typeof(ThemeCatalog).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("找不到内置默认主题资源。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class ThemeFile
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
        [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
        [JsonPropertyName("displayName")] public ThemeDisplayName? DisplayName { get; init; }
        [JsonPropertyName("light")] public Dictionary<string, string>? Light { get; init; }
        [JsonPropertyName("dark")] public Dictionary<string, string>? Dark { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class ThemeDisplayName
    {
        [JsonPropertyName("zh-CN")] public string ZhCn { get; init; } = string.Empty;
        [JsonPropertyName("en-US")] public string EnUs { get; init; } = string.Empty;
    }
}

internal sealed record ThemeDefinition(string Id, string ChineseName, string EnglishName, IReadOnlyDictionary<string, string> LightTokens, IReadOnlyDictionary<string, string> DarkTokens);

/// <summary>
/// A stable selectable theme. Its display text changes in place when the UI language changes so
/// a bound ComboBox keeps the same selected item instead of rebuilding its ItemsSource.
/// </summary>
public sealed class ThemeOption : System.ComponentModel.INotifyPropertyChanged
{
    private string _chineseName;
    private string _englishName;
    private string _displayName;

    internal ThemeOption(string id, string chineseName, string englishName, bool isEnglish)
    {
        Id = id;
        _chineseName = chineseName;
        _englishName = englishName;
        _displayName = isEnglish ? englishName : chineseName;
    }

    public string Id { get; }

    public string ChineseName => _chineseName;

    public string EnglishName => _englishName;

    public string DisplayName => _displayName;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    internal void Update(string chineseName, string englishName, bool isEnglish)
    {
        _chineseName = chineseName;
        _englishName = englishName;
        SetDisplayLanguage(isEnglish);
    }

    internal void SetDisplayLanguage(bool isEnglish)
    {
        string nextName = isEnglish ? _englishName : _chineseName;
        if (string.Equals(_displayName, nextName, StringComparison.Ordinal))
        {
            return;
        }

        _displayName = nextName;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayName)));
    }
}
