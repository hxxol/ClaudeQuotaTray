using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using CodexQuotaTray;
using CodexQuotaTray.Compatibility;

internal static class RegressionTests
{
    private static int _assertions;
    internal static void Run()
    {
        JsonTypes();
        DisplayCache();
        SettingsRoundTrip();
        TaskbarGeometry();
        Console.WriteLine("New regression checks passed: " + _assertions);
    }
    private static void Assert(bool condition, string message)
    { _assertions++; if (!condition) throw new InvalidOperationException(message); }
    private static void JsonTypes()
    {
        var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var map = (Dictionary<string, object>)serializer.DeserializeObject("{\"i\":1,\"l\":2147483648,\"d\":1.25,\"e\":1e2,\"a\":[true,null],\"same\":0,\"same\":2}");
        Assert(map["i"] is int && map["l"] is long && map["d"] is decimal && map["e"] is double, "Legacy numeric boxing must survive migration.");
        Assert(map["a"] is object[] && (int)map["same"] == 2, "Arrays and last duplicate property must match Framework.");
        Assert(serializer.DeserializeObject("\"2026-10-15T00:00:00Z\"") is string, "ISO dates must not be implicitly converted.");
        Assert(serializer.DeserializeObject("\"/Date(0)/\"") is string, "Unescaped ASP.NET date remains a string.");
        Assert(serializer.DeserializeObject("\"\\/Date(0)\\/\"") is DateTime, "Escaped ASP.NET date must preserve Framework conversion.");
        var permissive = (Dictionary<string, object>)serializer.DeserializeObject("{x:'text', y:+001}");
        Assert((string)permissive["x"] == "text" && (int)permissive["y"] == 1, "Legacy single quote and bare property input remains accepted.");
        Assert((string)serializer.DeserializeObject("\"\\uD800\"") == "\uFFFD", "Unpaired surrogate must be replaced like Framework.");
        foreach (var invalid in new[] { "[1,]", "{\"x\":1,}", "garbage", "null true", "1e400" })
        {
            bool rejected = false;
            try { serializer.DeserializeObject(invalid); } catch { rejected = true; }
            Assert(rejected, "Malformed/overflow JSON must fail: " + invalid);
        }
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            Assert((decimal)serializer.DeserializeObject("1.25") == 1.25m, "JSON must be culture-invariant.");
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }
    private static void DisplayCache()
    {
        var settings = new AppSettings();
        var noData = QuotaDisplayState.Update(null, settings);
        Assert(!noData.Known && noData.Remaining == 0 && noData.ResetWindow == null, "First run must not invent quota.");
        var snapshot = new QuotaSnapshot();
        var first = new QuotaWindow { UsedPercent = 30, WindowMinutes = 300, ResetsAt = DateTimeOffset.UtcNow.AddHours(2) };
        var second = new QuotaWindow { UsedPercent = 60, WindowMinutes = 10080 };
        snapshot.Windows.Add(first); snapshot.Windows.Add(second);
        var offline = new QuotaReadResult { IsCodexRunning = false, Snapshot = snapshot };
        var fallback = QuotaDisplayState.Update(offline, settings);
        Assert(fallback.Known && fallback.Remaining == 40 && fallback.ResetWindow == null, "First offline snapshot can seed display but no stale tooltip.");
        settings.LastRemainingPercent = 67;
        var kept = QuotaDisplayState.Update(offline, settings);
        Assert(kept.Remaining == 67 && !kept.CacheChanged, "Offline snapshots must not overwrite last successful display.");
        offline.IsCodexRunning = true;
        var live = QuotaDisplayState.Update(offline, settings);
        Assert(live.Remaining == 40 && live.ResetWindow == second && live.CacheChanged, "Live display must use most constrained window.");
        first.UsedPercent = second.UsedPercent;
        Assert(QuotaDisplayState.Update(offline, settings).ResetWindow == first, "Equal minima retain original input order.");
        snapshot.ReserveWindow = new QuotaWindow { UsedPercent = 15, WindowMinutes = 10080 };
        Assert(QuotaDisplayState.Update(offline, settings).Remaining == 85, "Used reserve bucket must win over exhausted normal quota.");
        snapshot.ReserveWindow.UsedPercent = 0;
        snapshot.OrdinaryUsageAllowed = false;
        Assert(QuotaDisplayState.Update(offline, settings).Remaining == 100, "Disallowed normal usage must select untouched reserve too.");
        Assert(QuotaDisplayState.Update(new QuotaReadResult(), settings).Remaining == 100, "Failed refresh keeps cache.");
    }
    private static void SettingsRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "quota-settings-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.ini");
        try
        {
            File.WriteAllLines(path, new[] { "TrayRingWidth=5", "TrayFontSize=17", "LastRemainingPercent=82", "TrayRingColor=-65536", "Language=zh" });
            var settings = AppSettings.Load(path);
            Assert(settings.TrayRingWidth == 5 && settings.TrayFontSize == 17 && settings.LastRemainingPercent == 82, "Legacy settings and quota cache must migrate.");
            Assert(!settings.FloatingVisible && settings.FloatingDiameter == 256, "Migration should not create an unrequested visible floating window.");
            settings.FloatingVisible = true; settings.FloatingDiameter = 512;
            settings.UiRecoveryVersion = 1;
            settings.FloatingLeft = -1400; settings.FloatingTop = 200;
            settings.FloatingRingColor = Color.Lime; settings.FloatingFontColor = Color.Blue;
            settings.FloatingFontSize = 120; settings.FloatingFontWeight = 500;
            settings.FloatingRingWidth = 19; settings.FloatingRingScalePercent = 75;
            settings.Save(path);
            var restored = AppSettings.Load(path);
            Assert(restored.UiRecoveryVersion == 1, "One-time recovery onboarding must survive restart.");
            Assert(restored.FloatingVisible && restored.FloatingDiameter == 512 && restored.FloatingLeft == -1400 && restored.FloatingTop == 200, "Floating state must survive restart, including negative monitor coordinates.");
            Assert(restored.FloatingRingColor.ToArgb() == Color.Lime.ToArgb() && restored.FloatingFontColor.ToArgb() == Color.Blue.ToArgb(), "Floating colors are independent.");
            Assert(restored.TrayRingColor.ToArgb() == Color.Red.ToArgb() && restored.TrayFontSize == 17, "Floating changes must not mutate tray settings.");
            Assert(restored.FloatingFontSize == 120 && restored.FloatingFontWeight == 500 && restored.FloatingRingWidth == 19 && restored.FloatingRingScalePercent == 75, "Floating appearance must persist.");
            File.WriteAllLines(path, new[] { "FloatingDiameter=300", "FloatingFontWeight=1", "FloatingRingScalePercent=500" });
            var invalid = AppSettings.Load(path);
            Assert(invalid.FloatingDiameter == 256 && invalid.FloatingFontWeight == 700 && invalid.FloatingRingScalePercent == 100, "Invalid settings must not create arbitrary resize values.");
            Assert(!File.Exists(path + ".tmp"), "Successful atomic save must clean temporary file.");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static void TaskbarGeometry()
    {
        Rectangle placement;
        var bar = new Rectangle(0, 1040, 1920, 40);
        var tray = new Rectangle(1700, 1040, 220, 40);
        Assert(TaskbarPlacement.EdgeSize(bar, 1) == 28 && TaskbarPlacement.EdgeSize(new Rectangle(0, 0, 1920, 72), 1.5f) == 54, "Original large-icon sizing formula must remain intact.");
        var layout = new TaskbarLayoutSnapshot(new IntPtr(1), bar, tray, new[] { tray }, 96);
        Assert(TaskbarPlacement.TryPlace(layout, out placement) && placement.X == 1664 && placement.Width == 28, "Placement should follow the fresh tray leading edge.");
        var moved = new Rectangle(1800, 1040, 120, 40);
        Assert(TaskbarPlacement.TryPlace(new TaskbarLayoutSnapshot(new IntPtr(1), bar, moved, new[] { moved }, 96), out placement) && placement.X == 1764, "Removing an icon must use new geometry, never old cache.");
        Assert(!TaskbarPlacement.TryPlace(TaskbarLayoutSnapshot.Empty, out placement), "Failed/missing Explorer probe invalidates geometry.");
        var blocked = new Rectangle(0, 1040, 1700, 40);
        Assert(!TaskbarPlacement.TryPlace(new TaskbarLayoutSnapshot(new IntPtr(2), bar, tray, new[] { tray, blocked }, 96), out placement), "Crowded taskbar must not cover genuine app icons.");
        var vertical = new Rectangle(-60, 0, 60, 1200);
        var verticalTray = new Rectangle(-60, 1000, 60, 200);
        Assert(TaskbarPlacement.TryPlace(new TaskbarLayoutSnapshot(new IntPtr(3), vertical, verticalTray, new[] { verticalTray }, 96), out placement) && placement.Y == 944 && placement.Width == 48, "Vertical and negative-coordinate taskbars must use physical coordinates.");
    }
}
