using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CodexQuotaTray;
#if LEGACY_ORACLE
using System.Web.Script.Serialization;
#else
using CodexQuotaTray.Compatibility;
#endif

internal static class CompatibilityContract
{
    // Synthetic input only. The contract never opens real auth/session files or starts an app-server.
    internal static void Emit(string path)
    {
        var lines = new List<string>();
        string[] values = {
            "null", "true", "false", "0", "-1", "2147483647", "2147483648", "9223372036854775807",
            "9223372036854775808", "1.00", "-0.5", "1e2", "1e-5", "1e100", "1e400",
            "[]", "{}", "[1,2.5,1e2,\"中😀\",true,null]", "{\"a\":1,\"A\":2,\"a\":3}",
            "\"2026-10-15T00:00:00Z\"", "\"/Date(0)/\"", "\"\\/Date(0)\\/\"", "\"\\/Date(123+0800)\\/\"",
            "{bare:'text',n:+001,v:.5}", "'hello'", "\"\\uD800\"", "[1,]", "{\"a\":1,}",
            "{\"value\":null}", "", "  ", "{", "null null", "{\"a\":[1,{\"b\":true}]}"
        };
        var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        for (int i = 0; i < values.Length; i++)
        {
            try { lines.Add("json:" + i + ":" + Describe(serializer.DeserializeObject(values[i]))); }
            catch { lines.Add("json:" + i + ":rejected"); }
        }
        var record = "{\"timestamp\":\"2026-10-15T00:00:00.001Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"total_tokens\":2147483648}},\"rate_limits\":{\"limit_id\":\"codex\",\"plan_type\":\"plus\",\"primary\":{\"used_percent\":48.5,\"window_minutes\":300,\"resets_at\":1786884758},\"secondary\":{\"used_percent\":20,\"window_minutes\":10080,\"resets_at\":1786400000}}}}";
        string[] records = { record, record.Replace("48.5", "4.85e1"), record.Replace("48.5", "\"48.5\""),
            record.Replace("48.5", "null"), record.Replace("48.5", "-10"), record.Replace("48.5", "110"),
            record.Replace("\"", "'"), record.Replace("1786884758", "0"), "not json" };
        for (int i = 0; i < records.Length; i++)
        {
            QuotaSnapshot quota;
            lines.Add("quota:" + i + ":" + (SessionQuotaReader.TryParseTokenCountLine(records[i], out quota) ? Snapshot(quota) : "rejected"));
            TokenActivitySample usage;
            lines.Add("token:" + i + ":" + (SessionQuotaReader.TryParseTokenUsageLine(records[i], out usage)
                ? usage.CapturedAt.ToString("O", CultureInfo.InvariantCulture) + ":" + usage.Tokens : "rejected"));
        }
        var appServer = "{\"id\":2,\"result\":{\"ordinaryUsageAllowed\":false,\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":99,\"windowDurationMins\":300},\"secondary\":{\"usedPercent\":100,\"windowDurationMins\":10080}},\"rateLimitsByLimitId\":{\"base_model_inference\":{\"limitName\":\"gpt-reserve\",\"primary\":{\"usedPercent\":15,\"windowDurationMins\":10080}}}}}";
        foreach (bool allowed in new[] { false, true })
        {
            QuotaSnapshot quota;
            var sample = appServer.Replace("\"ordinaryUsageAllowed\":false", "\"ordinaryUsageAllowed\":" + allowed.ToString().ToLowerInvariant());
            lines.Add("app:" + allowed + ":" + (SessionQuotaReader.TryParseAppServerRateLimitsResponse(sample, out quota) ? Snapshot(quota) : "rejected"));
        }
        File.WriteAllLines(path, lines.ToArray(), new System.Text.UTF8Encoding(false));
    }
    private static string Snapshot(QuotaSnapshot quota)
    {
        return quota.PlanType + ":" + quota.OrdinaryUsageAllowed + ":" + quota.IsReserveActive + ":" + quota.MostConstrainedRemaining + ":" +
            string.Join("|", quota.Windows.Select(Window).ToArray()) + ":reserve=" + (quota.ReserveWindow == null ? "none" : Window(quota.ReserveWindow));
    }
    private static string Window(QuotaWindow window)
    {
        return window.Key + "," + window.Name + "," + window.Kind + "," + window.UsedPercent.ToString("R", CultureInfo.InvariantCulture) +
            "," + window.RemainingPercent + "," + window.WindowMinutes + "," + (window.ResetsAt.HasValue ? window.ResetsAt.Value.ToString("O", CultureInfo.InvariantCulture) : "null");
    }
    private static string Describe(object value)
    {
        if (value == null) return "null";
        var map = value as IDictionary<string, object>;
        if (map != null) return "object{" + string.Join(",", map.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + Describe(p.Value)).ToArray()) + "}";
        var array = value as object[];
        if (array != null) return "array[" + string.Join(",", array.Select(Describe).ToArray()) + "]";
        if (value is string) return "string:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes((string)value));
        if (value is DateTime) return "date:" + ((DateTime)value).ToString("O", CultureInfo.InvariantCulture);
        return value.GetType().Name + ":" + Convert.ToString(value, CultureInfo.InvariantCulture);
    }
#if LEGACY_ORACLE
    private static void Main(string[] args) { Emit(args[0]); }
#endif
}
