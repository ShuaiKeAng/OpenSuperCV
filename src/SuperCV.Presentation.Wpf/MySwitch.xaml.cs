using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SuperCV
{
    public partial class MySwitch : UserControl, INotifyPropertyChanged
    {
        static MySwitch()
        {
            FrameworkElement.WidthProperty.OverrideMetadata(
                typeof(MySwitch),
                new FrameworkPropertyMetadata(
                    60.0,
                    FrameworkPropertyMetadataOptions.AffectsMeasure,
                    OnSizePropertyChanged));
        }

        // 开关状态依赖属性
        public static readonly DependencyProperty IsOnProperty =
            DependencyProperty.Register(nameof(IsOn), typeof(bool), typeof(MySwitch),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsOnChanged));

        // 尺寸相关依赖属性
        public static readonly DependencyProperty CornerRadiusProperty =
            DependencyProperty.Register(nameof(CornerRadius), typeof(double), typeof(MySwitch),
                new PropertyMetadata(15.0, OnSizePropertyChanged));

        // 颜色相关依赖属性
        public static readonly DependencyProperty SwitchColorOnProperty =
            DependencyProperty.Register(nameof(SwitchColorOn), typeof(Color), typeof(MySwitch),
                new PropertyMetadata(Color.FromRgb(76, 175, 80), OnColorPropertyChanged));

        public static readonly DependencyProperty SwitchColorOffProperty =
            DependencyProperty.Register(nameof(SwitchColorOff), typeof(Color), typeof(MySwitch),
                new PropertyMetadata(Color.FromRgb(204, 204, 204), OnColorPropertyChanged));

        public static readonly DependencyProperty SwitchColorOverProperty =
            DependencyProperty.Register(nameof(SwitchColorOver), typeof(Color), typeof(MySwitch),
                new PropertyMetadata(Color.FromRgb(56, 142, 60), OnColorPropertyChanged));

        public static readonly DependencyProperty SwitchColorOver2Property =
            DependencyProperty.Register(nameof(SwitchColorOver2), typeof(Color), typeof(MySwitch),
                new PropertyMetadata(Colors.White, OnColorPropertyChanged));

        public static readonly DependencyProperty ButtonForeColorProperty =
            DependencyProperty.Register(nameof(ButtonForeColor), typeof(Color), typeof(MySwitch),
                new PropertyMetadata(Colors.White, OnColorPropertyChanged));

        // 动画时长依赖属性（保留滑块位置动画）
        public static readonly DependencyProperty ThumbAnimationDurationProperty =
            DependencyProperty.Register(nameof(ThumbAnimationDuration), typeof(TimeSpan), typeof(MySwitch),
                new PropertyMetadata(TimeSpan.FromSeconds(0.2)));

        // CLR 属性包装器
        public bool IsOn
        {
            get { return (bool)GetValue(IsOnProperty); }
            set { SetValue(IsOnProperty, value); }
        }

        public double CornerRadius
        {
            get { return (double)GetValue(CornerRadiusProperty); }
            set { SetValue(CornerRadiusProperty, value); }
        }

        public Color SwitchColorOn
        {
            get { return (Color)GetValue(SwitchColorOnProperty); }
            set { SetValue(SwitchColorOnProperty, value); }
        }

        public Color SwitchColorOff
        {
            get { return (Color)GetValue(SwitchColorOffProperty); }
            set { SetValue(SwitchColorOffProperty, value); }
        }

        public Color SwitchColorOver
        {
            get { return (Color)GetValue(SwitchColorOverProperty); }
            set { SetValue(SwitchColorOverProperty, value); }
        }

        public Color SwitchColorOver2
        {
            get { return (Color)GetValue(SwitchColorOver2Property); }
            set { SetValue(SwitchColorOver2Property, value); }
        }

        public Color ButtonForeColor
        {
            get { return (Color)GetValue(ButtonForeColorProperty); }
            set { SetValue(ButtonForeColorProperty, value); }
        }

        public TimeSpan ThumbAnimationDuration
        {
            get { return (TimeSpan)GetValue(ThumbAnimationDurationProperty); }
            set { SetValue(ThumbAnimationDurationProperty, value); }
        }

        // 计算属性
        public double HeightValue => CornerRadius * 2;
        public double ThumbCornerRadius => CornerRadius * 3.0 / 4.0;
        public double ThumbSize => ThumbCornerRadius * 2;

        public event PropertyChangedEventHandler? PropertyChanged;

        // 路由事件
        public static readonly RoutedEvent SwitchChangedEvent =
            EventManager.RegisterRoutedEvent("SwitchChanged", RoutingStrategy.Bubble,
                typeof(RoutedEventHandler), typeof(MySwitch));

        public event RoutedEventHandler SwitchChanged
        {
            add { AddHandler(SwitchChangedEvent, value); }
            remove { RemoveHandler(SwitchChangedEvent, value); }
        }

        // 私有字段
        private bool _isMouseOver = false;
        private bool _isLoaded = false;

        public MySwitch()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;
            // 初始化颜色和位置
            InitializeBrushes();
            UpdateColors();
            UpdateThumbPosition(IsOn, true);
        }

        private static void OnIsOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as MySwitch;
            if (control != null && control._isLoaded)
            {
                control.UpdateThumbPosition((bool)e.NewValue, true);
                control.UpdateColors();
            }
        }

        private static void OnSizePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not MySwitch control)
            {
                return;
            }

            if (e.Property == CornerRadiusProperty)
            {
                control.OnPropertyChanged(nameof(HeightValue));
                control.OnPropertyChanged(nameof(ThumbCornerRadius));
                control.OnPropertyChanged(nameof(ThumbSize));
            }

            if (control._isLoaded)
            {
                control.UpdateThumbPosition(control.IsOn, false);
            }
        }

        private static void OnColorPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as MySwitch;
            if (control != null && control._isLoaded)
            {
                control.UpdateColors();
            }
        }

        private void InitializeBrushes()
        {
            // 确保Border有Background
            if (TrackBorder.Background == null || TrackBorder.Background.IsFrozen)
            {
                TrackBorder.Background = new SolidColorBrush(Colors.Transparent);
            }

            if (ThumbBorder.Background == null || ThumbBorder.Background.IsFrozen)
            {
                ThumbBorder.Background = new SolidColorBrush(Colors.Transparent);
            }
        }

        private void UpdateThumbPosition(bool isOn, bool useAnimation)
        {
            if (ThumbTransform == null) return;

            double margin = CornerRadius - ThumbCornerRadius;
            double targetX = isOn ? Width - ThumbSize - margin : margin;

            if (useAnimation)
            {
                var animation = new DoubleAnimation(targetX, new Duration(ThumbAnimationDuration))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                ThumbTransform.BeginAnimation(TranslateTransform.XProperty, animation);
            }
            else
            {
                ThumbTransform.BeginAnimation(TranslateTransform.XProperty, null);
                ThumbTransform.X = targetX;
            }
        }

        private void UpdateColors()
        {
            if (!_isLoaded) return;

            Color targetTrackColor, targetThumbColor;

            if (_isMouseOver)
            {
                targetTrackColor = IsOn ? SwitchColorOn : SwitchColorOff;
                targetThumbColor = SwitchColorOver2;
            }
            else
            {
                targetTrackColor = IsOn ? SwitchColorOn : SwitchColorOff;
                targetThumbColor = ButtonForeColor;
            }

            // 直接设置颜色，不使用动画
            SetColorImmediately(TrackBorder, targetTrackColor);
            SetColorImmediately(ThumbBorder, targetThumbColor);
        }

        private static void SetColorImmediately(Border target, Color color)
        {
            try
            {
                // 清除可能的动画
                if (target.Background is SolidColorBrush brush)
                {
                    brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                    brush.Color = color;
                }
                else
                {
                    target.Background = new SolidColorBrush(color);
                }
            }
            catch (InvalidOperationException)
            {
                target.Background = new SolidColorBrush(color);
            }
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsEnabled || !_isLoaded) return;

            IsOn = !IsOn;

            // 触发事件
            RoutedEventArgs args = new RoutedEventArgs(SwitchChangedEvent, this);
            RaiseEvent(args);

            e.Handled = true;
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            if (_isLoaded)
            {
                UpdateThumbPosition(IsOn, false);
            }
        }

        private void Border_MouseEnter(object sender, MouseEventArgs e)
        {
            _isMouseOver = true;
            if (_isLoaded)
            {
                UpdateColors();
            }
        }

        private void Border_MouseLeave(object sender, MouseEventArgs e)
        {
            _isMouseOver = false;
            if (_isLoaded)
            {
                UpdateColors();
            }
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
