using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SuperCV
{
    /// <summary>
    /// CapsuleButton.xaml 的交互逻辑
    /// </summary>
    public partial class CapsuleButton : UserControl, INotifyPropertyChanged
    {
        private const double ShadowInset = 4;

        static CapsuleButton()
        {
            FrameworkElement.MaxWidthProperty.OverrideMetadata(
                typeof(CapsuleButton),
                new FrameworkPropertyMetadata(
                    double.PositiveInfinity,
                    FrameworkPropertyMetadataOptions.AffectsMeasure,
                    OnMaxWidthChanged));
        }

        public CapsuleButton()
        {
            InitializeComponent();
            DataContext = this;
            UpdateSize();
        }

        public static readonly DependencyProperty CornerRadiusProperty =
            DependencyProperty.Register(
                nameof(CornerRadius),
                typeof(CornerRadius),
                typeof(CapsuleButton),
                new PropertyMetadata(new CornerRadius(20), OnCornerRadiusChanged));

        public static readonly DependencyProperty CapsuleBackgroundProperty =
            DependencyProperty.Register(
                nameof(CapsuleBackground),
                typeof(Brush),
                typeof(CapsuleButton),
                new FrameworkPropertyMetadata(
                    Brushes.LightGray,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(
                nameof(Text),
                typeof(string),
                typeof(CapsuleButton),
                new PropertyMetadata("胶囊文本", OnTextChanged, CoerceText));

        public event PropertyChangedEventHandler? PropertyChanged;

        // 事件声明
        public event RoutedEventHandler? TextClicked;
        public event RoutedEventHandler? CloseClicked;
        public event RoutedEventHandler? EditClicked;


        public CornerRadius CornerRadius
        {
            get { return (CornerRadius)GetValue(CornerRadiusProperty); }
            set { SetValue(CornerRadiusProperty, value); }
        }

        private double _cornerRadiusButton;
        private double _buttonHeight;
        private double _buttonMargin;

        public double CornerRadiusButton
        {
            get => _cornerRadiusButton;
            set
            {
                if (_cornerRadiusButton.Equals(value))
                {
                    return;
                }

                _cornerRadiusButton = value;
                OnPropertyChanged();
            }
        }

        public Brush CapsuleBackground
        {
            get => (Brush)GetValue(CapsuleBackgroundProperty);
            set => SetValue(CapsuleBackgroundProperty, value);
        }

        public double ButtonHeight
        {
            get => _buttonHeight;
            set
            {
                if (_buttonHeight.Equals(value))
                {
                    return;
                }

                _buttonHeight = value;
                OnPropertyChanged();
            }
        }

        public double ButtonMargin
        {
            get => _buttonMargin;
            set
            {
                if (_buttonMargin.Equals(value))
                {
                    return;
                }

                _buttonMargin = value;
                OnPropertyChanged();
            }
        }

        public string Text
        {
            get { return (string)GetValue(TextProperty); }
            set { SetValue(TextProperty, value); }
        }



        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }


        private static void OnCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as CapsuleButton;
            control?.UpdateSize();
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as CapsuleButton;
            control?.UpdateSize();
        }

        private static object CoerceText(DependencyObject d, object? value) =>
            value as string ?? string.Empty;

        private static void OnMaxWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as CapsuleButton;
            control?.UpdateSize();
        }

        private void UpdateSize()
        {
            // Reserve an internal shadow gutter so effects stay inside the item's rectangular
            // render bounds instead of being clipped by the horizontal bookmark viewport.
            double radius = CornerRadius.TopLeft;
            Height = (radius * 2) + (ShadowInset * 2);
            MainBorder.Height = radius * 2;
            CornerRadiusButton = radius * 0.8;
            ButtonMargin = radius * 0.2;
            ButtonHeight = CornerRadiusButton * 2;
            // 测量文本所需宽度
            var textBlock = new TextBlock { Text = Text, FontSize = TextContent.FontSize };
            textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            // 计算总宽度：文本宽度 + 关闭按钮宽度 + 边距
            double textWidth = textBlock.DesiredSize.Width;
            double totalWidth = textWidth + 32 + (ShadowInset * 2);

            // 应用最大宽度限制
            Width = Math.Min(totalWidth, MaxWidth);

            // 更新边框圆角
            MainBorder.CornerRadius = new CornerRadius(radius);
        }
        private bool _mouseLeftDown;

        private void TextContent_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _mouseLeftDown = true;
        }
        private void TextContent_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_mouseLeftDown)
            {
                TextClicked?.Invoke(this, new RoutedEventArgs());
                _mouseLeftDown = false;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            CloseClicked?.Invoke(this, new RoutedEventArgs());
        }

        internal void CancelPendingPointerActions()
        {
            _mouseLeftDown = false;
            _mouseRightDown = false;
        }

        private bool _mouseRightDown;
        private void MainBorder_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            _mouseRightDown = true;
        }

        private void MainBorder_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_mouseRightDown)
            {
                EditClicked?.Invoke(this, new RoutedEventArgs());
                _mouseRightDown = false;
            }
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            _mouseLeftDown = false;
            _mouseRightDown = false;
            base.OnMouseLeave(e);
        }
    }
}
