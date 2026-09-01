using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace SuperCV;

public static class TextBoxFocusBehavior
{
    public static readonly DependencyProperty ClearFocusOnEnterProperty =
        DependencyProperty.RegisterAttached(
            "ClearFocusOnEnter",
            typeof(bool),
            typeof(TextBoxFocusBehavior),
            new PropertyMetadata(false, OnClearFocusOnEnterChanged));

    public static bool GetClearFocusOnEnter(DependencyObject element) =>
        (bool)element.GetValue(ClearFocusOnEnterProperty);

    public static void SetClearFocusOnEnter(DependencyObject element, bool value) =>
        element.SetValue(ClearFocusOnEnterProperty, value);

    private static void OnClearFocusOnEnterChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is not TextBox textBox)
        {
            return;
        }

        if ((bool)eventArgs.OldValue)
        {
            textBox.PreviewKeyDown -= TextBox_PreviewKeyDown;
        }

        if ((bool)eventArgs.NewValue)
        {
            textBox.PreviewKeyDown += TextBox_PreviewKeyDown;
        }
    }

    private static void TextBox_PreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter && sender is TextBox { AcceptsReturn: false })
        {
            ((TextBox)sender).Dispatcher.BeginInvoke(
                Keyboard.ClearFocus,
                DispatcherPriority.Input);
        }
    }
}
