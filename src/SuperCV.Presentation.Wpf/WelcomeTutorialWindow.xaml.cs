using System.Globalization;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using SuperCV.Application.History;
using SuperCV.Application.Workspaces;
using SuperCV.Domain.Workspaces;

namespace SuperCV;

/// <summary>
/// A non-modal, self-contained visual tour.  Its previews are XAML composition only;
/// none of the live SuperCV controls or clipboard state are used here.
/// </summary>
public partial class WelcomeTutorialWindow : Window, System.ComponentModel.INotifyPropertyChanged
{
    private readonly UIElement[] _pages;
    private readonly Button[] _dots;
    private readonly AppRuntime _runtime;
    private readonly PersistentHistoryViewModel _historyViewModel;
    private int _currentPage;
    private bool _configurationChanged;
    private bool _isApplyingConfiguration;
    private bool _hasCompletedWelcome;
    private readonly bool _isInitialSetup;
    private UserAgreementWindow? _userAgreementWindow;

    public SettingViewModel Configuration { get; }

    public string PreviousNavigationText => LocalizationService.Current.T("上一步");

    public string NextNavigationText => LocalizationService.Current.T(
        _pages is not null && _currentPage == _pages.Length - 1 ? "完成" : "下一步");

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public WelcomeTutorialWindow(bool isInitialSetup = false)
    {
        InitializeComponent();
        _isInitialSetup = isInitialSetup;
        _runtime = GetRuntime();
        _historyViewModel = new PersistentHistoryViewModel(
            _runtime.PersistentHistory,
            _runtime.History,
            _runtime.Workspaces.Snapshot.ActiveWorkspaceId);
        Configuration = new SettingViewModel(_historyViewModel);
        Configuration.PropertyChanged += Configuration_PropertyChanged;
        LocalizationService.Current.LanguageChanged += Localization_LanguageChanged;
        DataContext = Configuration;
        _pages = [Page0, Page1, Page2, Page3, PageSettings];
        _dots = [Dot0, Dot1, Dot2, Dot3, DotSettings];
        ShowNavigationDescription("返回顶部");
        ShowPage(0);
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => ShowPage(_currentPage - 1);

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplyingConfiguration)
        {
            return;
        }

        if (_currentPage == _pages.Length - 1)
        {
            if (UserAgreementCheckBox.IsChecked != true)
            {
                new AlertDialog(LocalizationService.Current.T("请先阅读并同意用户协议后再完成初始设置。"))
                {
                    Owner = this,
                }.ShowDialog();
                return;
            }

            if (!await ApplyWelcomeConfigurationAsync())
            {
                return;
            }

            _hasCompletedWelcome = true;
            Close();
            return;
        }

        ShowPage(_currentPage + 1);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OpenUserAgreementLink_Click(object sender, RoutedEventArgs e)
    {
        if (_userAgreementWindow is not null)
        {
            if (_userAgreementWindow.WindowState == WindowState.Minimized)
            {
                _userAgreementWindow.WindowState = WindowState.Normal;
            }

            _userAgreementWindow.Activate();
            return;
        }

        var agreement = new UserAgreementWindow { Owner = this };
        _userAgreementWindow = agreement;
        agreement.Closed += (_, _) => _userAgreementWindow = null;
        agreement.Show();
    }

    private void SelectLanguage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string language })
        {
            Configuration.Language = language;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_hasCompletedWelcome)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        Configuration.PropertyChanged -= Configuration_PropertyChanged;
        LocalizationService.Current.LanguageChanged -= Localization_LanguageChanged;
        _historyViewModel.Dispose();
        base.OnClosed(e);
    }

    private void Localization_LanguageChanged(object? sender, EventArgs e)
    {
        Configuration.SynchronizeLanguage(LocalizationService.Current.Language);
        ShowNavigationDescription("返回顶部");
        ShowPage(_currentPage);
    }

    private void Configuration_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        _configurationChanged = true;

    private async Task<bool> ApplyWelcomeConfigurationAsync()
    {
        if (!_configurationChanged)
        {
            return true;
        }

        bool imageSupportChanged = Configuration.EnableImageSupport != Setting.EnableImageSupport;
        if (imageSupportChanged && !Configuration.EnableImageSupport)
        {
            bool confirmed = new AlertDialog("关闭图片支持会清除所有工作区中的图片条目。\n是否继续？")
            {
                Owner = this,
            }.ShowDialog();
            if (!confirmed)
            {
                return false;
            }
        }

        _isApplyingConfiguration = true;
        NextButton.IsEnabled = false;
        PreviousButton.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            if (imageSupportChanged)
            {
                await _runtime.SetImageSupportEnabledAsync(Configuration.EnableImageSupport);
            }

            await Configuration.SaveAsync();
            RenameDefaultWorkspaceForInitialEnglishSetup();
            return true;
        }
        catch (Exception exception)
        {
            if (imageSupportChanged)
            {
                await _runtime.SetImageSupportEnabledAsync(Setting.EnableImageSupport);
            }

            new AlertDialog($"应用初始设置失败：{GetErrorMessage(exception)}")
            {
                Owner = this,
            }.ShowDialog();
            return false;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _isApplyingConfiguration = false;
            ShowPage(_currentPage);
        }
    }

    private void RenameDefaultWorkspaceForInitialEnglishSetup()
    {
        if (!_isInitialSetup ||
            !string.Equals(Configuration.Language, "en-US", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        WorkspaceDefinition? defaultWorkspace = _runtime.Workspaces.Snapshot.Workspaces
            .FirstOrDefault(workspace => workspace.Id == WorkspaceDefinition.DefaultWorkspaceId);
        if (defaultWorkspace is not null &&
            string.Equals(
                defaultWorkspace.Name,
                WorkspaceService.DefaultWorkspaceName,
                StringComparison.Ordinal))
        {
            _ = _runtime.RenameWorkspace(defaultWorkspace.Id, "Default");
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri is not null)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }

        e.Handled = true;
    }

    private async void DebugHyperlink_Click(object sender, RoutedEventArgs e)
    {
        if (!Configuration.EnableAiFeatures)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Configuration.APIKey) &&
            !AIProviderDefaults.AllowsEmptyApiKey(Configuration.AIBaseUrl))
        {
            new AlertDialog("请先输入 API Key；仅本机回环地址可以留空。") { Owner = this }.ShowDialog();
            return;
        }

        if (Configuration.AIModel == AIProvider.Custom &&
            (string.IsNullOrWhiteSpace(Configuration.AIBaseUrl) ||
             string.IsNullOrWhiteSpace(Configuration.AIModelName)))
        {
            new AlertDialog("自定义模型请先输入 API 地址和模型名称。") { Owner = this }.ShowDialog();
            return;
        }

        Mouse.OverrideCursor = Cursors.Wait;
        using var ai = new AI2(
            Configuration.APIKey,
            Configuration.AIModel,
            "你是一个接口连通性测试助手。",
            Configuration.AIModelName,
            Configuration.AIBaseUrl);
        try
        {
            await ai.AskAsync("请只回复“调试成功”。", "ping");
            new AlertDialog("调试成功，当前 AI 配置可以正常连通。") { Owner = this }.ShowDialog();
        }
        catch (Exception exception)
        {
            new AlertDialog($"调试失败：{GetErrorMessage(exception)}") { Owner = this }.ShowDialog();
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private static AppRuntime GetRuntime() =>
        (System.Windows.Application.Current as App)?.Runtime
        ?? throw new InvalidOperationException("WPF application runtime is unavailable.");

    private static string GetErrorMessage(Exception exception)
    {
        Exception current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        string message = current.Message.Replace("\r", " ").Replace("\n", " ").Trim();
        return string.IsNullOrWhiteSpace(message) ? "未知错误" : message;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // DragMove can reject a click while the window is changing state.
            }
        }
    }

    private void CalloutLabel_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            if (element.Tag is string coordinates)
            {
                UpdateCalloutConnectorLine(coordinates);
            }
            else
            {
                CalloutConnectorLine.Points.Clear();
            }

            ShowNavigationDescription(LocalizationService.Current.GetOriginalText(
                element,
                System.Windows.Automation.AutomationProperties.NameProperty));
        }
    }

    private void ShowNavigationDescription(string navigationName)
    {
        (string title, string description) = navigationName switch
        {
            "选项" => ("选项", "打开应用设置，可调整快捷键、偏好、AI等配置。该按钮显示为圆环时可以双击最小化或切换浮窗模式。"),
            "文本搜索框" => ("文本搜索框", "输入关键词可过滤当前工作区的条目，快速找到内容。输入“/fs +文本”可以进行模糊搜索，输入“/ai +文本”可以进行AI问答。"),
            "展开 / 收起" => ("展开 / 收起", "切换主窗口的展开状态，在节省窗口空间与更多功能按键间快速切换。可同时改变选项按钮的状态。"),
            "关闭软件" => ("关闭软件", "关闭 SuperCV ；如已启用托盘运行，应用仍可在系统托盘中继续使用。"),
            "返回顶部" => ("返回顶部", "将当前工作区的条目列表滚动到顶部，快速回到最近的内容。"),
            "新建空白条目" => ("新建空白条目", "在当前工作区创建一个空白条目，可右键编辑输入内容。"),
            "清空工作区" => ("清空工作区", "移除当前工作区的所有条目。执行前会要求确认，请仅在内容不再需要时使用。"),
            "列表更新" => ("列表更新", "控制是否持续监测剪贴板并将新内容加入当前工作区的列表。"),
            "显示条目侧边按钮" => ("显示条目侧边按钮", "显示或隐藏每条内容旁的快捷操作按钮。"),
            "深色模式" => ("深色模式", "切换应用的明暗主题，使界面在不同环境光线下保持舒适易读。"),
            "移除收藏" => ("移除收藏", "将当前内容从收藏中移除，确保不需要时才移除内容。"),
            "AI编辑与问答" => ("AI编辑与问答", "打开 AI 助手，对当前条目进行改写、整理、翻译或围绕内容继续提问。可以添加自定义的AI指令。"),
            "更多菜单" => ("更多菜单", "打开当前条目的更多操作菜单，可执行与条目相关的附加管理操作。"),
            "删除条目" => ("删除条目", "从当前工作区删除这条内容。删除前请确认它不再需要。"),
            "滚动条目" => ("滚动条目", "将鼠标移到任意内容卡片上，再滚动鼠标滚轮，即可浏览当前工作区中更多的条目。"),
            "切换工作区" => ("切换工作区", "从“默认工作区”下拉控件选择其他工作区；切换后会显示该工作区独立的条目。右键可以编辑每个工作区的名称。"),
            "收藏条目" => ("收藏条目", "收藏栏保留常用内容。单击条目可快速粘贴，右键可编辑。推拽收藏条目可以改变前后顺序。鼠标滚轮可以左右滚动。"),
            "收藏当前条目" => ("收藏当前条目", "将当前条目加入收藏栏，可以设置简称。"),
            "置顶条目" => ("置顶条目", "将当前条目置于列表顶部，方便持续查看或反复使用。"),
            "导出条目" => ("导出条目", "导出当前条目的内容，用于保存、分享或在其他工具中继续处理。"),
            "撤销修改" => ("撤销修改", "撤销当前条目最近的编辑；没有可撤销记录时该操作不可用。"),
            "转换为纯文本" => ("转换为纯文本", "移除当前条目的富文本或其他格式，只保留纯文本内容。"),
            "查找替换" => ("查找替换", "在当前条目中查找指定内容，并可将匹配文本批量替换为新内容。"),
            "网络搜索" => ("网络搜索", "使用系统默认浏览器搜索当前条目的文本内容。"),
            "颜色标记" => ("颜色标记", "为当前条目设置颜色标识，帮助在列表中按用途快速辨认内容。"),
            _ => ("功能说明", "将鼠标移到任一标注导航上，即可查看该按钮的功能说明。"),
        };

        NavigationTitle.Text = LocalizationService.Current.T(title);
        NavigationDescription.Text = LocalizationService.Current.T(description);
    }

    private void UpdateCalloutConnectorLine(string coordinates)
    {
        string[] pointTexts = coordinates.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var points = new System.Windows.Media.PointCollection(pointTexts.Length);
        foreach (string pointText in pointTexts)
        {
            string[] values = pointText.Split(',');
            if (values.Length != 2 ||
                !double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ||
                !double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            {
                return;
            }

            points.Add(new Point(x, y));
        }

        if (points.Count >= 2)
        {
            CalloutConnectorLine.Points = points;
        }
    }

    private void ShowPage(int page)
    {
        _currentPage = Math.Clamp(page, 0, _pages.Length - 1);
        for (int index = 0; index < _pages.Length; index++)
        {
            _pages[index].Visibility = index == _currentPage
                ? Visibility.Visible
                : Visibility.Collapsed;
            _dots[index].Tag = index == _currentPage ? "Selected" : null;
        }

        PreviousButton.IsEnabled = _currentPage > 0;
        NextButton.IsEnabled = !_isApplyingConfiguration;
        PropertyChanged?.Invoke(
            this,
            new System.ComponentModel.PropertyChangedEventArgs(nameof(PreviousNavigationText)));
        PropertyChanged?.Invoke(
            this,
            new System.ComponentModel.PropertyChangedEventArgs(nameof(NextNavigationText)));
    }
}
