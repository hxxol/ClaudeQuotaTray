using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace CodexQuotaTray
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // Throw UI exceptions to the explicit boundary rather than showing the generic
            // WinForms Continue dialog and leaving an invisible half-working process.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs eventArgs)
            { RuntimeLog.Exception("app.unhandled", eventArgs.ExceptionObject as Exception); };
            System.Windows.Application wpf = null;
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                if (args.Contains("--smoke-test")) return InteractionSmokeTest.Run();
                RuntimeLog.Start();
                WaitForPreviousVersion(args);
                bool recover = args.Contains("--recover");
                using var mutex = new Mutex(true, "CodexQuotaTray.SingleInstance", out bool createdNew);
                if (!createdNew)
                {
                    if (WakeWindow.Signal(recover)) return 0;
                    RuntimeLog.Event("app.existing-instance-no-acknowledgement");
                    MessageBox.Show(
                        "已有版本正在运行，但没有响应设置请求。请先从旧版菜单退出；若无法操作，请在任务管理器中结束 CodexQuotaTray 后重新打开此 EXE。\n\n" +
                        "Another version is already running and did not respond. Exit it first, or end CodexQuotaTray in Task Manager, then open this EXE again.",
                        "Codex Quota", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 7;
                }
                wpf = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                wpf.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs eventArgs)
                { RuntimeLog.Exception("app.wpf-dispatcher", eventArgs.Exception); };
                using var context = new TrayApplicationContext(args.Contains("--settings"), recover);
                Application.Run(context);
                GC.KeepAlive(mutex);
                return 0;
            }
            catch (Exception exception)
            {
                RuntimeLog.Exception("app.fatal", exception);
                MessageBox.Show(
                    "程序遇到错误，已记录本地诊断信息。请重新打开 EXE；也可使用 --recover 参数恢复设置和悬浮窗。\n\n" +
                    "Codex Quota encountered an error. Local diagnostics: " + RuntimeLog.LogPath,
                    "Codex Quota", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            finally
            {
                try { wpf?.Shutdown(); }
                catch (Exception exception) { RuntimeLog.Exception("app.shutdown", exception); }
            }
        }

        private static void WaitForPreviousVersion(string[] args)
        {
            if (args.Length != 2 || args[0] != "--wait-for-pid" || !int.TryParse(args[1], out int id) || id <= 0) return;
            try { using var process = Process.GetProcessById(id); process.WaitForExit(30000); } catch { }
        }
    }

    internal sealed class WakeWindow : NativeWindow, IDisposable
    {
        private const string WindowCaption = "CodexQuotaTray.MessageWindow";
        private static readonly uint ControlMessage = RegisterWindowMessage("CodexQuotaTray.Control.v3.0.1");
        private const int Acknowledgement = 0x435154;
        private readonly Action<bool> _schedule;
        internal WakeWindow(Action<bool> schedule)
        {
            _schedule = schedule;
            CreateHandle(new CreateParams { Caption = WindowCaption, ExStyle = 0x80 });
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == ControlMessage)
            {
                _schedule(message.WParam != IntPtr.Zero);
                message.Result = new IntPtr(Acknowledgement);
                return;
            }
            base.WndProc(ref message);
        }
        internal static bool Signal(bool recover)
        {
            var window = FindWindow(null, WindowCaption);
            if (window == IntPtr.Zero) return false;
            GetWindowThreadProcessId(window, out uint processId);
            AllowSetForegroundWindow(processId); // The user explicitly launched this instance.
            return SendMessageTimeout(window, ControlMessage, new IntPtr(recover ? 1 : 0), IntPtr.Zero,
                0x0002 | 0x0020, 2000, out var result) != IntPtr.Zero && result.ToUInt64() == Acknowledgement;
        }
        public void Dispose() { DestroyHandle(); }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string caption);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wparam, IntPtr lparam,
            uint flags, uint timeout, out UIntPtr result);
    }
}
