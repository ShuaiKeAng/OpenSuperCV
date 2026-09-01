using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SuperCV
{
    /// <summary>
    /// MyTextBox.xaml 的交互逻辑
    /// </summary>
    public partial class MyTextBox : UserControl
    {
        private const string FuzzySearchPrefix = "/fs ";
        private const string AiQuestionPrefix = "/ai ";
        private static readonly Geometry FuzzySearchShortcutIcon = Geometry.Parse(
            "M24,18.1c-0.1,0-0.3,0-0.4-0.1l-3.3-3.3c-0.2-0.2-0.5-0.2-0.7,0l-3.3,3.3c-0.2,0.2-0.5,0.2-0.7,0s-0.2-0.5,0-0.7l3.3-3.3c0.6-0.6,1.5-0.6,2.1,0l3.3,3.3c0.2,0.2,0.2,0.5,0,0.7C24.3,18,24.1,18.1,24,18.1z M20,27.1c-0.3,0-0.5-0.2-0.5-0.5v-12c0-0.3,0.2-0.5,0.5-0.5s0.5,0.2,0.5,0.5v12C20.5,26.8,20.3,27.1,20,27.1z");
        private static readonly Geometry AiQuestionShortcutIcon = Geometry.Parse(
            "M20,26.5c-0.4,0-0.8-0.1-1.1-0.4l-3.3-3.3c-0.2-0.2-0.2-0.5,0-0.7s0.5-0.2,0.7,0l3.3,3.3c0.2,0.2,0.5,0.2,0.7,0l3.3-3.3c0.2-0.2,0.5-0.2,0.7,0s0.2,0.5,0,0.7l-3.3,3.3C20.8,26.4,20.4,26.5,20,26.5z M20,25.9c-0.3,0-0.5-0.2-0.5-0.5v-12c0-0.3,0.2-0.5,0.5-0.5s0.5,0.2,0.5,0.5v12C20.5,25.7,20.3,25.9,20,25.9z");
        private static readonly Geometry FilterModeShortcutIcon = Geometry.Parse(
            "M17.6,24.5c-0.1,0-0.3,0-0.4-0.1l-3.3-3.3c-0.6-0.6-0.6-1.5,0-2.1l3.3-3.3c0.2-0.2,0.5-0.2,0.7,0s0.2,0.5,0,0.7l-3.3,3.3c-0.2,0.2-0.2,0.5,0,0.7l3.3,3.3c0.2,0.2,0.2,0.5,0,0.7C17.8,24.5,17.7,24.5,17.6,24.5z M22.4,24.5c-0.1,0-0.3,0-0.4-0.1c-0.2-0.2-0.2-0.5,0-0.7l3.3-3.3c0.2-0.2,0.2-0.5,0-0.7l-3.3-3.3c-0.2-0.2-0.2-0.5,0-0.7s0.5-0.2,0.7,0l3.3,3.3c0.6,0.6,0.6,1.5,0,2.1l-3.3,3.3C22.7,24.5,22.6,24.5,22.4,24.5z M26.6,20.5h-12c-0.3,0-0.5-0.2-0.5-0.5s0.2-0.5,0.5-0.5h12c0.3,0,0.5,0.2,0.5,0.5S26.8,20.5,26.6,20.5z");
        private const double FilterModeHintFontSize = 12;

        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.Register("Input", typeof(bool), typeof(MyTextBox),
        new PropertyMetadata(false, OnIsActiveChanged));
        public event EventHandler? SearchCancel;
        public event EventHandler<string>? EnterKeyDown;
        public event EventHandler? FilterModeChanged;
        private readonly DispatcherTimer _commandHintTimer;
        private readonly DispatcherTimer _commandHintRevealTimer;
        private int _allFilterHintIndex;
        private bool _isCommandHintReady;
        private SearchFilterMode _filterMode = SearchFilterMode.All;

        
        public MyTextBox()
        {
            InitializeComponent();
            VisualStateManager.GoToState(this, "Button", true);
            ICON.Child = (Path)FindResource("Search");

            _commandHintTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _commandHintTimer.Tick += CommandHintTimer_Tick;
            _commandHintRevealTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _commandHintRevealTimer.Tick += CommandHintRevealTimer_Tick;
            Loaded += MyTextBox_Loaded;
            Unloaded += MyTextBox_Unloaded;

            textBox.PreviewKeyDown += (sender, e) =>
            {
                if (string.IsNullOrEmpty(Text) &&
                    (e.Key == Key.Left || e.Key == Key.Right))
                {
                    ChangeFilterMode(e.Key == Key.Left ? -1 : 1);
                    e.Handled = true;
                    return;
                }

                if (string.IsNullOrEmpty(Text) &&
                    _filterMode == SearchFilterMode.All &&
                    AiFeatureAvailabilityState.Current.IsEnabled &&
                    (e.Key == Key.Up || e.Key == Key.Down))
                {
                    Text = e.Key == Key.Up ? FuzzySearchPrefix : AiQuestionPrefix;
                    textBox.CaretIndex = Text.Length;
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Enter)
                {
                    _lastText = Text;
                    ChangeEnterIcon();
                    EnterKeyDown?.Invoke(this, Text);
                    e.Handled = true;
                }
            };





        }

        public bool Input
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }
        private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (MyTextBox)d;
            if (control.Input && string.IsNullOrEmpty(control.Text))
            {
                control.StartCommandHintRevealDelay();
            }
            else
            {
                control._commandHintRevealTimer.Stop();
                control._isCommandHintReady = false;
            }

            control.UpdateVisualState();
        }

        // 更新视觉状态
        private void UpdateVisualState()
        {
            if (Input && Text == "")
            {
                VisualStateManager.GoToState(this, "Back", true);
            }
            else if (Input && Text !="")
            {
                VisualStateManager.GoToState(this, "Edit", true);
            }
            else
            {
                VisualStateManager.GoToState(this, "Button", true);
            }

            UpdateCommandHint();
        }

        public string Text
        {
            get => textBox.Text;
            set => textBox.Text = value;
        }

        public SearchFilterMode FilterMode => _filterMode;

        public void ResetSearch()
        {
            _lastText = string.Empty;
            Text = string.Empty;
            EnterIcon.Visibility = Visibility.Collapsed;
            ResetFilterMode();
            SearchCancel?.Invoke(this, EventArgs.Empty);
            Input = false;
            ICON.Child = (Path)FindResource("Search");
            UpdateVisualState();
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            if (Input)
            {
                
                if (Text == "")
                {
                    EnterIcon.Visibility = Visibility.Collapsed;
                    ResetFilterMode();
                    SearchCancel?.Invoke(this, EventArgs.Empty);
                    ICON.Child = (Path)FindResource("Search");
                    Input = false;
                }

                    Text = "";
            }
            else
            {
                Input = true;
                ICON.Child = (Path)FindResource("Right");
            }

        }
        private string _lastText = "";
        private void textBox_TextChanged(object sender, TextChangedEventArgs e)
        {

            if (Text == "")
            {
                ICON.Child = (Path)FindResource("Right");
                


            }
            else if(Text != "" && Input)
            {
                var clearIcon = (Path)FindResource("Close");
                clearIcon.RenderTransformOrigin = new Point(0.5, 0.5);
                clearIcon.RenderTransform = new ScaleTransform(0.8, 0.8);
                ICON.Child = clearIcon;
            }

            ChangeEnterIcon();
            UpdateCommandHint();
            
        }


        private void ChangeEnterIcon()
        {
            if (_lastText != Text)
            {
                EnterIcon.Visibility = Visibility.Visible;
            }
            else
            {
                EnterIcon.Visibility = Visibility.Collapsed;
            }
        }

        private void MyTextBox_Loaded(object sender, RoutedEventArgs e)
        {
            AiFeatureAvailabilityState.Current.Changed += OnAiFeatureAvailabilityChanged;
            LocalizationService.Current.LanguageChanged += OnLanguageChanged;
            _commandHintTimer.Start();
            UpdateCommandHint();
        }

        private void MyTextBox_Unloaded(object sender, RoutedEventArgs e)
        {
            AiFeatureAvailabilityState.Current.Changed -= OnAiFeatureAvailabilityChanged;
            LocalizationService.Current.LanguageChanged -= OnLanguageChanged;
            _commandHintTimer.Stop();
            _commandHintRevealTimer.Stop();
        }

        private void OnAiFeatureAvailabilityChanged(object? sender, EventArgs e) =>
            UpdateCommandHint();

        private void OnLanguageChanged(object? sender, EventArgs e) =>
            UpdateCommandHint();

        private void CommandHintTimer_Tick(object? sender, EventArgs e)
        {
            _allFilterHintIndex = (_allFilterHintIndex + 1) % 3;
            UpdateCommandHint();
        }

        private void StartCommandHintRevealDelay()
        {
            _isCommandHintReady = false;
            _commandHintRevealTimer.Stop();
            _commandHintRevealTimer.Start();
        }

        private void CommandHintRevealTimer_Tick(object? sender, EventArgs e)
        {
            _commandHintRevealTimer.Stop();
            _isCommandHintReady = true;
            UpdateCommandHint();

            if (Input && string.IsNullOrEmpty(Text) && textBox.IsEnabled)
            {
                textBox.Focus();
                textBox.CaretIndex = 0;
            }
        }

        private void UpdateCommandHint()
        {
            CommandHintDirectionIcon.Visibility = Visibility.Collapsed;
            var commandHintParts = GetCommandHintParts();
            if (commandHintParts is null)
            {
                return;
            }

            var (commandHint, commandHintPrefix, commandHintDescription) = commandHintParts.Value;
            commandHintPrefix.FontSize = textBox.FontSize;

            if (!Input ||
                !_isCommandHintReady)
            {
                commandHint.Visibility = Visibility.Collapsed;
                return;
            }

            if (string.IsNullOrEmpty(Text))
            {
                SetEmptySearchHint(commandHintPrefix, commandHintDescription);
                commandHint.Visibility = Visibility.Visible;
                AlignCommandHintToInsertionPoint(commandHint);
                ShowCommandHintShortcutIcon();
                return;
            }

            string? completion = GetCommandCompletion(Text);
            if (completion is null)
            {
                commandHint.Visibility = Visibility.Collapsed;
                return;
            }

            commandHintPrefix.Text = completion;
            commandHintDescription.Text = string.Empty;
            commandHint.Visibility = Visibility.Visible;
            AlignCommandHintToInsertionPoint(commandHint);
        }

        private void ShowCommandHintShortcutIcon()
        {
            if (EnterIcon.Visibility != Visibility.Collapsed)
            {
                return;
            }

            CommandHintDirectionIcon.Data = _filterMode switch
            {
                SearchFilterMode.Text => null,
                SearchFilterMode.Image => null,
                _ => _allFilterHintIndex switch
                {
                    0 when AiFeatureAvailabilityState.Current.IsEnabled => FuzzySearchShortcutIcon,
                    1 when AiFeatureAvailabilityState.Current.IsEnabled => AiQuestionShortcutIcon,
                    _ => FilterModeShortcutIcon,
                },
            };
            if (CommandHintDirectionIcon.Data is null)
            {
                return;
            }

            CommandHintDirectionIcon.Visibility = Visibility.Visible;
        }

        private void SetEmptySearchHint(TextBlock prefix, TextBlock description)
        {
            switch (_filterMode)
            {
                case SearchFilterMode.Text:
                    SetFilterModeHint(prefix, description, "文本");
                    return;

                case SearchFilterMode.Image:
                    SetFilterModeHint(prefix, description, "图片");
                    return;

                case SearchFilterMode.All:
                    SetAllFilterHint(prefix, description);
                    return;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void SetAllFilterHint(TextBlock prefix, TextBlock description)
        {
            if (!AiFeatureAvailabilityState.Current.IsEnabled || _allFilterHintIndex == 2)
            {
                SetFilterModeHint(prefix, description, "切换文本与图片");
                return;
            }

            bool showFuzzySearch = _allFilterHintIndex == 0;
            prefix.Text = showFuzzySearch ? FuzzySearchPrefix : AiQuestionPrefix;
            description.Text = LocalizationService.Current.T(
                showFuzzySearch ? "模糊搜索" : "AI问答");
        }

        private static void SetFilterModeHint(
            TextBlock prefix,
            TextBlock description,
            string text)
        {
            prefix.FontSize = FilterModeHintFontSize;
            prefix.Text = LocalizationService.Current.T(text);
            description.Text = string.Empty;
        }

        private void ChangeFilterMode(int direction)
        {
            int current = (int)_filterMode;
            int next = Math.Clamp(current + direction, (int)SearchFilterMode.Text, (int)SearchFilterMode.Image);
            if (next == current)
            {
                return;
            }

            _filterMode = (SearchFilterMode)next;
            FilterModeChanged?.Invoke(this, EventArgs.Empty);
            UpdateCommandHint();
            RestoreInputFocusAfterFilterChange();
        }

        private void ResetFilterMode()
        {
            if (_filterMode == SearchFilterMode.All)
            {
                return;
            }

            _filterMode = SearchFilterMode.All;
            FilterModeChanged?.Invoke(this, EventArgs.Empty);
        }

        private void RestoreInputFocusAfterFilterChange()
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() =>
                {
                    if (!Input || !string.IsNullOrEmpty(Text) || !textBox.IsEnabled)
                    {
                        return;
                    }

                    Keyboard.Focus(textBox);
                    textBox.CaretIndex = 0;
                }));
        }

        private void AlignCommandHintToInsertionPoint(StackPanel commandHint)
        {
            textBox.UpdateLayout();
            Rect characterBounds = textBox.GetRectFromCharacterIndex(0, trailingEdge: false);
            if (characterBounds.IsEmpty)
            {
                characterBounds = textBox.GetRectFromCharacterIndex(0, trailingEdge: true);
            }

            if (characterBounds.IsEmpty)
            {
                return;
            }

            commandHint.Margin = new Thickness(
                characterBounds.X - textBox.BorderThickness.Left,
                0,
                0,
                0);
            commandHint.VerticalAlignment = VerticalAlignment.Center;
        }

        private (StackPanel Panel, TextBlock Prefix, TextBlock Description)? GetCommandHintParts()
        {
            textBox.ApplyTemplate();
            ControlTemplate? template = textBox.Template;
            if (template is null)
            {
                return null;
            }

            var commandHint = template.FindName("CommandHintPanel", textBox) as StackPanel;
            var commandHintPrefix = template.FindName("CommandHintPrefixPart", textBox) as TextBlock;
            var commandHintDescription = template.FindName("CommandHintDescriptionPart", textBox) as TextBlock;
            return commandHint is not null &&
                   commandHintPrefix is not null &&
                   commandHintDescription is not null
                ? (commandHint, commandHintPrefix, commandHintDescription)
                : null;
        }

        private static string? GetCommandCompletion(string text)
        {
            if (text == "/")
            {
                return FuzzySearchPrefix;
            }

            if (!string.Equals(text, FuzzySearchPrefix, StringComparison.OrdinalIgnoreCase) &&
                FuzzySearchPrefix.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            {
                return FuzzySearchPrefix;
            }

            if (!string.Equals(text, AiQuestionPrefix, StringComparison.OrdinalIgnoreCase) &&
                AiQuestionPrefix.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            {
                return AiQuestionPrefix;
            }

            return null;
        }

        private void textBox_GotFocus(object sender, RoutedEventArgs e)
        {
            UpdateVisualState();
            textBox.CaretIndex = textBox.Text.Length;
        }
    }
}
