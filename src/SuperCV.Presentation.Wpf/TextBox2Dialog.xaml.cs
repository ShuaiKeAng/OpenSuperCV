using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SuperCV
{
    /// <summary>
    /// Dialog.xaml 的交互逻辑
    /// </summary>
    public partial class TextBox2Dialog : Window, INotifyPropertyChanged
    {
        private string _labelName = string.Empty;
        private string _prompt = string.Empty;
        private string _title = string.Empty;
        private string _tip1 = string.Empty;
        private string _tip2 = string.Empty;
        private double _shadow1;
        private double _shadow2;
        private bool _confirm;
        private readonly bool _allowNull;
        private readonly Func<Task>? _openPromptDocumentAsync;

        public TextBox2Dialog(
            string title = "",
            string tip1 = "",
            string tip2 = "",
            string label = "",
            string prompt = "",
            bool allowNull = false,
            Func<Task>? openPromptDocumentAsync = null)
        {
            InitializeComponent();
            TitleText = LocalizationService.Current.T(title);
            Tip1 = LocalizationService.Current.T(tip1);
            Tip2 = LocalizationService.Current.T(tip2);
            LabelName = label;
            Prompt = prompt;
            DataContext = this;
            Shadow1 = 0.0;
            Shadow2 = 0.0;
            _allowNull = allowNull;
            _openPromptDocumentAsync = openPromptDocumentAsync;
            if (_openPromptDocumentAsync is not null)
            {
                MyTextBox2.Padding = new Thickness(8, 8, 42, 8);
                OpenPromptDocumentButton.Visibility = Visibility.Visible;
            }
        }

        public new bool ShowDialog()
        {
            base.ShowDialog();
        
            return _confirm;
        }

        public string LabelName
        {
            get => _labelName;
            set { _labelName = value ?? string.Empty; OnPropertyChanged(); }
        }
        public string Prompt
        {
            get => _prompt;
            set { _prompt = value ?? string.Empty; OnPropertyChanged(); }
        }
        public string TitleText
        {
            get => _title;
            set { _title = value ?? string.Empty; OnPropertyChanged(); }
        }
        public string Tip1
        {
            get => _tip1;
            set { _tip1 = value ?? string.Empty; OnPropertyChanged(); }
        }
        public string Tip2
        {
            get => _tip2;
            set { _tip2 = value ?? string.Empty; OnPropertyChanged(); }
        }

        public double Shadow1
        {
            get => _shadow1;
            set { _shadow1 = value; OnPropertyChanged(); }
        }

        public double Shadow2
        {
            get => _shadow2;
            set { _shadow2 = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void Button_OK_Click(object sender, RoutedEventArgs e)
        {
            bool hasLabel = !string.IsNullOrEmpty(LabelName);
            bool hasPrompt = !string.IsNullOrEmpty(Prompt);

            if (_allowNull || (hasLabel && hasPrompt))
            {
                _confirm = true;
                Close();
            }
            else if (!hasLabel)
            {
                Shadow1 = 0.5;
            }
            else
            {
                Shadow2 = 0.5;
            }
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            _confirm = false;
            Close();
        }

        private void MyTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            Shadow1 = 0.0;
        }

        private void MyTextBox2_TextChanged(object sender, TextChangedEventArgs e)
        {
            Shadow2 = 0.0;
        }

        private async void OpenPromptDocument_Click(object sender, RoutedEventArgs e)
        {
            if (_openPromptDocumentAsync is null)
            {
                return;
            }

            OpenPromptDocumentButton.IsEnabled = false;
            try
            {
                await _openPromptDocumentAsync().ConfigureAwait(true);
                _confirm = false;
                Close();
            }
            catch (Exception exception)
            {
                new AlertDialog($"打开指令文件失败: {exception.Message}").ShowDialog();
                OpenPromptDocumentButton.IsEnabled = true;
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
                    // The button may release the mouse between the state check and DragMove.
                }
            }
        }
    }
}
