using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace CodexQuotaTray
{
    internal static class RingIconFactory
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        public static Icon Create(int remainingPercent, bool active)
        {
            return Create(remainingPercent, active, ThemePalette.Presets()[0]);
        }

        public static Icon Create(int remainingPercent, bool active, ThemePalette palette)
        {
            return Create(remainingPercent, active, palette, false);
        }

        public static Icon Create(int remainingPercent, bool active, ThemePalette palette, bool completionAlert)
        {
            return Create(remainingPercent, active, palette, completionAlert, new AppSettings());
        }

        public static Icon Create(int remainingPercent, bool active, ThemePalette palette, bool completionAlert, AppSettings settings)
        {
            using (var bitmap = Render(remainingPercent, active, palette, completionAlert, settings, 32))
            {
                var handle = bitmap.GetHicon();
                try { return (Icon)Icon.FromHandle(handle).Clone(); }
                finally { DestroyIcon(handle); }
            }
        }

        internal static Bitmap Render(int remainingPercent, bool active, ThemePalette palette, bool completionAlert, AppSettings settings, int size, bool fullHitArea = false)
        {
            remainingPercent = Math.Max(0, Math.Min(100, remainingPercent));
            var bitmap = new Bitmap(size, size);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.ScaleTransform(size / 32f, size / 32f);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                // Layered windows pass mouse input through alpha-zero pixels. A 1/255 backdrop keeps the full square interactive.
                graphics.Clear(fullHitArea ? Color.FromArgb(1, 0, 0, 0) : Color.Transparent);

                var taskbarLight = ThemePalette.SystemUsesLightTaskbar();
                // No background track: the missing arc remains transparent and centered at the bottom.
                var strokeWidth = (float)Math.Max(1, Math.Min(6, settings.TrayRingWidth));
                var diameter = 31f - strokeWidth;
                var ring = new RectangleF(16f - diameter / 2f, 16f - diameter / 2f, diameter, diameter);
                using (var pen = new Pen(settings.TrayRingColor, strokeWidth))
                {
                    if (active && remainingPercent == 100) graphics.DrawEllipse(pen, ring);
                    else if (active && remainingPercent > 0)
                    {
                        var sweep = 360f * remainingPercent / 100f;
                        graphics.DrawArc(pen, ring, 270f - sweep / 2f, sweep);
                    }
                }

                var text = active ? remainingPercent.ToString() : "武";
                var fontSize = (float)Math.Max(8, Math.Min(20, settings.TrayFontSize));
                using (var format = (StringFormat)StringFormat.GenericTypographic.Clone())
                using (var path = new GraphicsPath())
                using (var brush = new SolidBrush(settings.TrayFontColor))
                {
                    using (var font = new Font(active ? FontFamily.GenericSansSerif.Name : "Microsoft YaHei UI", fontSize, FontStyle.Bold))
                    {
                        if (!active && !string.Equals(font.FontFamily.Name, "Microsoft YaHei UI", StringComparison.OrdinalIgnoreCase)) text = "00";
                        path.AddString(text, font.FontFamily, (int)FontStyle.Bold, fontSize, PointF.Empty, format);
                    }
                    var bounds = path.GetBounds();
                    // Fit the actual glyphs inside the ring, including three-digit 100.
                    var available = Math.Max(1f, diameter - strokeWidth - 2f);
                    var diagonal = (float)Math.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height);
                    var scale = Math.Min(1f, available / Math.Max(1f, diagonal));
                    using (var transform = new Matrix(scale, 0, 0, scale,
                        16f - (bounds.Left + bounds.Width / 2f) * scale,
                        16f - (bounds.Top + bounds.Height / 2f) * scale))
                    {
                        path.Transform(transform);
                        graphics.FillPath(brush, path);
                    }
                }

                if (completionAlert)
                {
                    using (var glow = new SolidBrush(Color.FromArgb(80, palette.Secondary)))
                    using (var dot = new SolidBrush(palette.Secondary))
                    using (var border = new Pen(taskbarLight ? Color.White : Color.FromArgb(232, 244, 255), 1.2f))
                    {
                        graphics.FillEllipse(glow, 20f, 0f, 12f, 12f);
                        graphics.FillEllipse(dot, 22f, 2f, 8f, 8f);
                        graphics.DrawEllipse(border, 22f, 2f, 8f, 8f);
                    }
                }

            }
            return bitmap;
        }

        internal static Color PickColor(int remainingPercent)
        {
            return ThemePalette.Presets()[0].QuotaColor(remainingPercent);
        }
    }
}
