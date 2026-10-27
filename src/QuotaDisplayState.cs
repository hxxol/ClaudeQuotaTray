using System.Linq;

namespace CodexQuotaTray
{
    // Extracted from the original TrayApplicationContext for characterization tests.
    // Reserve takes priority; otherwise the lowest remaining window wins (stable order for ties).
    internal sealed class QuotaDisplayState
    {
        internal int Remaining;
        internal bool Known;
        internal bool CacheChanged;
        internal QuotaWindow ResetWindow;

        internal static QuotaDisplayState Update(QuotaReadResult result, AppSettings settings)
        {
            var active = result != null && result.IsCodexRunning && result.Snapshot != null;
            var window = result == null || result.Snapshot == null
                ? null
                : (result.Snapshot.IsReserveActive
                    ? result.Snapshot.ReserveWindow
                    : result.Snapshot.Windows.OrderBy(item => item.RemainingPercent).FirstOrDefault());
            var changed = window != null && (active || !settings.LastRemainingPercent.HasValue) &&
                settings.LastRemainingPercent != window.RemainingPercent;
            if (changed) settings.LastRemainingPercent = window.RemainingPercent;
            return new QuotaDisplayState
            {
                Remaining = settings.LastRemainingPercent ?? 0,
                Known = settings.LastRemainingPercent.HasValue,
                CacheChanged = changed,
                ResetWindow = active ? window : null
            };
        }
    }
}
