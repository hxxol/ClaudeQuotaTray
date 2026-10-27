using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DrawingRectangle = System.Drawing.Rectangle;

namespace CodexQuotaTray
{
    /// <summary>An independent topmost window. Position and diameter are always physical pixels.</summary>
    internal sealed class FloatingQuotaWindow : Window
    {
        private readonly AppSettings _settings;
        private readonly FloatingRingVisual _ring;
        private IntPtr _handle;
        private HwndSource _source;
        private LegacyResetTooltip _tooltip;
        private QuotaWindow _resetWindow;
        private bool _active;
        private bool _dragging;
        private bool _geometryQueued;
        private bool _applyingGeometry;
        private NativePoint _dragStartCursor;
        private NativeRect _dragStartWindow;
        private int _physicalLeft;
        private int _physicalTop;

        public event Action PositionChanged;

        public FloatingQuotaWindow(AppSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            Title = "Codex Quota";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = Height = Diameter;
            SizeToContent = SizeToContent.Manual;
            UseLayoutRounding = false;
            _ring = new FloatingRingVisual(settings) { EnableCircularHitArea = true, Cursor = Cursors.SizeAll };
            _ring.SetVisual(0, false, false);
            Content = _ring;
            SourceInitialized += OnSourceInitialized;
            Loaded += delegate { QueuePhysicalGeometry(); };
            PreviewMouseLeftButtonDown += BeginDrag;
            PreviewMouseMove += ContinueDrag;
            PreviewMouseLeftButtonUp += EndDrag;
            LostMouseCapture += delegate { FinishDrag(); };
            IsVisibleChanged += delegate { if (!IsVisible) { FinishDrag(); _tooltip?.Hide(); } };
            Closed += delegate
            {
                FinishDrag();
                _tooltip?.Dispose();
                _tooltip = null;
                _source?.RemoveHook(WindowProcedure);
                _source = null;
                _handle = IntPtr.Zero;
            };
        }

        private int Diameter => _settings.FloatingDiameter == 128 || _settings.FloatingDiameter == 512
            ? _settings.FloatingDiameter : 256;

        public void SetVisual(int remaining, bool active, bool alert)
        {
            _active = active;
            _ring.SetVisual(remaining, active, alert);
            RefreshResetTooltip();
        }

        public void SetResetWindow(QuotaWindow window)
        {
            _resetWindow = window;
            RefreshResetTooltip();
        }

        public void RefreshResetTooltip()
        {
            _tooltip?.SetText(_active && _resetWindow != null
                ? _resetWindow.FormatResetCountdown(DateTimeOffset.Now, _settings.IsEnglish)
                : string.Empty);
        }

        public void ApplySettings()
        {
            _ring.Refresh();
            RefreshResetTooltip();
            if (_handle != IntPtr.Zero)
            {
                _physicalLeft = _settings.FloatingLeft ?? _physicalLeft;
                _physicalTop = _settings.FloatingTop ?? _physicalTop;
                KeepVisibleOnDesktop();
                ApplyPhysicalGeometry();
            }
        }

        public DrawingRectangle GetPhysicalBounds()
        {
            if (_handle != IntPtr.Zero && GetWindowRect(_handle, out var rectangle))
                return DrawingRectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
            return new DrawingRectangle(_physicalLeft, _physicalTop, Diameter, Diameter);
        }

        private void OnSourceInitialized(object sender, EventArgs args)
        {
            _handle = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(_handle);
            _source?.AddHook(WindowProcedure);
            var style = GetWindowLongPtr(_handle, -20).ToInt64();
            SetWindowLongPtr(_handle, -20, new IntPtr(style | 0x00000080L | 0x08000000L)); // TOOLWINDOW | NOACTIVATE
            var work = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            _physicalLeft = _settings.FloatingLeft ?? work.Right - Diameter - 24;
            _physicalTop = _settings.FloatingTop ?? work.Bottom - Diameter - 24;
            KeepVisibleOnDesktop();
            ApplyPhysicalGeometry();
            try { _tooltip = new LegacyResetTooltip(_handle, GetPhysicalBounds); }
            catch (Exception exception) { _tooltip = null; RuntimeLog.Exception("floating.tooltip-unavailable", exception); }
            RefreshResetTooltip();
        }

        private void BeginDrag(object sender, MouseButtonEventArgs args)
        {
            if (_handle == IntPtr.Zero || !GetCursorPos(out _dragStartCursor) || !GetWindowRect(_handle, out _dragStartWindow)) return;
            _tooltip?.Hide();
            _dragging = Mouse.Capture(_ring, CaptureMode.Element);
            args.Handled = true;
        }

        private void ContinueDrag(object sender, MouseEventArgs args)
        {
            if (!_dragging) return;
            if (args.LeftButton != MouseButtonState.Pressed) { FinishDrag(); return; }
            if (!GetCursorPos(out var cursor)) return;
            _tooltip?.Hide();
            _physicalLeft = _dragStartWindow.Left + cursor.X - _dragStartCursor.X;
            _physicalTop = _dragStartWindow.Top + cursor.Y - _dragStartCursor.Y;
            SetWindowPos(_handle, IntPtr.Zero, _physicalLeft, _physicalTop, Diameter, Diameter,
                SwpNoActivate | SwpNoZOrder);
            args.Handled = true;
        }

        private void EndDrag(object sender, MouseButtonEventArgs args)
        {
            if (!_dragging) return;
            FinishDrag();
            args.Handled = true;
        }

        private void FinishDrag()
        {
            if (!_dragging) return;
            _dragging = false;
            if (Mouse.Captured == _ring) Mouse.Capture(null);
            if (_handle != IntPtr.Zero && GetWindowRect(_handle, out var rectangle))
            {
                _physicalLeft = rectangle.Left;
                _physicalTop = rectangle.Top;
            }
            _settings.FloatingLeft = _physicalLeft;
            _settings.FloatingTop = _physicalTop;
            PositionChanged?.Invoke();
        }

        private void ApplyPhysicalGeometry()
        {
            if (_handle == IntPtr.Zero || _applyingGeometry) return;
            _applyingGeometry = true;
            try
            {
                // Let WPF keep correct DIPs while the HWND remains exactly 128, 256 or 512 px.
                var dpi = Math.Max(96u, GetDpiForWindow(_handle));
                Width = Height = Diameter * 96d / dpi;
                SetWindowPos(_handle, new IntPtr(-1), _physicalLeft, _physicalTop, Diameter, Diameter, SwpNoActivate);
            }
            finally { _applyingGeometry = false; }
        }

        private void QueuePhysicalGeometry()
        {
            if (_geometryQueued) return;
            _geometryQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                _geometryQueued = false;
                ApplyPhysicalGeometry();
            }));
        }

        private void KeepVisibleOnDesktop()
        {
            var bounds = new DrawingRectangle(_physicalLeft, _physicalTop, Diameter, Diameter);
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                var intersection = DrawingRectangle.Intersect(screen.WorkingArea, bounds);
                if (intersection.Width >= Math.Min(48, Diameter) && intersection.Height >= Math.Min(48, Diameter)) return;
            }
            var work = System.Windows.Forms.Screen.FromRectangle(bounds).WorkingArea;
            _physicalLeft = Math.Clamp(_physicalLeft, work.Left, Math.Max(work.Left, work.Right - Diameter));
            _physicalTop = Math.Clamp(_physicalTop, work.Top, Math.Max(work.Top, work.Bottom - Diameter));
        }

        private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0021) // WM_MOUSEACTIVATE: never steal focus from the user's app.
            {
                handled = true;
                return new IntPtr(3); // MA_NOACTIVATE, still deliver the click.
            }
            if (message == 0x0046 && lParam != IntPtr.Zero) // WM_WINDOWPOSCHANGING
            {
                var position = Marshal.PtrToStructure<WindowPosition>(lParam);
                if ((position.Flags & 0x0001) == 0)
                {
                    position.Width = Diameter;
                    position.Height = Diameter;
                }
                // A monitor's recommended DPI rectangle must not move the drag's physical anchor.
                if (_dragging && (position.Flags & 0x0002) == 0)
                {
                    position.X = _physicalLeft;
                    position.Y = _physicalTop;
                }
                Marshal.StructureToPtr(position, lParam, false);
            }
            if (message == 0x02E0) // WM_DPICHANGED: WPF still receives this to refresh its render scale.
            {
                QueuePhysicalGeometry();
            }
            if (message == 0x007E) // WM_DISPLAYCHANGE: bring a disconnected-monitor position back on screen.
            {
                KeepVisibleOnDesktop();
                QueuePhysicalGeometry();
            }
            return IntPtr.Zero;
        }

        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct WindowPosition
        {
            public IntPtr Window; public IntPtr InsertAfter; public int X; public int Y; public int Width; public int Height; public uint Flags;
        }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong32(IntPtr window, int index, int value);
        private static IntPtr GetWindowLongPtr(IntPtr window, int index) => IntPtr.Size == 8
            ? GetWindowLongPtr64(window, index) : new IntPtr(GetWindowLong32(window, index));
        private static void SetWindowLongPtr(IntPtr window, int index, IntPtr value)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr64(window, index, value);
            else SetWindowLong32(window, index, value.ToInt32());
        }
    }
}
