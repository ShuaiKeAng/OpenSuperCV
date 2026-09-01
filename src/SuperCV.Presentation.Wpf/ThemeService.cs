using System;
using System.Linq;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SuperCV
{
    public static class ThemeService
    {
        private static ThemeCatalog _catalog = ThemeCatalog.BuiltIn();
        private static ResourceDictionary? _appliedDictionary;
        private static bool _selectionResetRequired;

        internal static void Initialize(string dataRoot)
        {
            _catalog = ThemeCatalog.Load(dataRoot);
            _selectionResetRequired = _catalog.WasRecreatedFromEmpty;
        }

        internal static IReadOnlyList<ThemeOption> GetAvailableThemes(bool isEnglish) =>
            _catalog.Definitions
                .Select(item => new ThemeOption(item.Id, item.ChineseName, item.EnglishName, isEnglish))
                .ToArray();

        internal static IReadOnlyList<ThemeOption> RefreshAvailableThemes(bool isEnglish)
        {
            if (_catalog.DirectoryPath is not null)
            {
                _catalog = ThemeCatalog.Load(System.IO.Path.GetDirectoryName(_catalog.DirectoryPath) ?? _catalog.DirectoryPath);
                _selectionResetRequired |= _catalog.WasRecreatedFromEmpty;
            }

            return GetAvailableThemes(isEnglish);
        }

        internal static bool ConsumeSelectionResetRequirement()
        {
            bool required = _selectionResetRequired;
            _selectionResetRequired = false;
            return required;
        }

        /// <summary>Applies a validated theme and returns the selected (or fallback) identifier.</summary>
        internal static string Apply(string? themeId, bool darkMode)
        {
            System.Windows.Application app = System.Windows.Application.Current
                ?? throw new InvalidOperationException("应用程序尚未初始化。");
            ThemeDefinition theme = _catalog.Resolve(themeId);
            ResourceDictionary newTheme = CreateDictionary(theme, darkMode);

            var dictionaries = app.Resources.MergedDictionaries;
            int themeIndex = dictionaries
                .Select((dictionary, index) => (dictionary, index))
                .Where(item => ReferenceEquals(item.dictionary, _appliedDictionary) ||
                    item.dictionary.Source?.OriginalString.EndsWith("Theme.xaml", StringComparison.OrdinalIgnoreCase) == true)
                .Select(item => item.index)
                .DefaultIfEmpty(-1)
                .First();

            if (themeIndex >= 0)
            {
                dictionaries[themeIndex] = newTheme;
            }
            else
            {
                dictionaries.Insert(Math.Min(2, dictionaries.Count), newTheme);
            }

            _appliedDictionary = newTheme;
            return theme.Id;
        }

        private static ResourceDictionary CreateDictionary(ThemeDefinition theme, bool darkMode)
        {
            IReadOnlyDictionary<string, string> tokens = darkMode ? theme.DarkTokens : theme.LightTokens;
            var dictionary = new ResourceDictionary();
            foreach ((string key, string value) in tokens)
            {
                _ = ThemeCatalog.TryParseColor(value, out Color color);
                if (key.StartsWith("Color.", StringComparison.Ordinal))
                {
                    dictionary[key] = color;
                }
                else
                {
                    var brush = new SolidColorBrush(color);
                    brush.Freeze();
                    dictionary[key] = brush;
                }
            }

            var logo = new System.Windows.Media.Imaging.BitmapImage();
            logo.BeginInit();
            logo.UriSource = new Uri(
                darkMode
                    ? "pack://application:,,,/SuperCV;component/Assets/SuperCV.Remastered.Dark.png"
                    : "pack://application:,,,/SuperCV;component/Assets/SuperCV.Remastered.png",
                UriKind.Absolute);
            logo.EndInit();
            logo.Freeze();
            dictionary["WindowHeaderLogoSource"] = logo;
            return dictionary;
        }
    }
    public class TriangleBorderShape : Shape
    {
        // 优化2：移除 AffectsMeasure，因为圆角不影响占地大小，只需 AffectsRender 即可降低排版开销
        public static readonly DependencyProperty CornerRadiusProperty =
            DependencyProperty.Register(
                nameof(CornerRadius),
                typeof(CornerRadius),
                typeof(TriangleBorderShape),
                new FrameworkPropertyMetadata(new CornerRadius(0),
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public CornerRadius CornerRadius
        {
            get => (CornerRadius)GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }

        // 优化1：增加缓存字段
        private Geometry? _cachedGeometry;
        private Size _cachedSize;
        private CornerRadius _cachedCornerRadius;

        protected override Geometry DefiningGeometry
        {
            get
            {
                double W = ActualWidth;
                double H = ActualHeight;

                // 避免尺寸为0时报错
                if (W <= 0 || H <= 0) return Geometry.Empty;

                // 【核心优化】检查缓存：如果尺寸和圆角都没变，直接返回缓存的图形
                if (_cachedGeometry != null &&
                    _cachedSize.Width == W &&
                    _cachedSize.Height == H &&
                    _cachedCornerRadius == CornerRadius)
                {
                    return _cachedGeometry;
                }

                // 映射圆角
                double R_tl = CornerRadius.TopLeft;     // 左上角 (锐角)
                double R_tr = CornerRadius.TopRight;    // 右上角 (直角)
                double R_br = CornerRadius.BottomRight; // 右下角 (锐角)

                // 斜边长度
                double L = Math.Sqrt(W * W + H * H);

                // 计算锐角圆角切点到顶点的距离
                double d_tl = R_tl * (L + W) / H;
                double d_br = R_br * (L + H) / W;

                // 安全机制
                double k1 = (d_tl + R_tr) / W;
                double k2 = (R_tr + d_br) / H;
                double k3 = (d_tl + d_br) / L;
                double maxK = Math.Max(1.0, Math.Max(k1, Math.Max(k2, k3)));
                if (maxK > 1.0)
                {
                    R_tl /= maxK; R_tr /= maxK; R_br /= maxK;
                    d_tl /= maxK; d_br /= maxK;
                }

                // 计算所有的关键点坐标
                Point p_tl_top = new Point(d_tl, 0);
                Point p_tr_top = new Point(W - R_tr, 0);
                Point p_tr_right = new Point(W, R_tr);
                Point p_br_right = new Point(W, H - d_br);
                Point p_br_hyp = new Point(W - d_br * (W / L), H - d_br * (H / L));
                Point p_tl_hyp = new Point(d_tl * (W / L), d_tl * (H / L));

                // 绘制路径
                StreamGeometry geometry = new StreamGeometry();
                using (StreamGeometryContext context = geometry.Open())
                {
                    context.BeginFigure(p_tl_top, isFilled: true, isClosed: true);

                    // 顶边 -> 右上角 (直角)
                    context.LineTo(p_tr_top, isStroked: true, isSmoothJoin: true);
                    if (R_tr > 0)
                        context.ArcTo(p_tr_right, new Size(R_tr, R_tr), 0, false, SweepDirection.Clockwise, true, true);

                    // 右边 -> 右下角 (锐角)
                    context.LineTo(p_br_right, isStroked: true, isSmoothJoin: true);
                    if (R_br > 0)
                        context.ArcTo(p_br_hyp, new Size(R_br, R_br), 0, false, SweepDirection.Clockwise, true, true);

                    // 斜边 -> 左上角 (锐角)
                    context.LineTo(p_tl_hyp, isStroked: true, isSmoothJoin: true);
                    if (R_tl > 0)
                        context.ArcTo(p_tl_top, new Size(R_tl, R_tl), 0, false, SweepDirection.Clockwise, true, true);
                }

                // 【核心优化】冻结图形：极大地提升性能！告诉 WPF 这个图形不可修改，免去事件监听
                geometry.Freeze();

                // 更新缓存
                _cachedSize = new Size(W, H);
                _cachedCornerRadius = CornerRadius;
                _cachedGeometry = geometry;

                return _cachedGeometry;
            }
        }
    }
}
