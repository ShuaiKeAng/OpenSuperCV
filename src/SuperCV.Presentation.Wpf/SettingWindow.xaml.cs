using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using Microsoft.Win32;
using SuperCV.Application.History;
using SuperCV.Application.Workspaces;
using SuperCV.Domain.Settings;
using SuperCV.Infrastructure.Windows;
using Forms = System.Windows.Forms;

namespace SuperCV
{
    /// <summary>
    /// SettingWindow.xaml 的交互逻辑
    /// </summary>
    public partial class SettingWindow : Window
    {
        private bool _isDebugging;
        private bool _isClosed;
        private bool _ownsWaitCursor;
        private CancellationTokenSource? _autoApplyCancellation;
        private CancellationTokenSource? _debugCancellation;
        private Button? _shortcutCaptureButton;
        private EditableShortcut? _shortcutBeingCaptured;
        private ShortcutModifiers _capturedModifiers;
        private readonly AppRuntime _runtime;
        private readonly HashSet<long> _historyDeletesInProgress = [];
        private int _historyDeleteOperationCount;
        private bool _isChangingPersistentHistory;
        private bool _isChangingImageSupport;
        private bool _suppressAutoApply;
        private bool _isDataTransferInProgress;
        private WelcomeTutorialWindow? _welcomeTutorialWindow;
        private UserAgreementWindow? _userAgreementWindow;
        private TextEntryBackgroundWindow? _textEntryBackgroundWindow;

        public SettingViewModel ViewModel { get; private set; }

        public SettingWindow()
        {
            InitializeComponent();
            _runtime = GetRuntime();
            RadioGeneral.Checked += RadioButton_Checked;
            RadioShortcuts.Checked += RadioButton_Checked;
            RadioHistory.Checked += RadioButton_Checked;
            RadioAI.Checked += RadioButton_Checked;
            RadioAbout.Checked += RadioButton_Checked;
            MainTabControl.SelectionChanged += MainTabControl_SelectionChanged;
            PreviewKeyDown += CaptureShortcutPreviewKeyDown;
            PreviewKeyUp += CaptureShortcutPreviewKeyUp;
            PreviewMouseDown += CaptureShortcutPreviewMouseDown;
            PreviewMouseMove += SettingWindow_PreviewMouseMove;
            PreviewMouseUp += SettingWindow_PreviewMouseUp;
            PreviewMouseWheel += SettingWindow_PreviewMouseWheel;
            var historyViewModel = new PersistentHistoryViewModel(
                _runtime.PersistentHistory,
                _runtime.History,
                _runtime.Workspaces.Snapshot.ActiveWorkspaceId);
            ViewModel = new SettingViewModel(
                historyViewModel,
                new AiUsageDashboardViewModel(_runtime.AiTokenUsage));
            DataContext = ViewModel;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            LocalizationService.Current.LanguageChanged += Localization_LanguageChanged;
            _runtime.Workspaces.Changed += Workspaces_Changed;
            _runtime.PersistentHistory.Changed += PersistentHistory_Changed;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void OpenWelcomeTutorial_Click(object sender, RoutedEventArgs e)
        {
            if (_welcomeTutorialWindow is not null)
            {
                _welcomeTutorialWindow.Activate();
                return;
            }

            var tutorial = new WelcomeTutorialWindow();
            _welcomeTutorialWindow = tutorial;
            tutorial.Closed += (_, _) => _welcomeTutorialWindow = null;
            tutorial.Show();
        }

        private void OpenUserAgreement_Click(object sender, RoutedEventArgs e)
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

        private void OpenReleaseNotes_Click(object sender, RoutedEventArgs e)
        {
            var releaseNotes = new ReleaseNotesWindow
            {
                Owner = this,
            };
            releaseNotes.ShowDialog();
        }

        private async void ChangeDataRoot_Click(object sender, RoutedEventArgs e)
        {
            if (_isDataTransferInProgress)
            {
                return;
            }

            if (AppRuntime.IsDataRootEnvironmentOverrideActive())
            {
                ShowDataTransferMessage(
                    "无法更改数据路径",
                    "当前数据路径由环境变量指定。请先移除 SUPERCV_DATA_ROOT（或兼容旧变量 SUPERCV_V2_DATA_ROOT），再使用此功能。");
                return;
            }

            string sourceRoot = AppRuntime.ResolveDataRoot();
            using var dialog = new Forms.FolderBrowserDialog
            {
                Description = "选择一个空文件夹",
                SelectedPath = Directory.Exists(sourceRoot) ? sourceRoot : string.Empty,
                ShowNewFolderButton = true,
            };
            if (dialog.ShowDialog() != Forms.DialogResult.OK ||
                string.IsNullOrWhiteSpace(dialog.SelectedPath))
            {
                return;
            }

            var confirmation = new AlertDialog(
                "将把设置、工作区、历史、书签、指令、图片和 AI 用量迁移到目标目录。\n\n" +
                "目标文件夹必须为空。迁移验证完成后，SuperCV 会立即重启并从新路径继续运行；确认新路径可用后会删除原数据目录。",
                "迁移并重启")
            {
                Owner = this,
            };
            if (!confirmation.ShowDialog())
            {
                return;
            }

            await RunDataTransferAsync(async () =>
            {
                await _runtime.FlushForExportAsync();
                ImportPackageInfo package = await DataRootMigrationService.MigrateAsync(
                    sourceRoot,
                    dialog.SelectedPath);
                DataRootLocationStore locationStore = DataRootLocationStore.ForCurrentUser();
                await locationStore.ScheduleSourceDeletionAsync(sourceRoot, dialog.SelectedPath);
                await locationStore.SaveAsync(dialog.SelectedPath);

                ShowDataTransferMessage(
                    "数据路径已更改",
                    $"已验证并迁移 {package.FileCount} 个数据文件。SuperCV 将立即重启并使用新路径，同时删除原数据目录。\n\n{dialog.SelectedPath}");
                ((App)System.Windows.Application.Current).RestartAfterDataRootChange();
            });
        }

        private async void ExportData_Click(object sender, RoutedEventArgs e)
        {
            if (_isDataTransferInProgress)
            {
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "导出 SuperCV 数据",
                Filter = "SuperCV 迁移文件 (*" + DataTransferService.ArchiveExtension + ")|*" +
                         DataTransferService.ArchiveExtension,
                DefaultExt = DataTransferService.ArchiveExtension,
                AddExtension = true,
                FileName = "SuperCV-" + DateTime.Now.ToString("yyyyMMdd-HHmm") +
                           DataTransferService.ArchiveExtension,
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            await RunDataTransferAsync(async () =>
            {
                await _runtime.FlushForExportAsync();
                await _runtime.DataTransfer.ExportAsync(dialog.FileName);
                ShowDataTransferMessage("导出完成", "已导出为单个迁移文件。请妥善保存，该文件可能包含你的剪贴板内容与 AI 配置。");
            });
        }

        private async void ImportData_Click(object sender, RoutedEventArgs e)
        {
            if (_isDataTransferInProgress)
            {
                return;
            }

            var dialog = new OpenFileDialog
            {
                Title = "导入 SuperCV 数据",
                Filter = "SuperCV 迁移文件 (*" + DataTransferService.ArchiveExtension + ")|*" +
                         DataTransferService.ArchiveExtension,
                CheckFileExists = true,
                Multiselect = false,
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            var confirmation = new AlertDialog(
                "导入会在文件完整性、版本和内容结构全部通过验证后，于重启时替换当前设置和内容。\n\n" +
                "导入前的数据会保留一份本地回滚副本。跨 Windows 用户或电脑迁移时，AI API Key 受 Windows 保护，可能需要重新填写。",
                "验证并导入")
            {
                Owner = this,
            };
            if (!confirmation.ShowDialog())
            {
                return;
            }

            await RunDataTransferAsync(async () =>
            {
                ImportPackageInfo package = await _runtime.DataTransfer.StageImportAsync(dialog.FileName);
                var restartConfirmation = new AlertDialog(
                    $"迁移文件已通过校验（创建于 {package.CreatedAtUtc.LocalDateTime:g}，包含 {package.FileCount} 个文件）。\n\n" +
                    "SuperCV 现在需要重启以完成导入。",
                    "立即重启")
                {
                    Owner = this,
                };
                if (restartConfirmation.ShowDialog())
                {
                    ((App)System.Windows.Application.Current).RestartAfterImport();
                }
                else
                {
                    ShowDataTransferMessage("导入已准备", "迁移文件已通过校验；请退出并重新打开 SuperCV 以完成导入。");
                }
            });
        }

        private async Task RunDataTransferAsync(Func<Task> operation)
        {
            _isDataTransferInProgress = true;
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                await operation();
            }
            catch (Exception exception)
            {
                if (!_isClosed)
                {
                    ShowDataTransferMessage("操作失败", GetErrorMessage(exception));
                }
            }
            finally
            {
                _isDataTransferInProgress = false;
                Mouse.OverrideCursor = null;
            }
        }

        private void ShowDataTransferMessage(string title, string message)
        {
            new AlertDialog(message, "确定")
            {
                Owner = this,
                Title = title,
            }.ShowDialog();
        }

        private async void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            var confirmation = new AlertDialog(
                "确定将所有设置恢复为默认值吗？\n快捷键、AI 配置和 API Key 等都会重置。")
            {
                Owner = this,
            };
            if (!confirmation.ShowDialog())
            {
                return;
            }

            CancelShortcutCapture();
            CancelPendingAutoApply();
            ResetButton.IsEnabled = false;
            try
            {
                _suppressAutoApply = true;
                await ViewModel.ResetToDefaultAsync();
                await _runtime.SetImageSupportEnabledAsync(true);
            }
            catch (Exception exception)
            {
                if (!_isClosed)
                {
                    new AlertDialog($"重置设置失败：{GetErrorMessage(exception)}")
                    {
                        Owner = this,
                    }.ShowDialog();
                }
            }
            finally
            {
                _suppressAutoApply = false;
                if (!_isClosed)
                {
                    ResetButton.IsEnabled = true;
                }
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _isClosed = true;
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            MainTabControl.SelectionChanged -= MainTabControl_SelectionChanged;
            LocalizationService.Current.LanguageChanged -= Localization_LanguageChanged;
            _runtime.Workspaces.Changed -= Workspaces_Changed;
            _runtime.PersistentHistory.Changed -= PersistentHistory_Changed;
            PreviewMouseMove -= SettingWindow_PreviewMouseMove;
            PreviewMouseUp -= SettingWindow_PreviewMouseUp;
            PreviewMouseWheel -= SettingWindow_PreviewMouseWheel;
            ViewModel.History.Dispose();
            _debugCancellation?.Cancel();
            ReleaseWaitCursor();
            base.OnClosed(e);
        }

        private void Localization_LanguageChanged(object? sender, EventArgs e)
        {
            _suppressAutoApply = true;
            try
            {
                ViewModel.SynchronizeLanguage(LocalizationService.Current.Language);
                ViewModel.RefreshThemeDisplayNames();
            }
            finally
            {
                _suppressAutoApply = false;
            }
        }

        private void ThemeSelector_DropDownOpened(object sender, EventArgs e) =>
            ViewModel.RefreshThemes();

        private static void RecordMainWindowMouseActivity() =>
            (System.Windows.Application.Current.MainWindow as SuperCVWindow)?.RecordMouseActivity();

        private void SettingWindow_PreviewMouseMove(object sender, MouseEventArgs e) =>
            RecordMainWindowMouseActivity();

        private void SettingWindow_PreviewMouseUp(object sender, MouseButtonEventArgs e) =>
            RecordMainWindowMouseActivity();

        private void SettingWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e) =>
            RecordMainWindowMouseActivity();

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_isClosed || _suppressAutoApply)
            {
                return;
            }

            if (e.PropertyName == nameof(SettingViewModel.TextSizeIndex))
            {
                CVListControl.ApplyTextSize(ViewModel.TextSizeValue);
            }

            var nextCancellation = new CancellationTokenSource();
            CancellationTokenSource? previousCancellation =
                Interlocked.Exchange(ref _autoApplyCancellation, nextCancellation);
            previousCancellation?.Cancel();
            _ = ApplySettingsAfterDelayAsync(nextCancellation);
        }

        private async Task ApplySettingsAfterDelayAsync(CancellationTokenSource applyCancellation)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150), applyCancellation.Token);
                await ApplySettingsAsync();
            }
            catch (OperationCanceledException) when (applyCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                CVListControl.RefreshTextSize();
                if (!_isClosed)
                {
                    new AlertDialog($"自动应用设置失败：{GetErrorMessage(exception)}")
                    {
                        Owner = this,
                    }.ShowDialog();
                }
            }
            finally
            {
                Interlocked.CompareExchange(
                    ref _autoApplyCancellation,
                    null,
                    applyCancellation);
                applyCancellation.Dispose();
            }
        }

        private async Task ApplySettingsAsync()
        {
            await ViewModel.SaveAsync();
        }

        private void TextEntryBackgroundButton_Click(object sender, RoutedEventArgs e)
        {
            if (_textEntryBackgroundWindow is { IsVisible: true })
            {
                _textEntryBackgroundWindow.Activate();
                return;
            }

            var dialog = new TextEntryBackgroundWindow(ViewModel)
            {
                Owner = this,
            };
            _textEntryBackgroundWindow = dialog;
            dialog.Applied += TextEntryBackgroundWindow_Applied;
            dialog.Closed += (_, _) =>
            {
                dialog.Applied -= TextEntryBackgroundWindow_Applied;
                if (ReferenceEquals(_textEntryBackgroundWindow, dialog))
                {
                    _textEntryBackgroundWindow = null;
                }
            };
            dialog.Show();
        }

        private async void TextEntryBackgroundWindow_Applied(object? sender, EventArgs e)
        {
            if (sender is not TextEntryBackgroundWindow dialog)
            {
                return;
            }

            CancelPendingAutoApply();
            _suppressAutoApply = true;
            try
            {
                ViewModel.TextEntryBackgroundImagePath = dialog.SelectedImagePath;
                ViewModel.TextEntryBackgroundOpacityPercent = dialog.SelectedOpacityPercent;
                ViewModel.TextEntryBackgroundScale = dialog.SelectedScale;
                ViewModel.TextEntryBackgroundOffsetX = dialog.SelectedOffsetX;
                ViewModel.TextEntryBackgroundOffsetY = dialog.SelectedOffsetY;
            }
            finally
            {
                _suppressAutoApply = false;
            }

            try
            {
                await ApplySettingsAsync();
            }
            catch (Exception exception)
            {
                new AlertDialog($"应用文字条目背景失败：{GetErrorMessage(exception)}")
                {
                    Owner = this,
                }.ShowDialog();
            }
        }

        private async void RadioButton_Checked(object sender, RoutedEventArgs e)
        {
            if (MainTabControl == null)
            {
                return;
            }

            if (sender == RadioGeneral)
            {
                MainTabControl.SelectedIndex = 0;
            }
            else if (sender == RadioShortcuts)
            {
                MainTabControl.SelectedIndex = 1;
            }
            else if (sender == RadioHistory)
            {
                MainTabControl.SelectedIndex = 2;
                if (!ViewModel.PersistentHistoryEnabled)
                {
                    return;
                }

                try
                {
                    await ViewModel.History.EnsureLoadedAsync();
                }
                catch (Exception exception)
                {
                    ShowHistoryFailure("加载历史记录失败", exception);
                }
            }
            else if (sender == RadioAI)
            {
                MainTabControl.SelectedIndex = 3;
            }
            else if (sender == RadioAbout)
            {
                MainTabControl.SelectedIndex = 4;
            }
        }

        private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, MainTabControl))
            {
                return;
            }

            // TabControl realizes page content only after selection.  Queue localization until
            // that content is attached, so lazy pages such as About receive the current language.
            _ = Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() =>
                {
                    if (!_isClosed && MainTabControl.SelectedContent is DependencyObject page)
                    {
                        LocalizationService.Current.Localize(page);
                    }
                }));
        }

        private async void PersistentHistorySwitch_SwitchChanged(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is not MySwitch historySwitch ||
                _isChangingPersistentHistory)
            {
                return;
            }

            bool previousValue = ViewModel.PersistentHistoryEnabled;
            bool requestedValue = historySwitch.IsOn;
            if (requestedValue == previousValue)
            {
                return;
            }

            if (!requestedValue)
            {
                bool confirmed = new AlertDialog(
                    "关闭后将清空超出条目上限的记录。\n是否继续？")
                {
                    Owner = this,
                }.ShowDialog();
                if (!confirmed)
                {
                    historySwitch.IsOn = previousValue;
                    return;
                }
            }

            _isChangingPersistentHistory = true;
            historySwitch.IsEnabled = false;
            CancelPendingAutoApply();
            AcquireWaitCursor();
            try
            {
                if (!requestedValue)
                {
                    SetPersistentHistoryEnabledWithoutAutoApply(false);
                    _runtime.History.SetPersistentHistoryEnabled(false);
                    await _runtime.TrimPersistentHistoryToLimitAsync(
                        ViewModel.MaxHistoryItems);
                    ViewModel.History.Invalidate();
                }
                else
                {
                    await _runtime.SynchronizePersistentHistoryToRichLimitAsync(
                        ViewModel.MaxHistoryItems);
                    SetPersistentHistoryEnabledWithoutAutoApply(true);
                    _runtime.History.SetPersistentHistoryEnabled(true);
                }

                await ApplySettingsAsync();

                if (requestedValue && RadioHistory.IsChecked == true)
                {
                    try
                    {
                        await ViewModel.History.ReloadAsync();
                    }
                    catch (Exception exception)
                    {
                        ShowHistoryFailure("加载历史记录失败", exception);
                    }
                }
            }
            catch (Exception exception)
            {
                SetPersistentHistoryEnabledWithoutAutoApply(previousValue);
                _runtime.History.SetPersistentHistoryEnabled(previousValue);
                historySwitch.IsOn = previousValue;
                ShowHistoryFailure("切换长久储存失败", exception);
            }
            finally
            {
                ReleaseWaitCursor();
                historySwitch.IsEnabled = true;
                _isChangingPersistentHistory = false;
            }
        }

        private void SetPersistentHistoryEnabledWithoutAutoApply(bool value)
        {
            _suppressAutoApply = true;
            try
            {
                ViewModel.PersistentHistoryEnabled = value;
            }
            finally
            {
                _suppressAutoApply = false;
            }
        }

        private async void ImageSupportSwitch_SwitchChanged(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is not MySwitch imageSwitch || _isChangingImageSupport)
            {
                return;
            }

            bool previousValue = ViewModel.EnableImageSupport;
            bool requestedValue = imageSwitch.IsOn;
            if (requestedValue == previousValue)
            {
                return;
            }

            if (!requestedValue)
            {
                bool confirmed = new AlertDialog(
                    "关闭会清除所有工作区中的图片条目。\n是否继续？")
                {
                    Owner = this,
                }.ShowDialog();
                if (!confirmed)
                {
                    imageSwitch.IsOn = previousValue;
                    return;
                }
            }

            _isChangingImageSupport = true;
            imageSwitch.IsEnabled = false;
            CancelPendingAutoApply();
            AcquireWaitCursor();
            try
            {
                SetImageSupportEnabledWithoutAutoApply(requestedValue);
                await _runtime.SetImageSupportEnabledAsync(requestedValue);
                await ApplySettingsAsync();
                ViewModel.History.Invalidate();
            }
            catch (Exception exception)
            {
                SetImageSupportEnabledWithoutAutoApply(previousValue);
                await _runtime.SetImageSupportEnabledAsync(previousValue);
                imageSwitch.IsOn = previousValue;
                new AlertDialog($"切换图片支持失败：{GetErrorMessage(exception)}")
                {
                    Owner = this,
                }.ShowDialog();
            }
            finally
            {
                ReleaseWaitCursor();
                imageSwitch.IsEnabled = true;
                _isChangingImageSupport = false;
            }
        }

        private void SetImageSupportEnabledWithoutAutoApply(bool value)
        {
            _suppressAutoApply = true;
            try
            {
                ViewModel.EnableImageSupport = value;
            }
            finally
            {
                _suppressAutoApply = false;
            }
        }

        private void CancelPendingAutoApply()
        {
            CancellationTokenSource? pendingCancellation =
                Interlocked.Exchange(ref _autoApplyCancellation, null);
            pendingCancellation?.Cancel();
        }

        private void HistorySearchBox_Loaded(object sender, RoutedEventArgs e) =>
            UpdateHistorySearchWatermark();

        private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
            UpdateHistorySearchWatermark();

        private void UpdateHistorySearchWatermark()
        {
            if (HistorySearchWatermark is not null)
            {
                HistorySearchWatermark.Visibility = string.IsNullOrEmpty(HistorySearchBox.Text)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private async void HistorySearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            e.Handled = true;
            try
            {
                await ViewModel.History.SearchAsync();
            }
            catch (Exception exception)
            {
                ShowHistoryFailure("查找历史记录失败", exception);
            }
        }

        private async void HistoryDateRange_DateRangeChanged(
            object sender,
            RoutedEventArgs e)
        {
            if (RadioHistory.IsChecked != true ||
                !ViewModel.PersistentHistoryEnabled)
            {
                return;
            }

            try
            {
                await ViewModel.History.SearchAsync();
            }
            catch (Exception exception)
            {
                ShowHistoryFailure("按日期筛选历史记录失败", exception);
            }
        }

        private async void HistoryList_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ViewportHeight <= 0 ||
                e.VerticalOffset < e.ExtentHeight - e.ViewportHeight - 48)
            {
                return;
            }

            try
            {
                await ViewModel.History.LoadMoreAsync();
            }
            catch (Exception exception)
            {
                ShowHistoryFailure("加载更多历史记录失败", exception);
            }
        }

        private void CopyHistoryItem_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not FrameworkElement
                {
                    DataContext: PersistentHistoryItemViewModel item,
                })
            {
                return;
            }

            MyClipboard.SetMultipleTextFormats(item.IsImage
                ? new Dictionary<TextFormat, string>
                {
                    [TextFormat.Image] = item.Text,
                }
                : new Dictionary<TextFormat, string>
                {
                    [TextFormat.Text] = item.Text,
                    [TextFormat.UnicodeText] = item.Text,
                });
            e.Handled = true;
        }

        private async void DeleteHistoryItem_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not FrameworkElement
                {
                    DataContext: PersistentHistoryItemViewModel item,
                } ||
                !_historyDeletesInProgress.Add(item.StorageKey))
            {
                return;
            }

            e.Handled = true;
            Interlocked.Increment(ref _historyDeleteOperationCount);
            try
            {
                await ViewModel.History.DeleteAsync(item);
            }
            catch (Exception exception)
            {
                ShowHistoryFailure("删除历史记录失败", exception);
            }
            finally
            {
                _ = _historyDeletesInProgress.Remove(item.StorageKey);
                Interlocked.Decrement(ref _historyDeleteOperationCount);
            }
        }

        private void Workspaces_Changed(object? sender, WorkspacesChangedEventArgs e)
        {
            if (_isClosed)
            {
                return;
            }

            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.BeginInvoke(() => Workspaces_Changed(sender, e));
                return;
            }

            if (RadioHistory.IsChecked != true)
            {
                ViewModel.History.SelectWorkspace(e.Snapshot.ActiveWorkspaceId);
                return;
            }

            _ = ChangeHistoryWorkspaceAsync(e.Snapshot.ActiveWorkspaceId);
        }

        private void PersistentHistory_Changed(
            object? sender,
            PersistentHistoryChangedEventArgs e)
        {
            if (Volatile.Read(ref _isClosed) ||
                Volatile.Read(ref _isChangingPersistentHistory) ||
                Volatile.Read(ref _historyDeleteOperationCount) > 0 ||
                e.WorkspaceId != _runtime.Workspaces.Snapshot.ActiveWorkspaceId)
            {
                return;
            }

            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.BeginInvoke(() => PersistentHistory_Changed(sender, e));
                return;
            }

            if (RadioHistory.IsChecked != true)
            {
                ViewModel.History.Invalidate();
                return;
            }

            _ = ReloadChangedHistoryAsync();
        }

        private async Task ReloadChangedHistoryAsync()
        {
            try
            {
                await ViewModel.History.ReloadAsync();
            }
            catch (Exception exception)
            {
                ShowHistoryFailure("刷新历史记录失败", exception);
            }
        }

        private async Task ChangeHistoryWorkspaceAsync(Guid workspaceId)
        {
            try
            {
                await ViewModel.History.ChangeWorkspaceAsync(workspaceId);
            }
            catch (Exception exception)
            {
                ShowHistoryFailure("切换工作区历史失败", exception);
            }
        }

        private void ShowHistoryFailure(string title, Exception exception)
        {
            if (_isClosed)
            {
                return;
            }

            new AlertDialog($"{title}：{GetErrorMessage(exception)}")
            {
                Owner = this,
            }.ShowDialog();
        }

        private static AppRuntime GetRuntime() =>
            (System.Windows.Application.Current as App)?.Runtime
            ?? throw new InvalidOperationException("WPF application runtime is unavailable.");

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            if (e.Uri == null)
            {
                return;
            }

            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }

        private async void DebugHyperlink_Click(object sender, RoutedEventArgs e)
        {
            if (_isDebugging || !ViewModel.EnableAiFeatures)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(ViewModel.APIKey) &&
                !AIProviderDefaults.AllowsEmptyApiKey(ViewModel.AIBaseUrl))
            {
                new AlertDialog("请先输入 API Key；仅本机回环地址可以留空。").ShowDialog();
                return;
            }

            if (ViewModel.AIModel == AIProvider.Custom)
            {
                if (string.IsNullOrWhiteSpace(ViewModel.AIBaseUrl))
                {
                    new AlertDialog("自定义模型请先输入 API 地址。").ShowDialog();
                    return;
                }

                if (string.IsNullOrWhiteSpace(ViewModel.AIModelName))
                {
                    new AlertDialog("自定义模型请先输入模型名称。").ShowDialog();
                    return;
                }
            }

            _isDebugging = true;
            AcquireWaitCursor();
            var requestCancellation = new CancellationTokenSource();
            _debugCancellation = requestCancellation;
            AI2? ai = null;

            try
            {
                ai = new AI2(
                    ViewModel.APIKey,
                    ViewModel.AIModel,
                    "你是一个接口连通性测试助手。",
                    ViewModel.AIModelName,
                    ViewModel.AIBaseUrl);
                await ai.AskAsync(
                    "请只回复“调试成功”。",
                    "ping",
                    requestCancellation.Token);
                if (_isClosed || requestCancellation.IsCancellationRequested)
                {
                    return;
                }

                new AlertDialog("调试成功，当前 AI 配置可以正常连通。").ShowDialog();
            }
            catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (!_isClosed)
                {
                    new AlertDialog($"调试失败：{GetErrorMessage(ex)}").ShowDialog();
                }
            }
            finally
            {
                ai?.Dispose();
                if (ReferenceEquals(_debugCancellation, requestCancellation))
                {
                    _debugCancellation = null;
                }

                requestCancellation.Dispose();
                ReleaseWaitCursor();
                _isDebugging = false;
            }
        }

        private void AcquireWaitCursor()
        {
            Mouse.OverrideCursor = Cursors.Wait;
            _ownsWaitCursor = true;
        }

        private void ReleaseWaitCursor()
        {
            if (!_ownsWaitCursor)
            {
                return;
            }

            _ownsWaitCursor = false;
            Mouse.OverrideCursor = null;
        }

        private static string GetErrorMessage(Exception ex)
        {
            Exception current = ex;
            while (current.InnerException != null)
            {
                current = current.InnerException;
            }

            string message = current.Message.Replace("\r", " ").Replace("\n", " ").Trim();
            if (message.Length > 48)
            {
                message = message[..48] + "...";
            }

            return string.IsNullOrWhiteSpace(message) ? "未知错误" : message;
        }

        private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
        {
            Regex regex = new Regex("[^0-9.]+");
            e.Handled = regex.IsMatch(e.Text);
        }

        private void TextBoxPasting(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                string text = (string)e.DataObject.GetData(typeof(string));
                Regex regex = new Regex("[^0-9.]+");
                if (regex.IsMatch(text))
                {
                    e.CancelCommand();
                }
            }
            else
            {
                e.CancelCommand();
            }
        }

        private void TextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox && !double.TryParse(textBox.Text, out _))
            {
                BindingExpression? bindingExpression = textBox.GetBindingExpression(TextBox.TextProperty);
                bindingExpression?.UpdateTarget();
            }
        }

        private void Grid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                try
                {
                    DragMove();
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        private void NumberValidationTextBox2(object sender, TextCompositionEventArgs e)
        {
            Regex regex = new Regex("[^0-9]+");
            e.Handled = regex.IsMatch(e.Text);
        }

        private void TextBoxPasting2(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                string text = (string)e.DataObject.GetData(typeof(string));
                Regex regex = new Regex("[^0-9]+");
                if (regex.IsMatch(text))
                {
                    e.CancelCommand();
                }
            }
            else
            {
                e.CancelCommand();
            }
        }

        private void TextBox_LostFocus2(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox && !double.TryParse(textBox.Text, out _))
            {
                BindingExpression? bindingExpression = textBox.GetBindingExpression(TextBox.TextProperty);
                bindingExpression?.UpdateTarget();
            }
        }

        private void ShortcutButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button ||
                button.Tag is not string tag ||
                !Enum.TryParse(tag, ignoreCase: false, out EditableShortcut shortcut))
            {
                return;
            }

            CancelShortcutCapture();
            _shortcutCaptureButton = button;
            _shortcutBeingCaptured = shortcut;
            button.SetCurrentValue(
                ContentControl.ContentProperty,
                LocalizationService.Current.T(
                    IsModifierOnlyShortcut(shortcut) ? "请按修饰键…" : "请按快捷键…"));
        }

        private void CaptureShortcutPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_shortcutCaptureButton is null || _shortcutBeingCaptured is null)
            {
                return;
            }

            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            e.Handled = true;
            if (key == Key.Escape)
            {
                CancelShortcutCapture();
                return;
            }

            if (IsModifierKey(key))
            {
                _capturedModifiers |=
                    ToShortcutModifiers(Keyboard.Modifiers) |
                    GetModifierForKey(key);
                if (IsModifierOnlyShortcut(_shortcutBeingCaptured.Value))
                {
                    _shortcutCaptureButton.SetCurrentValue(
                        ContentControl.ContentProperty,
                        ShortcutGesturePresentation.FormatModifiers(_capturedModifiers) + " …");
                }

                return;
            }

            if (IsModifierOnlyShortcut(_shortcutBeingCaptured.Value))
            {
                _shortcutCaptureButton.SetCurrentValue(
                    ContentControl.ContentProperty,
                    LocalizationService.Current.T("只需按修饰键…"));
                return;
            }

            int virtualKey = KeyInterop.VirtualKeyFromKey(key);
            CompleteShortcutCapture(new ShortcutGesture(
                ToShortcutModifiers(Keyboard.Modifiers),
                virtualKey));
        }

        private void CaptureShortcutPreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (_shortcutCaptureButton is null ||
                _shortcutBeingCaptured is not EditableShortcut shortcut ||
                !IsModifierOnlyShortcut(shortcut))
            {
                return;
            }

            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (!IsModifierKey(key))
            {
                return;
            }

            e.Handled = true;
            _capturedModifiers |=
                ToShortcutModifiers(Keyboard.Modifiers) |
                GetModifierForKey(key);
            CompleteShortcutModifiers(_capturedModifiers);
        }

        private void CaptureShortcutPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            RecordMainWindowMouseActivity();
            if (_shortcutCaptureButton is null || _shortcutBeingCaptured is null)
            {
                return;
            }

            e.Handled = true;
            if (IsModifierOnlyShortcut(_shortcutBeingCaptured.Value))
            {
                _shortcutCaptureButton.SetCurrentValue(
                    ContentControl.ContentProperty,
                    LocalizationService.Current.T("只需按修饰键…"));
                return;
            }

            CompleteShortcutCapture(new ShortcutGesture(
                ToShortcutModifiers(Keyboard.Modifiers),
                e.ChangedButton switch
                {
                    MouseButton.Left => ShortcutMouseButton.Left,
                    MouseButton.Right => ShortcutMouseButton.Right,
                    MouseButton.Middle => ShortcutMouseButton.Middle,
                    MouseButton.XButton1 => ShortcutMouseButton.XButton1,
                    MouseButton.XButton2 => ShortcutMouseButton.XButton2,
                    _ => throw new ArgumentOutOfRangeException(nameof(e)),
                }));
        }

        private void CompleteShortcutCapture(ShortcutGesture gesture)
        {
            Button? button = _shortcutCaptureButton;
            EditableShortcut? shortcut = _shortcutBeingCaptured;
            if (button is null || shortcut is null)
            {
                return;
            }

            if (!ViewModel.TrySetShortcut(shortcut.Value, gesture, out string error))
            {
                CancelShortcutCapture();
                new AlertDialog(error) { Owner = this }.ShowDialog();
                return;
            }

            CancelShortcutCapture();
        }

        private void CompleteShortcutModifiers(ShortcutModifiers modifiers)
        {
            EditableShortcut? shortcut = _shortcutBeingCaptured;
            if (shortcut is null)
            {
                return;
            }

            if (!ViewModel.TrySetShortcutModifiers(shortcut.Value, modifiers, out string error))
            {
                CancelShortcutCapture();
                new AlertDialog(error) { Owner = this }.ShowDialog();
                return;
            }

            CancelShortcutCapture();
        }

        private void CancelShortcutCapture()
        {
            Button? button = _shortcutCaptureButton;
            _shortcutCaptureButton = null;
            _shortcutBeingCaptured = null;
            _capturedModifiers = ShortcutModifiers.None;
            button?.GetBindingExpression(ContentControl.ContentProperty)?.UpdateTarget();
        }

        private static bool IsModifierOnlyShortcut(EditableShortcut shortcut) =>
            shortcut is EditableShortcut.AbsoluteEntriesModifiers or
                EditableShortcut.VisibleEntriesModifiers;

        private static bool IsModifierKey(Key key) =>
            key is Key.LeftAlt or Key.RightAlt or
                Key.LeftCtrl or Key.RightCtrl or
                Key.LeftShift or Key.RightShift or
                Key.LWin or Key.RWin;

        private static ShortcutModifiers GetModifierForKey(Key key) =>
            key switch
            {
                Key.LeftAlt or Key.RightAlt => ShortcutModifiers.Alt,
                Key.LeftCtrl or Key.RightCtrl => ShortcutModifiers.Control,
                Key.LeftShift or Key.RightShift => ShortcutModifiers.Shift,
                Key.LWin or Key.RWin => ShortcutModifiers.Windows,
                _ => ShortcutModifiers.None,
            };

        private static ShortcutModifiers ToShortcutModifiers(ModifierKeys modifiers)
        {
            ShortcutModifiers result = ShortcutModifiers.None;
            if ((modifiers & ModifierKeys.Alt) != 0)
            {
                result |= ShortcutModifiers.Alt;
            }

            if ((modifiers & ModifierKeys.Control) != 0)
            {
                result |= ShortcutModifiers.Control;
            }

            if ((modifiers & ModifierKeys.Shift) != 0)
            {
                result |= ShortcutModifiers.Shift;
            }

            if ((modifiers & ModifierKeys.Windows) != 0)
            {
                result |= ShortcutModifiers.Windows;
            }

            return result;
        }

        private void OpenWelcomeTutorial_Click(object sender, MouseButtonEventArgs e)
        {
            OpenWelcomeTutorial_Click(sender, (RoutedEventArgs)e);
        }
    }
}
