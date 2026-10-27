using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

namespace CodexQuotaTray
{
    // Only reads Explorer. No SetParent, injection, remote memory, or moving shell windows.
    internal sealed class TaskbarLayoutMonitor : IDisposable
    {
        private sealed class CompletedProbe
        {
            internal readonly int Generation;
            internal readonly TaskbarLayoutSnapshot Snapshot;
            internal CompletedProbe(int generation, TaskbarLayoutSnapshot snapshot) { Generation = generation; Snapshot = snapshot; }
        }
        private CompletedProbe _completed;
        private readonly Action<TaskbarLayoutSnapshot> _publish;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly object _probeGate = new object();
        private bool _probeDisposed;
        private readonly AutoResetEvent _probe = new AutoResetEvent(false);
        private readonly Thread _worker;
        private readonly WinEventDelegate _eventDelegate;
        private readonly List<IntPtr> _hooks = new List<IntPtr>();
        private volatile bool _disposed;
        private volatile int _generation;
        private IntPtr _lastHandle;
        private Rectangle _lastBounds;
        private uint _lastProcess;
        private uint _lastDpi;
        private int _ticks;
        private int _dirty = 1;
        private long _lastValidProbe;
        private bool _publishedValid;
        private const int MaximumSnapshotAgeMilliseconds = 2500;

        public TaskbarLayoutMonitor(Action<TaskbarLayoutSnapshot> publish)
        {
            _publish = publish;
            _eventDelegate = OnWinEvent;
            // Out-of-context observers. Callbacks only signal a worker; UIA never runs on the UI thread.
            AddHook(0x8000, 0x8003); // object create/destroy/show/hide
            AddHook(0x800B, 0x800B); // location change
            _worker = new Thread(ProbeLoop) { IsBackground = true, Name = "Taskbar layout observer" };
            _worker.SetApartmentState(ApartmentState.MTA);
            _worker.Start();
            _timer = new System.Windows.Forms.Timer { Interval = 200 };
            _timer.Tick += Tick;
            _timer.Start();
            Tick(null, EventArgs.Empty);
        }

        private void AddHook(uint first, uint last)
        {
            var hook = SetWinEventHook(first, last, IntPtr.Zero, _eventDelegate, 0, 0, 0);
            if (hook != IntPtr.Zero) _hooks.Add(hook);
        }

        private void OnWinEvent(IntPtr hook, uint kind, IntPtr window, int objectId, int childId, uint thread, uint time)
        {
            var taskbar = _lastHandle;
            if (window != IntPtr.Zero && taskbar != IntPtr.Zero &&
                (window == taskbar || IsChild(taskbar, window))) Interlocked.Exchange(ref _dirty, 1);
        }

        internal void Invalidate()
        {
            if (_disposed) return;
            _publishedValid = false;
            Interlocked.Increment(ref _generation);
            Interlocked.Exchange(ref _dirty, 1);
            _publish(TaskbarLayoutSnapshot.Empty);
            SignalProbe();
        }

        private void Tick(object sender, EventArgs args)
        {
            if (_disposed) return;
            var handle = FindWindow("Shell_TrayWnd", null);
            NativeRect native;
            var bounds = handle != IntPtr.Zero && GetWindowRect(handle, out native) ? native.Rectangle : Rectangle.Empty;
            uint process = 0;
            if (handle != IntPtr.Zero) GetWindowThreadProcessId(handle, out process);
            var dpi = handle == IntPtr.Zero ? 96u : GetDpiForWindow(handle);
            if (handle != _lastHandle || bounds != _lastBounds || process != _lastProcess || dpi != _lastDpi)
            {
                _lastHandle = handle;
                _lastBounds = bounds;
                _lastProcess = process;
                _lastDpi = dpi;
                Invalidate();
            }
            if (_publishedValid && Stopwatch.GetElapsedTime(_lastValidProbe).TotalMilliseconds > MaximumSnapshotAgeMilliseconds)
                Invalidate(); // A stuck UIA provider cannot leave stale hit geometry on screen indefinitely.
            // Drain results on the known WinForms UI timer. Do not capture an ambient WPF/
            // WinForms SynchronizationContext or post to a destroyed handle from the worker.
            var completed = Interlocked.Exchange(ref _completed, null);
            if (completed != null && completed.Generation == _generation)
            {
                _publishedValid = completed.Snapshot.Handle != IntPtr.Zero;
                if (_publishedValid) _lastValidProbe = Stopwatch.GetTimestamp();
                _publish(completed.Snapshot);
            }
            // Also re-probe when a shell build does not emit the expected accessibility events.
            if (Interlocked.Exchange(ref _dirty, 0) != 0 || ++_ticks >= 4)
            {
                _ticks = 0;
                SignalProbe();
            }
        }

        private void SignalProbe()
        {
            lock (_probeGate) { if (!_probeDisposed) _probe.Set(); }
        }

        private void ProbeLoop()
        {
            try
            {
            while (true)
            {
                _probe.WaitOne();
                if (_disposed) return;
                int generation = _generation;
                long started = Stopwatch.GetTimestamp();
                TaskbarLayoutSnapshot result;
                try { result = ReadSnapshot(); }
                catch (Exception exception)
                {
                    Trace.WriteLine("Taskbar observer: " + exception.GetType().Name);
                    result = TaskbarLayoutSnapshot.Empty;
                }
                if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > MaximumSnapshotAgeMilliseconds)
                    result = TaskbarLayoutSnapshot.Empty;
                if (_disposed) return;
                // Failure replaces the entire snapshot; stale successful flags cannot survive.
                Interlocked.Exchange(ref _completed, new CompletedProbe(generation, result));
            }
            }
            finally { lock (_probeGate) { _probeDisposed = true; _probe.Dispose(); } }
        }

        private static TaskbarLayoutSnapshot ReadSnapshot()
        {
            var taskbar = FindWindow("Shell_TrayWnd", null);
            NativeRect native;
            if (taskbar == IntPtr.Zero || !IsWindowVisible(taskbar) || !GetWindowRect(taskbar, out native))
                return TaskbarLayoutSnapshot.Empty;
            var bounds = native.Rectangle;
            if (bounds.Width <= 2 || bounds.Height <= 2) return TaskbarLayoutSnapshot.Empty;
            bool horizontal = bounds.Width >= bounds.Height;
            var occupied = new List<Rectangle>();
            Rectangle? notifications = null;
            var tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (tray != IntPtr.Zero && IsWindowVisible(tray) && GetWindowRect(tray, out native) &&
                native.Rectangle.IntersectsWith(bounds)) notifications = native.Rectangle;
            var root = AutomationElement.FromHandle(taskbar);
            if (root == null) return TaskbarLayoutSnapshot.Empty;
            var controls = root.FindAll(TreeScope.Descendants, new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox)));
            int notificationEdge = int.MaxValue;
            for (int i = 0; i < controls.Count; i++)
            {
                var element = controls[i].Current;
                if (element.IsOffscreen) continue;
                var box = element.BoundingRectangle;
                if (box.IsEmpty || box.Width < 2 || box.Height < 2 || box.Width > 400 || box.Height > 400) continue;
                var rect = Rectangle.FromLTRB((int)Math.Floor(box.Left), (int)Math.Floor(box.Top),
                    (int)Math.Ceiling(box.Right), (int)Math.Ceiling(box.Bottom));
                if (!rect.IntersectsWith(bounds)) continue;
                occupied.Add(rect);
                var id = element.AutomationId ?? string.Empty;
                bool notification = id.IndexOf("SystemTray", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    id.IndexOf("Notify", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    id.IndexOf("Chevron", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    id.IndexOf("Clock", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (notifications.HasValue && notifications.Value.IntersectsWith(rect));
                if (notification) notificationEdge = Math.Min(notificationEdge, horizontal ? rect.Left : rect.Top);
            }
            if (notificationEdge != int.MaxValue)
            {
                notifications = horizontal
                    ? Rectangle.FromLTRB(notificationEdge, bounds.Top, bounds.Right, bounds.Bottom)
                    : Rectangle.FromLTRB(bounds.Left, notificationEdge, bounds.Right, bounds.Bottom);
            }
            if (notifications.HasValue) occupied.Add(notifications.Value);
            // No trusted notification anchor means no guessed overlay on top of a genuine shell icon.
            if (!notifications.HasValue) return TaskbarLayoutSnapshot.Empty;
            NativeRect after;
            if (FindWindow("Shell_TrayWnd", null) != taskbar || !GetWindowRect(taskbar, out after) || after.Rectangle != bounds)
                return TaskbarLayoutSnapshot.Empty;
            return new TaskbarLayoutSnapshot(taskbar, bounds, notifications.Value, occupied.ToArray(),
                Math.Max(96u, GetDpiForWindow(taskbar)));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            _timer.Dispose();
            foreach (var hook in _hooks) UnhookWinEvent(hook);
            _hooks.Clear();
            SignalProbe();
            // UIA can be blocked inside Explorer; never join it on the UI thread or spawn replacement workers.
            // Worker is background-only and owns no shell resources. Event released when it exits.
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativeRect
        {
            public int Left, Top, Right, Bottom;
            public Rectangle Rectangle { get { return Rectangle.FromLTRB(Left, Top, Right, Bottom); } }
        }
        private delegate void WinEventDelegate(IntPtr hook, uint kind, IntPtr hwnd, int obj, int child, uint thread, uint time);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string name, string text);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string name, string text);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEventDelegate callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    }

}
