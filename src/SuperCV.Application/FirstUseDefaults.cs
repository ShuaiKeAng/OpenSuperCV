using SuperCV.Domain.Clipboard;
using SuperCV.Domain.Instructions;

namespace SuperCV.Application;

public static class FirstUseDefaults
{
    public const int CurrentInstructionPresetVersion = 1;
    public const string WelcomeEntryText = "欢迎使用SuperCV";
    public const string WelcomeEntryTextEnglish = "Welcome to SuperCV";
    public const string TranslationInstructionLabel = "中英互译";
    public const string TranslationInstructionPrompt =
        "如果原文主要为中文，则翻译成自然、准确的英文；如果原文主要为英文，则翻译成自然、准确的中文。" +
        "准确传达原意和语气，并尽量保留原文的段落、标点与格式。仅输出译文。";
    public const string SummaryInstructionLabel = "智能摘要";
    public const string SummaryInstructionPrompt =
        "概括原文的核心信息，保留关键事实、数据、结论和限制条件。" +
        "使用简洁的中文；内容较复杂时采用要点列表。不要添加原文没有的信息，仅输出摘要。";
    public const string PolishInstructionLabel = "文本润色";
    public const string PolishInstructionPrompt =
        "在不改变原意、事实和语气的前提下，修正错别字、语法和标点，" +
        "改善表达的清晰度、流畅度与专业度，并尽量保留原有段落和格式。仅输出润色后的文本。";
    public const string ExplainInstructionLabel = "通俗解释";
    public const string ExplainInstructionPrompt =
        "用清晰、易懂的中文解释原文。先说明核心含义，再解释必要的术语和逻辑；" +
        "可在有帮助时给出简短例子。不要假设读者具备专业背景，也不要引入未经原文支持的结论。";

    public static readonly Guid TranslationInstructionId =
        Guid.Parse("d9d50853-1583-4ec2-9aac-6f45e02f8c61");
    public static readonly Guid SummaryInstructionId =
        Guid.Parse("9a169b90-60e1-4cbb-98f5-684e40fd3918");
    public static readonly Guid PolishInstructionId =
        Guid.Parse("7b71cb4b-bb1f-44bc-910e-c11adbca663e");
    public static readonly Guid ExplainInstructionId =
        Guid.Parse("344cbd7f-9f35-4918-a169-0d463900c664");

    public static ClipboardPayload CreateWelcomePayload(string? language = null) =>
        new(new Dictionary<ClipboardFormat, string>
        {
            [ClipboardFormat.UnicodeText] = IsEnglish(language) ? WelcomeEntryTextEnglish : WelcomeEntryText,
        });

    public static CustomInstruction CreateTranslationInstruction(DateTimeOffset createdAtUtc) =>
        new(
            TranslationInstructionId,
            TranslationInstructionLabel,
            TranslationInstructionPrompt,
            createdAtUtc);

    public static IReadOnlyList<CustomInstruction> CreatePresetInstructions(
        DateTimeOffset createdAtUtc,
        string? language = null)
    {
        DateTimeOffset firstCreatedAtUtc = createdAtUtc.ToUniversalTime();
        if (IsEnglish(language))
        {
            return Array.AsReadOnly(new[]
            {
                new CustomInstruction(TranslationInstructionId, "Translate", "Translate between Chinese and English with natural, idiomatic wording. Preserve meaning, tone, paragraphs, punctuation, and formatting. Return only the translation.", firstCreatedAtUtc),
                new CustomInstruction(SummaryInstructionId, "Summarize", "Summarize the source faithfully. Retain key facts, numbers, conclusions, and limits. Use concise English and bullets only when they improve clarity. Do not add information. Return only the summary.", firstCreatedAtUtc.AddTicks(1)),
                new CustomInstruction(PolishInstructionId, "Polish", "Improve spelling, grammar, punctuation, clarity, and flow without changing meaning, facts, or tone. Preserve useful structure and formatting. Return only the revised text.", firstCreatedAtUtc.AddTicks(2)),
                new CustomInstruction(ExplainInstructionId, "Explain simply", "Explain the source in plain, accessible English. State the main idea first, then clarify essential terms or reasoning. Add a short example only when useful. Do not invent conclusions. Return only the explanation.", firstCreatedAtUtc.AddTicks(3)),
            });
        }

        return Array.AsReadOnly(new[]
        {
            CreateTranslationInstruction(firstCreatedAtUtc),
            new CustomInstruction(
                SummaryInstructionId,
                SummaryInstructionLabel,
                SummaryInstructionPrompt,
                firstCreatedAtUtc.AddTicks(1)),
            new CustomInstruction(
                PolishInstructionId,
                PolishInstructionLabel,
                PolishInstructionPrompt,
                firstCreatedAtUtc.AddTicks(2)),
            new CustomInstruction(
                ExplainInstructionId,
                ExplainInstructionLabel,
                ExplainInstructionPrompt,
                firstCreatedAtUtc.AddTicks(3)),
        });
    }

    private static bool IsEnglish(string? language) =>
        string.Equals(language, "en-US", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
}
