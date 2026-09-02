using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using static SuperCV.CustomInstructionsManager;


namespace SuperCV
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class CV : Window, INotifyPropertyChanged
    {
        private static readonly Brush[] TagColors =
        {
            Brushes.Transparent,
            CreateTagBrush(201, 109, 109),
            CreateTagBrush(193, 133, 96),
            CreateTagBrush(151, 120, 176),
            CreateTagBrush(110, 154, 122),
            CreateTagBrush(111, 145, 184),
        };

        private static SolidColorBrush CreateTagBrush(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }

        private static readonly TimeSpan VerticalExitAnimationDuration =
            TimeSpan.FromMilliseconds(420);
        private static readonly TimeSpan ExitAnimationClosePadding =
            TimeSpan.FromMilliseconds(20);
        private static readonly TimeSpan ContentPressAnimationDuration =
            TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan ContentReleaseAnimationDuration =
            TimeSpan.FromMilliseconds(300);
        private const int GwlExStyle = -20;
        private const long WsExToolWindow = 0x00000080L;
        private const long WsExAppWindow = 0x00040000L;
        private static readonly nint HwndTopmost = new(-1);
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoOwnerZOrder = 0x0200;
        private const uint RestoreTopmostFlags =
            SwpNoSize | SwpNoMove | SwpNoActivate | SwpNoOwnerZOrder;
        private static readonly TimeSpan ActionButtonsTransitionDuration =
            TimeSpan.FromMilliseconds(420);
        private static readonly EasingFunctionBase ActionButtonsTransitionEasing =
            new QuarticEase { EasingMode = EasingMode.EaseInOut };
        private const double ContentPressedScale = 0.98;
        private const double ActionButtonsExpandedWidth = 35;
        private const double FullContentClipWidth = 270;
        private const double ContentBorderInset = 2;
        private const double ContentClipHeight = 98;
        private const double SpringImpulseVelocity = 250;
        private const double MaximumSpringVelocity = 300;
        private const double BottomRestoreSpringTriggerDistance = 32;
        internal const int MaximumDisplayPreviewCharacters = 2048;

        private readonly CancellationTokenSource _lifetimeCancellation = new();
        private readonly BitmapCache _textBorderShadowCache = new()
        {
            EnableClearType = false,
            RenderAtScale = 1,
            SnapsToDevicePixels = false,
        };
        private string _LaunchTime = string.Empty;
        private string _CVContent = string.Empty;
        internal Guid? EntryId { get; set; }
        private string _displayCVContent = string.Empty;
        internal Func<int>? UnicodeTextTokenCountProvider { get; set; }
        private double _textSize = (double)global::SuperCV.TextSize.Medium;
        private ImageSource? _imageSource;
        private ImageSource? _previewImageSource;
        private string? _imageLink;
        private double _previewImageHeight = 215;
        private BitmapScalingMode _imageBitmapScalingMode = BitmapScalingMode.HighQuality;
        private SolidColorBrush _imageMetricsForeground = CreateGrayBrush(235);
        private SolidColorBrush _imageLaunchTimeForeground = CreateGrayBrush(235);
        private SolidColorBrush _imageIndexForeground = CreateGrayBrush(235);
        private int _imagePixelWidth;
        private int _imagePixelHeight;
        private int _imageLoadVersion;
        private int _previewImageLoadVersion;
        private int _RealIndex;
        private int _SelectedIndex;
        private string _question = string.Empty;
        private CancellationTokenSource? _visibilityTransition;
        private DispatcherTimer? _closeTimer;
        private DispatcherTimer? _colorPopupCloseTimer;
        private bool _isClosing;
        private bool _isClosed;
        private bool _cleanupCompleted;
        private bool _followFrameAttached;
        private bool _isSuspendedForReuse;
        private bool _closeToReusablePool;
        private bool _reusableSubscriptionsDetached;
        private bool _hasMotionPosition;
        private double _motionLeft;
        private double _motionTop;
        private Point? _dragStartPoint;
        private bool _dragInProgress;
        private bool _suppressClickPaste;
        private bool _isContentPressed;
        private bool _isContentSpringActive;
        private bool _isBottomRestoreSpringPending;
        private int _actionButtonsAnimationVersion;
        private int _shadowAnimationVersion;
        private static readonly TimeSpan ColorPopupCloseDelay = TimeSpan.FromMilliseconds(180);
        private static readonly TimeSpan ColorPopupTransitionDelay = TimeSpan.FromMilliseconds(600);

        internal bool HasOpenPopup =>
            CustomPopup.IsOpen ||
            ColorPopup.IsOpen ||
            AIPopup.IsOpen ||
            EditPopup.IsOpen;

		public int BiasTop;
        private int IndexUI;
        public new void Show()
        {
            Show(IndexUI == 0 ? AnimationDirection.UP : AnimationDirection.Down);
        }

        internal void Show(AnimationDirection direction)
        {
                if (direction == AnimationDirection.UP ||
                    (direction == AnimationDirection.None && IndexUI == 0))
                {
                    this.Left = TargetWindow.Left;
                    this.Top = -200;
                }
                else
                {
                    this.Left = TargetWindow.Left;
                    this.Top = SystemParameters.FullPrimaryScreenHeight + 200;
                }

            SetMotionPosition(Left, Top);
            base.Show();
            if (TargetWindow.IsCollapsed || TargetWindow.IsHiddenToTray)
            {
                this.Visibility = Visibility.Hidden;
                WindowHidePosition = SystemParameters.FullPrimaryScreenHeight + 200;
                UpdateWindowPosition(TargetWindow.Left, WindowHidePosition);
            }

            if (!_followFrameAttached)
            {
                TargetWindow.FollowFrame += Timer_Tick;
                _followFrameAttached = true;
            }


        }

        internal void ShowReused(AnimationDirection direction)
        {
            if (_isClosing || _isClosed || !_isSuspendedForReuse)
            {
                throw new InvalidOperationException("The clipboard window cannot be reused.");
            }

            double startLeft = TargetWindow.Left;
            double startTop = direction == AnimationDirection.UP ||
                              (direction == AnimationDirection.None && IndexUI == 0)
                ? -200
                : SystemParameters.FullPrimaryScreenHeight + 200;
            SetMotionPosition(startLeft, startTop);
            UpdateWindowPosition(startLeft, startTop);
            _isSuspendedForReuse = false;

            if (TargetWindow.IsCollapsed || TargetWindow.IsHiddenToTray)
            {
                Visibility = Visibility.Hidden;
                WindowHidePosition = SystemParameters.FullPrimaryScreenHeight + 200;
                UpdateWindowPosition(TargetWindow.Left, WindowHidePosition);
            }
            else
            {
                if (Visibility != Visibility.Visible)
                {
                    Visibility = Visibility.Visible;
                }

                // A window reclaimed during its exit animation is already visible. Activating it
                // here steals focus and forces a synchronous activation transition while scrolling.
            }

            if (!_followFrameAttached)
            {
                TargetWindow.FollowFrame += Timer_Tick;
                _followFrameAttached = true;
            }

            AttachReusableSubscriptions();
        }

        internal void PrepareForReuse(
            DateTimeOffset capturedAtUtc,
            string text,
            int tag,
            bool top,
            string? imageLink)
        {
            if (_isClosing || _isClosed || !_isSuspendedForReuse)
            {
                throw new InvalidOperationException("The clipboard window cannot be prepared.");
            }

            _capturedAtUtc = capturedAtUtc.ToUniversalTime();
            RefreshLaunchTime();
            RefreshTextSize();
            UpdateContent(text, imageLink);
            ShowActivated = true;
            Tag = tag;
            TopIcon = top ? Visibility.Visible : Visibility.Collapsed;
            Question = string.Empty;
            loadingState = false;
            VisualStateManager.GoToElementState(ContentGrid, "NormalState", false);
            BiasTop = 0;
            _biasSpringPosition = 0;
            _biasSpringVelocity = 0;
            _hasMotionPosition = false;
            _lastPhysicalX = null;
            _lastPhysicalY = null;
            _dragStartPoint = null;
            _dragInProgress = false;
            _suppressClickPaste = false;
            _isContentPressed = false;
            _isContentSpringActive = false;
            _isBottomRestoreSpringPending = false;
            WindowHidePosition = 0;
            ResetAdvancedInteractionVisuals();
            ApplyActionButtonsVisibility(
                CvActionButtonsVisibilityState.Current.AreVisible,
                animate: false);
            UpdateImageScrollRendering();
        }

        internal bool TryCloseForReuse(AnimationDirection direction)
        {
            if (_isClosing ||
                _isClosed ||
                _isSuspendedForReuse ||
                _closeToReusablePool ||
                _visibilityTransition is not null ||
                loadingState ||
                EditBox.IsVisible ||
                CustomPopup.IsOpen ||
                AIPopup.IsOpen ||
                Mouse.Captured is not null ||
                _dragInProgress ||
                _isContentPressed ||
                _isContentSpringActive)
            {
                return false;
            }

            DetachReusableSubscriptions();
            _imageLoadVersion++;
            ResetAdvancedInteractionVisuals();
            _closeToReusablePool = true;
            if (!TryStartExitAnimation(direction, out TimeSpan closeDelay))
            {
                FinishCloseToReusablePool();
                return true;
            }

            CVListControl.RegisterReusableWindowTransition(this);
            StartCloseTimer(closeDelay);
            return true;
        }


        public void UpDataIndex(int realIndex,int selectedIndex,int indexUI)
        {
            RealIndex=realIndex;
            SelectedIndex=selectedIndex;
            IndexUI = indexUI;
		}


        public CV(
            SuperCVWindow target,
            DateTimeOffset capturedAtUtc,
            string? cvtext = "",
            int tag = 0,
            bool top = false,
            string? imageLink = null)
        {
            ArgumentNullException.ThrowIfNull(target);
            InitializeComponent();
            SourceInitialized += OnSourceInitialized;
            PreviewMouseMove += Window_PreviewMouseMove;
            PreviewMouseDown += Window_PreviewMouseDown;
            PreviewMouseUp += Window_PreviewMouseUp;
            PreviewMouseWheel += Window_PreviewMouseWheel;
            DataContext = this;
            RefreshTextSize();
            TargetWindow=target;
            _capturedAtUtc = capturedAtUtc.ToUniversalTime();
            RefreshLaunchTime();
            UpdateContent(cvtext ?? string.Empty, imageLink);
            Tag = tag;
            if (top)
            {
                TopIcon=Visibility.Visible;
            }
            else
            {
                TopIcon = Visibility.Collapsed;
            }
            GlobalFocusManager.Initialize(this);
            TargetWindow.VisibilityStateChanged += WindowVisible;
            TargetWindow.ItemScrollStateChanged += OnItemScrollStateChanged;
            AiFeatureAvailabilityState.Current.Changed += OnAiFeatureAvailabilityChanged;
            CvActionButtonsVisibilityState.Current.PropertyChanged +=
                OnCvActionButtonsVisibilityStateChanged;
            RelativeTimeTicker.Current.MinuteTick += OnRelativeTimeMinuteTick;
            LocalizationService.Current.LanguageChanged += OnLanguageChanged;
            ApplyActionButtonsVisibility(
                CvActionButtonsVisibilityState.Current.AreVisible,
                animate: false);
            if (TargetWindow.IsItemScrolling && Setting.EnableAdvancedAnimation)
            {
                HideDynamicShadowImmediately();
            }
            UpdateImageScrollRendering();
            Closed += OnClosed;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            nint windowHandle = new WindowInteropHelper(this).Handle;
            nint extendedStyle = GetWindowLongPtr(windowHandle, GwlExStyle);
            nint toolWindowStyle = (nint)((extendedStyle.ToInt64() | WsExToolWindow) &
                                          ~WsExAppWindow);
            if (toolWindowStyle != extendedStyle)
            {
                _ = SetWindowLongPtr(windowHandle, GwlExStyle, toolWindowStyle);
            }
        }

        private double WindowHidePosition = 0;
        private async void WindowVisible(object? sender, EventArgs e)
        {
            if (_isClosing)
            {
                return;
            }

            CancelVisibilityTransition();
            var transition = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token);
            _visibilityTransition = transition;

            try
            {
                if (TargetWindow.IsHiddenToTray)
                {
                    _isBottomRestoreSpringPending = false;
                    WindowHidePosition = SystemParameters.FullPrimaryScreenHeight + 200;
                    Visibility = Visibility.Hidden;
                    UpdateWindowPosition(TargetWindow.Left, WindowHidePosition);
                    return;
                }

                if (TargetWindow.IsCollapsed)
                {
                    _isBottomRestoreSpringPending = false;
                    WindowHidePosition = SystemParameters.FullPrimaryScreenHeight + 200;
                    await Task.Delay(300, transition.Token);
                    if (_isClosing)
                    {
                        return;
                    }

                    Visibility = Visibility.Hidden;
                    UpdateWindowPosition(TargetWindow.Left, WindowHidePosition);
                }
                else
                {
                    bool isReturningFromBottom = WindowHidePosition != 0;
                    UpdateWindowPosition(TargetWindow.Left, WindowHidePosition);
                    Visibility = Visibility.Visible;
                    WindowHidePosition = 0;
                    if (isReturningFromBottom)
                    {
                        BiasTop = 0;
                        _biasSpringPosition = 0;
                        _biasSpringVelocity = 0;
                        _isBottomRestoreSpringPending = Setting.EnableAdvancedAnimation;
                    }
                }
            }
            catch (OperationCanceledException) when (transition.IsCancellationRequested)
            {
            }
            finally
            {
                if (ReferenceEquals(_visibilityTransition, transition))
                {
                    _visibilityTransition = null;
                }

                transition.Dispose();
            }
        }


        public double TextSize => _textSize;

        internal void RefreshTextSize()
        {
            ApplyTextSize((double)Setting.TextSize);
        }

        internal void ApplyTextSize(double textSize)
        {
            if (_textSize.Equals(textSize))
            {
                return;
            }

            _textSize = textSize;
            OnPropertyChanged(nameof(TextSize));
        }

        private bool _canRevoke;
        public bool CanRevoke
        {
            get => _canRevoke;
            internal set
            {
                if (_canRevoke == value)
                {
                    return;
                }

                _canRevoke = value;
                OnPropertyChanged();
                RevokeButton.IsEnabled = !IsImage && value;
            }
        }

        public void CloseUI(AnimationDirection direction = AnimationDirection.Right)
        {
            if (_isClosing)
            {
                return;
            }

            _isClosing = true;
            CleanupSubscriptions();
            if (!TryStartExitAnimation(direction, out TimeSpan closeDelay))
            {
                Close();
                return;
            }

            StartCloseTimer(closeDelay);
        }

        private bool TryStartExitAnimation(
            AnimationDirection direction,
            out TimeSpan closeDelay)
        {
            TimeSpan animationDuration;
            if (direction == AnimationDirection.UP)
            {
                animationDuration = VerticalExitAnimationDuration;
                closeDelay = animationDuration + ExitAnimationClosePadding;
                DoubleAnimation slideDown = new DoubleAnimation(this.Top, -160, animationDuration);
                slideDown.EasingFunction = new CubicEase() { EasingMode = EasingMode.EaseOut };

                this.BeginAnimation(Window.TopProperty, slideDown);
            }
            else if (direction==AnimationDirection.Down)
            {
                animationDuration = VerticalExitAnimationDuration;
                closeDelay = animationDuration + ExitAnimationClosePadding;
                DoubleAnimation slideDown = new DoubleAnimation(this.Top, SystemParameters.FullPrimaryScreenHeight+100, animationDuration);
                slideDown.EasingFunction = new CubicEase() { EasingMode = EasingMode.EaseOut };

                this.BeginAnimation(Window.TopProperty, slideDown);

            }
            else if(direction == AnimationDirection.Right)
            {
                closeDelay = TimeSpan.FromMilliseconds(
                    Setting.EnableAdvancedAnimation ? 240 : 160);
                DoubleAnimation slideDown = new DoubleAnimation(this.Left, SystemParameters.FullPrimaryScreenWidth + 200, TimeSpan.FromSeconds(1.5));
                slideDown.EasingFunction = new CubicEase() { EasingMode = EasingMode.EaseOut };

                this.BeginAnimation(Window.LeftProperty, slideDown);
                if (Setting.EnableAdvancedAnimation)
                {
                    var fadeOut = new DoubleAnimation
                    {
                        To = 0,
                        Duration = closeDelay,
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    };
                    BeginAnimation(
                        UIElement.OpacityProperty,
                        fadeOut,
                        HandoffBehavior.SnapshotAndReplace);
                }
            }
            else
            {
                closeDelay = TimeSpan.Zero;
                return false;
            }

            return true;
        }

        private void StartCloseTimer(TimeSpan closeDelay)
        {
            _closeTimer = new DispatcherTimer(
                DispatcherPriority.Normal,
                Dispatcher)
            {
                Interval = closeDelay,
            };
            _closeTimer.Tick += CloseTimer_Tick;
            _closeTimer.Start();
        }

        private void CloseTimer_Tick(object? sender, EventArgs e)
        {
            StopCloseTimer();
            if (_closeToReusablePool)
            {
                FinishCloseToReusablePool();
                return;
            }

            if (!_isClosed)
            {
                Close();
            }
        }

        internal void CloseTransitionImmediately()
        {
            if (_isClosed)
            {
                return;
            }

            StopCloseTimer();
            CVListControl.UnregisterReusableWindowTransition(this);
            _closeToReusablePool = false;
            CloseUI(AnimationDirection.None);
        }

        private void FinishCloseToReusablePool(bool returnToPool = true)
        {
            BeginAnimation(Window.LeftProperty, null);
            BeginAnimation(Window.TopProperty, null);
            BeginAnimation(UIElement.OpacityProperty, null);
            double parkedLeft = TargetWindow.Left;
            double parkedTop = SystemParameters.FullPrimaryScreenHeight + 200;
            SetMotionPosition(parkedLeft, parkedTop);
            UpdateWindowPosition(parkedLeft, parkedTop);
            // Parking an AllowsTransparency window outside the work area still leaves its
            // layered HWND visible to DWM.  The reusable pool therefore used to retain a
            // composition surface (plus the last entry's image bindings) for every spare
            // window.  Hide only after the exit animation has completed; ShowReused restores
            // visibility after positioning the already-created HWND, so scrolling still avoids
            // XAML construction and first-layout work.
            Visibility = Visibility.Hidden;
            ReleasePooledContent();
            _closeToReusablePool = false;
            _isSuspendedForReuse = true;
            CVListControl.UnregisterReusableWindowTransition(this);
            if (returnToPool)
            {
                CVListControl.ReturnReusableWindow(this);
            }
        }

        private void ReleasePooledContent()
        {
            ++_imageLoadVersion;
            ++_previewImageLoadVersion;
            _imageLink = null;
            _imageSource = null;
            _previewImageSource = null;
            _imagePixelWidth = 0;
            _imagePixelHeight = 0;
            _previewImageHeight = 215d;
            CVContent = string.Empty;
            Question = string.Empty;
            ReleaseTransientEditingContent();

            OnPropertyChanged(nameof(IsImage));
            OnPropertyChanged(nameof(TextContentVisibility));
            OnPropertyChanged(nameof(ImageContentVisibility));
            OnPropertyChanged(nameof(ImageSource));
            OnPropertyChanged(nameof(PreviewImageSource));
            OnPropertyChanged(nameof(ContentMetrics));
            OnPropertyChanged(nameof(PreviewImageHeight));
            OnPropertyChanged(nameof(PreviewPopupHeight));
            ApplyTextActionAvailability();
        }

        private void ReleaseTransientEditingContent()
        {
            // The edit TextBox is not bound to CVContent. A closed editor can otherwise keep a
            // large, no-longer-visible clipboard string and up to 20 undo snapshots alive while
            // this window sits in the reusable pool or displays a different entry.
            EditBox.UndoLimit = 0;
            EditBox.Text = string.Empty;
            EditBox.UndoLimit = 20;
            // Instruction buttons capture their per-entry session in click handlers. Detaching
            // cancels requests; clearing the visual list also releases the old entry text.
            AIList.Children.Clear();
            AIList.Tag = "0";
        }

        private void DetachReusableSubscriptions()
        {
            if (_reusableSubscriptionsDetached)
            {
                return;
            }

            if (_followFrameAttached)
            {
                TargetWindow.FollowFrame -= Timer_Tick;
                _followFrameAttached = false;
            }

            TargetWindow.VisibilityStateChanged -= WindowVisible;
            TargetWindow.ItemScrollStateChanged -= OnItemScrollStateChanged;
            AiFeatureAvailabilityState.Current.Changed -= OnAiFeatureAvailabilityChanged;
            CvActionButtonsVisibilityState.Current.PropertyChanged -=
                OnCvActionButtonsVisibilityStateChanged;
            RelativeTimeTicker.Current.MinuteTick -= OnRelativeTimeMinuteTick;
            LocalizationService.Current.LanguageChanged -= OnLanguageChanged;
            CustomInstructionsManager.Detach(AIList);
            _reusableSubscriptionsDetached = true;
        }

        private void AttachReusableSubscriptions()
        {
            if (!_reusableSubscriptionsDetached)
            {
                return;
            }

            TargetWindow.VisibilityStateChanged += WindowVisible;
            TargetWindow.ItemScrollStateChanged += OnItemScrollStateChanged;
            AiFeatureAvailabilityState.Current.Changed += OnAiFeatureAvailabilityChanged;
            CvActionButtonsVisibilityState.Current.PropertyChanged +=
                OnCvActionButtonsVisibilityStateChanged;
            RelativeTimeTicker.Current.MinuteTick += OnRelativeTimeMinuteTick;
            LocalizationService.Current.LanguageChanged += OnLanguageChanged;
            _reusableSubscriptionsDetached = false;
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _isClosing = true;
            _isClosed = true;
            PreviewMouseMove -= Window_PreviewMouseMove;
            PreviewMouseDown -= Window_PreviewMouseDown;
            PreviewMouseUp -= Window_PreviewMouseUp;
            PreviewMouseWheel -= Window_PreviewMouseWheel;
            StopColorPopupCloseTimer();
            CleanupSubscriptions();
            StopCloseTimer();
            _imageLoadVersion++;
            _imageSource = null;
        }

        private void Window_PreviewMouseMove(object sender, MouseEventArgs e) =>
            TargetWindow.RecordMouseActivity();

        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e) =>
            TargetWindow.RecordMouseActivity();

        private void Window_PreviewMouseUp(object sender, MouseButtonEventArgs e) =>
            TargetWindow.RecordMouseActivity();

        private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e) =>
            TargetWindow.RecordMouseActivity();

        private void CleanupSubscriptions()
        {
            if (_cleanupCompleted)
            {
                return;
            }

            _cleanupCompleted = true;
            if (_followFrameAttached)
            {
                TargetWindow.FollowFrame -= Timer_Tick;
                _followFrameAttached = false;
            }

            TargetWindow.VisibilityStateChanged -= WindowVisible;
            TargetWindow.ItemScrollStateChanged -= OnItemScrollStateChanged;
            AiFeatureAvailabilityState.Current.Changed -= OnAiFeatureAvailabilityChanged;
            CvActionButtonsVisibilityState.Current.PropertyChanged -=
                OnCvActionButtonsVisibilityStateChanged;
            RelativeTimeTicker.Current.MinuteTick -= OnRelativeTimeMinuteTick;
            LocalizationService.Current.LanguageChanged -= OnLanguageChanged;
            CustomInstructionsManager.Detach(AIList);
            _lifetimeCancellation.Cancel();
            CancelVisibilityTransition();
            _lifetimeCancellation.Dispose();
        }

        private void OnCvActionButtonsVisibilityStateChanged(
            object? sender,
            PropertyChangedEventArgs e)
        {
            if (_isClosing ||
                e.PropertyName != nameof(CvActionButtonsVisibilityState.AreVisible))
            {
                return;
            }

            bool areVisible = CvActionButtonsVisibilityState.Current.AreVisible;
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.BeginInvoke(
                    DispatcherPriority.Render,
                    new Action(() => ApplyActionButtonsVisibility(areVisible, animate: true)));
                return;
            }

            ApplyActionButtonsVisibility(areVisible, animate: true);
        }

        private void ApplyActionButtonsVisibility(bool areVisible, bool animate)
        {
            if (_isClosing)
            {
                return;
            }

            int animationVersion = ++_actionButtonsAnimationVersion;
            double targetWidth = areVisible ? ActionButtonsExpandedWidth : 0;
            double targetOpacity = areVisible ? 1 : 0;
            Rect targetClip = CreateContentClipRect(targetWidth);

            if (areVisible)
            {
                DragPanel.Visibility = Visibility.Visible;
            }

            if (!animate)
            {
                CommitActionButtonsVisibility(areVisible, targetWidth, targetOpacity, targetClip);
                return;
            }

            DoubleAnimationUsingKeyFrames widthAnimation =
                CreateActionButtonsWidthAnimation(DragPanel.Width, areVisible);
            var opacityAnimation = new DoubleAnimation(
                DragPanel.Opacity,
                targetOpacity,
                ActionButtonsTransitionDuration)
            {
                EasingFunction = ActionButtonsTransitionEasing,
            };
            RectAnimationUsingKeyFrames clipAnimation =
                CreateActionButtonsClipAnimation(
                ContentClipGeometry.Rect,
                areVisible);
            ThicknessAnimationUsingKeyFrames marginAnimation =
                CreateActionButtonsMarginAnimation(TextBorder.Margin);

            widthAnimation.Completed += (_, _) =>
            {
                if (_isClosing || animationVersion != _actionButtonsAnimationVersion)
                {
                    return;
                }

                CommitActionButtonsVisibility(
                    areVisible,
                    targetWidth,
                    targetOpacity,
                    targetClip);
            };

            DragPanel.BeginAnimation(
                FrameworkElement.WidthProperty,
                widthAnimation,
                HandoffBehavior.SnapshotAndReplace);
            DragPanel.BeginAnimation(
                UIElement.OpacityProperty,
                opacityAnimation,
                HandoffBehavior.SnapshotAndReplace);
            ContentClipGeometry.BeginAnimation(
                RectangleGeometry.RectProperty,
                clipAnimation,
                HandoffBehavior.SnapshotAndReplace);
            TextBorder.BeginAnimation(
                FrameworkElement.MarginProperty,
                marginAnimation,
                HandoffBehavior.SnapshotAndReplace);
        }

        private static DoubleAnimationUsingKeyFrames CreateActionButtonsWidthAnimation(
            double currentWidth,
            bool areVisible)
        {
            var animation = new DoubleAnimationUsingKeyFrames
            {
                Duration = ActionButtonsTransitionDuration,
                FillBehavior = FillBehavior.HoldEnd,
            };
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(
                currentWidth,
                KeyTime.FromTimeSpan(TimeSpan.Zero)));

            AddActionButtonsWidthKeyFrame(
                animation,
                areVisible ? ActionButtonsExpandedWidth : 0);

            return animation;
        }

        private static void AddActionButtonsWidthKeyFrame(
            DoubleAnimationUsingKeyFrames animation,
            double width)
        {
            animation.KeyFrames.Add(new EasingDoubleKeyFrame
            {
                Value = width,
                KeyTime = KeyTime.FromTimeSpan(ActionButtonsTransitionDuration),
                EasingFunction = ActionButtonsTransitionEasing,
            });
        }

        private RectAnimationUsingKeyFrames CreateActionButtonsClipAnimation(
            Rect currentClip,
            bool areVisible)
        {
            var animation = new RectAnimationUsingKeyFrames
            {
                Duration = ActionButtonsTransitionDuration,
                FillBehavior = FillBehavior.HoldEnd,
            };
            animation.KeyFrames.Add(new LinearRectKeyFrame(
                currentClip,
                KeyTime.FromTimeSpan(TimeSpan.Zero)));

            AddActionButtonsClipKeyFrame(
                animation,
                areVisible ? ActionButtonsExpandedWidth : 0);

            return animation;
        }

        private void AddActionButtonsClipKeyFrame(
            RectAnimationUsingKeyFrames animation,
            double buttonsWidth)
        {
            animation.KeyFrames.Add(new EasingRectKeyFrame
            {
                Value = CreateContentClipRect(buttonsWidth),
                KeyTime = KeyTime.FromTimeSpan(ActionButtonsTransitionDuration),
                EasingFunction = ActionButtonsTransitionEasing,
            });
        }

        private static ThicknessAnimationUsingKeyFrames CreateActionButtonsMarginAnimation(
            Thickness currentMargin)
        {
            var animation = new ThicknessAnimationUsingKeyFrames
            {
                Duration = ActionButtonsTransitionDuration,
                FillBehavior = FillBehavior.HoldEnd,
            };
            animation.KeyFrames.Add(new LinearThicknessKeyFrame(
                currentMargin,
                KeyTime.FromTimeSpan(TimeSpan.Zero)));

            AddActionButtonsMarginKeyFrame(animation, new Thickness(5));

            return animation;
        }

        private static void AddActionButtonsMarginKeyFrame(
            ThicknessAnimationUsingKeyFrames animation,
            Thickness margin)
        {
            animation.KeyFrames.Add(new EasingThicknessKeyFrame
            {
                Value = margin,
                KeyTime = KeyTime.FromTimeSpan(ActionButtonsTransitionDuration),
                EasingFunction = ActionButtonsTransitionEasing,
            });
        }

        private Rect CreateContentClipRect(double buttonsWidth) =>
            new(
                0,
                0,
                FullContentClipWidth - buttonsWidth - ContentBorderInset,
                ContentClipHeight);

        private void CommitActionButtonsVisibility(
            bool areVisible,
            double width,
            double opacity,
            Rect contentClip)
        {
            DragPanel.Width = width;
            DragPanel.Opacity = opacity;
            ContentClipGeometry.Rect = contentClip;
            TextBorder.Margin = new Thickness(5);

            DragPanel.BeginAnimation(FrameworkElement.WidthProperty, null);
            DragPanel.BeginAnimation(UIElement.OpacityProperty, null);
            ContentClipGeometry.BeginAnimation(RectangleGeometry.RectProperty, null);
            TextBorder.BeginAnimation(FrameworkElement.MarginProperty, null);
            DragPanel.Visibility = areVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnAiFeatureAvailabilityChanged(object? sender, EventArgs e)
        {
            if (AiFeatureAvailabilityState.Current.IsEnabled || _isClosing)
            {
                return;
            }

            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.BeginInvoke(
                    DispatcherPriority.DataBind,
                    new Action(DeactivateAiFeatures));
                return;
            }

            DeactivateAiFeatures();
        }

        private void DeactivateAiFeatures()
        {
            if (_isClosing || AiFeatureAvailabilityState.Current.IsEnabled)
            {
                return;
            }

            AIPopup.IsOpen = false;
            Question = string.Empty;
            loadingState = false;
            VisualStateManager.GoToElementState(ContentGrid, "NormalState", false);
            CustomInstructionsManager.Detach(AIList);
        }

        private void CancelVisibilityTransition()
        {
            CancellationTokenSource? transition = _visibilityTransition;
            _visibilityTransition = null;
            if (transition is null)
            {
                return;
            }

            transition.Cancel();
            transition.Dispose();
        }

        private void StopCloseTimer()
        {
            if (_closeTimer is null)
            {
                return;
            }

            _closeTimer.Stop();
            _closeTimer.Tick -= CloseTimer_Tick;
            _closeTimer = null;
        }



        // 2. 新增两个私有变量，用于模拟真实的物理弹簧
        private double _biasSpringPosition = 0; // 弹簧当前的拉伸偏移量
        private double _biasSpringVelocity = 0; // 弹簧的运动速度

        private void Timer_Tick(object sender, FollowFrame frame)
        {
            double itemStep = PixelAlignedWindowMotion.AlignLogicalStep(
                this.ActualHeight - 26,
                frame.DpiScaleY);
            double baseTargetTop = WindowHidePosition == 0
                ? IndexUI * itemStep + frame.TargetTop + frame.TargetHeight -26
                : WindowHidePosition;
            double currentLeft = _hasMotionPosition ? _motionLeft : Left;
            double currentTop = _hasMotionPosition ? _motionTop : Top;

            if (!Setting.EnableAdvancedAnimation)
            {
                _isBottomRestoreSpringPending = false;
            }

            if (ShouldStartBottomRestoreSpring(
                    Setting.EnableAdvancedAnimation,
                    _isBottomRestoreSpringPending,
                    currentTop,
                    baseTargetTop))
            {
                AddSpringImpulse(-1);
                _isBottomRestoreSpringPending = false;
            }

            if (BiasTop != 0)
            {
                AddSpringImpulse(BiasTop);

                // 消耗掉这次触发指令，等待弹簧自然回弹
                BiasTop = 0;
            }

            // 计算弹簧物理演算。正常 60 FPS 下仍是原来的一次积分；在 120 FPS 下使用
            // 更细的真实帧间隔；发生丢帧时才拆分子步，避免 Euler 积分失稳或改变时长。
            double springK = 180.0; // 弹簧刚度（越大回弹越干脆，拉扯感越紧）
            double damper = 9.0;   // 阻尼系数（摩擦力，防止弹簧无限震荡，调小会更有“果冻”感）
            for (int step = 0; step < frame.PhysicsSteps; step++)
            {
                // 胡克定律：弹簧受力 = -刚度 * 当前位置 - 阻尼 * 当前速度
                double springForce =
                    -springK * _biasSpringPosition - damper * _biasSpringVelocity;

                // 半隐式 Euler；子步只在实际掉帧时增加，不制造不可见的“补帧”。
                _biasSpringVelocity += springForce * frame.PhysicsDelta;
                _biasSpringPosition += _biasSpringVelocity * frame.PhysicsDelta;
            }

            if (PixelAlignedWindowMotion.IsResidualMotionSettled(
                    _biasSpringPosition,
                    _biasSpringVelocity,
                    frame.DpiScaleY))
            {
                _biasSpringPosition = 0;
                _biasSpringVelocity = 0;
            }

            // === 第二部分：计算主窗口的滞后跟随 ===

            // 【核心】实际的目标位置 = 基础排版位置 + 弹簧拉扯产生的偏移
            double targetTop = baseTargetTop + _biasSpringPosition;
            double targetLeft = frame.TargetLeft;

            // 2. 滞后平滑跟随系数
            // 3. 计算本帧新位置
            double newLeft = currentLeft + (targetLeft - currentLeft) * frame.LerpFactor;
            double newTop = currentTop + (targetTop - currentTop) * frame.LerpFactor;

            // 结束判定以物理像素为准。在高 DPI 下固定的 DIP 阈值会提前宣告结束，
            // 导致 WPF 与原生窗口坐标在动画交接时相差一个像素。
            newLeft = PixelAlignedWindowMotion.SnapToTargetPhysicalPixel(
                newLeft,
                targetLeft,
                frame.DpiScaleX);
            if (_biasSpringPosition == 0 && _biasSpringVelocity == 0)
            {
                newTop = PixelAlignedWindowMotion.SnapToTargetPhysicalPixel(
                    newTop,
                    targetTop,
                    frame.DpiScaleY);
            }

            bool isSettled =
                newLeft == targetLeft &&
                newTop == targetTop &&
                _biasSpringPosition == 0 &&
                _biasSpringVelocity == 0;
            TargetWindow.QueueChildWindowPosition(
                this,
                newLeft,
                newTop,
                verifyActualPosition: isSettled);
            TargetWindow.ReportChildWindowMotion(isSettled);

        }

        private void AddSpringImpulse(double direction)
        {
            _biasSpringVelocity += Math.Sign(direction) * SpringImpulseVelocity;
            _biasSpringVelocity = Math.Clamp(
                _biasSpringVelocity,
                -MaximumSpringVelocity,
                MaximumSpringVelocity);
        }

        internal static bool ShouldStartBottomRestoreSpring(
            bool isAdvancedAnimationEnabled,
            bool isPending,
            double currentTop,
            double targetTop) =>
            isAdvancedAnimationEnabled &&
            isPending &&
            currentTop >= targetTop &&
            currentTop - targetTop <= BottomRestoreSpringTriggerDistance;

        private DateTimeOffset _capturedAtUtc;

        public string LaunchTime {
            get=>_LaunchTime;
            private set {
                _LaunchTime = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        internal void UpdateCapturedAtUtc(DateTimeOffset capturedAtUtc)
        {
            _capturedAtUtc = capturedAtUtc.ToUniversalTime();
            RefreshLaunchTime();
        }

        private void OnRelativeTimeMinuteTick(object? sender, EventArgs e) =>
            RefreshLaunchTime();

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            RefreshLaunchTime();
            OnPropertyChanged(nameof(ContentMetrics));
            if (ColorPopup.IsOpen)
            {
                InitializeColorButtons(Tag);
            }
        }

        private void RefreshLaunchTime() =>
            LaunchTime = RelativeTimeFormatter.Format(
                _capturedAtUtc,
                DateTimeOffset.UtcNow);

        private int _tag=0;
        private Visibility _tagVisibility=Visibility.Collapsed;
        private Brush _tagColor=Brushes.Transparent;
        public new int Tag
        {
            get => _tag;
            set
            {
                _tag = value;

                if (_tag >= 0 && _tag < TagColors.Length)
                {
                    TagColor = TagColors[_tag];
                    TagVisibility = _tag == 0 ? Visibility.Collapsed : Visibility.Visible;
                }
                else
                {
                    // 超出范围的处理
                    TagColor = Brushes.Gray;
                    TagVisibility = Visibility.Visible;
                }

                OnPropertyChanged();
            }
        }

        public Visibility TagVisibility
        {
            get => _tagVisibility;
            set
            {
                _tagVisibility = value;
                OnPropertyChanged();
            }
        }


        public Brush TagColor
        {
            get => _tagColor;
            set
            {
                _tagColor = value;
                OnPropertyChanged();
            }
        }


        private int RealIndex
        {
            get => _RealIndex;
            set
            {
                _RealIndex = value;
                IndexShow = value.ToString();
                OnPropertyChanged();

            }
        }
		private int SelectedIndex
		{
			get => _SelectedIndex;
			set
			{
				_SelectedIndex = value;
				IndexShow = value.ToString();
				OnPropertyChanged();

			}
		}

        public string Question
        {
            get => _question;
            set
            {
                _question = value ?? string.Empty;
                OnPropertyChanged();
            }
        }
        public string IndexShow
        {
            get {
                if (CVListControl.FilterEnabled)
                {
                    return $"{SelectedIndex + 1}/{CVListControl.SelectedIndexes.Count} {RealIndex + 1}/{CVListControl.ListAll.Count}";
				}
                else
                {
                    return $"{RealIndex + 1}/{CVListControl.ListAll.Count}";
				}
                
                
            }
            set
            {
                OnPropertyChanged();
            }
        }

        public string CVContent
        {
            get => _CVContent;
            set
            {
                _CVContent = value ?? string.Empty;
                _displayCVContent = CreateDisplayPreview(_CVContent);
                OnPropertyChanged(nameof(ContentMetrics));
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayCVContent));
            }
        }

        public string DisplayCVContent => _displayCVContent;

        internal static string CreateDisplayPreview(string? content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return string.Empty;
            }

            int start = 0;
            while (start < content.Length && char.IsWhiteSpace(content[start]))
            {
                start++;
            }

            int previewLength = Math.Min(
                MaximumDisplayPreviewCharacters,
                content.Length - start);
            if (start == 0 && previewLength == content.Length)
            {
                return content;
            }

            return previewLength == 0
                ? string.Empty
                : content.Substring(start, previewLength);
        }

        public bool IsImage => !string.IsNullOrWhiteSpace(_imageLink);

        public Visibility TextContentVisibility =>
            IsImage ? Visibility.Collapsed : Visibility.Visible;

        public Visibility ImageContentVisibility =>
            IsImage ? Visibility.Visible : Visibility.Collapsed;

        public ImageSource? ImageSource => _imageSource;

        public ImageSource? PreviewImageSource => _previewImageSource;

        public BitmapScalingMode ImageBitmapScalingMode => _imageBitmapScalingMode;

        public Brush ImageMetricsForeground => _imageMetricsForeground;

        public Brush ImageLaunchTimeForeground => _imageLaunchTimeForeground;

        public Brush ImageIndexForeground => _imageIndexForeground;

        public double PreviewImageHeight => _previewImageHeight;

        public double PreviewPopupHeight => _previewImageHeight + 10;

        internal void UpdateContent(string text, string? imageLink)
        {
            int loadVersion = ++_imageLoadVersion;
            ++_previewImageLoadVersion;
            _imageLink = string.IsNullOrWhiteSpace(imageLink) ? null : imageLink;
            _imageSource = null;
            _previewImageSource = null;
            _imagePixelWidth = 0;
            _imagePixelHeight = 0;
            _previewImageHeight = 215d;
            if (_imageLink is not null)
            {
                if (ClipboardImageVisualCache.TryGet(
                        _imageLink,
                        out CachedClipboardImage? cachedImage))
                {
                    ApplyDecodedImage(cachedImage);
                }
                else
                {
                    _ = LoadImageAsync(_imageLink, loadVersion);
                }
            }

            CVContent = text;
            OnPropertyChanged(nameof(IsImage));
            OnPropertyChanged(nameof(TextContentVisibility));
            OnPropertyChanged(nameof(ImageContentVisibility));
            OnPropertyChanged(nameof(ImageSource));
            OnPropertyChanged(nameof(PreviewImageSource));
            OnPropertyChanged(nameof(ContentMetrics));
            OnPropertyChanged(nameof(PreviewImageHeight));
            OnPropertyChanged(nameof(PreviewPopupHeight));
            // The image is clipped inside the 1px, 15px-radius content border.
            // Its radius must therefore be one pixel smaller so it fills the inner
            // surface exactly without covering the border or leaving corner gaps.
            const double clipRadius = 14;
            ContentClipGeometry.RadiusX = clipRadius;
            ContentClipGeometry.RadiusY = clipRadius;
            ContentClipGeometry.Rect = CreateContentClipRect(
                CvActionButtonsVisibilityState.Current.AreVisible
                    ? ActionButtonsExpandedWidth
                    : 0);
            ApplyTextActionAvailability();
            if (_imageSource is BitmapSource currentImage)
            {
                _ = UpdateImageMetadataForegroundsAsync(
                    currentImage,
                    loadVersion);
            }
        }

        private async Task LoadImageAsync(string imageLink, int loadVersion)
        {
            CachedClipboardImage? image =
                await ClipboardImageVisualCache.GetAsync(imageLink);
            if (_isClosed ||
                loadVersion != _imageLoadVersion ||
                !string.Equals(_imageLink, imageLink, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ApplyDecodedImage(image);
            OnPropertyChanged(nameof(ImageSource));
            OnPropertyChanged(nameof(ContentMetrics));
            OnPropertyChanged(nameof(PreviewImageHeight));
            OnPropertyChanged(nameof(PreviewPopupHeight));
            if (_imageSource is BitmapSource currentImage)
            {
                _ = UpdateImageMetadataForegroundsAsync(
                    currentImage,
                    loadVersion);
            }
        }

        private void ApplyDecodedImage(CachedClipboardImage? image)
        {
            _imageSource = image?.Source;
            _imagePixelWidth = image?.PixelWidth ?? 0;
            _imagePixelHeight = image?.PixelHeight ?? 0;
            _previewImageHeight = image is { PixelWidth: > 0, PixelHeight: > 0 }
                ? 270d * image.PixelHeight / image.PixelWidth
                : 205d;
            OnPropertyChanged(nameof(ContentMetrics));
        }

        private async Task UpdateImageMetadataForegroundsAsync(
            BitmapSource source,
            int loadVersion)
        {
            (byte Metrics, byte LaunchTime, byte Index)? colors = await Task.Run(
                () => ImageMetadataContrastSampler.TrySample(source));
            if (colors is not { } sampled ||
                _isClosed ||
                loadVersion != _imageLoadVersion)
            {
                return;
            }

            SetImageMetadataForeground(
                ref _imageMetricsForeground,
                sampled.Metrics,
                nameof(ImageMetricsForeground));
            SetImageMetadataForeground(
                ref _imageLaunchTimeForeground,
                sampled.LaunchTime,
                nameof(ImageLaunchTimeForeground));
            SetImageMetadataForeground(
                ref _imageIndexForeground,
                sampled.Index,
                nameof(ImageIndexForeground));
        }

        private void SetImageMetadataForeground(
            ref SolidColorBrush field,
            byte gray,
            string propertyName)
        {
            if (field.Color.R == gray)
            {
                return;
            }

            field = CreateGrayBrush(gray);
            OnPropertyChanged(propertyName);
        }

        private static SolidColorBrush CreateGrayBrush(byte gray)
        {
            var brush = new SolidColorBrush(Color.FromRgb(gray, gray, gray));
            brush.Freeze();
            return brush;
        }

        internal static ImageSource? LoadCachedImage(
            string? imageLink,
            out int pixelWidth,
            out int pixelHeight)
        {
            CachedClipboardImage? image = ClipboardImageVisualCache.Decode(imageLink);
            pixelWidth = image?.PixelWidth ?? 0;
            pixelHeight = image?.PixelHeight ?? 0;
            return image?.Source;
        }

        private void ApplyTextActionAvailability()
        {
            bool enabled = !IsImage;
            AiPrimaryActionButton.IsEnabled = true;
            BookmarkButton.IsEnabled = enabled;
            ExportButton.IsEnabled = true;
            PureTextButton.IsEnabled = enabled;
            FindReplaceButton.IsEnabled = enabled;
            RevokeButton.IsEnabled = enabled && CanRevoke;
        }
        
        public string ContentMetrics =>
            IsImage
                ? _imagePixelWidth > 0 && _imagePixelHeight > 0
                    ? $"{_imagePixelWidth} × {_imagePixelHeight}"
                    : string.Empty
                : LocalizationService.Current.IsEnglish
                    ? $"{_CVContent.Length} chars"
                    : $"{_CVContent.Length} 字符";



        public SuperCVWindow TargetWindow { get; }



        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern nint GetWindowLongPtr(nint windowHandle, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern nint SetWindowLongPtr(
            nint windowHandle,
            int index,
            nint newValue);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            nint windowHandle,
            nint insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }


        private IntPtr _hwnd;
        private int? _lastPhysicalX;
        private int? _lastPhysicalY;

        private void UpdateWindowPosition(double logicalX, double logicalY)
        {
            ChildWindowMotionScheduler.MoveImmediately(this, logicalX, logicalY);
        }

        internal void SetMotionPosition(double logicalX, double logicalY)
        {
            _motionLeft = logicalX;
            _motionTop = logicalY;
            _hasMotionPosition = true;
        }

        internal bool TryPrepareNativePosition(
            double logicalX,
            double logicalY,
            bool verifyActualPosition,
            bool hasFrameContext,
            NativeChildWindowFrameContext frameContext,
            out NativeChildWindowPosition position)
        {
            if (_hwnd == IntPtr.Zero)
            {
                _hwnd = new WindowInteropHelper(this).Handle;
            }

            if (_hwnd == IntPtr.Zero ||
                !hasFrameContext)
            {
                Left = logicalX;
                Top = logicalY;
                SetMotionPosition(logicalX, logicalY);
                position = default;
                return false;
            }

            // Anchor to the main window's physical rectangle instead of multiplying a global
            // virtual-desktop coordinate by one monitor's scale. This remains correct when the
            // main window moves between monitors with different DPI settings.
            double exactPhysicalX = frameContext.TargetPhysicalLeft +
                ((logicalX - frameContext.TargetLogicalLeft) * frameContext.DpiScale);
            double exactPhysicalY = frameContext.TargetPhysicalTop +
                ((logicalY - frameContext.TargetLogicalTop) * frameContext.DpiScale);

            // A deterministic nearest-pixel mapping is intentionally used here. Carrying the
            // fractional error between frames makes a stationary coordinate alternate between
            // adjacent pixels, which is visible as jitter at high animation rates.
            int physicalX = PixelAlignedWindowMotion.RoundPhysicalCoordinate(exactPhysicalX);
            int physicalY = PixelAlignedWindowMotion.RoundPhysicalCoordinate(exactPhysicalY);

            if (_lastPhysicalX == physicalX &&
                _lastPhysicalY == physicalY &&
                (!verifyActualPosition ||
                 IsAtNativePosition(physicalX, physicalY)))
            {
                position = default;
                return false;
            }

            position = new NativeChildWindowPosition(
                this,
                _hwnd,
                physicalX,
                physicalY);
            return true;
        }

        private bool IsAtNativePosition(int physicalX, int physicalY) =>
            GetWindowRect(_hwnd, out NativeRect currentRect) &&
            currentRect.Left == physicalX &&
            currentRect.Top == physicalY;

        internal void AcceptNativePosition(int physicalX, int physicalY)
        {
            _lastPhysicalX = physicalX;
            _lastPhysicalY = physicalY;
        }

        private void Button_Delete(object sender, RoutedEventArgs e)
        {
            Delete();
        }


        public void Delete()
        {
            if (_isClosing)
            {
                return;
            }

            if (Setting.DeleteConfirm
                && !new AlertDialog("确定删除吗？").ShowDialog())
            {
                return;
            }

            int indexToRemove = RealIndex;
            CVListControl.RemoveAt(indexToRemove);
            if (indexToRemove == 0)
            {
                TargetWindow.Detector.Reset();
            }
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            if (CustomPopup.IsOpen)
            {
                return;
            }

            CustomPopup.IsOpen = true;

            // 创建淡入动画
            DoubleAnimation fadeInAnimation = new DoubleAnimation
            {
                From = 0,
                Duration = TimeSpan.FromSeconds(0.2),
                FillBehavior = FillBehavior.Stop
            };

            // 直接对Popup的内容应用动画
            CustomPopup.Child.BeginAnimation(UIElement.OpacityProperty, fadeInAnimation);


        }




        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }



        private void TextBorder_MouseUp(object sender, MouseButtonEventArgs e)
        {

            
        }

        private void TextBorder_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {

                if (e.Delta > 0)
                {
                    CVListControl.MoveUp();
                }
                else if (e.Delta < 0)
                {
                    CVListControl.MoveDown();
                }
                //e.Handled = true;
            
            
        }

        private void TextBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            
        }



        private void TextBorder_MouseEnter(object sender, MouseEventArgs e)
        {
            //if (SuperCV.Timer.IsEnabled) return;

            GlobalFocusManager.RecordCurrentForegroundWindow();
            if (CanShowAdvancedShadow)
            {
                AnimateShadowProperties(true);
            }
            else if (Setting.EnableAdvancedAnimation)
            {
                HideDynamicShadowImmediately();
            }
            else
            {
                ResetAdvancedInteractionVisuals();
            }

        }

        private void TextBorder_MouseLeave(object sender, MouseEventArgs e)
        {

            if (TargetWindow.IsItemScrolling && Setting.EnableAdvancedAnimation)
            {
                HideDynamicShadowImmediately();
            }
            else if (Setting.EnableAdvancedAnimation && !_isContentSpringActive)
            {
                AnimateShadowProperties(showShadow: false);
            }
            else if (!Setting.EnableAdvancedAnimation)
            {
                ResetAdvancedInteractionVisuals();
            }
            textBlockDown = false;

        }

        private void TextBorder_MouseMove(object sender, MouseEventArgs e)
        {
            if (CanShowAdvancedShadow)
            {
                var epoint = UpdateShadowPosition(
                    e.GetPosition(TextBorder),
                    TextBorder,
                    DynamicShadow);
                RelativeTransMove(epoint, MoveTransform);
            }
            else if (Setting.EnableAdvancedAnimation)
            {
                HideDynamicShadowImmediately();
            }
            else
            {
                ResetAdvancedInteractionVisuals();
            }

            TryStartContentDrag(e);

        }

        private void TryStartContentDrag(MouseEventArgs e)
        {
            if (_dragInProgress ||
                _dragStartPoint is not Point dragStartPoint ||
                e.LeftButton != MouseButtonState.Pressed ||
                EditBox.IsVisible)
            {
                return;
            }

            Point currentPoint = e.GetPosition(TextBorder);
            if (Math.Abs(currentPoint.X - dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(currentPoint.Y - dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            var args = new ClipboardDragDataRequestedEventArgs();
            DragDataRequested?.Invoke(this, args);
            if (args.DataObject is null)
            {
                return;
            }

            _dragInProgress = true;
            _suppressClickPaste = true;
            _dragStartPoint = null;
            if (ReferenceEquals(Mouse.Captured, TextBorder))
            {
                Mouse.Capture(null);
            }

            try
            {
                _ = DragDrop.DoDragDrop(TextBorder, args.DataObject, DragDropEffects.Copy);
            }
            finally
            {
                _dragInProgress = false;
                SetContentPressed(false);
            }
        }

        private void SetContentPressed(bool isPressed)
        {
            if (_isContentPressed == isPressed)
            {
                return;
            }

            _isContentPressed = isPressed;
            if (!Setting.EnableAdvancedAnimation)
            {
                ResetContentPressScale();
                return;
            }

            _isContentSpringActive = true;
            double currentScaleX = ContentPressScaleTransform.ScaleX;
            double currentScaleY = ContentPressScaleTransform.ScaleY;
            DoubleAnimationUsingKeyFrames scaleXAnimation =
                CreateContentSpringAnimation(currentScaleX, isPressed);
            DoubleAnimationUsingKeyFrames scaleYAnimation =
                CreateContentSpringAnimation(currentScaleY, isPressed);
            if (!isPressed)
            {
                scaleXAnimation.Completed += ContentReleaseAnimation_Completed;
            }

            ContentPressScaleTransform.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                scaleXAnimation,
                HandoffBehavior.SnapshotAndReplace);
            ContentPressScaleTransform.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                scaleYAnimation,
                HandoffBehavior.SnapshotAndReplace);
        }

        private static DoubleAnimationUsingKeyFrames CreateContentSpringAnimation(
            double currentScale,
            bool isPressed)
        {
            var animation = new DoubleAnimationUsingKeyFrames
            {
                Duration = isPressed
                    ? ContentPressAnimationDuration
                    : ContentReleaseAnimationDuration,
                FillBehavior = FillBehavior.HoldEnd,
            };

            animation.KeyFrames.Add(new LinearDoubleKeyFrame(
                currentScale,
                KeyTime.FromTimeSpan(TimeSpan.Zero)));

            if (isPressed)
            {
                AddSpringKeyFrame(animation, 0.974, 90);
                AddSpringKeyFrame(animation, 0.984, 165);
                AddSpringKeyFrame(animation, ContentPressedScale, 250);
            }
            else
            {
                AddSpringKeyFrame(animation, 1.012, 95);
                AddSpringKeyFrame(animation, 0.994, 185);
                AddSpringKeyFrame(animation, 1, 300);
            }

            return animation;
        }

        private static void AddSpringKeyFrame(
            DoubleAnimationUsingKeyFrames animation,
            double value,
            int milliseconds)
        {
            animation.KeyFrames.Add(new EasingDoubleKeyFrame
            {
                Value = value,
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }

        private void ContentReleaseAnimation_Completed(object? sender, EventArgs e)
        {
            _isContentSpringActive = false;
            if (!Setting.EnableAdvancedAnimation)
            {
                ResetAdvancedInteractionVisuals();
            }
            else if (TargetWindow.IsItemScrolling)
            {
                HideDynamicShadowImmediately();
            }
            else if (!_isClosing && !IsPointerInsideTextBorder())
            {
                AnimateShadowProperties(showShadow: false);
            }
        }

        private bool IsPointerInsideTextBorder()
        {
            Point position = Mouse.GetPosition(TextBorder);
            return TextBorder.ActualWidth > 0 &&
                   TextBorder.ActualHeight > 0 &&
                   position.X >= 0 &&
                   position.Y >= 0 &&
                   position.X <= TextBorder.ActualWidth &&
                   position.Y <= TextBorder.ActualHeight;
        }

        private bool CanShowAdvancedShadow =>
            Setting.EnableAdvancedAnimation && !TargetWindow.IsItemScrolling;

        private void OnItemScrollStateChanged(object? sender, EventArgs e)
        {
            if (_isClosing)
            {
                return;
            }

            UpdateImageScrollRendering();
            if (!Setting.EnableAdvancedAnimation)
            {
                return;
            }

            if (TargetWindow.IsItemScrolling)
            {
                HideDynamicShadowImmediately();
                return;
            }

            if (!IsPointerInsideTextBorder())
            {
                HideDynamicShadowImmediately();
                return;
            }

            Point position = Mouse.GetPosition(TextBorder);
            Point relativePosition = UpdateShadowPosition(position, TextBorder, DynamicShadow);
            RelativeTransMove(relativePosition, MoveTransform);
            AnimateShadowProperties(showShadow: true);
        }

        private void UpdateImageScrollRendering()
        {
            BitmapScalingMode scalingMode = TargetWindow.IsItemScrolling
                ? BitmapScalingMode.LowQuality
                : BitmapScalingMode.HighQuality;
            if (_imageBitmapScalingMode == scalingMode)
            {
                return;
            }

            _imageBitmapScalingMode = scalingMode;
            OnPropertyChanged(nameof(ImageBitmapScalingMode));
        }

        private void HideDynamicShadowImmediately()
        {
            _shadowAnimationVersion++;
            if (TextBorderShadowSurface.Effect is null &&
                !DynamicShadow.HasAnimatedProperties &&
                DynamicShadow.Opacity == 0 &&
                DynamicShadow.BlurRadius == 0 &&
                DynamicShadow.ShadowDepth == 0 &&
                MoveTransform.X == 0 &&
                MoveTransform.Y == 0)
            {
                SetTextBorderShadowRendering(hasShadow: false);
                return;
            }

            DynamicShadow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
            DynamicShadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
            DynamicShadow.Opacity = 0;
            DynamicShadow.BlurRadius = 0;
            DynamicShadow.ShadowDepth = 0;
            MoveTransform.X = 0;
            MoveTransform.Y = 0;
            TextBorderShadowSurface.Effect = null;
            SetTextBorderShadowRendering(hasShadow: false);
        }

        private void ResetContentPressScale()
        {
            _isContentSpringActive = false;
            ContentPressScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ContentPressScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ContentPressScaleTransform.ScaleX = 1;
            ContentPressScaleTransform.ScaleY = 1;
        }

        private void ResetAdvancedInteractionVisuals()
        {
            _shadowAnimationVersion++;
            if (_isContentSpringActive ||
                ContentPressScaleTransform.HasAnimatedProperties ||
                ContentPressScaleTransform.ScaleX != 1 ||
                ContentPressScaleTransform.ScaleY != 1)
            {
                ResetContentPressScale();
            }

            if (MoveTransform.X != 0 || MoveTransform.Y != 0)
            {
                MoveTransform.X = 0;
                MoveTransform.Y = 0;
            }

            if (DynamicShadow.HasAnimatedProperties)
            {
                DynamicShadow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
                DynamicShadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
            }

            if (DynamicShadow.Opacity != 0)
            {
                DynamicShadow.Opacity = 0;
            }

            if (DynamicShadow.BlurRadius != 1)
            {
                DynamicShadow.BlurRadius = 1;
            }

            if (DynamicShadow.ShadowDepth != 0)
            {
                DynamicShadow.ShadowDepth = 0;
            }

            TextBorderShadowSurface.Effect = null;
            SetTextBorderShadowRendering(hasShadow: false);
        }

        private void AnimateShadowProperties(bool showShadow)
        {
            if (showShadow && TargetWindow.IsItemScrolling)
            {
                HideDynamicShadowImmediately();
                return;
            }

            int shadowAnimationVersion = ++_shadowAnimationVersion;
            if (showShadow && !ReferenceEquals(TextBorderShadowSurface.Effect, DynamicShadow))
            {
                TextBorderShadowSurface.Effect = DynamicShadow;
            }

            if (showShadow)
            {
                SetTextBorderShadowRendering(hasShadow: true);
            }
            else if (!ReferenceEquals(TextBorderShadowSurface.Effect, DynamicShadow))
            {
                HideDynamicShadowImmediately();
                return;
            }

            // 阴影显示/隐藏的动画
            double targetOpacity = showShadow ? 0.6 : 0.0;
            double targetBlurRadius = showShadow ? 20 : 0;

            // 透明度动画
            DoubleAnimation opacityAnimation = new DoubleAnimation
            {
                To = targetOpacity,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            // 模糊半径动画
            DoubleAnimation blurAnimation = new DoubleAnimation
            {
                To = targetBlurRadius,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            if (!showShadow)
            {
                opacityAnimation.Completed += (_, _) =>
                    CompleteDynamicShadowHide(shadowAnimationVersion);
            }

            // 应用动画
            DynamicShadow.BeginAnimation(DropShadowEffect.OpacityProperty, opacityAnimation);
            DynamicShadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, blurAnimation);
            MoveTransform.X = 0;
            MoveTransform.Y = 0;
        }

        private void CompleteDynamicShadowHide(int shadowAnimationVersion)
        {
            if (shadowAnimationVersion != _shadowAnimationVersion)
            {
                return;
            }

            DynamicShadow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
            DynamicShadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
            DynamicShadow.Opacity = 0;
            DynamicShadow.BlurRadius = 0;
            DynamicShadow.ShadowDepth = 0;
            TextBorderShadowSurface.Effect = null;
            SetTextBorderShadowRendering(hasShadow: false);
        }

        private Point UpdateShadowPosition(
            Point mousePosition,
            FrameworkElement border,
            DropShadowEffect shadow)
        {
            double centerX = border.ActualWidth / 2;
            double centerY = border.ActualHeight / 2;
            if (centerX <= 0 || centerY <= 0)
            {
                shadow.ShadowDepth = 0;
                return default;
            }

            double relativeX = (mousePosition.X - centerX) / centerX;
            double relativeY = (mousePosition.Y - centerY) / centerY;


            double distanceFromCenter = Math.Sqrt(relativeX * relativeX + relativeY * relativeY);
            double shadowDepth = Math.Min(distanceFromCenter * maxShadowDepth, maxShadowDepth);
            double angle = Math.Atan2(relativeY, -relativeX) * (180 / Math.PI);

            // 使用动画更新位置，让移动更平滑
            shadow.ShadowDepth = shadowDepth;
            shadow.Direction = angle;
            
            return new Point(relativeX, relativeY);
            

        }
        private void RelativeTransMove(Point p,TranslateTransform transform)
        {
            SetTextBorderShadowRendering(hasShadow: true);
            transform.X = p.X;
            transform.Y = p.Y;
        }

        private void SetTextBorderShadowRendering(bool hasShadow)
        {
            TextHintingMode mode = hasShadow
                ? TextHintingMode.Animated
                : TextHintingMode.Fixed;
            if (TextOptions.GetTextHintingMode(TextBorderContentSurface) != mode)
            {
                TextOptions.SetTextHintingMode(TextBorderContentSurface, mode);
            }

            if (hasShadow)
            {
                if (!ReferenceEquals(
                        TextBorderContentSurface.CacheMode,
                        _textBorderShadowCache))
                {
                    TextBorderContentSurface.CacheMode = _textBorderShadowCache;
                }
            }
            else if (TextBorderContentSurface.CacheMode is not null)
            {
                TextBorderContentSurface.ClearValue(UIElement.CacheModeProperty);
            }
        }

        double maxShadowDepth = 6;
        // 添加字段跟踪鼠标状态


        private void TextBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            maxShadowDepth = 10;

            _dragStartPoint = e.GetPosition(TextBorder);
            _suppressClickPaste = false;
            _ = TextBorder.CaptureMouse();
            SetContentPressed(true);

            if (CanShowAdvancedShadow)
            {
                var epoint = UpdateShadowPosition(
                    e.GetPosition(TextBorder),
                    TextBorder,
                    DynamicShadow);
                RelativeTransMove(epoint, MoveTransform);
            }
            else if (Setting.EnableAdvancedAnimation)
            {
                HideDynamicShadowImmediately();
            }
            else
            {
                ResetAdvancedInteractionVisuals();
            }

        }

        private async void TextBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            maxShadowDepth = 6;

            _dragStartPoint = null;
            if (ReferenceEquals(Mouse.Captured, TextBorder))
            {
                Mouse.Capture(null);
            }

            SetContentPressed(false);

            if (CanShowAdvancedShadow)
            {
                var epoint = UpdateShadowPosition(
                    e.GetPosition(TextBorder),
                    TextBorder,
                    DynamicShadow);
                RelativeTransMove(epoint, MoveTransform);
            }
            else if (Setting.EnableAdvancedAnimation)
            {
                HideDynamicShadowImmediately();
            }
            else
            {
                ResetAdvancedInteractionVisuals();
            }
            if (_suppressClickPaste)
            {
                _suppressClickPaste = false;
                return;
            }

            GlobalFocusManager.RestorePreviousFocus();
            try
            {
                await Task.Delay(100, _lifetimeCancellation.Token);
                if (!_isClosing)
                {
                    OnCVPaste();
                }
            }
            catch (OperationCanceledException) when (_isClosing)
            {
            }
            //MyClipboard.Paste();
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            if (!_dragInProgress && Mouse.LeftButton != MouseButtonState.Pressed)
            {
                _dragStartPoint = null;
                SetContentPressed(false);
            }
        }


        public event EventHandler<BoolEventArgs>? CVPaste;

        internal event EventHandler<ClipboardDragDataRequestedEventArgs>? DragDataRequested;

        private void OnCVPaste()
        {
            CVPaste?.Invoke(this, new BoolEventArgs(true));
        }


        private void TextBorder_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            textBlockDown = true;
            e.Handled = true;
        }

        private bool textBlockDown = false;



        private void TextBlock_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            textBlockDown = true;
        }

        private void TextBorder_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!textBlockDown)
            {
                return;
            }

            textBlockDown = false;
            EditMode();
            e.Handled = true;
        }

        private void TextBlock_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (textBlockDown)
            {
                
                textBlockDown = false;
                EditMode();

                
            }
        }

        private void EditMode()
        {
            if (IsImage)
            {
                _ = OpenFullResolutionPreviewAsync();
                return;
            }

            ShowEditPopup();
            EditBox.Text = CVContent;
            EditBox.UndoLimit = 0;
            EditBox.UndoLimit = 20; // 恢复限制，但清除了历史
        }

        private async Task OpenFullResolutionPreviewAsync()
        {
            string? imageLink = _imageLink;
            if (string.IsNullOrWhiteSpace(imageLink))
            {
                return;
            }

            int imageLoadVersion = _imageLoadVersion;
            int previewLoadVersion = ++_previewImageLoadVersion;
            try
            {
                BitmapSource? fullResolutionImage =
                    await ClipboardImageVisualCache.LoadFullResolutionAsync(
                        imageLink,
                        _lifetimeCancellation.Token);
                if (_isClosing ||
                    imageLoadVersion != _imageLoadVersion ||
                    previewLoadVersion != _previewImageLoadVersion ||
                    !string.Equals(
                        imageLink,
                        _imageLink,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _previewImageSource = fullResolutionImage ?? _imageSource;
                OnPropertyChanged(nameof(PreviewImageSource));
                if (_previewImageSource is not null)
                {
                    ShowEditPopup();
                }
            }
            catch (OperationCanceledException) when (
                _isClosing || _lifetimeCancellation.IsCancellationRequested)
            {
            }
        }

        private void ShowEditPopup()
        {
            EditPopup.IsOpen = true;
            EditPopup.Visibility = Visibility.Visible;
            var fadeInAnimation = new DoubleAnimation
            {
                From = 0,
                Duration = TimeSpan.FromSeconds(0.2),
                FillBehavior = FillBehavior.Stop,
            };

            EditPopup.Child.BeginAnimation(
                UIElement.OpacityProperty,
                fadeInAnimation);
        }

        private void EditPopup_Closed(object? sender, EventArgs e)
        {
            ++_previewImageLoadVersion;
            if (_previewImageSource is null)
            {
                return;
            }

            _previewImageSource = null;
            OnPropertyChanged(nameof(PreviewImageSource));
        }

        private void PreviewImageBorder_MouseLeftButtonUp(
            object sender,
            MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
            {
                return;
            }

            e.Handled = true;
            OpenImagePreviewWindow();
        }

        private async void OpenImageButton_Click(object sender, RoutedEventArgs e)
        {
            await OpenImageWithDefaultApplicationAsync(CustomPopup);
        }

        private async Task OpenImageWithDefaultApplicationAsync(Popup popupToClose)
        {
            if (string.IsNullOrWhiteSpace(_imageLink))
            {
                return;
            }

            try
            {
                string imagePath = System.IO.Path.GetFullPath(_imageLink);
                if (!File.Exists(imagePath))
                {
                    throw new FileNotFoundException("图片文件不存在。", imagePath);
                }

                // A WPF Popup owns a separate HWND. Close it before ShellExecute transfers
                // foreground activation to the image viewer so its teardown cannot split the
                // independent SuperCV windows across the viewer's Z-order position.
                popupToClose.IsOpen = false;
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);

                System.Windows.Application application = System.Windows.Application.Current;
                var deactivated = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler onDeactivated = (_, _) => deactivated.TrySetResult(true);
                application.Deactivated += onDeactivated;
                try
                {
                    _ = Process.Start(new ProcessStartInfo
                    {
                        FileName = imagePath,
                        UseShellExecute = true,
                    });

                    Task timeout = Task.Delay(
                        TimeSpan.FromSeconds(2),
                        _lifetimeCancellation.Token);
                    _ = await Task.WhenAny(deactivated.Task, timeout);

                    // The viewer keeps foreground focus; only restore the native topmost band.
                    await Task.Delay(50, _lifetimeCancellation.Token);
                    RestoreApplicationTopmostWindows(application);

                    // Windows Photos can replace its activation/splash HWND shortly afterward.
                    // One bounded follow-up keeps the fix reliable without a persistent timer.
                    await Task.Delay(500, _lifetimeCancellation.Token);
                    RestoreApplicationTopmostWindows(application);
                }
                finally
                {
                    application.Deactivated -= onDeactivated;
                }
            }
            catch (OperationCanceledException) when (
                _isClosing || _lifetimeCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                new AlertDialog($"打开图片失败: {exception.Message}").ShowDialog();
            }
        }

        private void OpenImagePreviewWindow()
        {
            if (_previewImageSource is null || !PopBorder.IsVisible)
            {
                return;
            }

            const double previewShadowMargin = 20;
            Point topLeft = PopBorder.PointToScreen(
                new Point(-previewShadowMargin, -previewShadowMargin));
            Point bottomRight = PopBorder.PointToScreen(
                new Point(
                    PopBorder.ActualWidth + previewShadowMargin,
                    PopBorder.ActualHeight + previewShadowMargin));
            var previewWindow = new ImagePreviewWindow(_previewImageSource);

            // Show the independent topmost window in the preview's exact screen bounds before
            // closing the Popup. That preserves the visual surface during the HWND transition.
            previewWindow.ShowAt(new Rect(topLeft, bottomRight));
            EditPopup.IsOpen = false;
        }

        internal static void RestoreApplicationTopmostWindows(System.Windows.Application application)
        {
            foreach (Window window in application.Windows)
            {
                if (!window.IsVisible ||
                    !window.Topmost ||
                    window is not (SuperCVWindow or CV or ImagePreviewWindow))
                {
                    continue;
                }

                nint handle = new WindowInteropHelper(window).Handle;
                if (handle != nint.Zero)
                {
                    _ = SetWindowPos(
                        handle,
                        HwndTopmost,
                        0,
                        0,
                        0,
                        0,
                        RestoreTopmostFlags);
                }
            }
        }



        public event EventHandler? CVChanged;




        private async Task CompleteEditAsync(bool must = false)
        {
            if (IsImage)
            {
                EditPopup.IsOpen = false;
                return;
            }

            // 判断鼠标是否在文本框内
            if (IsMouseOverTextBox() && !must)
            {
                return; // 鼠标还在文本框内，不完成编辑
            }


            // 更新TextBlock的文本
            CVContent = EditBox.Text;
            CVChanged?.Invoke(this, EventArgs.Empty);
            DoubleAnimation fadeInAnimation = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromSeconds(0.3)
            };

            // 直接对Popup的内容应用动画
            EditPopup.Child.BeginAnimation(UIElement.OpacityProperty, fadeInAnimation);
            await Task.Delay(300, _lifetimeCancellation.Token);
            if (!_isClosing)
            {
                EditPopup.IsOpen = false;
            }

                


        }

        private bool IsMouseOverTextBox()
        {


            // 获取鼠标相对位置
            Point mousePos = Mouse.GetPosition(EditBox);

            // 检查鼠标是否在文本框边界内
            return mousePos.X >= 0 && mousePos.Y >= 0 &&
                   mousePos.X <= EditBox.ActualWidth &&
                   mousePos.Y <= EditBox.ActualHeight;
        }

        private async void Border_MouseLeave(object sender, MouseEventArgs e)
        {
            try
            {
                await CompleteEditAsync();
            }
            catch (OperationCanceledException) when (_isClosing)
            {
            }
        }



        private void Button_Click_1(object sender, RoutedEventArgs e)
        {
            if (IsImage || !AiFeatureAvailabilityState.Current.IsEnabled)
            {
                return;
            }

            var dialog = new TextBox2Dialog(title:"AI文本处理设置",tip1:"请输入标签名", tip2:"请输入提示词");
            //dialog.ShowDialog();
            if (dialog.ShowDialog() == true) // 等待 Dialog 关闭，并检查是否点击了"确定"
            {
                CustomInstructionsManager.AddInstruction(dialog.LabelName, dialog.Prompt);
            }
        }

        private void Button_Click_2(object sender, RoutedEventArgs e)
        {
            if (IsImage || !AiFeatureAvailabilityState.Current.IsEnabled)
            {
                CustomCommand?.Invoke(this, "top");
                return;
            }

            Question = "";
            int length=WordBasedTokenEstimator.EstimateTokenCount(CVContent);
            if (length > 10000)
            {
                var alert = new AlertDialog("内容过长，请删减重试");
                alert.ShowDialog();
                
                return; // 直接返回，不执行后续操作
                
            }
            else
            {
                AIPopup.IsOpen = true;

                // 创建淡入动画
                DoubleAnimation fadeInAnimation = new DoubleAnimation
                {
                    From = 0,
                    Duration = TimeSpan.FromSeconds(0.2),
                    FillBehavior = FillBehavior.Stop
                };

                // 直接对Popup的内容应用动画
                AIPopup.Child.BeginAnimation(UIElement.OpacityProperty, fadeInAnimation);
                CustomInstructionsManager.Init(AIList, _content: _CVContent);
                CustomInstructionsManager.Response -= OnResponse;
                CustomInstructionsManager.Response += OnResponse;
            }








            
        }

        private bool loadingState = false;
        private void OnResponse(object? sender, ResponseEventArgs response)
        {
            if (!AiFeatureAvailabilityState.Current.IsEnabled)
            {
                return;
            }

            if (!response.IsComplete)
            {
                AIPopup.IsOpen = false;
                VisualStateManager.GoToElementState(ContentGrid, "LoadingState", true);
                loadingState = true;
            }
            else
            {
                VisualStateManager.GoToElementState(ContentGrid, "NormalState", true);
                if (loadingState == true) 
                { 
                    if (response.Message != null && response.Message!= "SuperCV网络连接异常，请稍后重试！")
                    {
                        CVContent = response.Message;
                        CVChanged?.Invoke(this, EventArgs.Empty);
                    }
                    else if (response.Message== "SuperCV网络连接异常，请稍后重试！")
                    {
                        var alert = new AlertDialog("网络连接异常，请重试");
                        alert.ShowDialog();
                    }
                    loadingState = false;
                    AIPopup.IsOpen = false;
                }
         
            }

        }


        [DllImport("User32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);
        private void EditPopup_GotFocus(object sender, RoutedEventArgs e)
        {
            if (IsImage)
            {
                return;
            }

            if (PresentationSource.FromVisual(EditPopup.Child) is HwndSource source)
            {
                SetFocus(source.Handle);
                EditBox.Focus();
            }
        }

        private void AIPopup_GotFocus(object sender, RoutedEventArgs e)
        {
            if (PresentationSource.FromVisual(AIPopup.Child) is HwndSource source)
            {
                SetFocus(source.Handle);
                AITextBox.Focus();
            }
        }
        public event EventHandler<string>? CustomCommand;
        private void Button_Click_3(object sender, RoutedEventArgs e)
        {
            if (IsImage)
            {
                return;
            }

            CustomCommand?.Invoke(this, "bookmark");
        }



        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            CustomPopup.IsOpen = false;
            CustomCommand?.Invoke(this, "export");
        }

        private void Button_Click_5(object sender, RoutedEventArgs e)
        {
            if (IsImage)
            {
                return;
            }

            CustomPopup.IsOpen = false;
            CustomCommand?.Invoke(this, "onlyText");
        }

        private void Button_Click_10(object sender, RoutedEventArgs e)
        {
            if (IsImage)
            {
                return;
            }

            CustomPopup.IsOpen = false;
            CustomCommand?.Invoke(this, "revoke");
        }

        private void WebSearchButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsImage || string.IsNullOrWhiteSpace(CVContent))
            {
                return;
            }

            string searchUrl = $"https://www.bing.com/search?q={Uri.EscapeDataString(CVContent.Trim())}";
            CustomPopup.IsOpen = false;

            try
            {
                _ = Process.Start(new ProcessStartInfo
                {
                    FileName = searchUrl,
                    UseShellExecute = true,
                });
            }
            catch (Exception exception)
            {
                new AlertDialog($"无法启动默认浏览器: {exception.Message}").ShowDialog();
            }
        }



        private void Button_Click_7(object sender, RoutedEventArgs e)
        {
            AskAI();
        }

        private void AITextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                AskAI();
            }
        }
        private void AskAI()
        {
            if (IsImage || !AiFeatureAvailabilityState.Current.IsEnabled)
            {
                return;
            }

            string question = Question?.Trim() ?? string.Empty;
            int tokenCount = UnicodeTextTokenCountProvider?.Invoke()
                ?? WordBasedTokenEstimator.EstimateTokenCount(CVContent);
            var chatWindow = new AIChatbot(
                CVContent,  // 内容对象
                question,
                sourceTokenCount: tokenCount,
                agentTool: new AgentTool(
                    ((App)System.Windows.Application.Current).Runtime,
                    focusedEntryId: EntryId));
            chatWindow.Show();
            AIPopup.IsOpen = false;
        }






        private void InitializeColorButtons(int tag = 0)
        {
            // 定义颜色名称和对应的Tag值
            var colorMappings = new (string Name, int Tag)[]
            {
        ("红色", 1),
        ("橙色", 2),
        ("紫色", 3),
        ("绿色", 4),
        ("蓝色", 5),
        ("无标记", 0)  // 添加清除颜色的选项
            };

            // 清空现有按钮
            ColorStackPanel.Children.Clear();

            // 动态创建按钮
            foreach (var color in colorMappings)
            {
                Button colorButton = new Button
                {
                    Style = (Style)FindResource("moreButtonStyle"),
                    Padding = new Thickness(8, 0, 8, 0),
                    BorderThickness = new Thickness(0),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Height = 26,
                    FontSize = 14,
                    Tag = color.Tag  // 设置Tag值
                };
                colorButton.SetResourceReference(Button.BackgroundProperty, "Brush.Surface.Raised");
                colorButton.SetResourceReference(Button.ForegroundProperty, "Brush.Text.Primary");

                // 创建Grid作为Button的内容
                Grid grid = new Grid();

                // 定义Grid的列
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); // 图标列
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 文本列

                // 创建左边的图标（Checkmark）
                TextBlock checkIcon = new TextBlock
                {
                    Text = "✓",  // 或者使用Segoe UI Symbol图标的字符，如"" (Unicode: E10B)
                    FontFamily = new FontFamily("Segoe UI Symbol"), // 支持图标的字体
                    Visibility = (tag == color.Tag) ? Visibility.Visible : Visibility.Collapsed,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                Grid.SetColumn(checkIcon, 0);

                // 创建右边的颜色名称
                TextBlock colorNameText = new TextBlock
                {
                    Text = LocalizationService.Current.T(color.Name),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                Grid.SetColumn(colorNameText, 1);

                // 将元素添加到Grid
                grid.Children.Add(checkIcon);
                grid.Children.Add(colorNameText);

                // 设置Button的内容为Grid
                colorButton.Content = grid;

                // 添加点击事件
                colorButton.Click += ColorButton_Click;

                // 添加到StackPanel
                ColorStackPanel.Children.Add(colorButton);
            }
        }
        private void ColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is int tagValue)
            {
                CloseColorPopupAndRestoreParentMenu(closeParentMenu: true);
                CustomCommand?.Invoke(this, tagValue.ToString());
            }
        }

        private void ColorPopup_MouseLeave(object sender, MouseEventArgs e)
        {
            ScheduleColorPopupClose();
        }

        private void ColorPopup_MouseEnter(object sender, MouseEventArgs e) =>
            StopColorPopupCloseTimer();

        private void ColorMenuButton_MouseEnter(object sender, MouseEventArgs e)
        {
            StopColorPopupCloseTimer();
            ShowColorPopup();
        }

        private void ColorMenuButton_MouseLeave(object sender, MouseEventArgs e) =>
            ScheduleColorPopupClose(ColorPopupTransitionDelay);

        private void Button_Click_8(object sender, RoutedEventArgs e)
            => ShowColorPopup();

        private void ShowColorPopup()
        {
            StopColorPopupCloseTimer();
            if (ColorPopup.IsOpen)
            {
                return;
            }

            // A StaysOpen=False Popup captures the mouse. Release that capture
            // while its hover submenu is active so the color buttons can receive
            // their own pointer and click events.
            CustomPopup.StaysOpen = true;
            InitializeColorButtons(Tag);
            

            ColorPopup.IsOpen = true;

            // 创建淡入动画
            DoubleAnimation fadeInAnimation = new DoubleAnimation
            {
                From = 0,
                Duration = TimeSpan.FromSeconds(0.2),
                FillBehavior = FillBehavior.Stop
            };

            // 直接对Popup的内容应用动画
            ColorPopup.Child.BeginAnimation(UIElement.OpacityProperty, fadeInAnimation);
        }

        private void ScheduleColorPopupClose(TimeSpan? delay = null)
        {
            if (!ColorPopup.IsOpen)
            {
                return;
            }

            _colorPopupCloseTimer ??= new DispatcherTimer(
                DispatcherPriority.Background,
                Dispatcher)
            {
                Interval = delay ?? ColorPopupCloseDelay,
            };
            _colorPopupCloseTimer.Interval = delay ?? ColorPopupCloseDelay;
            _colorPopupCloseTimer.Tick -= ColorPopupCloseTimer_Tick;
            _colorPopupCloseTimer.Tick += ColorPopupCloseTimer_Tick;
            _colorPopupCloseTimer.Stop();
            _colorPopupCloseTimer.Start();
        }

        private void ColorPopupCloseTimer_Tick(object? sender, EventArgs e)
        {
            StopColorPopupCloseTimer();
            if (!ColorMenuButton.IsMouseOver &&
                !ColorPopupContent.IsMouseOver)
            {
                CloseColorPopupAndRestoreParentMenu(closeParentMenu: !CustomPopupContent.IsMouseOver);
            }
        }

        private void CloseColorPopupAndRestoreParentMenu(bool closeParentMenu)
        {
            StopColorPopupCloseTimer();
            ColorPopup.IsOpen = false;
            CustomPopup.StaysOpen = false;

            if (closeParentMenu)
            {
                CustomPopup.IsOpen = false;
            }
        }

        private void StopColorPopupCloseTimer()
        {
            _colorPopupCloseTimer?.Stop();
        }

        private void CustomPopup_Closed(object? sender, EventArgs e) =>
            CloseColorPopupAndRestoreParentMenu(closeParentMenu: false);

        private void EditBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // 禁用 Ctrl+C（复制）、Ctrl+X（剪切）、Ctrl+V（粘贴）
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control ||
                e.Key == Key.X && Keyboard.Modifiers == ModifierKeys.Control ||
                e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true; // 阻止事件继续传播
            }
        }

        private void Button_Click_6(object sender, RoutedEventArgs e)
        {
            CustomPopup.IsOpen = false;
            CustomCommand?.Invoke(this, "top");
        }

        private void Button_Click_9(object sender, RoutedEventArgs e)
        {
            if (IsImage)
            {
                return;
            }

            CustomCommand?.Invoke(this, "charExchange");
        }
        private Visibility _topIcon =  Visibility.Collapsed;
        public Visibility TopIcon
        {
            get => _topIcon;

            set {
                _topIcon = value;
                OnPropertyChanged();
            }
        }


    }
}
