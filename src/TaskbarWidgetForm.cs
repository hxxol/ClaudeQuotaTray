using System;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexQuotaTray
{
    internal sealed class TaskbarWidgetForm : Form
    {


        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr window, int index);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct NotifyHeader { public IntPtr Window; public UIntPtr Id; public int Code; }



        private int _remaining;
        private bool _active;
        private bool _alert;
        private readonly ToolTip _tooltip = new ToolTip { ShowAlways = true, OwnerDraw = true, UseAnimation = false, UseFading = false };
        private readonly Font _tooltipFont = new Font(SystemFonts.MessageBoxFont.FontFamily, 10f, FontStyle.Regular, GraphicsUnit.Point);
        private QuotaWindow _resetWindow;
        private float _dpiScale = 1f;
        private AppSettings _settings = new AppSettings();
        private ThemePalette _palette;
        private TaskbarLayoutMonitor _layoutMonitor;
        private TaskbarLayoutSnapshot _layout = TaskbarLayoutSnapshot.Empty;
        private readonly System.Windows.Forms.Timer _positionTimer = new System.Windows.Forms.Timer { Interval = 100 };
        private bool _requestedVisible;
        private MouseButtons _pressedButton;
        private Point _pressedAt;
        private bool _movedWhilePressed;
        private bool _processingMouseUp;
        private readonly bool _observeTaskbar;
        private bool _renderFailed;

        internal bool OverlayAvailable
        {
            get
            {
                if (!Visible || _renderFailed || !IsHandleCreated) return false;
                if (SuppressZOrderRefresh) return true; // Our popup legitimately owns the pointer.
                var anchor = GetAnchorBounds();
                return WindowFromPoint(new Point(anchor.Left + anchor.Width / 2, anchor.Top + anchor.Height / 2)) == Handle;
            }
        }
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);

        public event MouseEventHandler RingClick;

        public TaskbarWidgetForm(bool observeTaskbar = true)
        {
            _observeTaskbar = observeTaskbar;
            AutoScaleMode = AutoScaleMode.None;
            _palette = ThemePalette.FromSettings(_settings);
            ClientSize = new Size(56, 56);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = _palette.Background;
            Cursor = Cursors.Hand;
            _positionTimer.Tick += delegate { EnsureOnTop(); };
            if (_observeTaskbar) _positionTimer.Start();
            _tooltip.Popup += delegate(object sender, PopupEventArgs e)
            {
                using (var graphics = CreateGraphics())
                {
                    var textSize = TooltipText.Measure(graphics, _tooltip.GetToolTip(this), _tooltipFont);
                    e.ToolTipSize = new Size(textSize.Width + (int)(12 * _dpiScale), textSize.Height + (int)(8 * _dpiScale));
                }
            };
            _tooltip.Draw += delegate(object sender, DrawToolTipEventArgs e)
            {
                using (var background = new SolidBrush(Color.FromArgb(35, 35, 35))) e.Graphics.FillRectangle(background, e.Bounds);
                TooltipText.Draw(e.Graphics, e.ToolTipText, _tooltipFont, e.Bounds, Color.White);
            };
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WsExToolWindow = 0x00000080;
                const int WsExNoActivate = 0x08000000;
                var parameters = base.CreateParams;
                parameters.ExStyle |= WsExToolWindow | WsExNoActivate | 0x00080000; // Per-pixel alpha.
                return parameters;
            }
        }

        public void ApplyResult(QuotaReadResult result)
        {
            var snapshot = result == null ? null : result.Snapshot;
            SetVisual(snapshot == null ? 0 : (snapshot.IsReserveActive ? snapshot.ReserveWindow.RemainingPercent : snapshot.MostConstrainedRemaining),
                result != null && result.IsCodexRunning && snapshot != null, false);
        }

        public void SetVisual(int remaining, bool active, bool alert)
        {
            _remaining = remaining;
            _active = active;
            _alert = alert;
            UpdateImage();
        }

        internal Func<string> ResetTextProvider { get; set; }

        public void SetResetWindow(QuotaWindow window)
        {
            _resetWindow = window;
            RefreshResetTooltip();
        }

        public void RefreshResetTooltip()
        {
            var text = _resetWindow == null ? string.Empty : (ResetTextProvider != null ? ResetTextProvider() : _resetWindow.FormatResetCountdown(DateTimeOffset.Now, _settings.IsEnglish));
            if (_tooltip.GetToolTip(this) == text) return;
            if (text.Length == 0) _tooltip.Hide(this);
            _tooltip.SetToolTip(this, text);
        }

        public void ApplySettings(AppSettings settings)
        {
            _settings = settings ?? new AppSettings();
            RefreshResetTooltip();
            _palette = ThemePalette.FromSettings(_settings);
            ApplyScaledSize();
            Reposition();
            UpdateImage();
        }

        public void SetVisible(bool visible)
        {
            _requestedVisible = visible;
            EnsureOnTop();
        }

        public void EnsureOnTop()
        {
            if (IsDisposed || !IsHandleCreated || SuppressZOrderRefresh || AnyMouseButtonDown() || _pressedButton != MouseButtons.None) return;
            Rectangle placement;
            if (_renderFailed || !_requestedVisible || !TaskbarPlacement.TryPlace(_layout, out placement))
            {
                if (Visible) Hide();
                return;
            }
            _dpiScale = _layout.Dpi / 96f;
            if (Size != placement.Size) Size = placement.Size;
            if (!Visible) Show();
            NativeRect current;
            bool changed = !GetWindowRect(Handle, out current) || current.Left != placement.Left ||
                current.Top != placement.Top || current.Right - current.Left != placement.Width ||
                current.Bottom - current.Top != placement.Height;
            // WS_EX_TOPMOST only says this HWND belongs to the topmost band. Explorer can
            // put another topmost HWND above it, so reassert our own order without activation.
            // Only this app's HWND is ever positioned. Never move shell icons or the taskbar itself.
            SetWindowPos(Handle, new IntPtr(-1), placement.Left, placement.Top, placement.Width, placement.Height,
                0x0010 | (changed ? 0u : 0x0003u));
            if (changed) UpdateImage();
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal bool SuppressZOrderRefresh { get; set; }

        internal void ShowForSmoke(Rectangle bounds)
        {
            if (_observeTaskbar) throw new InvalidOperationException("Synthetic placement requires an isolated widget.");
            Bounds = bounds;
            Show();
            SetWindowPos(Handle, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010);
            UpdateImage();
        }

        public void RefreshDynamicAnchor() { if (_layoutMonitor != null) _layoutMonitor.Invalidate(); }

        private static bool AnyMouseButtonDown()
        {
            return (GetAsyncKeyState(1) & 0x8000) != 0 || (GetAsyncKeyState(2) & 0x8000) != 0 ||
                (GetAsyncKeyState(4) & 0x8000) != 0 || (GetAsyncKeyState(5) & 0x8000) != 0 ||
                (GetAsyncKeyState(6) & 0x8000) != 0;
        }
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

        public Rectangle GetAnchorBounds()
        {
            NativeRect bounds;
            if (IsHandleCreated && GetWindowRect(Handle, out bounds))
            {
                return Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            }

            return new Rectangle(Left, Top, Width, Height);
        }

        public void Reposition(bool forceZOrder = false) { EnsureOnTop(); }

        internal static int CalculateLeadingPlacement(int taskbarStart, int taskbarEnd, int widgetExtent, int leadingEdge, int gap)
        { return Math.Max(taskbarStart + 4, Math.Min(leadingEdge - widgetExtent - gap, taskbarEnd - widgetExtent - 4)); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _dpiScale = GraphicsExtensions.GetDpiScale(Handle);
            if (_observeTaskbar) _layoutMonitor = new TaskbarLayoutMonitor(snapshot => { _layout = snapshot; EnsureOnTop(); });
            UpdateImage();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (_layoutMonitor != null) { _layoutMonitor.Dispose(); _layoutMonitor = null; }
            base.OnHandleDestroyed(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right) { base.OnMouseDown(e); return; }
            _tooltip.Hide(this);
            _pressedButton = e.Button;
            _pressedAt = PointToScreen(e.Location);
            _movedWhilePressed = false;
            Capture = true;
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_pressedButton != MouseButtons.None)
            {
                var p = PointToScreen(e.Location);
                if (Math.Abs(p.X - _pressedAt.X) >= SystemInformation.DragSize.Width / 2 ||
                    Math.Abs(p.Y - _pressedAt.Y) >= SystemInformation.DragSize.Height / 2) _movedWhilePressed = true;
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool click = e.Button == _pressedButton && !_movedWhilePressed && ClientRectangle.Contains(e.Location);
            _pressedButton = MouseButtons.None;
            Capture = false;
            _tooltip.Hide(this);
            base.OnMouseUp(e);
            // Return to the WinForms message loop before opening a popup. The framework
            // finishes its capture/click cleanup after OnMouseUp has returned.
            if (click && RingClick != null)
                BeginInvoke(new Action(() => { if (!IsDisposed) RingClick?.Invoke(this, e); }));
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            if (!Capture && !_processingMouseUp) { _pressedButton = MouseButtons.None; _movedWhilePressed = false; }
            base.OnMouseCaptureChanged(e);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0084) // WM_NCHITTEST: every nonzero-alpha pixel is a client hit.
            {
                message.Result = new IntPtr(1); // HTCLIENT; never a resize/drag/transparent region.
                return;
            }
            if (message.Msg == 0x001F) // WM_CANCELMODE: do not retain a stale pressed state.
            {
                _pressedButton = MouseButtons.None;
                _movedWhilePressed = false;
                Capture = false;
            }
            // WinForms releases native capture before raising MouseUp. Preserve the down state
            // through that normal release, but cancel it for any genuinely lost capture.
            if (message.Msg == 0x0202 || message.Msg == 0x0205)
            {
                _processingMouseUp = true;
                try { base.WndProc(ref message); }
                finally { _processingMouseUp = false; }
                return;
            }
            // TTN_SHOW: position the native tooltip above the whole icon, rather than beside the cursor.
            if (message.Msg == 0x004E && message.LParam != IntPtr.Zero)
            {
                var notification = (NotifyHeader)Marshal.PtrToStructure(message.LParam, typeof(NotifyHeader));
                if (notification.Code == -521)
                {
                    base.WndProc(ref message);
                    NativeRect tip;
                    if (GetWindowRect(notification.Window, out tip))
                    {
                        var anchor = GetAnchorBounds();
                        var work = Screen.FromRectangle(anchor).WorkingArea;
                        var width = tip.Right - tip.Left;
                        var height = tip.Bottom - tip.Top;
                        var x = Math.Max(work.Left, Math.Min(anchor.Left + (anchor.Width - width) / 2, work.Right - width));
                        var y = anchor.Top - height - (int)(8 * _dpiScale);
                        if (y < work.Top) y = anchor.Bottom + (int)(8 * _dpiScale);
                        AcrylicEffect.ConfigureRoundedCorners(notification.Window);
                        SetWindowPos(notification.Window, new IntPtr(-1), x, y, width, height, 0x0010);
                        message.Result = new IntPtr(1);
                    }
                    return;
                }
            }
            if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; } // Do not eat the first click.
            const int DpiChanged = 0x02E0;
            if (message.Msg == DpiChanged)
            {
                var newDpi = message.WParam.ToInt64() & 0xFFFF;
                base.WndProc(ref message);
                _dpiScale = Math.Max(1f, newDpi / 96f);
                if (_layoutMonitor != null) _layoutMonitor.Invalidate();
                BeginInvoke(new Action(delegate { Reposition(); }));
                Invalidate();
                return;
            }
            if (message.Msg == 0x007E || message.Msg == 0x001A) // display/work-area/taskbar settings changed
            {
                if (_layoutMonitor != null) _layoutMonitor.Invalidate();
            }
            base.WndProc(ref message);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BlendFunction { public byte Operation, Flags, Alpha, Format; }
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screen, ref Point destination,
            ref Size size, IntPtr source, ref Point origin, int key, ref BlendFunction blend, int flags);

        private void UpdateImage()
        {
            if (!IsHandleCreated || IsDisposed || Width <= 0 || Height <= 0) return;
            try
            {
                UpdateLayeredImage();
                _renderFailed = false;
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception || exception is ExternalException)
            {
                if (!_renderFailed) RuntimeLog.Exception("taskbar-render", exception);
                _renderFailed = true;
                if (Visible) Hide(); // Keep recovery UI alive if the compositor rejects an update.
            }
        }

        private void UpdateLayeredImage()
        {
            using (var bitmap = RingIconFactory.Render(_remaining, _active, _palette, _alert, _settings, Width, true))
            {
                var screen = IntPtr.Zero;
                var memory = IntPtr.Zero;
                var hBitmap = IntPtr.Zero;
                var previous = IntPtr.Zero;
                try
                {
                    screen = GetDC(IntPtr.Zero);
                    if (screen == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    memory = CreateCompatibleDC(screen);
                    if (memory == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
                    previous = SelectObject(memory, hBitmap);
                    if (previous == IntPtr.Zero || previous == new IntPtr(-1))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    var destination = Location;
                    var size = Size;
                    var origin = Point.Empty;
                    var blend = new BlendFunction { Alpha = 255, Format = 1 };
                    if (!UpdateLayeredWindow(Handle, screen, ref destination, ref size, memory, ref origin, 0, ref blend, 2))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
                finally
                {
                    if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(memory, previous);
                    if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                    if (memory != IntPtr.Zero) DeleteDC(memory);
                    if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _positionTimer.Stop();
                _positionTimer.Dispose();
                if (_layoutMonitor != null) { _layoutMonitor.Dispose(); _layoutMonitor = null; }
                _tooltip.Dispose();
                _tooltipFont.Dispose();
            }
            base.Dispose(disposing);
        }

        internal static string FormatResetHours(DateTimeOffset? resetsAt, DateTimeOffset now)
        {
            if (!resetsAt.HasValue) return "--H";
            var hours = Math.Max(0d, (resetsAt.Value - now).TotalHours);
            return hours.ToString("0.#", CultureInfo.InvariantCulture) + "H";
        }

        private void ApplyScaledSize() { EnsureOnTop(); }
    }
}
