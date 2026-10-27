using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Interop;

namespace CodexQuotaTray
{
    // Native tooltip for the WPF HWND; shares the legacy taskbar tooltip's font,
    // owner-drawn palette, padding, placement, and default 500/100/5000 ms timing.
    internal sealed class LegacyResetTooltip : IDisposable
    {
        private readonly IntPtr _owner;
        private readonly Func<Rectangle> _bounds;
        private readonly HwndSource _source;
        private readonly Font _font = new Font(SystemFonts.MessageBoxFont.FontFamily, 10f, FontStyle.Regular, GraphicsUnit.Point);
        private IntPtr _tooltip;
        private IntPtr _fontHandle;
        private IntPtr _textPointer;
        private string _text = string.Empty;
        private bool _disposed;

        internal LegacyResetTooltip(IntPtr ownerHwnd, Func<Rectangle> physicalBounds)
        {
            _owner = ownerHwnd;
            _bounds = physicalBounds;
            _source = HwndSource.FromHwnd(ownerHwnd);
            _source.AddHook(Hook);
            var controls = new CommonControls { Size = 8, Classes = 0x000000FF };
            InitCommonControlsEx(ref controls);
            _tooltip = CreateWindowEx(0x00000088, "tooltips_class32", null, 0x80000000u | 0x01 | 0x02 | 0x10 | 0x20,
                0, 0, 0, 0, ownerHwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_tooltip == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            _fontHandle = _font.ToHfont();
            SendMessage(_tooltip, 0x0030, _fontHandle, IntPtr.Zero);
            SendMessage(_tooltip, 0x0403, new IntPtr(3), new IntPtr(500)); // initial
            SendMessage(_tooltip, 0x0403, new IntPtr(1), new IntPtr(100)); // reshow
            SendMessage(_tooltip, 0x0403, new IntPtr(2), new IntPtr(5000)); // auto-pop
            _textPointer = Marshal.StringToHGlobalUni(string.Empty);
            var tool = Tool();
            if (SendTool(_tooltip, 0x0432, IntPtr.Zero, ref tool) == IntPtr.Zero)
                throw new InvalidOperationException("The floating tooltip could not register its window.");
            SendMessage(_tooltip, 0x0401, IntPtr.Zero, IntPtr.Zero);
        }

        internal void SetText(string text)
        {
            if (_disposed || _text == (text ?? string.Empty)) return;
            _text = text ?? string.Empty;
            if (_text.Length == 0) Hide();
            var previous = _textPointer;
            _textPointer = Marshal.StringToHGlobalUni(_text);
            var tool = Tool();
            SendTool(_tooltip, 0x0439, IntPtr.Zero, ref tool); // TTM_UPDATETIPTEXTW
            Marshal.FreeHGlobal(previous);
            SendMessage(_tooltip, 0x0401, new IntPtr(_text.Length == 0 ? 0 : 1), IntPtr.Zero);
        }

        internal void Hide()
        {
            if (!_disposed && _tooltip != IntPtr.Zero) SendMessage(_tooltip, 0x041C, IntPtr.Zero, IntPtr.Zero);
        }

        private ToolInfo Tool()
        {
            return new ToolInfo { Size = (uint)Marshal.SizeOf<ToolInfo>(), Flags = 0x11,
                Owner = _owner, Id = (UIntPtr)(ulong)_owner.ToInt64(), Text = _textPointer };
        }

        private IntPtr Hook(IntPtr window, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            if (_disposed || message != 0x004E || lparam == IntPtr.Zero) return IntPtr.Zero;
            var header = Marshal.PtrToStructure<NotifyHeader>(lparam);
            if (header.Window != _tooltip) return IntPtr.Zero;
            if (header.Code == -521) // TTN_SHOW
            {
                var anchor = _bounds();
                var work = Screen.FromRectangle(anchor).WorkingArea;
                var dpi = Math.Max(1f, GetDpiForWindow(_owner) / 96f);
                Size textSize;
                using (var graphics = Graphics.FromHwnd(_owner))
                    textSize = TooltipText.Measure(graphics, _text, _font);
                int width = textSize.Width + (int)(12 * dpi), height = textSize.Height + (int)(8 * dpi);
                int x = Math.Max(work.Left, Math.Min(anchor.Left + (anchor.Width - width) / 2, work.Right - width));
                int y = anchor.Top - height - (int)(8 * dpi);
                if (y < work.Top) y = anchor.Bottom + (int)(8 * dpi);
                AcrylicEffect.ConfigureRoundedCorners(_tooltip);
                SetWindowPos(_tooltip, new IntPtr(-1), x, y, width, height, 0x0010);
                handled = true;
                return new IntPtr(1);
            }
            if (header.Code == -12) // NM_CUSTOMDRAW
            {
                var draw = Marshal.PtrToStructure<CustomDraw>(lparam);
                if (draw.Stage == 1)
                {
                    NativeRect client;
                    GetClientRect(_tooltip, out client);
                    var bounds = Rectangle.FromLTRB(client.Left, client.Top, client.Right, client.Bottom);
                    using (var graphics = Graphics.FromHdc(draw.Dc))
                    using (var background = new SolidBrush(Color.FromArgb(35, 35, 35)))
                    {
                        graphics.FillRectangle(background, bounds);
                        TooltipText.Draw(graphics, _text, _font, bounds, Color.White);
                    }
                    handled = true;
                    return new IntPtr(4); // CDRF_SKIPDEFAULT
                }
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _source.RemoveHook(Hook);
            if (_tooltip != IntPtr.Zero) DestroyWindow(_tooltip);
            _tooltip = IntPtr.Zero;
            if (_fontHandle != IntPtr.Zero) DeleteObject(_fontHandle);
            if (_textPointer != IntPtr.Zero) Marshal.FreeHGlobal(_textPointer);
            _font.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)] private struct CommonControls { public int Size, Classes; }
        [DllImport("comctl32.dll")] private static extern bool InitCommonControlsEx(ref CommonControls controls);
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct NotifyHeader { public IntPtr Window; public UIntPtr Id; public int Code; }
        [StructLayout(LayoutKind.Sequential)] private struct CustomDraw
        { public NotifyHeader Header; public uint Stage; public IntPtr Dc; public NativeRect Bounds; public UIntPtr Item; public uint State; public IntPtr Param; }
        [StructLayout(LayoutKind.Sequential)] private struct ToolInfo
        { public uint Size, Flags; public IntPtr Owner; public UIntPtr Id; public NativeRect Bounds; public IntPtr Instance, Text, Param, Reserved; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint extended, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendTool(IntPtr window, uint message, IntPtr wparam, ref ToolInfo info);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    }
}
