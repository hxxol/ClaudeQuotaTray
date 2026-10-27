using System;
using System.Drawing;
using System.Linq;

namespace CodexQuotaTray
{
    internal sealed class TaskbarLayoutSnapshot
    {
        internal static readonly TaskbarLayoutSnapshot Empty = new TaskbarLayoutSnapshot(IntPtr.Zero, Rectangle.Empty, Rectangle.Empty, Array.Empty<Rectangle>(), 96);
        internal readonly IntPtr Handle;
        internal readonly Rectangle Bounds;
        internal readonly Rectangle Notifications;
        internal readonly Rectangle[] Occupied;
        internal readonly uint Dpi;
        internal TaskbarLayoutSnapshot(IntPtr handle, Rectangle bounds, Rectangle notifications, Rectangle[] occupied, uint dpi)
        { Handle = handle; Bounds = bounds; Notifications = notifications; Occupied = occupied; Dpi = dpi; }
    }

    internal static class TaskbarPlacement
    {
        internal static int EdgeSize(Rectangle taskbar, float dpiScale)
        { return Math.Max(16, Math.Min(taskbar.Width, taskbar.Height) - (int)Math.Round(12 * dpiScale)); }

        internal static bool TryPlace(TaskbarLayoutSnapshot layout, out Rectangle placement)
        {
            placement = Rectangle.Empty;
            if (layout.Handle == IntPtr.Zero || layout.Bounds.IsEmpty || layout.Notifications.IsEmpty) return false;
            var bar = layout.Bounds;
            bool horizontal = bar.Width >= bar.Height;
            var dpiScale = layout.Dpi / 96f;
            int extent = EdgeSize(bar, dpiScale);
            int start = horizontal ? bar.Left : bar.Top;
            int limit = (horizontal ? layout.Notifications.Left : layout.Notifications.Top) - extent - 8;
            for (int attempt = 0; attempt <= layout.Occupied.Length; attempt++)
            {
                if (limit < start + 4) return false;
                int cross = horizontal
                    ? Math.Min(bar.Top + Math.Max(0, (bar.Height - extent) / 2) + Math.Max(1, (int)Math.Round(dpiScale)), bar.Bottom - extent - 1)
                    : bar.Left + Math.Max(0, (bar.Width - extent) / 2);
                var candidate = horizontal ? new Rectangle(limit, cross, extent, extent) : new Rectangle(cross, limit, extent, extent);
                if (!bar.Contains(candidate)) return false;
                var collisions = layout.Occupied.Where(r => r.IntersectsWith(candidate)).ToArray();
                if (collisions.Length == 0) { placement = candidate; return true; }
                limit = collisions.Min(r => horizontal ? r.Left : r.Top) - extent - 8;
            }
            return false;
        }
    }
}
