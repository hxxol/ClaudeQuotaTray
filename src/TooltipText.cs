using System;
using System.Drawing;
using System.Windows.Forms;

namespace CodexQuotaTray
{
    // Tooltip text may contain line breaks; each line is measured and drawn separately
    // because GDI vertical centering only works for single-line text.
    internal static class TooltipText
    {
        private const TextFormatFlags LineFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

        internal static Size Measure(Graphics graphics, string text, Font font)
        {
            int width = 0, height = 0;
            foreach (var line in (text ?? string.Empty).Split('\n'))
            {
                var size = TextRenderer.MeasureText(graphics, line, font, new Size(int.MaxValue, int.MaxValue), LineFlags);
                width = Math.Max(width, size.Width);
                height += size.Height;
            }
            return new Size(width, height);
        }

        internal static void Draw(Graphics graphics, string text, Font font, Rectangle bounds, Color color)
        {
            var lines = (text ?? string.Empty).Split('\n');
            var total = Measure(graphics, text, font).Height;
            var y = bounds.Top + (bounds.Height - total) / 2;
            foreach (var line in lines)
            {
                var height = TextRenderer.MeasureText(graphics, line, font, new Size(int.MaxValue, int.MaxValue), LineFlags).Height;
                TextRenderer.DrawText(graphics, line, font, new Rectangle(bounds.Left, y, bounds.Width, height), color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | LineFlags);
                y += height;
            }
        }
    }
}