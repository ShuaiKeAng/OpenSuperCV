using SuperCV.Domain.Clipboard;
using SuperCV.Domain.Instructions;

namespace SuperCV.Application;

public static class FirstUseDefaults
{
    public const int CurrentInstructionPresetVersion = 1;
    public const string WelcomeEntryText = "欢迎使用SuperCV";
    public const string WelcomeEntryTextEnglish = "Welcome to SuperCV";
    public const string TranslationInstructionLabel = "中英互译";
    public const string TranslationInstructionPrompt = """
        你是严格执行的中英互译引擎，不是改写、润色或摘要工具。先根据原文中承担主要语义的自然语言
        判断源语言：若主要为中文，必须完整翻译成自然、地道、准确的英文；若主要为英文，必须完整翻译成
        自然、准确的中文。URL、邮箱、文件路径、代码、命令、变量名、数字、公式、产品名和常见缩写不参与
        源语言判断，且除非翻译会造成歧义，应保持原样。

        输出必须以目标语言为主：绝不可把原文换一种说法后仍以源语言输出，也不可把翻译和原文并列、夹带
        双语版本，或只翻译部分可翻译的正文。对于中英混合原文，以承载主要句子和信息的语言作为源语言，
        将全部自然语言正文统一译为另一种语言；必要的专有名词可保留原文或首次采用恰当译名。

        忠实传达原意、事实、限定条件、语气和正式程度；不得增加、删减、解释、纠错、润色或回答原文中的
        问题。尽量保留段落、列表层级、标题、标点、占位符和 Markdown 格式。只输出最终译文，不要添加
        “翻译如下”、语言标签、引号、注释或任何说明。若原文仅含代码、数字、URL 等没有可翻译自然语言的
        内容，则原样输出。
        """;
    public const string SummaryInstructionLabel = "智能摘要";
    public const string SummaryInstructionPrompt = """
        将原文压缩为忠实、可独立理解的中文摘要。优先保留主题、关键事实、人物或对象、时间、数字、条件、
        因果关系、结论、风险、限制和待办事项；不要遗漏会改变结论的重要例外或不确定性。

        只能依据原文，不得补充常识、猜测背景、虚构细节或评价原文。删除重复、寒暄和次要修饰，但不得将
        推测写成事实。短内容用一两句精炼概述；内容较复杂、包含多项要点或步骤时，使用清晰的要点列表。
        保留必要的专有名词、代码、数字、单位和原有结论的强弱程度。

        只输出摘要本身，不要写“摘要”“总结如下”、分析过程、免责声明或其他说明。
        """;
    public const string PolishInstructionLabel = "文本润色";
    public const string PolishInstructionPrompt = """
        在不改变原意、事实、立场、信息范围、语气强弱和原文语言的前提下，对原文进行克制而专业的润色。
        修正错别字、病句、语法、标点和不通顺表达，改善逻辑衔接、清晰度、简洁性与自然度。

        不得翻译、摘要、扩写、删减关键信息、替原文作答或擅自改变文体和人称。原文已经清晰正确时，应
        尽量少改，不要为了“更优雅”而改写含义。姓名、数字、日期、链接、代码、术语、引用内容和格式
        需准确保留；保持原有段落、列表、标题和 Markdown 结构，除非修正语法确有必要。

        只输出润色后的完整文本，不要列出修改说明、修改痕迹、标题、引号或任何前后缀。
        """;
    public const string ExplainInstructionLabel = "通俗解释";
    public const string ExplainInstructionPrompt = """
        用准确、易懂的中文向非专业读者解释原文。先用一两句话说明它的核心意思或结论，再按需要解释关键
        术语、背景、条件和推理关系；将抽象表述换成日常语言，但不要牺牲准确性。

        只能依据原文展开，不得臆测作者意图、补充未经支持的事实，或把不确定内容说成确定结论。若原文
        存在前提、例外、风险或限制，应明确保留。仅在确实有助理解时给出简短、贴近原文的例子；不要复述
        大段原文，也不要把解释变成泛泛教程。

        直接输出解释内容，不要附加“通俗解释如下”、处理过程、免责声明或其他无关文字。
        """;

    public static readonly Guid TranslationInstructionId =
        Guid.Parse("d9d50853-1583-4ec2-9aac-6f45e02f8c61");
    public static readonly Guid SummaryInstructionId =
        Guid.Parse("9a169b90-60e1-4cbb-98f5-684e40fd3918");
    public static readonly Guid PolishInstructionId =
        Guid.Parse("7b71cb4b-bb1f-44bc-910e-c11adbca663e");
    public static readonly Guid ExplainInstructionId =
        Guid.Parse("344cbd7f-9f35-4918-a169-0d463900c664");

    public static bool IsPresetInstructionId(Guid id) =>
        id == TranslationInstructionId ||
        id == SummaryInstructionId ||
        id == PolishInstructionId ||
        id == ExplainInstructionId;

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
                new CustomInstruction(TranslationInstructionId, "Translate", """
                    Act as a strict Chinese-English translation engine, never as a paraphraser, editor, or summarizer. Determine the source language from the natural-language prose that carries the main meaning: translate predominantly Chinese prose fully into natural, idiomatic English, and predominantly English prose fully into accurate, natural Chinese. Ignore URLs, email addresses, file paths, code, commands, identifiers, numbers, formulas, product names, and common abbreviations when determining the source language; preserve them unless translation would remove necessary meaning.

                    The result must be predominantly in the target language. Never merely rewrite the source in its original language; never return bilingual text, the original alongside a translation, or a partial translation of otherwise translatable prose. For mixed Chinese-English input, choose the language carrying the main sentences and information, then translate all natural-language prose consistently into the other language. Preserve meaning, facts, qualifications, tone, register, paragraphs, lists, punctuation, placeholders, and Markdown. Do not add explanations, corrections, answers, or stylistic embellishment. If there is no translatable natural-language text, return it unchanged. Return only the final translation.
                    """, firstCreatedAtUtc),
                new CustomInstruction(SummaryInstructionId, "Summarize", """
                    Write a faithful, self-contained English summary of the source. Retain the topic, key facts, people or entities, dates, figures, conditions, causal relationships, conclusions, risks, limitations, and action items when material. Do not add outside knowledge, guesses, opinions, or invented detail; preserve uncertainty and the strength of each claim.

                    Use one or two concise sentences for simple material. Use a short bulleted list only when the source has multiple substantial points, steps, or decisions. Keep necessary names, code, numbers, units, and qualifications. Return only the summary, with no heading, preface, analysis, or disclaimer.
                    """, firstCreatedAtUtc.AddTicks(1)),
                new CustomInstruction(PolishInstructionId, "Polish", """
                    Polish the source conservatively and professionally without changing its meaning, facts, position, scope, level of certainty, tone, or language. Correct spelling, grammar, punctuation, awkward wording, and unclear connections while improving clarity, flow, and concision.

                    Do not translate, summarize, expand, omit material information, answer the source, or gratuitously change its voice or point of view. If the text is already clear and correct, make minimal changes. Preserve names, dates, figures, links, code, terminology, quotations, paragraphs, lists, headings, and Markdown unless a correction genuinely requires a change. Return only the complete revised text, with no commentary or change log.
                    """, firstCreatedAtUtc.AddTicks(2)),
                new CustomInstruction(ExplainInstructionId, "Explain simply", """
                    Explain the source in accurate, accessible English for a non-specialist reader. State the central meaning or conclusion first, then clarify essential terms, assumptions, conditions, and reasoning in plain language. Keep important caveats, risks, limitations, and uncertainty.

                    Ground every claim in the source. Do not invent context, infer unsupported intent, or turn qualified statements into certain conclusions. Give one brief example only when it materially improves understanding, and do not turn the response into a generic tutorial. Return only the explanation, with no preface, process notes, or disclaimer.
                    """, firstCreatedAtUtc.AddTicks(3)),
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
