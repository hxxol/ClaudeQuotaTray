using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Windows.Interop;

namespace CodexQuotaTray
{
    /// <summary>
    /// Synthetic Windows UI checks using the same WinForms message pump as production.
    /// Only this process's windows receive posted messages. No SendInput, cursor movement,
    /// saved settings, sessions, authentication, network requests, or quota history are used.
    /// Requires an unlocked interactive Windows desktop; it is not a headless render test.
    /// </summary>
    internal static class InteractionSmokeTest
    {
        internal static int Run()
        {
            SmokeContext context = null;
            System.Windows.Application wpf = null;
            bool ownsWpf = false;
            System.Threading.ThreadExceptionEventHandler formsException = null;
            System.Windows.Threading.DispatcherUnhandledExceptionEventHandler wpfException = null;
            try
            {
                Require(OperatingSystem.IsWindows(), "The interaction smoke test requires Windows.");
                Require(System.Threading.Thread.CurrentThread.GetApartmentState() == System.Threading.ApartmentState.STA,
                    "The interaction smoke test requires an STA entry point.");
                Require(!Application.MessageLoop, "Run the interaction smoke test before starting another message loop.");
                wpf = System.Windows.Application.Current;
                if (wpf == null)
                {
                    wpf = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                    ownsWpf = true;
                }
                context = new SmokeContext();
                formsException = (sender, args) => context.Fail(args.Exception);
                wpfException = (sender, args) => { args.Handled = true; context.Fail(args.Exception); };
                Application.ThreadException += formsException;
                wpf.DispatcherUnhandledException += wpfException;
                context.Start();
                // A DispatcherFrame alone cannot exercise the WinForms message filters,
                // BeginInvoke dispatch and ContextMenuStrip interaction used by the app.
                Application.Run(context);
                Require(context.Finished, "The Windows message loop ended before all smoke stages completed.");
                return context.Error == null ? 0 : 1;
            }
            catch (Exception exception)
            {
                ReportFailure("startup or message loop", exception);
                return 1;
            }
            finally
            {
                if (formsException != null) Application.ThreadException -= formsException;
                if (wpf != null && wpfException != null) wpf.DispatcherUnhandledException -= wpfException;
                context?.Dispose();
                if (ownsWpf) wpf.Shutdown();
            }
        }

        private sealed class SmokeContext : ApplicationContext
        {
            private readonly Timer _timer = new Timer { Interval = 100 };
            private readonly Stopwatch _elapsed = new Stopwatch();
            private readonly Queue<(string Name, Action Check)> _stages = new Queue<(string, Action)>();
            private readonly AppSettings _settings = new AppSettings();
            private TaskbarWidgetForm _widget;
            private NativeDispatchProbe _probe;
            private ContextMenuStrip _menu;
            private ToolTip _tooltip;
            private SettingsWindow _settingsWindow;
            private FloatingQuotaWindow _floating;
            private IntPtr _settingsHandle;
            private IntPtr _floatingHandle;
            private int _clicks;
            private int _menuOpened;
            private MouseButtons _lastButton;
            private string _stage = "initialization";
            private string _tooltipText;
            private bool _stopping;
            internal bool Finished { get; private set; }
            internal Exception Error { get; private set; }

            internal SmokeContext()
            {
                _timer.Tick += Tick;
                Add("in-memory parser and render", VerifyInMemoryInputs);
                Add("create native taskbar widget", CreateWidget);
                Add("native hit rectangle and tooltip registration", () =>
                {
                    VerifyNativeHitRectangle();
                    var field = typeof(TaskbarWidgetForm).GetField("_tooltip", BindingFlags.Instance | BindingFlags.NonPublic);
                    _tooltip = field?.GetValue(_widget) as ToolTip;
                    Require(_tooltip != null, "The taskbar widget's ToolTip component is missing.");
                    _tooltipText = _tooltip.GetToolTip(_widget);
                    Require(!string.IsNullOrWhiteSpace(_tooltipText), "The taskbar reset tooltip has no registered text.");
                    // Explicit display checks tooltip registration/rendering without moving
                    // the user's cursor. Physical hover timing is intentionally not claimed.
                    _tooltip.Show(_tooltipText, _widget, _widget.Width / 2, 0, 5000);
                });
                Add("visible native tooltip and inactive hit rectangle", () =>
                {
                    VerifyTooltip(_widget.Handle, _tooltipText, true);
                    _tooltip.Hide(_widget);
                    _widget.SetResetWindow(null);
                    Require(_tooltip.GetToolTip(_widget).Length == 0, "Clearing reset information left stale tooltip text.");
                    _widget.SetVisual(0, false, false);
                    VerifyNativeHitRectangle();
                    _widget.SetVisual(73, true, false);
                });
                Add("post left click at background corner", () => PostClick(MouseButtons.Left, new Point(1, 1)));
                Add("left click opens one queued menu", () => ExpectMenuAndClose(1, MouseButtons.Left));
                Add("post left click at ring gap", () => PostClick(MouseButtons.Left, new Point(_widget.Width / 2, _widget.Height - 2)));
                Add("gap click opens one queued menu", () => ExpectMenuAndClose(2, MouseButtons.Left));
                Add("post right click at center", () => PostClick(MouseButtons.Right, Center));
                Add("right click opens one queued menu", () => ExpectMenuAndClose(3, MouseButtons.Right));
                Add("post repeated right click", () => PostClick(MouseButtons.Right, new Point(_widget.Width - 2, 1)));
                Add("repeated right click opens once", () => ExpectMenuAndClose(4, MouseButtons.Right));
                Add("post unmatched button releases", () =>
                {
                    Post(LeftUp, 0, Center);
                    Post(RightUp, 0, Center);
                });
                Add("unmatched releases do not click; cancel pending press", () =>
                {
                    ExpectNoClick(4);
                    Post(LeftDown, 1, Center);
                    Post(0x001F, 0, Point.Empty); // WM_CANCELMODE, restricted to our own HWND.
                    Post(LeftUp, 0, Center);
                });
                Add("cancelled press does not click; post drag", () =>
                {
                    ExpectNoClick(4);
                    Post(LeftDown, 1, Center);
                    var distance = Math.Max(SystemInformation.DragSize.Width, SystemInformation.DragSize.Height) + 8;
                    Post(MouseMove, 1, new Point(Center.X + distance, Center.Y + distance));
                    Post(MouseMove, 1, Center); // Returning inside must not turn a drag into a click.
                    Post(LeftUp, 0, Center);
                });
                Add("drag does not click; release outside", () =>
                {
                    ExpectNoClick(4);
                    Post(LeftDown, 1, Center);
                    Post(LeftUp, 0, new Point(-4, -4));
                });
                Add("outside release does not click; recover normal input", () =>
                {
                    ExpectNoClick(4);
                    PostClick(MouseButtons.Left, Center);
                });
                Add("input recovers after cancelled sequences", () =>
                {
                    ExpectMenuAndClose(5, MouseButtons.Left);
                    Require(_probe.MouseMoves >= 7 && _probe.ButtonDowns == 8 && _probe.ButtonUps == 10,
                        "The expected native mouse messages did not all traverse the widget HWND.");
                    _widget.Hide();
                    _settingsWindow = new SettingsWindow(_settings);
                    System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(_settingsWindow);
                    _settingsWindow.Show();
                });
                Add("WPF settings show and native dismissal", () =>
                {
                    _settingsHandle = new WindowInteropHelper(_settingsWindow).Handle;
                    RequireVisible(_settingsHandle, "settings");
                    PostNative(_settingsHandle, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE must hide, not destroy.
                });
                Add("settings dismissal retains reusable HWND", () =>
                {
                    Require(!_settingsWindow.IsVisible && IsWindow(_settingsHandle), "Closing settings did not preserve its hidden window.");
                    _settingsWindow.Show();
                });
                Add("settings reopen and floating 128 px", () =>
                {
                    RequireVisible(_settingsHandle, "reopened settings");
                    Require(new WindowInteropHelper(_settingsWindow).Handle == _settingsHandle, "Settings reopened with an unexpected HWND.");
                    _settingsWindow.Hide();
                    _settings.FloatingDiameter = 128;
                    _floating = new FloatingQuotaWindow(_settings);
                    _floating.SetVisual(100, true, false);
                    _floating.SetResetWindow(new QuotaWindow { ResetsAt = DateTimeOffset.Now.AddHours(1) });
                    _floating.Show();
                });
                Add("floating 128 px native geometry", () => { VerifyFloatingSize(128); SetFloatingSize(256); });
                Add("floating 256 px native geometry", () => { VerifyFloatingSize(256); SetFloatingSize(512); });
                Add("floating 512 px native geometry and hide", () =>
                {
                    VerifyFloatingSize(512);
                    _floatingHandle = new WindowInteropHelper(_floating).Handle;
                    _floating.Hide();
                });
                Add("floating hide and reopen", () =>
                {
                    Require(!_floating.IsVisible && !IsWindowVisible(_floatingHandle), "The floating window did not hide.");
                    _floating.Show();
                });
                Add("floating reopen, clear tooltip and close", () =>
                {
                    VerifyFloatingSize(512);
                    Require(new WindowInteropHelper(_floating).Handle == _floatingHandle, "The floating window did not reuse its HWND.");
                    _floating.SetResetWindow(null);
                    _floating.RefreshResetTooltip();
                    _floating.Close();
                    _floating = null;
                    _settingsWindow.ClosePermanently();
                    _settingsWindow = null;
                });
                Add("closed WPF native windows are destroyed", () =>
                {
                    Require(!IsWindow(_floatingHandle), "The floating HWND survived Close.");
                    Require(!IsWindow(_settingsHandle), "The settings HWND survived ClosePermanently.");
                });
            }

            internal void Start() { _elapsed.Start(); _timer.Start(); }
            private Point Center => new Point(_widget.Width / 2, _widget.Height / 2);
            private void Add(string name, Action action) => _stages.Enqueue((name, action));

            private void Tick(object sender, EventArgs args)
            {
                // Stop during a stage to guard against nested UI callbacks. Every next
                // stage is reached only by the real outer Application.Run message pump.
                _timer.Stop();
                if (_stopping) return;
                try
                {
                    Require(Application.MessageLoop, "The smoke stages are not running inside the WinForms message loop.");
                    Require(_elapsed.Elapsed < TimeSpan.FromSeconds(30), "The interaction smoke test exceeded its 30-second deadline.");
                    if (_stages.Count == 0) { Finish(); return; }
                    var next = _stages.Dequeue();
                    _stage = next.Name;
                    next.Check();
                    if (!_stopping) _timer.Start();
                }
                catch (Exception exception) { Fail(exception); }
            }

            private void VerifyInMemoryInputs()
            {
                Require(SessionQuotaReader.TryParseTokenCountLine(
                    "{\"timestamp\":\"2026-10-15T00:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":{\"primary\":{\"used_percent\":25,\"window_minutes\":300}}}}",
                    out var snapshot) && snapshot.MostConstrainedRemaining == 75, "The synthetic quota parser fixture failed.");
                using var bitmap = RingIconFactory.Render(100, true, ThemePalette.FromSettings(_settings), false, _settings, 56, true);
                Require(bitmap.Width == 56 && bitmap.Height == 56, "The synthetic taskbar render has the wrong size.");
            }

            private void CreateWidget()
            {
                var work = Screen.PrimaryScreen.WorkingArea;
                Require(work.Width >= 128 && work.Height >= 128, "No usable interactive desktop work area is available.");
                _widget = new TaskbarWidgetForm(false);
                _menu = new ContextMenuStrip();
                _menu.Items.Add("Synthetic smoke test");
                _menu.Opened += (sender, args) => _menuOpened++;
                _widget.RingClick += (sender, args) =>
                {
                    try
                    {
                        Require(Application.MessageLoop, "RingClick ran outside the WinForms message loop.");
                        Require(!_probe.DispatchingMouseUp, "RingClick ran synchronously inside native MouseUp instead of the queued callback.");
                        Require(!_widget.Capture, "RingClick ran before native mouse capture was released.");
                        _clicks++;
                        _lastButton = args.Button;
                        _menu.Show(_widget, new Point(0, _widget.Height));
                    }
                    catch (Exception exception) { Fail(exception); }
                };
                _widget.ShowForSmoke(new Rectangle(work.Left + 32, work.Top + 64, 56, 56));
                _widget.SetVisual(73, true, false);
                _widget.SetResetWindow(new QuotaWindow { ResetsAt = DateTimeOffset.Now.AddHours(2).AddSeconds(15) });
                _probe = new NativeDispatchProbe(_widget.Handle);
            }

            private void VerifyNativeHitRectangle()
            {
                RequireVisible(_widget.Handle, "taskbar widget");
                Require(_widget.Enabled && IsWindowEnabled(_widget.Handle), "The taskbar widget is disabled.");
                Require((GetWindowLong(_widget.Handle, -20) & 0x00000020) == 0, "WS_EX_TRANSPARENT makes the widget click-through.");
                Require(_widget.ClientSize == new Size(56, 56), "The smoke widget's native target area changed size.");
                int[] positions = { 0, 1, 14, 28, 42, 54, 55 };
                foreach (int x in positions)
                foreach (int y in positions)
                {
                    var screen = _widget.PointToScreen(new Point(x, y));
                    Require(WindowFromPoint(screen) == _widget.Handle,
                        "WindowFromPoint skipped the taskbar widget at client point " + x + "," + y + ".");
                    Require(SendMessage(_widget.Handle, 0x0084, IntPtr.Zero, Pack(screen)) == new IntPtr(1),
                        "WM_NCHITTEST was not HTCLIENT at " + x + "," + y + ".");
                }
                Require(SendMessage(_widget.Handle, 0x0021, IntPtr.Zero, new IntPtr((LeftDown << 16) | 1)) == new IntPtr(3),
                    "WM_MOUSEACTIVATE must return MA_NOACTIVATE without eating the click.");
            }

            private void PostClick(MouseButtons button, Point point)
            {
                Require(!_menu.Visible, "The previous context menu was not dismissed.");
                Post(MouseMove, 0, point);
                Post(button == MouseButtons.Left ? LeftDown : RightDown, button == MouseButtons.Left ? 1 : 2, point);
                Post(button == MouseButtons.Left ? LeftUp : RightUp, 0, point);
            }

            private void Post(uint message, int keys, Point point) => PostNative(_widget.Handle, message, new IntPtr(keys), Pack(point));

            private void ExpectMenuAndClose(int expected, MouseButtons button)
            {
                Require(_clicks == expected && _menuOpened == expected, "Expected exactly " + expected + " click/menu callbacks, got " + _clicks + "/" + _menuOpened + ".");
                Require(_lastButton == button, "RingClick reported the wrong mouse button.");
                Require(_menu.Visible && _menu.IsHandleCreated && IsWindowVisible(_menu.Handle), "The queued callback did not open a native ContextMenuStrip.");
                Require(!_widget.Capture, "The widget retained capture after opening its context menu.");
                _menu.Close(ToolStripDropDownCloseReason.CloseCalled);
                Require(!_menu.Visible, "The context menu did not dismiss.");
            }

            private void ExpectNoClick(int expected)
            {
                Require(_clicks == expected && _menuOpened == expected && !_menu.Visible,
                    "An unmatched, cancelled, dragged, or outside release produced a spurious click/menu.");
                Require(!_widget.Capture, "A cancelled mouse sequence retained capture.");
            }

            private void SetFloatingSize(int diameter) { _settings.FloatingDiameter = diameter; _floating.ApplySettings(); }
            private void VerifyFloatingSize(int diameter)
            {
                var handle = new WindowInteropHelper(_floating).Handle;
                RequireVisible(handle, "floating window");
                var bounds = _floating.GetPhysicalBounds();
                Require(bounds.Width == diameter && bounds.Height == diameter,
                    "Floating geometry must be exactly " + diameter + " physical px; got " + bounds.Width + "x" + bounds.Height + ".");
            }

            internal void Fail(Exception exception)
            {
                if (_stopping) return;
                Error = exception;
                ReportFailure(_stage, exception);
                Finish();
            }

            private void Finish()
            {
                if (_stopping) return;
                _stopping = true;
                _timer.Stop();
                Finished = true;
                ExitThread();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _timer.Stop();
                    _timer.Dispose();
                    _probe?.ReleaseHandle();
                    _menu?.Dispose();
                    _widget?.Dispose();
                    _floating?.Close();
                    _settingsWindow?.ClosePermanently();
                }
                base.Dispose(disposing);
            }
        }

        private sealed class NativeDispatchProbe : NativeWindow
        {
            internal bool DispatchingMouseUp { get; private set; }
            internal int MouseMoves { get; private set; }
            internal int ButtonDowns { get; private set; }
            internal int ButtonUps { get; private set; }
            internal NativeDispatchProbe(IntPtr handle) { AssignHandle(handle); }
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == MouseMove) MouseMoves++;
                if (message.Msg == LeftDown || message.Msg == RightDown) ButtonDowns++;
                bool mouseUp = message.Msg == LeftUp || message.Msg == RightUp;
                if (mouseUp) { ButtonUps++; DispatchingMouseUp = true; }
                try { base.WndProc(ref message); }
                finally { if (mouseUp) DispatchingMouseUp = false; }
            }
        }

        private static void VerifyTooltip(IntPtr owner, string expected, bool visible)
        {
            bool found = false;
            EnumThreadWindows(GetCurrentThreadId(), (window, parameter) =>
            {
                var className = new StringBuilder(128);
                GetClassName(window, className, className.Capacity);
                if (className.ToString() != "tooltips_class32" || (visible && !IsWindowVisible(window))) return true;
                var buffer = Marshal.AllocHGlobal(2048);
                try
                {
                    Marshal.WriteInt16(buffer, 0);
                    var info = new ToolInfo
                    {
                        Size = (uint)Marshal.SizeOf<ToolInfo>(), Owner = owner,
                        Id = (UIntPtr)(ulong)owner.ToInt64(), Text = buffer
                    };
                    if (SendTool(window, 0x0435, IntPtr.Zero, ref info) == IntPtr.Zero) return true; // TTM_GETTOOLINFOW
                    info.Text = buffer;
                    SendTool(window, 0x0438, new IntPtr(1024), ref info); // TTM_GETTEXTW
                    found = Marshal.PtrToStringUni(buffer) == expected;
                    return !found;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }, IntPtr.Zero);
            Require(found, "The visible native tooltip does not contain the widget's registered reset text.");
        }

        private static void RequireVisible(IntPtr window, string name) => Require(window != IntPtr.Zero && IsWindow(window) && IsWindowVisible(window), "The " + name + " has no visible native HWND.");
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static void ReportFailure(string stage, Exception exception)
        {
            Trace.WriteLine("Interaction smoke test [" + stage + "]: " + exception);
            RuntimeLog.Exception("Interaction smoke test: " + stage, exception);
        }
        private static IntPtr Pack(Point point) => new IntPtr(unchecked((int)((uint)(ushort)point.X | ((uint)(ushort)point.Y << 16))));
        private static void PostNative(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (!PostMessage(window, message, wParam, lParam)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Posting a synthetic message to the smoke-test HWND failed.");
        }

        private const uint MouseMove = 0x0200, LeftDown = 0x0201, LeftUp = 0x0202, RightDown = 0x0204, RightUp = 0x0205;
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct ToolInfo
        {
            public uint Size, Flags;
            public IntPtr Owner;
            public UIntPtr Id;
            public NativeRect Bounds;
            public IntPtr Instance, Text, Param, Reserved;
        }
        private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, EnumWindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int length);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendTool(IntPtr window, uint message, IntPtr wParam, ref ToolInfo info);
        [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    }
}
