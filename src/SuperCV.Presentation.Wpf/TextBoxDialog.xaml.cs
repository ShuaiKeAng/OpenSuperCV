using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SuperCV;

public partial class TextBoxDialog : Window, INotifyPropertyChanged
{
    private string _value = string.Empty;
    private double _inputShadowOpacity;
    private bool _confirmed;

    public TextBoxDialog(string title, string placeholder, string value = "")
    {
        InitializeComponent();
        TitleText = LocalizationService.Current.T(title ?? string.Empty);
        Placeholder = LocalizationService.Current.T(placeholder ?? string.Empty);
        Value = value ?? string.Empty;
        DataContext = this;
        ContentRendered += OnContentRendered;
    }

    public string TitleText { get; }

    public string Placeholder { get; }

    public string Value
    {
        get => _value;
        set
        {
            if (_value == value)
            {
                return;
            }

            _value = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    public double InputShadowOpacity
    {
        get => _inputShadowOpacity;
        private set
        {
            if (Math.Abs(_inputShadowOpacity - value) < double.Epsilon)
            {
                return;
            }

            _inputShadowOpacity = value;
            OnPropertyChanged();
        }
    }

    public new bool ShowDialog()
    {
        _ = base.ShowDialog();
        return _confirmed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Confirm()
    {
        string normalized = Value.Trim();
        if (normalized.Length == 0)
        {
            InputShadowOpacity = 0.5;
            InputTextBox.Focus();
            return;
        }

        Value = normalized;
        _confirmed = true;
        Close();
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;
        InputTextBox.Focus();
        InputTextBox.SelectAll();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e) => Confirm();

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _confirmed = false;
        Close();
    }

    private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e) =>
        InputShadowOpacity = 0;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Confirm();
            e.Handled = true;
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The mouse can be released between the state check and DragMove.
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
