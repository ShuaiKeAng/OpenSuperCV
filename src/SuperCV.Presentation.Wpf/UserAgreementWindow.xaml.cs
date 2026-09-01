using System.ComponentModel;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace SuperCV;

/// <summary>
/// Presents the local-use, privacy, AI-provider, and liability terms in the application's display language.
/// </summary>
public partial class UserAgreementWindow : Window, INotifyPropertyChanged
{
    private bool _isEnglish;

    public UserAgreementWindow()
    {
        InitializeComponent();
        _isEnglish = LocalizationService.Current.IsEnglish;
        DataContext = this;
        RefreshContent();
        LocalizationService.Current.LanguageChanged += Localization_LanguageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string WindowTitle => _isEnglish ? "User Agreement" : "用户协议";

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

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

    private void RefreshContent()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WindowTitle)));
        Title = WindowTitle;
        AgreementDocument.Document = CreateDocument(_isEnglish ? EnglishSections : ChineseSections);
        AgreementDocument.ScrollToHome();
    }

    private void Localization_LanguageChanged(object? sender, EventArgs e)
    {
        _isEnglish = LocalizationService.Current.IsEnglish;
        RefreshContent();
    }

    private static FlowDocument CreateDocument(IReadOnlyList<(string Heading, string Body)> sections)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            LineHeight = 21,
            Foreground = (Brush)System.Windows.Application.Current.Resources["Brush.Text.Primary"],
        };

        foreach ((string heading, string body) in sections)
        {
            document.Blocks.Add(new Paragraph(new Run(heading))
            {
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 11, 0, 5),
            });
            document.Blocks.Add(new Paragraph(new Run(body))
            {
                Margin = new Thickness(0, 0, 0, 3),
                TextAlignment = TextAlignment.Left,
            });
        }

        return document;
    }

    private static readonly IReadOnlyList<(string Heading, string Body)> ChineseSections =
    [
        ("1. 协议适用与接受", "欢迎使用 SuperCV。本协议适用于您下载、安装、启动或使用 SuperCV 及其本地剪贴板、工作区、历史记录、书签、自定义指令和 AI 对话功能的行为。继续使用即表示您已阅读、理解并同意本协议；如不同意，请停止使用并删除本软件及其本地数据。未成年人应在监护人同意和指导下使用。"),
        ("2. 本地运行与数据控制", "SuperCV 是以本机运行为核心的软件。除非您主动开启或调用联网功能，软件不会因基础剪贴板管理、工作区、历史、书签或本地指令功能而将内容上传至 SuperCV 运营方。您应负责管理设备、Windows 账户、磁盘加密、备份、数据迁移文件及任何可访问本机数据的第三方程序。共享设备或共享账户可能使他人看到本地内容。"),
        ("3. 本地数据的范围与保存", "软件可能在您选择的位置保存设置、工作区名称、剪贴板条目、文本、图片、书签、AI 指令、迁移文件和用量统计等，以实现历史记录、搜索、同步式迁移和个性化功能。是否启用长期历史、图片支持及保存时长取决于您的设置。删除条目、关闭功能或卸载软件并不必然清除操作系统缓存、备份、迁移文件或其他应用已复制的数据；请按需要自行清理。"),
        ("4. 剪贴板与敏感信息", "启用剪贴板监听后，您复制到 Windows 剪贴板的内容可能被 SuperCV 收纳到当前工作区。该内容可能包括密码、验证码、身份证件、财务信息、医疗信息、商业秘密、源代码或他人的个人信息。请勿在未获得合法授权时收集、保存、导出、共享或提交此类内容；请依据组织政策关闭监听、清除历史或使用隔离工作区。"),
        ("5. AI 服务与跨境传输", "AI 功能需要您自行配置并主动调用第三方 AI 提供商或兼容接口。发送请求时，您输入的提示词、选中的剪贴板上下文、附件或图片、系统/自定义指令以及为生成回复所必需的会话信息，可能被传输至您所选择服务商的服务器并由其处理。服务商的服务器可能位于您所在地区以外。请在发送前审查内容，并确认您拥有发送该数据、进行跨境传输及让服务商处理的合法权限。"),
        ("6. 第三方 AI 提供商条款", "第三方 AI 服务独立于 SuperCV。其隐私政策、数据保留、模型训练、内容审核、地区可用性、费用、服务连续性和安全措施由该服务商决定，且可能随时变更。您应自行阅读并接受所选服务商的条款，并自行承担 API Key、账户、套餐、流量、费用和合规义务。SuperCV 不承诺任何服务商不会保留、使用、披露或以其他方式处理您提交的数据。"),
        ("7. API Key 与网络安全", "请仅将 API Key 配置到您信任的设备和服务端点，并避免将密钥写入剪贴板、截图、迁移文件、共享日志或公开内容。尽管软件会尽力采取合理的本地保护措施，您仍应保护 Windows 登录凭据、设备访问权和备份介质。网络传输可能遭受中断、错误配置、代理、恶意软件、账户泄露或第三方服务漏洞等风险。"),
        ("8. AI 输出与专业建议免责声明", "AI 输出由概率模型生成，可能包含错误、遗漏、偏见、过时信息、侵权风险或不符合您具体情形的建议。AI 输出仅供参考，不构成医疗、法律、财务、投资、税务、就业、安全或其他专业意见，也不应作为高风险、紧急、生命健康、关键基础设施或自动化决策的唯一依据。使用前请进行人工复核、事实核验并在必要时咨询合格专业人士。"),
        ("9. 使用限制与内容责任", "您不得利用本软件或连接的 AI 服务实施违法、侵权、欺诈、骚扰、歧视、绕过安全控制、传播恶意代码、侵犯隐私或违反第三方条款的行为。您对输入、上传、导出、共享和基于输出采取的行动承担全部责任，并应确保拥有必要的授权、同意、权利及合规基础。"),
        ("10. 软件按现状提供与责任限制", "在适用法律允许的最大范围内，SuperCV 按“现状”和“可用”基础提供，不作任何明示或默示保证，包括适销性、特定用途适用性、无侵权、准确性、持续可用性、安全性、无错误或无数据丢失的保证。因使用、无法使用、AI 输出、第三方服务、数据丢失、业务中断或安全事件造成的任何间接、附带、特殊、惩罚性或后果性损失，开发者在法律允许范围内不承担责任。"),
        ("11. 协议更新、终止与适用法律", "我们可在合理范围内更新本协议，以反映功能、法律或服务变化；更新版本将随软件发布或在关于页面展示。您在更新后继续使用即表示接受更新。您可随时停止使用并删除本地数据。若本协议任何条款被认定无效，其余条款仍继续有效。本协议不替代您所在地适用的消费者保护、数据保护或其他强制性法律权利。"),
    ];

    private static readonly IReadOnlyList<(string Heading, string Body)> EnglishSections =
    [
        ("1. Scope and acceptance", "Welcome to SuperCV. These terms apply when you download, install, launch, or use SuperCV and its local clipboard, workspaces, history, bookmarks, custom instructions, and AI chat features. By continuing, you confirm that you have read, understood, and accepted these terms. If you do not agree, stop using the software and remove it and its local data. Minors should use it with a guardian's consent and guidance."),
        ("2. Local operation and data control", "SuperCV is designed to run primarily on your device. Unless you deliberately enable or use a networked feature, basic clipboard management, workspaces, history, bookmarks, and local instructions do not upload content to the SuperCV operator. You are responsible for your device, Windows account, disk encryption, backups, migration files, and any third-party software that can access local data. Shared devices or accounts may expose local content to others."),
        ("3. Local data scope and retention", "To provide history, search, migration, and personalization, the app may save settings, workspace names, clipboard entries, text, images, bookmarks, AI instructions, migration files, and usage statistics in a location you choose. Long-term history, image support, and retention depend on your settings. Deleting an item, disabling a feature, or uninstalling the app may not erase operating-system caches, backups, migration files, or data copied by other applications; clean these separately when needed."),
        ("4. Clipboard and sensitive information", "When clipboard capture is enabled, content copied to the Windows clipboard may be collected in the active workspace. This can include passwords, one-time codes, identity documents, financial or medical information, trade secrets, source code, or another person's personal data. Do not collect, retain, export, share, or submit such information without lawful authorization. Follow your organization's policies and disable capture, clear history, or use an isolated workspace where appropriate."),
        ("5. AI services and cross-border transfers", "AI features require you to configure and deliberately call a third-party AI provider or compatible endpoint. When a request is sent, your prompt, selected clipboard context, attachments or images, system or custom instructions, and session information needed to generate a response may be transmitted to and processed by that provider. Its servers may be outside your region. Review content before sending and ensure that you have the legal authority to send it, transfer it across borders, and permit the provider to process it."),
        ("6. Third-party AI provider terms", "Third-party AI services are independent from SuperCV. Their privacy practices, retention, model training, moderation, regional availability, fees, continuity, and security are determined by those providers and may change. You must read and accept the terms of the provider you choose and remain responsible for your API key, account, plan, usage, charges, and compliance. SuperCV does not promise that a provider will not retain, use, disclose, or otherwise process data you submit."),
        ("7. API keys and network security", "Configure API keys only on devices and endpoints you trust. Avoid placing keys in the clipboard, screenshots, migration files, shared logs, or public materials. Although the app aims to use reasonable local safeguards, you must protect Windows credentials, device access, and backup media. Network use can involve interruption, misconfiguration, proxies, malware, account compromise, or vulnerabilities in third-party services."),
        ("8. AI output and professional-advice disclaimer", "AI output is probabilistic and may be inaccurate, incomplete, biased, outdated, infringing, or unsuitable for your circumstances. It is for general reference only and is not medical, legal, financial, investment, tax, employment, safety, or other professional advice. Do not use it as the sole basis for high-risk, emergency, health-and-safety, critical-infrastructure, or automated decisions. Review outputs, verify facts, and consult qualified professionals when appropriate."),
        ("9. Usage restrictions and content responsibility", "You must not use the software or connected AI services for unlawful, infringing, fraudulent, harassing, discriminatory, privacy-invasive, security-circumventing, malicious-code, or third-party-terms-violating activity. You remain fully responsible for your inputs, uploads, exports, sharing, and actions based on outputs, and must have all required permissions, consent, rights, and compliance grounds."),
        ("10. Software provided as-is and limitation of liability", "To the maximum extent permitted by applicable law, SuperCV is provided on an \"as is\" and \"as available\" basis without express or implied warranties, including merchantability, fitness for a particular purpose, non-infringement, accuracy, availability, security, error-free operation, or freedom from data loss. To the extent permitted by law, the developers are not liable for indirect, incidental, special, punitive, or consequential loss arising from use, inability to use, AI output, third-party services, data loss, business interruption, or security incidents."),
        ("11. Updates, termination, and applicable law", "We may reasonably update these terms to reflect changes in features, law, or services. Updated terms will be released with the software or displayed on the About page; continued use after an update means acceptance. You may stop using the software and remove local data at any time. If any term is held invalid, the remaining terms continue in effect. These terms do not replace mandatory consumer, data-protection, or other legal rights available where you live."),
    ];
}
