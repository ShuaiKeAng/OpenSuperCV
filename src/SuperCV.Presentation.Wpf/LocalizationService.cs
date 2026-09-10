using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SuperCV.Application;
using SuperCV.Application.Settings;
using SuperCV.Domain.Instructions;

namespace SuperCV;

/// <summary>
/// Provides a small, runtime-switchable UI language layer.  Existing Chinese XAML remains the
/// source of truth: the original local value is retained so changing back to Chinese is lossless.
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged, IDisposable
{
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, string>> Originals = new();

    /// <summary>Excludes a stable, explicitly language-switched visual subtree from automatic text localization.</summary>
    public static readonly DependencyProperty SkipLocalizationProperty = DependencyProperty.RegisterAttached(
        "SkipLocalization",
        typeof(bool),
        typeof(LocalizationService),
        new PropertyMetadata(false));

    public static void SetSkipLocalization(DependencyObject element, bool value) =>
        element.SetValue(SkipLocalizationProperty, value);

    public static bool GetSkipLocalization(DependencyObject element) =>
        (bool)element.GetValue(SkipLocalizationProperty);

    private static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["欢迎"] = "Welcome", ["提示"] = "Notice", ["确定"] = "OK", ["取消"] = "Cancel", ["完成"] = "Finish",
        ["上一步"] = "Back", ["下一步"] = "Next", ["最小化"] = "Minimize", ["关闭窗口"] = "Close",
        ["选项"] = "Settings", ["常规"] = "General", ["快捷键"] = "HotKey", ["历史记录"] = "History", ["关于"] = "About",
        ["用户协议"] = "User Agreement", ["查看隐私说明、AI 服务条款与免责声明"] = "Review privacy, AI service terms, and disclaimers",
        ["最小化用户协议"] = "Minimize user agreement", ["关闭用户协议"] = "Close user agreement",
        ["更新日志"] = "Release notes", ["查看版本的功能更新与改进"] = "Review features and improvements",
        ["检查更新"] = "Check for updates", ["检查是否有新版本并打开下载页面"] = "Check for a new version",
        ["下载更新"] = "Download update",
        ["关闭更新日志"] = "Close release notes",
        ["我已阅读并同意"] = "I have read and agree to ",
        ["阅读并同意用户协议"] = "Read and agree to the User Agreement", ["勾选后才能完成初始设置"] = "You must check this before completing setup",
        ["请先阅读并同意用户协议后再完成初始设置。"] = "Please read and agree to the User Agreement before completing initial setup.",
        ["开机自启"] = "Start with Windows", ["登录 Windows 后自动启动 SuperCV"] = "Launch SuperCV after you sign in",
        ["关闭应用直接退出"] = "Exit when closed", ["开启后关闭应用直接退出，否则保留到后台"] = "Exit instead of keeping SuperCV in the tray",
        ["双击多功能按钮行为"] = "Double-click action", ["选择双击主界面左侧按钮时的行为"] = "The main button's double-click action",
        ["浮窗"] = "FloatWin", ["无操作自动缩回浮窗"] = "Auto-collapse floating window", ["无鼠标操作达到时长后自动缩回"] = "Collapse after this idle time",
        ["关闭"] = "Off", ["增强动画效果"] = "Enhanced animation", ["开启可提升动画效果，但可能导致卡顿掉帧"] = "Smoother motion; may reduce performance",
        ["主题"] = "Theme", ["选择应用的配色主题"] = "Choose the application's color theme", ["选择已验证的应用配色主题"] = "Choose a validated application color theme",
        ["显示条目数量"] = "Visible items", ["同屏显示的剪贴板条目数量"] = "Clipboard items shown at once",
        ["剪贴板文字大小"] = "Clipboard text size", ["窗体动画跟随"] = "Window motion response", ["窗体不透明度"] = "Window opacity",
        ["文字条目背景"] = "Text entry background", ["为文字条目添加半透明图片背景"] = "Set background to text entries",
        ["配置背景"] = "Set", ["选择图片、调整不透明度、缩放和位置"] = "Choose an image and adjust opacity, zoom, and position",
        ["配置文字条目背景"] = "Configure text entry background", ["最小化文字条目背景设置"] = "Minimize text entry background settings",
        ["关闭文字条目背景设置"] = "Close text entry background settings",
        ["选择图片后，在预览中拖动位置、使用滚轮缩放。图片会始终覆盖两个虚线框：左侧按钮显示时的区域，以及隐藏按钮后的完整区域。"] = "After choosing an image, drag it in the preview to position it and use the mouse wheel to zoom. The image always covers both dashed outlines: the area with the left-side button visible and the full area with it hidden.",
        ["隐藏侧边按钮后的完整文字区域"] = "Full text area with side buttons hidden",
        ["选择图片"] = "Choose", ["选择背景图片"] = "Choose background image", ["移除背景图片"] = "Remove background image",
        ["尚未选择图片"] = "No image selected", ["不透明度"] = "Opacity", ["背景图片不透明度"] = "Background image opacity",
        ["应用"] = "Apply", ["应用文字条目背景设置"] = "Apply text entry background settings", ["应用文字条目背景失败："] = "Could not apply text entry background: ",
        ["选择文字条目背景图片"] = "Choose text entry background image",
        ["图片文件"] = "Image files", ["所有文件"] = "All files", ["移除"] = "Remove",
        ["删除确认"] = "Confirm deletion", ["开启后删除条目会进行二次确认"] = "Ask before deleting an item",
        ["重复复制时清理旧条目"] = "Replace duplicate copies", ["重复复制相同内容时，仅保留新复制条目"] = "Keep only the newest copy",
        ["支持图片"] = "Image support", ["开启后支持复制图片"] = "Capture copied images", ["AI 功能"] = "AI features",
        ["统一控制所有 AI 功能"] = "Turn all AI features on or off", ["大模型选择"] = "AI provider", ["自定义"] = "Custom",
        ["语言"] = "Language", ["中文"] = "中文", ["English"] = "English", ["显示语言"] = "Display language",
        ["选择 SuperCV 的显示语言"] = "Choose SuperCV's display language", ["深色模式"] = "Dark mode", ["列表更新"] = "Clipboard capture",
        ["展开更多选项"] = "Open settings", ["选择工作区"] = "Choose workspace", ["新建工作区"] = "New workspace", ["新建"] = "New",
        ["重命名工作区"] = "Rename workspace", ["工作区名称"] = "Workspace name",
        ["删除工作区"] = "Delete workspace", ["清空当前工作区"] = "Clear workspace", ["新建空白"] = "New blank item", ["返回顶部"] = "Back to top",
        ["显示条目左侧按钮"] = "Show item actions", ["欢迎使用 SuperCV"] = "Welcome to SuperCV",
        ["按你的习惯配置常用功能，所有选项都可以稍后在“选项”中调整。"] = "Set up the essentials now. You can refine everything later in Settings.",
        ["关闭窗口后直接退出，而不是留在后台"] = "Exit instead of keeping SuperCV in the tray",
        ["请输入 API Key"] = "Enter API key", ["测试连接"] = "Test connection", ["调试成功"] = "Connection successful",
        ["保存"] = "Save", ["恢复默认"] = "Restore defaults", ["导出数据"] = "Export data", ["导入数据"] = "Import data",
        ["帮助与说明"] = "Help and guide", ["打开欢迎页"] = "Open welcome tour", ["退出"] = "Exit",
        ["开始处理请求..."] = "Processing…", ["SuperCV网络连接异常，请稍后重试！"] = "SuperCV could not reach the service. Please try again.",
        ["AI文本处理设置"] = "AI text action", ["请输入标签名"] = "Enter a label", ["请输入提示词"] = "Enter an instruction",
        ["设置书签"] = "Edit bookmark", ["请输入内容"] = "Enter content", ["查找原始字符"] = "Find text", ["替换为"] = "Replace with",
        ["已有重名标签，请重新设置"] = "That label is already in use.", ["标签名含无效字符，请重新设置"] = "The label contains invalid characters.",
        ["功能说明"] = "Feature guide", ["文本搜索框"] = "Search box", ["展开 / 收起"] = "Expand / collapse",
        ["关闭软件"] = "Close SuperCV", ["新建空白条目"] = "New blank item", ["清空工作区"] = "Clear workspace",
        ["AI编辑与问答"] = "AI edit and chat", ["更多菜单"] = "More menu", ["删除条目"] = "Delete item",
        ["切换工作区"] = "Switch workspace", ["收藏条目"] = "Bookmarks", ["收藏当前条目"] = "Bookmark this item",
        ["置顶条目"] = "Pin item", ["导出条目"] = "Export item", ["撤销修改"] = "Undo edit",
        ["转换为纯文本"] = "To plain text", ["查找替换"] = "Replace", ["网络搜索"] = "Web search", ["颜色标记"] = "Color tag",
        ["单击以置顶窗口打开图片"] = "Open image in an always-on-top window", ["提示内容"] = "Message",
        ["3条"] = "3 items", ["4条"] = "4 items", ["5条"] = "5 items", ["6条"] = "6 items",
        ["5秒"] = "5 sec", ["10秒"] = "10 sec", ["20秒"] = "20 sec", ["30秒"] = "30 sec",
        ["小"] = "Small", ["中"] = "Medium", ["大"] = "Large", ["纯文本"] = "Plain text", ["富文本"] = "Rich text",
        ["工作区"] = "Workspace", ["默认工作区"] = "Default", ["持久历史记录"] = "Persistent history",
        ["长久储存"] = "Long-term storage", ["长久储存历史记录"] = "Save long-term history", ["条目最大数量"] = "Item limit",
        ["达到最大数量后，旧的记录将不再保留"] = "Maximum number of entries",
        ["窗体动画灵敏度"] = "Window motion response", ["数值越低越柔和，数值越高越迅捷"] = "Lower is gentler; higher is faster",
        ["调整主界面与剪贴板条目的整体不透明度"] = "Adjust window opacity",
        ["AI 对话助手"] = "AI chat", ["AI对话"] = "AI chat", ["AI驱动的智能剪贴板"] = "AI-powered clipboard",
        ["AI 功能总开关"] = "AI feature master switch", ["统一控制所有AI功能"] = "Control all AI features",
        ["模型名称"] = "Model name", ["API 地址"] = "API address", ["输入当前模型提供商对应的密钥"] = "Enter the key for this provider",
        ["默认厂商自动切换，自定义可直接输入"] = "Custom values are editable",
        ["统一使用 OpenAI 兼容调用格式"] = "OpenAI-compatible request format",
        ["支持 base_url 或完整 chat/completions 地址"] = "Accepts a base URL or full chat/completions endpoint",
        ["最大上下文"] = "Max context", ["当前最大上下文"] = "Current context limit", ["最大上下文档位"] = "Context limit level",
        ["上下文档位"] = "Context level", ["上下文档位调节器"] = "Context level selector", ["可用左右方向键切换档位"] = "Use Left/Right to change level",
        ["可使用左右方向键在各档位之间切换"] = "Use Left/Right to switch between levels", ["思考深度"] = "Reasoning depth",
        ["对话长度达到阈值后会自动压缩"] = "Long conversations will be condensed", ["思考过程"] = "Reasoning activity",
        ["编辑问题"] = "Edit question", ["编辑这条回复对应的问题"] = "Edit the question for this reply", ["重新生成"] = "Regenerate",
        ["发送问题（Enter）"] = "Send question (Enter)", ["发送消息（Enter；Shift+Enter 换行）"] = "Send message (Enter; Shift+Enter for a new line)",
        ["发送消息或终止回答"] = "Send message or stop response", ["输入问题"] = "Enter question", ["输入消息..."] = "Enter message…",
        ["直接问AI"] = "Ask AI", ["模糊搜索"] = "Fuzzy search", ["请求次数"] = "Requests", ["近七天TOKEN 用量信息"] = "Token use in the last seven days",
        ["查找历史条目"] = "Search history", ["历史记录起始日期"] = "History start date", ["历史记录截止日期"] = "History end date",
        ["选择历史记录的起始日期"] = "Choose history start date", ["选择历史记录的截止日期"] = "Choose history end date",
        ["点击日期即可应用"] = "Click a date to apply", ["今天"] = "Today", ["上个月"] = "Last month", ["下个月"] = "Next month",
        ["复制"] = "Copy", ["删除"] = "Del", ["复制历史条目"] = "Copy history item", ["删除历史条目"] = "Delete history item",
        ["右键编辑输入文本"] = "Right-click to edit text", ["搜索"] = "Search",
        ["导出"] = "Export", ["导出与迁移"] = "Export and migration", ["导入配置与内容"] = "Import settings and content",
        ["导出设置与全部内容为单个迁移文件"] = "Export settings and content as one migration file",
        ["导入迁移文件，从中还原配置与条目记录"] = "Import a migration file to restore items",
        ["检查迁移文件后，在重启时导入设置、工作区、历史、书签、指令和图片"] = "Validate then import settings and content after restart",
        ["更改数据存储路径"] = "Change data location", ["迁移全部数据到新的空文件夹"] = "Move all data to a new empty folder",
        ["将全部 SuperCV 数据迁移到一个新的空文件夹，并重启到新路径"] = "Move all SuperCV data to a new empty folder and restart there",
        ["将设置、工作区、历史、书签、指令和图片导出为单个迁移文件"] = "Export settings, workspaces, history, bookmarks, instructions, and images as one file",
        ["将常规、快捷键、历史记录和 AI 设置恢复为默认值"] = "Restore general, shortcut, history, and AI settings",
        ["重置所有设置"] = "Reset all settings", ["打开初次使用时显示的欢迎页"] = "Open the first-run welcome tour",
        ["打开 SuperCV"] = "Open SuperCV", ["打开操作菜单"] = "Open actions menu", ["打开图片"] = "Open image", ["关闭图片预览"] = "Close image preview",
        ["使用 Windows 默认应用打开图片"] = "Open image with the Windows default app", ["使用默认浏览器搜索此条目"] = "Search this item in your default browser",
        ["使用记事本打开指令文件"] = "Open the instruction file in Notepad", ["添加书签"] = "Add bookmark", ["暂无书签"] = "No bookmarks yet",
        ["添加新指令"] = "Add instruction", ["通俗解释"] = "Explain simply", ["智能摘要"] = "Summarize", ["文本润色"] = "Polish", ["中英互译"] = "Translate",
        ["输入内容查找"] = "Find text", ["输入文本后按回车查找"] = "Enter text and press Enter to search",
        ["鼠标右键文本区域可以编辑，鼠标移出后自动保存更改"] = "Right-click text to edit; changes save when the pointer leaves",
        ["单击 / 拖拽内容区域"] = "Click or drag content", ["单击 CV 的内容卡片直接粘贴"] = "Click an item card to paste",
        ["单击此内容直接粘贴；或按住后拖到目标软件。"] = "Click to paste, or hold and drag into a target app.",
        ["单击粘贴前，请先在目标软件中保持需要输入的位置，让键盘输入焦点和光标留在那里。"] = "Before pasting, keep the target app focused with its cursor at the destination.",
        ["目标软件"] = "Target app", ["目标位置保持输入光标"] = "Keep the input cursor at the target", ["输入光标在这里"] = "Input cursor here",
        ["也可把内容卡片拖到支持拖放的目标"] = "You can also drag an item card to a drop target", ["前台顺序"] = "Front order", ["固定序号"] = "Fixed number",
        ["向前粘贴"] = "Paste older", ["向后粘贴"] = "Paste newer", ["向前粘贴快捷键"] = "Paste older shortcut", ["向后粘贴快捷键"] = "Paste newer shortcut",
        ["从上次位置继续粘贴更旧条目"] = "Continue pasting older items", ["从上次位置继续粘贴更新条目"] = "Continue pasting newer items",
        ["从最新粘贴的条目开始往前、后粘贴"] = "Paste earlier or later from the last pasted item", ["粘贴"] = "Paste", ["粘贴最近条目"] = "Paste latest item",
        ["粘贴可见条目"] = "Paste visible item", ["粘贴当前显示在前台的条目"] = "Paste the front visible item",
        ["当前可见条目的修饰键"] = "Visible-item modifier", ["最前面十个条目的修饰键"] = "First-ten-item modifier",
        ["修饰键可改，数字键固定 1 2 ……"] = "Number keys stay fixed", ["修饰键可改，字母键固定 Q W ……"] = "Letter keys stay fixed",
        ["点击后按下新的修饰键组合"] = "Click, then press new modifiers", ["点击后按下新的组合键或鼠标键"] = "Click, then press a key or mouse button",
        ["快捷键拦截"] = "Shortcut interception", ["拦截快捷键"] = "Intercept shortcuts", ["快捷键粘贴"] = "Shortcut paste",
        ["开启后，快捷键不会继续传递"] = "When on, shortcuts are not passed through", ["关闭后快捷键仍会执行，但原按键也会传给当前应用"] = "When off, the shortcut also reaches the active app",
        ["接管 Windows 剪贴板"] = "Take over Windows clipboard", ["接管 Windows 剪贴板快捷键"] = "Take over the Windows clipboard shortcut",
        ["开启后按 Win 加 V 会打开 SuperCV，并阻止 Windows 系统剪贴板弹出"] = "Win + V opens SuperCV instead of the Windows clipboard",
        ["移动主窗口"] = "Move main window", ["移动主窗口快捷键"] = "Move window shortcut", ["将 SuperCV 移到鼠标位置"] = "Move SuperCV to the pointer",
        ["关闭设置"] = "Close settings", ["最小化设置"] = "Minimize settings", ["最小化欢迎页"] = "Minimize welcome", ["最小化 AI 对话助手"] = "Minimize AI chat",
        ["直接关闭应用"] = "Exit app", ["退出 SuperCV"] = "Exit SuperCV", ["停止监听并关闭"] = "Stop capture and close",
        ["关闭 AI 对话助手"] = "Close AI chat", ["关闭时退出应用；关闭此开关时应用会留在系统托盘并继续监听剪贴板"] = "Exit on close; otherwise stay in the tray and keep capturing",
        ["Agent 模式"] = "Agent mode", ["文本"] = "Text", ["图片"] = "Image", ["切换文本与图片"] = "Text or Images", ["图片预览"] = "Image preview", ["电话"] = "Phone", ["电子邮箱"] = "Email",
        ["软件中包括工作区名称、收藏条目、AI指令、文本内容等均通过鼠标右键打开编辑"] = "Right-click workspace names, bookmarks, AI instructions, and text to edit them",
        ["主窗口包含搜索、工作区控制、快捷按钮和收藏栏；下方条目显示剪贴板内容。将鼠标移到说明上查看详细内容。"] = "The main window includes search, workspace, quick actions, and bookmarks. Hover a label for details.",
        ["版本 "] = "Version ", ["SuperCV 欢迎"] = "Welcome to SuperCV", ["SuperCV 软件信息"] = "About SuperCV", ["SuperCV 托盘菜单"] = "SuperCV tray menu", ["SuperCV 标志"] = "SuperCV logo", ["Win + V 打开 SuperCV"] = "Win + V opens SuperCV",
        ["/ai 总结最近的工作"] = "/ai summarize recent work", ["/fs 昨天的python代码"] = "/fs Python code from yesterday",
        ["本周项目排期：周二评审，周五提交最终方案。"] = "This week's plan: review Tuesday; final submission Friday.",
        ["第 1 页"] = "Page 1", ["第 2 页"] = "Page 2", ["第 3 页"] = "Page 3", ["第 4 页"] = "Page 4", ["第 5 页：初始设置"] = "Page 5: setup",
        ["调试"] = "Test", ["刚刚"] = "Just now", ["滚动条目"] = "Scroll items", ["获取"] = "Get", ["清除"] = "Clear",
        ["仅在双击多功能按钮设为浮窗时生效；选择无鼠标操作多久后自动缩回浮窗"] = "Available in floating-window mode; choose when idle time collapses it",
        ["开启后，重复复制相同内容会保留最新条目并删除较早的相同条目"] = "Keep the latest copy and remove older duplicates",
        ["没有可撤销记录时不可用"] = "Unavailable when there is nothing to undo", ["每个工作区独立保留，最多 99,999 条"] = "Stored per workspace, up to 99,999 items",
        ["模糊搜索可以从语义或时间等多维条件筛选条目。\n                                       AI 问答具备Agent功能，可以编辑、筛选条目等。\n                                       AI 指令通过预制提示词，实现自定义的内容转换。"] = "Fuzzy search filters items by meaning, time, and more.\nAI chat can edit and filter entries through its Agent tools.\nAI instructions run reusable, custom text transformations.",
        ["前后切换"] = "Previous / next", ["切换列表更新"] = "Toggle clipboard capture", ["切换为中文"] = "Switch to Chinese",
        ["删除当前条目"] = "Delete current item", ["输出"] = "Output", ["输入"] = "Input", ["双击主按钮行为"] = "Main-button double-click",
        ["显示条目侧边按钮"] = "Show item side actions", ["显示主窗口和剪贴板"] = "Show main window", ["显示字体大小"] = "Display text size",
        ["选择双击左侧 SuperCV 按钮时显示浮窗或最小化到任务栏"] = "Choose floating window or taskbar minimize when double-clicking SuperCV",
        ["移除收藏"] = "Remove bookmark", ["以条目固有序号索引粘贴"] = "Paste by fixed item number", ["以置顶窗口打开图片"] = "Open image always on top",
        ["在目标软件中输入时，可以直接调用快捷键粘贴。下面三组可在“选项 → 快捷键”中调整。"] = "Use shortcuts while typing in a target app. Configure these groups in Settings > Shortcuts.",
        ["在其他软件中复制内容时，SuperCV 会自动监听并收纳到当前工作区。"] = "When you copy in another app, SuperCV captures it in the active workspace.",
        ["置顶"] = "Pin", ["主窗口与条目"] = "Main window and items", ["字体大小会同时改变每项显示的行数"] = "Also changes the number of visible lines",
        ["AI 问答"] = "AI chat", ["示例：/ai 将刚刚复制的这几条内容转为为中文"] = "Example: /ai translate these recent items into Chinese",
        ["示例：/ai 将最新的代码片段组合为完整内容"] = "Example: /ai combine the latest code snippets", ["示例：/ai 这个代码什么意思，和前一条有什么区别"] = "Example: /ai explain this code and compare it with the previous item",
        ["示例：/fs 上周的一个电话号码"] = "Example: /fs a phone number from last week", ["示例：/fs 英文的论文片段"] = "Example: /fs an English paper excerpt",
        ["打开应用设置，可调整快捷键、偏好、AI等配置。该按钮显示为圆环时可以双击最小化或切换浮窗模式。"] = "Open Settings to adjust shortcuts, preferences, and AI options. When this button appears as a ring, double-click it to minimize or switch to floating-window mode.",
        ["输入关键词可过滤当前工作区的条目，快速找到内容。输入“/fs +文本”可以进行模糊搜索，输入“/ai +文本”可以进行AI问答。"] = "Enter keywords to filter the current workspace. Use “/fs + text” for fuzzy search or “/ai + text” to chat with AI.",
        ["切换主窗口的展开状态，在节省窗口空间与更多功能按键间快速切换。可同时改变选项按钮的状态。"] = "Expand or collapse the main window to switch between a compact layout and more controls. This also changes the Settings button state.",
        ["关闭 SuperCV ；如已启用托盘运行，应用仍可在系统托盘中继续使用。"] = "Close SuperCV. If tray mode is enabled, it will remain available from the system tray.",
        ["将当前工作区的条目列表滚动到顶部，快速回到最近的内容。"] = "Scroll the current workspace to the top to return to your most recent items.",
        ["在当前工作区创建一个空白条目，可右键编辑输入内容。"] = "Create a blank item in the current workspace, then right-click it to edit.",
        ["移除当前工作区的所有条目。执行前会要求确认，请仅在内容不再需要时使用。"] = "Remove every item in the current workspace. Confirmation is required; use this only when the content is no longer needed.",
        ["控制是否持续监测剪贴板并将新内容加入当前工作区的列表。"] = "Choose whether SuperCV keeps capturing clipboard changes into the current workspace.",
        ["显示或隐藏每条内容旁的快捷操作按钮。"] = "Show or hide the quick action buttons next to each item.",
        ["切换应用的明暗主题，使界面在不同环境光线下保持舒适易读。"] = "Switch between light and dark themes for comfortable reading in different lighting.",
        ["将当前内容从收藏中移除，确保不需要时才移除内容。"] = "Remove the current item from bookmarks. Only do this when you no longer need it there.",
        ["打开 AI 助手，对当前条目进行改写、整理、翻译或围绕内容继续提问。可以添加自定义的AI指令。"] = "Open the AI assistant to rewrite, organize, translate, or continue asking about this item. You can add custom AI instructions.",
        ["打开当前条目的更多操作菜单，可执行与条目相关的附加管理操作。"] = "Open more actions for the current item, including additional management commands.",
        ["从当前工作区删除这条内容。删除前请确认它不再需要。"] = "Delete this item from the current workspace. Confirm that you no longer need it first.",
        ["将鼠标移到任意内容卡片上，再滚动鼠标滚轮，即可浏览当前工作区中更多的条目。"] = "Point at any item card and use the mouse wheel to browse more items in the current workspace.",
        ["从“默认工作区”下拉控件选择其他工作区；切换后会显示该工作区独立的条目。右键可以编辑每个工作区的名称。"] = "Choose another workspace from the Default workspace menu. Each workspace has its own items, and you can right-click a name to edit it.",
        ["收藏栏保留常用内容。单击条目可快速粘贴，右键可编辑。推拽收藏条目可以改变前后顺序。鼠标滚轮可以左右滚动。"] = "Bookmarks keep frequently used content. Click to paste, right-click to edit, drag to reorder, and use the mouse wheel to scroll sideways.",
        ["将当前条目加入收藏栏，可以设置简称。"] = "Add the current item to bookmarks and give it a short name.",
        ["将当前条目置于列表顶部，方便持续查看或反复使用。"] = "Pin the current item to the top of the list for easy repeated use.",
        ["导出当前条目的内容，用于保存、分享或在其他工具中继续处理。"] = "Export the current item for saving, sharing, or continuing work in another tool.",
        ["撤销当前条目最近的编辑；没有可撤销记录时该操作不可用。"] = "Undo the latest edit to this item. It is unavailable when there is nothing to undo.",
        ["移除当前条目的富文本或其他格式，只保留纯文本内容。"] = "Remove rich text and other formatting from the current item, keeping plain text only.",
        ["在当前条目中查找指定内容，并可将匹配文本批量替换为新内容。"] = "Find content in the current item and replace all matching text with new content.",
        ["使用系统默认浏览器搜索当前条目的文本内容。"] = "Search this item's text using your default browser.",
        ["为当前条目设置颜色标识，帮助在列表中按用途快速辨认内容。"] = "Set a color tag for the current item to identify it more quickly in the list.",
        ["将鼠标移到任一标注导航上，即可查看该按钮的功能说明。"] = "Hover any callout label to see what that control does.",
        ["一"] = "One", ["二"] = "Two", ["三"] = "Three", ["四"] = "Four", ["五"] = "Five", ["六"] = "Six",
        ["Alt + Shift + 2\n                                                             Alt + Shift + W\n                                                             此条目刚刚被粘贴"] = "Alt + Shift + 2\nAlt + Shift + W\nThis item was just pasted",
    };

    // Runtime messages often contain a file name, count, or exception message. Keep their fixed
    // UI language here so the variable data stays intact while the surrounding message is English.
    private static readonly IReadOnlyDictionary<string, string> EnglishFragments =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["当前没有可用于问答的条目。"] = "No items are available for AI chat.",
            ["请输入模糊搜索内容。"] = "Enter a fuzzy-search query.",
            ["未找到相应内容！"] = "No matching content was found.",
            ["模糊搜索失败："] = "Fuzzy search failed: ",
            ["切换剪贴板监听失败："] = "Could not change clipboard capture: ",
            ["确定清空“"] = "Clear ", ["吗？"] = "?", ["确定删除工作区“"] = "Delete workspace ",
            ["新建空白失败："] = "Could not create a blank item: ",
            ["切换工作区失败"] = "Could not switch workspace", ["新建工作区失败"] = "Could not create workspace",
            ["删除工作区失败"] = "Could not delete workspace", ["重命名工作区失败"] = "Could not rename workspace",
            ["是否删除该标签"] = "Delete this tag?", ["当前工作区"] = "Current workspace",
            ["无法更改数据路径"] = "Cannot change data location", ["选择一个空文件夹"] = "Choose an empty folder",
            ["迁移并重启"] = "Migrate and restart", ["数据路径已更改"] = "Data location changed",
            ["导出完成"] = "Export complete", ["导入已准备"] = "Import ready", ["操作失败"] = "Operation failed",
            ["验证并导入"] = "Validate and import", ["立即重启"] = "Restart now",
            ["重置设置失败："] = "Could not reset settings: ", ["自动应用设置失败："] = "Could not apply settings: ",
            ["加载历史记录失败"] = "Could not load history", ["切换长久储存失败"] = "Could not change long-term storage",
            ["切换图片支持失败："] = "Could not change image support: ", ["查找历史记录失败"] = "Could not search history",
            ["按日期筛选历史记录失败"] = "Could not filter history by date", ["加载更多历史记录失败"] = "Could not load more history",
            ["删除历史记录失败"] = "Could not delete history", ["刷新历史记录失败"] = "Could not refresh history",
            ["切换工作区历史失败"] = "Could not switch workspace history", ["调试成功，当前 AI 配置可以正常连通。"] = "Connection successful. Your AI configuration is working.",
            ["调试失败："] = "Connection test failed: ", ["未知错误"] = "Unknown error", ["请按修饰键…"] = "Press modifier keys…",
            ["请按快捷键…"] = "Press a shortcut…", ["只需按修饰键…"] = "Press modifier keys only…",
            ["读取指令失败: "] = "Could not read instructions: ", ["保存失败: "] = "Could not save: ",
            ["确定删除\""] = "Delete \"", ["开始处理请求..."] = "Processing…", ["网络连接异常，请重试"] = "Network error. Please try again.",
            ["打开指令文件失败: "] = "Could not open instruction file: ", ["调整书签顺序失败"] = "Could not reorder bookmarks",
            ["添加书签失败"] = "Could not add bookmark", ["修改书签失败"] = "Could not update bookmark",
            ["粘贴书签失败"] = "Could not paste bookmark", ["删除书签失败"] = "Could not delete bookmark",
            ["确定删除吗？"] = "Delete this item?", ["打开图片失败: "] = "Could not open image: ",
            ["内容过长，请删减重试"] = "The content is too long. Shorten it and try again.", ["无法启动默认浏览器: "] = "Could not open the default browser: ",
            ["快捷键应用失败："] = "Could not apply shortcut: ", ["快捷键粘贴失败："] = "Shortcut paste failed: ",
            ["关闭列表更新"] = "Pause clipboard capture", ["打开列表更新"] = "Resume clipboard capture",
            ["正在监听内容更新"] = "Capturing updates", ["剪贴板监听已暂停"] = "Capture is paused",
            ["监听中"] = "Capturing", ["已暂停"] = "Paused", ["打开历史记录后按需加载"] = "Open History to load items",
            ["正在加载…"] = "Loading…", ["当前工作区暂无持久历史"] = "No persistent history in this workspace",
            ["未找到匹配条目"] = "No matching items", ["向下滚动继续加载"] = "Scroll down to load more",
            ["共 "] = "Total: ", [" 条"] = " items", ["刚刚"] = "Just now", ["分钟前"] = " min ago", ["小时前"] = " hr ago",
            ["未设置"] = "Not set", ["数字键盘 "] = "Numpad ", ["鼠标左键"] = "LMB",
            ["鼠标右键"] = "RMB", ["鼠标中键"] = "MMB", ["鼠标侧键 1"] = "Mouse 4",
            ["鼠标侧键 2"] = "Mouse 5", ["鼠标键"] = "Mouse",
            ["思考：关闭"] = "Reasoning: Off", ["思考：较弱"] = "Reasoning: Low", ["思考：较高"] = "Reasoning: High", ["思考：最高"] = "Reasoning: Max",
            ["直接回答；自定义接口不支持参数时也会自动回退"] = "Reply directly; custom endpoints fall back automatically",
            ["较短思考，优先速度与较低用量"] = "Short reasoning for speed and lower usage", ["更充分思考，兼顾质量与耗时"] = "More reasoning for quality and time",
            ["使用服务商允许的最高思考强度"] = "Use the provider's maximum reasoning level", ["Agent：只读"] = "Agent: Read only",
            ["Agent：审核批准"] = "Agent: Ask approval", ["Agent：完全访问"] = "Agent: Full access",
            ["允许读取、筛选与定位，不允许修改条目"] = "Can read, filter, and locate items; cannot change them",
            ["每次写入、删除或标记前复用确认弹窗"] = "Ask before every write, delete, or tag change", ["允许 Agent 直接修改当前工作区条目"] = "Allow Agent to change items directly",
            ["原始内容"] = "Source", ["我"] = "You", ["AI助手"] = "AI assistant", ["重试"] = "Retry",
            ["AI 请求失败，请检查配置后重试"] = "AI request failed. Check the configuration and try again.",
            ["网络连接异常。是否重新生成这条回答？"] = "Network error. Generate this response again?", ["AI 思考"] = "AI reasoning",
            ["【连接恢复】"] = "[Connection recovery]", ["连接中断，正在重试（"] = "Connection interrupted; retrying (",
            ["工具"] = "Tool", ["上下文压缩"] = "Context compression", ["Agent 请求执行："] = "Agent requests: ",
            ["是否批准本次操作？"] = "Approve this action?", ["已复制到系统剪贴板，但未能新增到 SuperCV，请稍后重试。"] = "Copied to the system clipboard, but SuperCV could not add it. Please try again.",
            ["复制失败，剪贴板正被其他程序占用，请稍后重试。"] = "Copy failed because another application is using the clipboard. Please try again.",
            ["Agent 执行过程"] = "Agent activity", ["第 "] = "Step ", [" 轮"] = "", ["输出未完成"] = "response incomplete",
            ["思考过程"] = "Reasoning", ["较弱"] = "Low", ["较高"] = "High", ["最高"] = "Max", ["生成中"] = "generating",
            ["字符"] = "chars", ["红色"] = "Red", ["橙色"] = "Orange", ["紫色"] = "Purple", ["绿色"] = "Green", ["蓝色"] = "Blue", ["无标记"] = "None",
            ["确定将所有设置恢复为默认值吗？\n快捷键、AI 配置和 API Key 等都会重置。"] = "Restore all settings to their defaults?\nShortcuts, AI configuration, and API keys will be reset.",
            ["关闭后将清空超出条目上限的记录。\n是否继续？"] = "Turning this off removes items over the limit. Continue?",
            ["关闭会清除所有工作区中的图片条目。\n是否继续？"] = "Turning this off removes image items from every workspace. Continue?",
            ["请先输入 API Key；仅本机回环地址可以留空。"] = "Enter an API key. Only local loopback endpoints may leave it blank.",
            ["自定义模型请先输入 API 地址。"] = "Enter the API address for the custom model.", ["自定义模型请先输入模型名称。"] = "Enter the custom model name.",
            ["导出 SuperCV 数据"] = "Export SuperCV data", ["导入 SuperCV 数据"] = "Import SuperCV data", ["SuperCV 迁移文件"] = "SuperCV migration file",
            ["当前数据路径由环境变量指定。请先移除 SUPERCV_DATA_ROOT（或兼容旧变量 SUPERCV_V2_DATA_ROOT），再使用此功能。"] =
                "The current data location is set by an environment variable. Remove SUPERCV_DATA_ROOT (or the legacy SUPERCV_V2_DATA_ROOT) before changing it here.",
            ["将把设置、工作区、历史、书签、指令、图片和 AI 用量迁移到目标目录。\n\n目标文件夹必须为空。迁移验证完成后，SuperCV 会立即重启并从新路径继续运行；确认新路径可用后会删除原数据目录。"] =
                "Settings, workspaces, history, bookmarks, instructions, images, and AI usage will be migrated to the destination.\n\nThe destination folder must be empty. After verification, SuperCV will restart at the new location; once it is confirmed available, the original data directory will be removed.",
            ["已导出为单个迁移文件。请妥善保存，该文件可能包含你的剪贴板内容与 AI 配置。"] =
                "Exported as one migration file. Keep it secure: it may contain clipboard content and AI configuration.",
            ["导入会在文件完整性、版本和内容结构全部通过验证后，于重启时替换当前设置和内容。\n\n导入前的数据会保留一份本地回滚副本。跨 Windows 用户或电脑迁移时，AI API Key 受 Windows 保护，可能需要重新填写。"] =
                "After integrity, version, and content checks pass, the import will replace current settings and content when SuperCV restarts.\n\nA local rollback copy of the existing data will be kept. When moving between Windows users or computers, the AI API key may need to be entered again because it is protected by Windows.",
            ["迁移文件已通过校验；请退出并重新打开 SuperCV 以完成导入。"] =
                "The migration file is verified. Exit and reopen SuperCV to complete the import.",
            ["已验证并迁移 "] = "Verified and migrated ", [" 个数据文件。"] = " data files.",
            ["SuperCV 将立即重启并使用新路径，同时删除原数据目录。"] =
                "SuperCV will now restart at the new location and remove the original data directory.",
            ["迁移文件已通过校验（创建于 "] = "Migration file verified (created ",
            ["，包含 "] = ", containing ", [" 个文件）。"] = " files).",
            ["SuperCV 现在需要重启以完成导入。"] = "SuperCV must restart to complete the import.",
            ["打开历史记录后按需加载"] = "Open History to load items", ["开始日期"] = "Start date", ["截止日期"] = "End date",
            ["选择起始日期"] = "Choose start date", ["选择截止日期"] = "Choose end date", ["AI问答"] = "AI chat",
        };

    private SettingsService? _settings;
    private Dispatcher? _dispatcher;
    private string _language = "zh-CN";

    public static LocalizationService Current { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the application display language has changed.</summary>
    public event EventHandler? LanguageChanged;

    public bool IsEnglish => string.Equals(_language, "en-US", StringComparison.OrdinalIgnoreCase);

    public string Language => _language;

    internal bool IsInitialized => _settings is not null;

    public static string NormalizeLanguage(string? language) =>
        string.Equals(language, "en-US", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
            ? "en-US"
            : "zh-CN";

    public void Initialize(SettingsService settings, Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (_settings is not null)
        {
            return;
        }

        _settings = settings;
        _dispatcher = dispatcher;
        bool languageIsAlreadyCurrent = string.Equals(
            _language,
            NormalizeLanguage(settings.Snapshot.Language),
            StringComparison.OrdinalIgnoreCase);
        SetLanguageCore(settings.Snapshot.Language);
        if (languageIsAlreadyCurrent)
        {
            // SetLanguageCore intentionally skips an unchanged language. Still migrate an
            // untouched legacy default prompt at startup so existing Chinese installations
            // receive the current text-transformation safeguards.
            Setting.RefreshLocalizedDefaultPrompts();
        }
        settings.Changed += Settings_Changed;
    }

    public void SelectLanguage(string? language)
    {
        string normalized = NormalizeLanguage(language);
        if (_settings is not null && !string.Equals(_settings.Snapshot.Language, normalized, StringComparison.OrdinalIgnoreCase))
        {
            // SettingsService is the sole persisted source of truth.  Do not route this back
            // through the legacy Setting adapter: that used to create a second update path.
            _settings.Update(settings => settings with { Language = normalized });
        }

        SetLanguageCore(normalized);
    }

    public string T(string source)
    {
        if (!IsEnglish)
        {
            return source;
        }

        if (English.TryGetValue(source, out string? translated))
        {
            return translated;
        }

        // These tutorial labels contain indentation preserved by XAML, so match their normalized
        // opening text before applying reusable fragments such as "刚刚".
        if (source.TrimStart().StartsWith("模糊搜索可以从语义", StringComparison.Ordinal))
        {
            return "Fuzzy search filters items by meaning, time, and more.\nAI chat can edit and filter entries through Agent tools.\nAI instructions run reusable, custom transformations.";
        }

        if (source.TrimStart().StartsWith("Alt + Shift + 2", StringComparison.Ordinal))
        {
            return "Alt + Shift + 2\nAlt + Shift + W\nThis item was just pasted";
        }

        string partiallyTranslated = source;
        foreach ((string chinese, string english) in EnglishFragments.OrderByDescending(pair => pair.Key.Length))
        {
            partiallyTranslated = partiallyTranslated.Replace(chinese, english, StringComparison.Ordinal);
        }

        if (!string.Equals(partiallyTranslated, source, StringComparison.Ordinal))
        {
            return partiallyTranslated;
        }

        return source == "日" ? "Day" : source;
    }

    public void Localize(DependencyObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Localize(root, new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance));
    }

    /// <summary>
    /// Localizes one newly loaded control.  This intentionally does not walk descendants:
    /// WPF raises Loaded for every element, so recursively walking here would repeatedly scan
    /// the same window and make a language change feel unresponsive.
    /// </summary>
    public void LocalizeElement(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (GetSkipLocalization(element))
        {
            return;
        }

        Apply(element);

        // Context menus, tooltips, and drop-downs live in their own popup tree.  They do not
        // inherit the window's visual tree, so localizing only a Window leaves those surfaces in
        // the previous language until the application is restarted.
        LocalizeAttachedSurfaces(element);
    }

    /// <summary>
    /// Returns the authoring-language value preserved for a localized property.  Dynamic code
    /// can use this for stable keys instead of branching on the currently displayed wording.
    /// </summary>
    public string GetOriginalText(DependencyObject element, DependencyProperty property)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(property);

        return Originals.TryGetValue(element, out Dictionary<DependencyProperty, string>? values) &&
               values.TryGetValue(property, out string? original)
            ? original
            : element.GetValue(property) as string ?? string.Empty;
    }

    private void Localize(DependencyObject root, HashSet<DependencyObject> visited)
    {
        if (GetSkipLocalization(root))
        {
            return;
        }

        if (!visited.Add(root))
        {
            return;
        }

        Apply(root);

        LocalizeAttachedSurfaces(root, visited);

        // Visual children cover control templates. Logical children cover menu headers, inline
        // hyperlink text, DataTemplate content, and collapsed controls that do not have visuals.
        // Both trees are needed for a complete WPF language switch.
        if (root is Visual)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < count; index++)
            {
                Localize(VisualTreeHelper.GetChild(root, index), visited);
            }
        }

        if (root is FrameworkElement or FrameworkContentElement)
        {
            // Rich text uses a live RangeContentEnumerator. Translating one Run can change the
            // range while it is being enumerated, so take a stable snapshot before descending.
            DependencyObject[] logicalChildren = LogicalTreeHelper
                .GetChildren(root)
                .OfType<DependencyObject>()
                .ToArray();
            foreach (DependencyObject dependencyObject in logicalChildren)
            {
                Localize(dependencyObject, visited);
            }
        }
    }

    public void Dispose()
    {
        if (_settings is not null)
        {
            _settings.Changed -= Settings_Changed;
        }

        _settings = null;
        _dispatcher = null;
    }

    private void Settings_Changed(object? sender, SettingsChangedEventArgs e)
    {
        if (_dispatcher is null)
        {
            return;
        }

        string changedLanguage = NormalizeLanguage(e.Settings.Language);
        _ = _dispatcher.BeginInvoke(() =>
        {
            // A later selection can arrive before this dispatcher operation runs.  Ignore this
            // stale notification instead of briefly restoring the previous language.
            if (_settings is null ||
                string.Equals(_settings.Snapshot.Language, changedLanguage, StringComparison.OrdinalIgnoreCase))
            {
                SetLanguageCore(changedLanguage);
            }
        });
    }

    private void SetLanguageCore(string? language)
    {
        string normalized = NormalizeLanguage(language);
        if (string.Equals(_language, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _language = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnglish)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
        if (_settings is not null)
        {
            // Only untouched built-in prompts are replaced; customized prompts stay exactly as authored.
            Setting.RefreshLocalizedDefaultPrompts();
            _ = RefreshBuiltInPresetsAsync(normalized);
        }
        LocalizeOpenWindows();
    }

    private void LocalizeOpenWindows()
    {
        if (System.Windows.Application.Current is null)
        {
            return;
        }

        foreach (Window window in System.Windows.Application.Current.Windows)
        {
            Localize(window);
        }

        // Open ComboBox, ContextMenu, and ToolTip popups are hosted in separate HwndSources and
        // are therefore absent from System.Windows.Application.Current.Windows.
        foreach (PresentationSource source in PresentationSource.CurrentSources)
        {
            if (source.RootVisual is DependencyObject root)
            {
                Localize(root);
            }
        }
    }

    private static async Task RefreshBuiltInPresetsAsync(string language)
    {
        if (System.Windows.Application.Current is not App { Runtime: { } runtime })
        {
            return;
        }

        IReadOnlyList<CustomInstruction> chinese =
            FirstUseDefaults.CreatePresetInstructions(DateTimeOffset.UtcNow, "zh-CN");
        IReadOnlyList<CustomInstruction> english =
            FirstUseDefaults.CreatePresetInstructions(DateTimeOffset.UtcNow, "en-US");
        IReadOnlyList<CustomInstruction> target = NormalizeLanguage(language) == "en-US" ? english : chinese;
        bool changed = false;
        foreach (CustomInstruction desired in target)
        {
            CustomInstruction? existing = runtime.Instructions.Snapshot
                .FirstOrDefault(item => item.Id == desired.Id);
            if (existing is null || !IsBuiltInPreset(existing, chinese, english))
            {
                continue;
            }

            if (existing.Label == desired.Label && existing.Prompt == desired.Prompt)
            {
                continue;
            }

            _ = await runtime.Instructions.UpdateAsync(
                desired with { CreatedAtUtc = existing.CreatedAtUtc }).ConfigureAwait(true);
            changed = true;
        }

        if (changed)
        {
            CustomInstructionsManager.RefreshForLanguageChange();
        }
    }

    private static bool IsBuiltInPreset(
        CustomInstruction candidate,
        IReadOnlyList<CustomInstruction> chinese,
        IReadOnlyList<CustomInstruction> english) =>
        chinese.Concat(english).Any(item =>
            item.Id == candidate.Id && item.Label == candidate.Label && item.Prompt == candidate.Prompt);

    private void Apply(DependencyObject element)
    {
        ApplyString(element, Window.TitleProperty);
        ApplyString(element, TextBlock.TextProperty);
        ApplyString(element, Run.TextProperty);
        ApplyString(element, ContentControl.ContentProperty);
        ApplyString(element, HeaderedContentControl.HeaderProperty);
        ApplyString(element, HeaderedItemsControl.HeaderProperty);
        ApplyString(element, FrameworkElement.ToolTipProperty);
        ApplyString(element, AutomationProperties.NameProperty);
        ApplyString(element, AutomationProperties.HelpTextProperty);

        // Tutorial previews expose their labels through custom dependency properties instead of
        // direct TextBlock values.  Treat them as normal UI strings while leaving actual clipboard
        // data untouched.
        if (element is TutorialCvPreview)
        {
            ApplyString(element, TutorialCvPreview.PreviewTextProperty);
            ApplyString(element, TutorialCvPreview.MetricsTextProperty);
            ApplyString(element, TutorialCvPreview.IndexTextProperty);
        }
        else if (element is TutorialMainWindowPreview)
        {
            ApplyString(element, TutorialMainWindowPreview.SearchPreviewTextProperty);
            ApplyString(element, TutorialMainWindowPreview.WorkspacePreviewTextProperty);
        }
        else if (element is CapsuleButton)
        {
            ApplyString(element, CapsuleButton.TextProperty);
        }
    }

    private void LocalizeAttachedSurfaces(DependencyObject element) =>
        LocalizeAttachedSurfaces(element, new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance));

    private void LocalizeAttachedSurfaces(
        DependencyObject element,
        HashSet<DependencyObject> visited)
    {
        if (element is FrameworkElement frameworkElement)
        {
            if (frameworkElement.ContextMenu is { } contextMenu)
            {
                Localize(contextMenu, visited);
            }

            if (frameworkElement.ToolTip is DependencyObject toolTip)
            {
                Localize(toolTip, visited);
            }
        }

        if (element is Popup { Child: { } child })
        {
            Localize(child, visited);
        }
    }

    private void ApplyString(DependencyObject element, DependencyProperty property)
    {
        if (element.ReadLocalValue(property) is not string current)
        {
            return;
        }

        Dictionary<DependencyProperty, string> values = Originals.GetOrCreateValue(element);
        if (!values.TryGetValue(property, out string? original))
        {
            original = current;
            values[property] = original;
        }

        string localized = IsEnglish ? T(original) : original;
        if (!string.Equals(current, localized, StringComparison.Ordinal))
        {
            element.SetValue(property, localized);
        }
    }
}
