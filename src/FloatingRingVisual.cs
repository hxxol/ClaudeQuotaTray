using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CodexQuotaTray
{
    /// <summary>Vector-only floating display. Every measurement is relative to a 256 px canvas.</summary>
    internal sealed class FloatingRingVisual : FrameworkElement
    {
        private readonly AppSettings _settings;
        private int _remaining = 73;
        private bool _active = true;
        private bool _alert;

        public FloatingRingVisual(AppSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            SnapsToDevicePixels = false;
            Focusable = false;
        }

        // A barely nonzero circular alpha enables input over the hollow center of a layered HWND.
        // The exterior stays fully transparent and does not block other desktop windows.
        public bool EnableCircularHitArea { get; set; }

        public void SetVisual(int remaining, bool active, bool alert)
        {
            _remaining = Math.Clamp(remaining, 0, 100);
            _active = active;
            _alert = alert;
            InvalidateVisual();
        }

        public void Refresh() => InvalidateVisual();

        protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
        {
            var point = hitTestParameters.HitPoint;
            var radius = Math.Min(ActualWidth, ActualHeight) / 2;
            var x = point.X - ActualWidth / 2;
            var y = point.Y - ActualHeight / 2;
            return x * x + y * y <= radius * radius ? new PointHitTestResult(this, point) : null;
        }

        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            var size = Math.Min(ActualWidth, ActualHeight);
            if (size <= 0) return;
            drawing.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
            drawing.PushTransform(new ScaleTransform(size / 256d, size / 256d));

            var center = new Point(128, 128);
            if (EnableCircularHitArea)
                drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, center, 128, 128);

            var stroke = Math.Clamp(_settings.FloatingRingWidth, 2, 32);
            var ringScale = Math.Clamp(_settings.FloatingRingScalePercent, 50, 100) / 100d;
            var radius = (248d * ringScale - stroke) / 2d;
            var ringBrush = Brush(_settings.FloatingRingColor);
            var pen = new Pen(ringBrush, stroke) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
            pen.Freeze();
            if (_active && _remaining == 100)
                drawing.DrawEllipse(null, pen, center, radius, radius);
            else if (_active && _remaining > 0)
            {
                // GDI-compatible clockwise arc, with its missing segment centered at the bottom.
                var sweep = 360d * _remaining / 100d;
                var start = 270d - sweep / 2d;
                var arc = new StreamGeometry();
                using (var geometry = arc.Open())
                {
                    geometry.BeginFigure(PointOnCircle(center, radius, start), false, false);
                    geometry.ArcTo(PointOnCircle(center, radius, start + sweep), new Size(radius, radius),
                        0, sweep > 180, SweepDirection.Clockwise, true, false);
                }
                arc.Freeze();
                drawing.DrawGeometry(null, pen, arc);
            }

            var text = _active ? _remaining.ToString(CultureInfo.InvariantCulture) : "武";
            var typeface = new Typeface(new FontFamily(_active ? "Segoe UI" : "Microsoft YaHei UI"),
                FontStyles.Normal, FontWeight.FromOpenTypeWeight(ClampWeight(_settings.FloatingFontWeight)), FontStretches.Normal);
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, Math.Clamp(_settings.FloatingFontSize, 32, 160), Brush(_settings.FloatingFontColor),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var glyphs = formatted.BuildGeometry(new Point());
            var bounds = glyphs.Bounds;
            if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
            {
                var available = Math.Max(1, radius * 2 - stroke - 12);
                var diagonal = Math.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height);
                var fit = Math.Min(1d, available / diagonal);
                var matrix = new Matrix(fit, 0, 0, fit,
                    128 - (bounds.X + bounds.Width / 2) * fit,
                    128 - (bounds.Y + bounds.Height / 2) * fit);
                drawing.PushTransform(new MatrixTransform(matrix));
                drawing.DrawGeometry(Brush(_settings.FloatingFontColor), null, glyphs);
                drawing.Pop();
            }

            if (_alert)
            {
                var accent = Brush(ThemePalette.FromSettings(_settings).Secondary);
                var marker = new Point(128 + radius * 0.7071, 128 - radius * 0.7071);
                drawing.PushOpacity(0.3);
                drawing.DrawEllipse(accent, null, marker, 16, 16);
                drawing.Pop();
                drawing.DrawEllipse(accent, new Pen(Brushes.White, 2), marker, 10, 10);
            }
            drawing.Pop();
            drawing.Pop();
        }

        internal static int ClampWeight(int weight) => weight switch
        {
            400 => 400,
            500 => 500,
            600 => 600,
            800 => 800,
            _ => 700
        };

        private static Point PointOnCircle(Point center, double radius, double degrees)
        {
            var radians = degrees * Math.PI / 180;
            return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
        }

        private static SolidColorBrush Brush(System.Drawing.Color color)
        {
            var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
            brush.Freeze();
            return brush;
        }
    }
}
