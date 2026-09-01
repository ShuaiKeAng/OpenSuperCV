using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace SuperCV
{
    /// <summary>
    /// AlertDialog.xaml 的交互逻辑
    /// </summary>
    public partial class AlertDialog : Window, INotifyPropertyChanged
    {
        private bool _confirm;
        private string _tip = string.Empty;
        private string _primaryButtonText = "确定";
        private string _sourceTip = string.Empty;
        private string _sourcePrimaryButtonText = "确定";

        public AlertDialog(string tip = "", string primaryButtonText = "确定")
        {
            InitializeComponent();
            ApplyWorkAreaHeightLimit();
            Tip = tip;
            PrimaryButtonText = primaryButtonText;
            DataContext = this;
            LocalizationService.Current.PropertyChanged += Localization_PropertyChanged;
        }

        public new bool ShowDialog()
        {
            base.ShowDialog();

            return _confirm;
        }

        public string Tip
        {
            get => _tip;
            set
            {
                string newValue = value ?? string.Empty;
                string localized = LocalizationService.Current.T(newValue);
                if (_sourceTip == newValue && _tip == localized)
                {
                    return;
                }

                _sourceTip = newValue;
                _tip = localized;
                OnPropertyChanged();
            }
        }

        public string PrimaryButtonText
        {
            get => _primaryButtonText;
            set
            {
                string newValue = string.IsNullOrWhiteSpace(value) ? "确定" : value;
                string localized = LocalizationService.Current.T(newValue);
                if (_sourcePrimaryButtonText == newValue && _primaryButtonText == localized)
                {
                    return;
                }

                _sourcePrimaryButtonText = newValue;
                _primaryButtonText = localized;
                OnPropertyChanged();
            }
        }

        public double MessageMaxHeight { get; private set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected override void OnClosed(EventArgs e)
        {
            LocalizationService.Current.PropertyChanged -= Localization_PropertyChanged;
            base.OnClosed(e);
        }

        private void Localization_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(LocalizationService.Language))
            {
                return;
            }

            Tip = _sourceTip;
            PrimaryButtonText = _sourcePrimaryButtonText;
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void Button_OK_Click(object sender, RoutedEventArgs e)
        {
            _confirm = true;
            Close();
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            _confirm = false;
            Close();
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

        private void ApplyWorkAreaHeightLimit()
        {
            const double screenSafetyMargin = 32;
            const double dialogChromeHeight = 170;
            Rect workArea = SystemParameters.WorkArea;
            MaxHeight = Math.Max(MinHeight, workArea.Height - screenSafetyMargin);
            MessageMaxHeight = Math.Max(80, MaxHeight - dialogChromeHeight);
        }
    }
}
