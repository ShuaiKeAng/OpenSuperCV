using SuperCV.Domain.AI;

namespace SuperCV.Domain.Settings;

public enum MainWindowDoubleClickAction
{
    FloatingWindow,
    Minimize,
}

public static class FloatingWindowAutoCollapseDelay
{
    public const int Disabled = 0;

    public static bool IsValid(int value) => value is Disabled or 5 or 10 or 20 or 30;
}

public sealed record AppSettings
{
    public Guid CredentialRevision { get; init; } = Guid.NewGuid();

    public AiProvider AiProvider { get; init; } = AiProvider.DeepSeek;

    public string AiBaseUrl { get; init; } = "https://api.deepseek.com";

    public string AiModelName { get; init; } = "deepseek-v4-flash";

    public AiReasoningEffort AiChatReasoningEffort { get; init; } = AiReasoningEffort.Off;

    public bool EnableAiFeatures { get; init; } = true;

    /// <summary>
    /// Maximum AI input context capacity selected by the user.
    /// Every model request is truncated to this hard budget before it is sent.
    /// </summary>
    public int MaxAiContextTokens { get; init; } = 256 * 1024;

    public bool EnableAdvancedAnimation { get; init; } = true;

    public bool DarkMode { get; init; }

    /// <summary>
    /// The identifier of the selected color theme. Theme files are discovered from the
    /// application's data directory; availability is validated by the presentation layer.
    /// </summary>
    public string ThemeId { get; init; } = "default";

    public int DisplayItems { get; init; } = 4;

    public ClipboardTextSize TextSize { get; init; } = ClipboardTextSize.Medium;

    public double SmoothFactor { get; init; } = 16.0;

    public double MainSurfaceOpacity { get; init; } = 1.0;

    /// <summary>Absolute path to the optional artwork shown behind text entries.</summary>
    public string TextEntryBackgroundImagePath { get; init; } = string.Empty;

    /// <summary>Artwork opacity in whole percent, deliberately capped for legibility.</summary>
    public int TextEntryBackgroundOpacityPercent { get; init; } = 20;

    /// <summary>Relative artwork zoom used by the entry background brush.</summary>
    public double TextEntryBackgroundScale { get; init; } = 1.0;

    /// <summary>Relative horizontal artwork offset, expressed in entry-width units.</summary>
    public double TextEntryBackgroundOffsetX { get; init; }

    /// <summary>Relative vertical artwork offset, expressed in entry-height units.</summary>
    public double TextEntryBackgroundOffsetY { get; init; }

    public bool CanDuplicatePaste { get; init; }

    public bool RemoveOldDuplicateEntriesOnCopy { get; init; }

    public bool EnableImageSupport { get; init; } = true;

    public int MaxHistoryItems { get; init; } = 32;

    public bool PersistentHistoryEnabled { get; init; } = true;

    public bool DeleteConfirm { get; init; } = true;

    public int MaxTokenLength { get; init; } = 5000;

    public string Language { get; init; } = "zh-CN";

    public bool StartWithWindows { get; init; }

    public bool ExitOnClose { get; init; }

    public MainWindowDoubleClickAction MainWindowDoubleClickAction { get; init; } =
        MainWindowDoubleClickAction.FloatingWindow;

    /// <summary>
    /// Number of seconds without mouse activity before an expanded floating-mode window retracts.
    /// A value of zero disables automatic retraction.
    /// </summary>
    public int FloatingWindowAutoCollapseDelaySeconds { get; init; } =
        FloatingWindowAutoCollapseDelay.Disabled;

    public bool InterceptHotkeys { get; init; } = true;

    public bool TakeOverWindowsClipboardShortcut { get; init; } = true;

    /// <summary>
    /// Indicates that the non-modal first-run tour has been shown at least once.
    /// </summary>
    public bool WelcomeTutorialCompleted { get; init; }

    public ShortcutModifiers AbsoluteEntriesModifiers { get; init; } =
        ClipboardShortcutDefaults.AbsoluteEntriesModifiers;

    public ShortcutModifiers VisibleEntriesModifiers { get; init; } =
        ClipboardShortcutDefaults.VisibleEntriesModifiers;

    public ShortcutGesture MoveWindowShortcut { get; init; } =
        ClipboardShortcutDefaults.MoveWindow;

    public ShortcutGesture PasteOlderShortcut { get; init; } =
        ClipboardShortcutDefaults.PasteOlder;

    public ShortcutGesture PasteNewerShortcut { get; init; } =
        ClipboardShortcutDefaults.PasteNewer;

    public string BasePrompt { get; init; } = string.Empty;

    public string AiChatPrompt { get; init; } = string.Empty;

    public int InstructionPresetVersion { get; init; }

    /// <summary>
    /// Built-in instruction IDs explicitly removed by the user.  These are retained separately
    /// from the instruction documents so a later preset upgrade cannot mistake a deletion for a
    /// missing first-run item and recreate it.
    /// </summary>
    public Guid[] DeletedInstructionPresetIds { get; init; } = [];

    public AppSettings Normalize()
    {
        AiProvider provider = Enum.IsDefined(AiProvider) ? AiProvider : AiProvider.DeepSeek;
        AiReasoningEffort reasoningEffort = Enum.IsDefined(AiChatReasoningEffort)
            ? AiChatReasoningEffort
            : AiReasoningEffort.Off;
        MainWindowDoubleClickAction doubleClickAction = Enum.IsDefined(MainWindowDoubleClickAction)
            ? MainWindowDoubleClickAction
            : MainWindowDoubleClickAction.FloatingWindow;
        ClipboardTextSize textSize = Enum.IsDefined(TextSize) ? TextSize : ClipboardTextSize.Medium;
        double surfaceOpacity = double.IsFinite(MainSurfaceOpacity)
            ? Math.Clamp(MainSurfaceOpacity, 0.80, 1.0)
            : 1.0;
        ShortcutGesture moveWindowShortcut = MoveWindowShortcut;
        ShortcutGesture pasteOlderShortcut = PasteOlderShortcut;
        ShortcutGesture pasteNewerShortcut = PasteNewerShortcut;
        ShortcutModifiers absoluteEntriesModifiers = AbsoluteEntriesModifiers;
        ShortcutModifiers visibleEntriesModifiers = VisibleEntriesModifiers;
        if (!ClipboardShortcutDefaults.IsValidConfiguration(
                absoluteEntriesModifiers,
                visibleEntriesModifiers,
                moveWindowShortcut,
                pasteOlderShortcut,
                pasteNewerShortcut))
        {
            absoluteEntriesModifiers = ClipboardShortcutDefaults.AbsoluteEntriesModifiers;
            visibleEntriesModifiers = ClipboardShortcutDefaults.VisibleEntriesModifiers;
            moveWindowShortcut = ClipboardShortcutDefaults.MoveWindow;
            pasteOlderShortcut = ClipboardShortcutDefaults.PasteOlder;
            pasteNewerShortcut = ClipboardShortcutDefaults.PasteNewer;
        }

        return this with
        {
            CredentialRevision = CredentialRevision == Guid.Empty
                ? Guid.NewGuid()
                : CredentialRevision,
            AiProvider = provider,
            AiChatReasoningEffort = reasoningEffort,
            MaxAiContextTokens = NormalizeMaxAiContextTokens(MaxAiContextTokens),
            MainWindowDoubleClickAction = doubleClickAction,
            FloatingWindowAutoCollapseDelaySeconds =
                FloatingWindowAutoCollapseDelay.IsValid(FloatingWindowAutoCollapseDelaySeconds)
                    ? FloatingWindowAutoCollapseDelaySeconds
                    : FloatingWindowAutoCollapseDelay.Disabled,
            AiBaseUrl = (AiBaseUrl ?? string.Empty).Trim(),
            AiModelName = (AiModelName ?? string.Empty).Trim(),
            ThemeId = string.IsNullOrWhiteSpace(ThemeId) ? "default" : ThemeId.Trim(),
            DisplayItems = Math.Clamp(DisplayItems, 3, 6),
            TextSize = textSize,
            SmoothFactor = Math.Clamp(SmoothFactor, 5.0, 40.0),
            MainSurfaceOpacity = Math.Round(
                surfaceOpacity * 20.0,
                MidpointRounding.AwayFromZero) / 20.0,
            TextEntryBackgroundImagePath = (TextEntryBackgroundImagePath ?? string.Empty).Trim(),
            TextEntryBackgroundOpacityPercent = Math.Clamp(
                (int)Math.Round(TextEntryBackgroundOpacityPercent / 5.0, MidpointRounding.AwayFromZero) * 5,
                0,
                75),
            TextEntryBackgroundScale = double.IsFinite(TextEntryBackgroundScale)
                ? Math.Clamp(TextEntryBackgroundScale, 1.0, 3.0)
                : 1.0,
            TextEntryBackgroundOffsetX = double.IsFinite(TextEntryBackgroundOffsetX)
                ? Math.Clamp(TextEntryBackgroundOffsetX, -1.0, 1.0)
                : 0.0,
            TextEntryBackgroundOffsetY = double.IsFinite(TextEntryBackgroundOffsetY)
                ? Math.Clamp(TextEntryBackgroundOffsetY, -1.0, 1.0)
                : 0.0,
            MaxHistoryItems = Math.Clamp(MaxHistoryItems, 8, 128),
            MaxTokenLength = Math.Max(1, MaxTokenLength),
            Language = string.IsNullOrWhiteSpace(Language) ? "zh-CN" : Language.Trim(),
            AbsoluteEntriesModifiers = absoluteEntriesModifiers,
            VisibleEntriesModifiers = visibleEntriesModifiers,
            MoveWindowShortcut = moveWindowShortcut,
            PasteOlderShortcut = pasteOlderShortcut,
            PasteNewerShortcut = pasteNewerShortcut,
            BasePrompt = BasePrompt ?? string.Empty,
            AiChatPrompt = AiChatPrompt ?? string.Empty,
            InstructionPresetVersion = Math.Max(0, InstructionPresetVersion),
            DeletedInstructionPresetIds = (DeletedInstructionPresetIds ?? [])
                .Where(static id => id != Guid.Empty)
                .Distinct()
                .Order()
                .ToArray(),
        };
    }

    private static int NormalizeMaxAiContextTokens(int value) => value switch
    {
        // 32K was supported by older releases. Migrate it to the new minimum
        // instead of silently replacing a user's explicit selection with the default.
        32 * 1024 => 64 * 1024,
        64 * 1024 or
        128 * 1024 or
        256 * 1024 or
        512 * 1024 or
        1024 * 1024 => value,
        _ => 256 * 1024,
    };
}
