using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CodexQuotaTray
{
    internal static class GraphicsExtensions
    {
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr window);

        public static float GetDpiScale(IntPtr window)
        {
            try
            {
                var dpi = GetDpiForWindow(window);
                return dpi <= 0 ? 1f : dpi / 96f;
            }
            catch (EntryPointNotFoundException) { return 1f; }
        }

        public static Size ScaleSize(Size size, float scale)
        {
            return new Size((int)Math.Round(size.Width * scale), (int)Math.Round(size.Height * scale));
        }

        public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, int radius)
        {
            using (var path = CreateRoundedPath(new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height), radius)) graphics.FillPath(brush, path);
        }

        public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, Rectangle bounds, int radius)
        {
            using (var path = CreateRoundedPath(new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height), radius)) graphics.DrawPath(pen, path);
        }

        public static GraphicsPath CreateRoundedPath(RectangleF bounds, float radius)
        {
            radius = Math.Max(0f, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2f));
            if (radius <= 0f)
            {
                var rectanglePath = new GraphicsPath();
                rectanglePath.AddRectangle(bounds);
                return rectanglePath;
            }
            var diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
