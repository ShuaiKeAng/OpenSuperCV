using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace SuperCV;

/// <summary>
/// Displays the release highlights that ship with the current application version.
/// </summary>
public partial class ReleaseNotesWindow : Window, INotifyPropertyChanged
{
    private const string ReleaseNotesResourceName = "SuperCV.ReleaseNotes.json";
    private static readonly JsonSerializerOptions ReleaseNotesJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private bool _isEnglish;
    private readonly IReadOnlyList<LocalizedReleaseNotes> _releaseNotes;

    public ReleaseNotesWindow()
    {
        InitializeComponent();
        _isEnglish = LocalizationService.Current.IsEnglish;
        _releaseNotes = LoadReleaseNotes();
        DataContext = this;
        RefreshContent();
        LocalizationService.Current.LanguageChanged += Localization_LanguageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string WindowTitle => _isEnglish ? "Release notes" : "更新日志";

    public string CloseButtonText => _isEnglish ? "Close" : "关闭";

    public IReadOnlyList<ReleaseNotesSection> Sections =>
        _releaseNotes.Select(note => note.ToSection(_isEnglish)).ToArray();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        LocalizationService.Current.LanguageChanged -= Localization_LanguageChanged;
        base.OnClosed(e);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Localization_LanguageChanged(object? sender, EventArgs e)
    {
        _isEnglish = LocalizationService.Current.IsEnglish;
        RefreshContent();
    }

    private void RefreshContent()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WindowTitle)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CloseButtonText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Sections)));
        Title = WindowTitle;
        ReleaseNotesScroller.ScrollToTop();
    }

    public sealed record ReleaseNotesSection(
        string Version,
        string Title,
        IReadOnlyList<string> Items);

    private static IReadOnlyList<LocalizedReleaseNotes> LoadReleaseNotes()
    {
        try
        {
            using Stream? stream = typeof(ReleaseNotesWindow).Assembly
                .GetManifestResourceStream(ReleaseNotesResourceName);
            if (stream is null)
            {
                return FallbackReleaseNotes;
            }

            ReleaseNotesFile? file = JsonSerializer.Deserialize<ReleaseNotesFile>(
                stream,
                ReleaseNotesJsonOptions);
            if (file?.SchemaVersion != 1 || file.Releases is null)
            {
                return FallbackReleaseNotes;
            }

            LocalizedReleaseNotes[] notes = file.Releases
                .Where(static release =>
                    !string.IsNullOrWhiteSpace(release.Version) &&
                    IsValidContent(release.Zh) &&
                    IsValidContent(release.En))
                .Select(static release => new LocalizedReleaseNotes(
                    release.Version!,
                    new ReleaseNotesText(
                        release.Zh!.Title!.Trim(),
                        release.Zh.Items!.Where(static item => !string.IsNullOrWhiteSpace(item)).ToArray()),
                    new ReleaseNotesText(
                        release.En!.Title!.Trim(),
                        release.En.Items!.Where(static item => !string.IsNullOrWhiteSpace(item)).ToArray())))
                .ToArray();

            return notes.Length > 0 ? notes : FallbackReleaseNotes;
        }
        catch (JsonException)
        {
            return FallbackReleaseNotes;
        }
        catch (IOException)
        {
            return FallbackReleaseNotes;
        }
    }

    private static bool IsValidContent(ReleaseNotesText? content) =>
        !string.IsNullOrWhiteSpace(content?.Title) &&
        content.Items?.Any(static item => !string.IsNullOrWhiteSpace(item)) == true;

    private sealed record ReleaseNotesFile(
        int SchemaVersion,
        IReadOnlyList<ReleaseNotesDefinition>? Releases);

    private sealed record ReleaseNotesDefinition(
        string? Version,
        ReleaseNotesText? Zh,
        ReleaseNotesText? En);

    private sealed record ReleaseNotesText(
        string? Title,
        IReadOnlyList<string>? Items);

    private sealed record LocalizedReleaseNotes(
        string Version,
        ReleaseNotesText Chinese,
        ReleaseNotesText English)
    {
        public ReleaseNotesSection ToSection(bool isEnglish)
        {
            ReleaseNotesText content = isEnglish ? English : Chinese;
            return new ReleaseNotesSection(
                Version,
                content.Title ?? string.Empty,
                content.Items ?? []);
        }
    }

    private static readonly IReadOnlyList<LocalizedReleaseNotes> FallbackReleaseNotes =
    [
        new(
            SuperCVWindow.ApplicationVersion,
            new ReleaseNotesText("更新日志不可用", ["• 无法读取内置更新日志。"]),
            new ReleaseNotesText("Release notes unavailable", ["• The embedded release notes could not be read."])),
    ];
}
