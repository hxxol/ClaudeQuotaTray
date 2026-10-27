using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CodexQuotaTray.Compatibility;

namespace CodexQuotaTray
{
    internal sealed class SessionQuotaReader
    {
        private const int MaximumFilesToInspect = 24;
        private const int MaximumActivityFiles = 300;
        private const int TailBytes = 768 * 1024;
        private readonly string _codexHome;
        private readonly Func<bool> _processDetector;
        private readonly string _liveQuotaHistoryPath;
        private List<TokenActivitySample> _activityCache;
        private List<TokenActivitySample> _activityTimelineCache;
        private List<QuotaUsageSample> _quotaHistoryCache;
        private List<QuotaUsageSample> _liveQuotaHistory;
        private DateTimeOffset _activityCacheExpiresAt;

        public SessionQuotaReader()
            : this(ResolveCodexHome(), IsClaudeLoggedIn)
        {
        }

        internal SessionQuotaReader(string codexHome, Func<bool> processDetector)
        {
            _codexHome = codexHome;
            _processDetector = processDetector;
            _liveQuotaHistoryPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClaudeMeter", "quota-history-v1.tsv");
        }

        public QuotaReadResult ReadLatest()
        {
            var result = new QuotaReadResult
            {
                IsCodexRunning = _processDetector()
            };

            try
            {
                // Claude: read the OAuth usage endpoint with the Claude Code credentials.
                string failure;
                var latest = ReadLatestFromClaudeApi(out failure);
                result.Snapshot = latest;
                result.Activity = new List<TokenActivitySample>();
                result.ActivityTimeline = new List<TokenActivitySample>();
                if (latest != null) RecordLiveQuotaSnapshot(latest);
                result.QuotaHistory = GetCombinedQuotaHistory();
                if (latest == null) result.Error = failure ?? "读取额度时出现未知错误";
            }
            catch (Exception)
            {
                result.Error = "读取额度时出现未知错误";
            }

            return result;
        }

        private List<QuotaUsageSample> GetCombinedQuotaHistory()
        {
            EnsureLiveQuotaHistoryLoaded();
            var cutoff = DateTimeOffset.Now.AddDays(-31);
            return (_quotaHistoryCache ?? new List<QuotaUsageSample>())
                .Concat(_liveQuotaHistory)
                .Where(item => item.CapturedAt >= cutoff)
                .GroupBy(item => new { item.CapturedAt, item.WindowKey, item.WindowMinutes })
                .Select(group => group.Last())
                .OrderBy(item => item.CapturedAt)
                .ToList();
        }

        private void RecordLiveQuotaSnapshot(QuotaSnapshot snapshot)
        {
            EnsureLiveQuotaHistoryLoaded();
            foreach (var window in snapshot.Windows)
            {
                var sample = new QuotaUsageSample
                {
                    CapturedAt = snapshot.CapturedAt,
                    WindowKey = window.Key,
                    WindowKind = window.Kind,
                    WindowMinutes = window.WindowMinutes,
                    RemainingPercent = window.RemainingPercent,
                    ResetsAt = window.ResetsAt
                };
                var previous = _liveQuotaHistory.LastOrDefault(item =>
                    string.Equals(item.WindowKey, sample.WindowKey, StringComparison.OrdinalIgnoreCase) &&
                    item.WindowMinutes == sample.WindowMinutes);
                if (!ShouldRecordQuotaSample(previous, sample)) continue;

                _liveQuotaHistory.Add(sample);
                try
                {
                    var directory = Path.GetDirectoryName(_liveQuotaHistoryPath);
                    if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                    File.AppendAllText(_liveQuotaHistoryPath, SerializeQuotaHistoryLine(sample) + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                }
            }
        }

        private void EnsureLiveQuotaHistoryLoaded()
        {
            if (_liveQuotaHistory != null) return;
            _liveQuotaHistory = new List<QuotaUsageSample>();
            try
            {
                if (!File.Exists(_liveQuotaHistoryPath)) return;
                var cutoff = DateTimeOffset.Now.AddDays(-31);
                foreach (var line in File.ReadLines(_liveQuotaHistoryPath, Encoding.UTF8))
                {
                    QuotaUsageSample sample;
                    if (TryParseQuotaHistoryLine(line, out sample) && sample.CapturedAt >= cutoff)
                        _liveQuotaHistory.Add(sample);
                }
                _liveQuotaHistory = _liveQuotaHistory.OrderBy(item => item.CapturedAt).ToList();
            }
            catch
            {
                _liveQuotaHistory.Clear();
            }
        }

        internal static bool ShouldRecordQuotaSample(QuotaUsageSample previous, QuotaUsageSample current)
        {
            if (current == null) return false;
            if (previous == null) return true;
            if (previous.RemainingPercent != current.RemainingPercent) return true;
            if (previous.ResetsAt != current.ResetsAt) return true;
            return current.CapturedAt - previous.CapturedAt >= TimeSpan.FromMinutes(5);
        }

        internal static string SerializeQuotaHistoryLine(QuotaUsageSample sample)
        {
            var reset = sample.ResetsAt.HasValue ? sample.ResetsAt.Value.ToUnixTimeSeconds() : 0;
            return string.Join("\t", new[]
            {
                sample.CapturedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                sample.WindowKey ?? "",
                ((int)sample.WindowKind).ToString(CultureInfo.InvariantCulture),
                sample.WindowMinutes.ToString(CultureInfo.InvariantCulture),
                sample.RemainingPercent.ToString(CultureInfo.InvariantCulture),
                reset.ToString(CultureInfo.InvariantCulture)
            });
        }

        internal static bool TryParseQuotaHistoryLine(string line, out QuotaUsageSample sample)
        {
            sample = null;
            if (string.IsNullOrWhiteSpace(line)) return false;
            var parts = line.Split('\t');
            long capturedAt;
            int kind;
            int minutes;
            int remaining;
            long resetsAt;
            if (parts.Length != 6 ||
                !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out capturedAt) ||
                !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out kind) ||
                !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes) ||
                !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out remaining) ||
                !long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out resetsAt) ||
                capturedAt <= 0 || minutes <= 0 || remaining < 0 || remaining > 100 ||
                !Enum.IsDefined(typeof(QuotaWindowKind), kind)) return false;

            sample = new QuotaUsageSample
            {
                CapturedAt = DateTimeOffset.FromUnixTimeSeconds(capturedAt),
                WindowKey = parts[1],
                WindowKind = (QuotaWindowKind)kind,
                WindowMinutes = minutes,
                RemainingPercent = remaining,
                ResetsAt = resetsAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(resetsAt) : (DateTimeOffset?)null
            };
            return true;
        }

        private List<TokenActivitySample> ReadActivity(string sessionsPath)
        {
            var now = DateTimeOffset.Now;
            if (_activityCache != null && now < _activityCacheExpiresAt)
            {
                return new List<TokenActivitySample>(_activityCache);
            }

            var cutoffUtc = DateTime.UtcNow.AddDays(-31);
            var activity = new List<TokenActivitySample>();
            var activityTimeline = new List<TokenActivitySample>();
            var quotaHistory = new List<QuotaUsageSample>();
            var files = new DirectoryInfo(sessionsPath)
                .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .Where(file => file.LastWriteTimeUtc >= cutoffUtc)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(MaximumActivityFiles);

            foreach (var file in files)
            {
                try
                {
                    TokenActivitySample latest = null;
                    foreach (var line in ReadTailLines(file.FullName))
                    {
                        TokenActivitySample candidate;
                        if (TryParseTokenUsageLine(line, out candidate))
                        {
                            candidate.SourceKey = file.FullName;
                            candidate.SourceStartedAt = new DateTimeOffset(file.CreationTimeUtc);
                            if (candidate.CapturedAt.UtcDateTime >= cutoffUtc)
                                activityTimeline.Add(candidate);
                            if (latest == null || candidate.CapturedAt >= latest.CapturedAt)
                                latest = candidate;
                        }

                        QuotaSnapshot quotaSnapshot;
                        if (TryParseTokenCountLine(line, out quotaSnapshot))
                        {
                            foreach (var window in quotaSnapshot.Windows)
                            {
                                quotaHistory.Add(new QuotaUsageSample
                                {
                                    CapturedAt = quotaSnapshot.CapturedAt,
                                    WindowKey = window.Key,
                                    WindowKind = window.Kind,
                                    WindowMinutes = window.WindowMinutes,
                                    RemainingPercent = window.RemainingPercent,
                                    ResetsAt = window.ResetsAt
                                });
                            }
                        }
                    }

                    if (latest != null && latest.Tokens > 0 && latest.CapturedAt.UtcDateTime >= cutoffUtc)
                    {
                        activity.Add(latest);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            activity = activity.OrderBy(item => item.CapturedAt).ToList();
            activityTimeline = activityTimeline.OrderBy(item => item.CapturedAt).ToList();
            quotaHistory = quotaHistory
                .GroupBy(item => new { item.CapturedAt, item.WindowKey, item.WindowMinutes })
                .Select(group => group.Last())
                .OrderBy(item => item.CapturedAt)
                .ToList();
            _activityCache = activity;
            _activityTimelineCache = activityTimeline;
            _quotaHistoryCache = quotaHistory;
            _activityCacheExpiresAt = now.AddMinutes(5);
            return new List<TokenActivitySample>(activity);
        }

        private static string ResolveCodexHome()
        {
            var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured;
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }

        private static string ResolveClaudeCredentialsPath()
        {
            var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            var dir = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
                : configured;
            return Path.Combine(dir, ".credentials.json");
        }

        private static bool IsClaudeLoggedIn()
        {
            return File.Exists(ResolveClaudeCredentialsPath());
        }

        private static readonly System.Net.Http.HttpClient HttpClient =
            new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        private QuotaSnapshot ReadLatestFromClaudeApi(out string failure)
        {
            failure = null;
            string token = null, plan = null;
            try
            {
                var path = ResolveClaudeCredentialsPath();
                if (!File.Exists(path)) { failure = "尚未找到 Claude 登录凭据，请先运行 Claude Code 并登录"; return null; }
                string json;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, false))
                {
                    json = reader.ReadToEnd();
                }
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var oauth = AsDictionary(Get(serializer.DeserializeObject(json) as Dictionary<string, object>, "claudeAiOauth"));
                token = AsString(Get(oauth, "accessToken"));
                plan = AsString(Get(oauth, "subscriptionType"));
            }
            catch
            {
                failure = "无法读取 Claude 登录凭据";
                return null;
            }
            if (string.IsNullOrWhiteSpace(token)) { failure = "Claude 登录凭据中没有访问令牌"; return null; }

            try
            {
                using (var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage"))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                    request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
                    request.Headers.TryAddWithoutValidation("User-Agent", "claude-code/2.1.0");
                    var response = HttpClient.SendAsync(request).GetAwaiter().GetResult();
                    var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (!response.IsSuccessStatusCode)
                    {
                        var code = (int)response.StatusCode;
                        failure = code == 401 || code == 403
                            ? "Claude 登录已过期，请打开一次 Claude Code 以刷新登录"
                            : "Claude 额度接口返回错误 (" + code + ")";
                        return null;
                    }
                    QuotaSnapshot snapshot;
                    if (TryParseClaudeUsageResponse(body, plan, out snapshot)) return snapshot;
                    failure = "Claude 返回的额度数据无法识别";
                    return null;
                }
            }
            catch
            {
                failure = "无法连接 Claude 额度接口，稍后会自动重试";
                return null;
            }
        }

        internal static bool TryParseClaudeUsageResponse(string json, string planType, out QuotaSnapshot snapshot)
        {
            snapshot = null;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = serializer.DeserializeObject(json) as Dictionary<string, object>;
                if (root == null) return false;
                var parsed = new QuotaSnapshot { CapturedAt = DateTimeOffset.Now, LimitId = "claude", PlanType = planType };

                var limits = Get(root, "limits") as System.Collections.IEnumerable;
                if (limits != null && !(limits is string))
                {
                    foreach (var item in limits)
                    {
                        var limit = AsDictionary(item);
                        var kind = AsString(Get(limit, "kind"));
                        double percent;
                        if (!TryDouble(Get(limit, "percent", "utilization"), out percent)) continue;
                        if (kind == "session") AddClaudeWindow(parsed, 300, percent, Get(limit, "resets_at"));
                        else if (kind == "weekly_all") AddClaudeWindow(parsed, 10080, percent, Get(limit, "resets_at"));
                    }
                }
                if (parsed.Windows.Count == 0)
                {
                    AddClaudeFlat(parsed, AsDictionary(Get(root, "five_hour")), 300);
                    AddClaudeFlat(parsed, AsDictionary(Get(root, "seven_day")), 10080);
                }
                if (parsed.Windows.Count == 0) return false;
                snapshot = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void AddClaudeFlat(QuotaSnapshot snapshot, Dictionary<string, object> source, int minutes)
        {
            double percent;
            if (source == null || !TryDouble(Get(source, "utilization"), out percent)) return;
            AddClaudeWindow(snapshot, minutes, percent, Get(source, "resets_at"));
        }

        private static void AddClaudeWindow(QuotaSnapshot snapshot, int minutes, double usedPercent, object resetsAt)
        {
            foreach (var existing in snapshot.Windows)
            {
                if (existing.WindowMinutes == minutes) return;
            }
            DateTimeOffset? reset = null;
            var parsed = ParseTimestamp(resetsAt);
            if (parsed != DateTimeOffset.MinValue) reset = parsed;
            snapshot.Windows.Add(new QuotaWindow
            {
                Name = BuildWindowName("claude", minutes, minutes != 300),
                Key = "claude:" + minutes,
                Kind = ClassifyWindow(minutes),
                UsedPercent = Math.Max(0, Math.Min(100, usedPercent)),
                WindowMinutes = minutes,
                ResetsAt = reset
            });
        }

        private static bool IsCodexProcessRunning()
        {
            return HasProcess("ChatGPT") || HasProcess("codex");
        }

        private static bool HasProcess(string name)
        {
            Process[] processes = null;
            try
            {
                processes = Process.GetProcessesByName(name);
                return processes.Length > 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (processes != null)
                {
                    foreach (var process in processes)
                    {
                        process.Dispose();
                    }
                }
            }
        }

        private QuotaSnapshot ReadLatestFromAppServer()
        {
            try
            {
                var executable = ResolveCodexExecutable();
                if (string.IsNullOrWhiteSpace(executable)) return null;

                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = executable,
                        Arguments = "app-server --stdio",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    if (!process.Start()) return null;
                    try
                    {
                        process.StandardError.ReadToEndAsync();
                        process.StandardInput.AutoFlush = true;
                        process.StandardInput.WriteLine("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"codex-quota-tray\",\"version\":\"2.6.0\"},\"capabilities\":{}}}");
                        if (ReadAppServerResponse(process, 1, 2500) == null) return null;

                        process.StandardInput.WriteLine("{\"id\":2,\"method\":\"account/rateLimits/read\",\"params\":{\"excludeResetCreditDetails\":true,\"supportsLunaReserve\":true}}");
                        var response = ReadAppServerResponse(process, 2, 4000);
                        QuotaSnapshot snapshot;
                        return TryParseAppServerRateLimitsResponse(response, out snapshot) ? snapshot : null;
                    }
                    finally
                    {
                        try { process.StandardInput.Close(); }
                        catch { }
                        try
                        {
                            if (!process.WaitForExit(300)) process.Kill();
                        }
                        catch { }
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static string ReadAppServerResponse(Process process, long expectedId, int timeoutMilliseconds)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            while (!process.HasExited)
            {
                var remaining = (int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds);
                if (remaining <= 1) return null;

                var read = process.StandardOutput.ReadLineAsync();
                if (!read.Wait(remaining)) return null;
                var line = read.Result;
                if (line == null) return null;

                try
                {
                    var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    var root = serializer.DeserializeObject(line) as Dictionary<string, object>;
                    long id;
                    if (TryLong(Get(root, "id"), out id) && id == expectedId) return line;
                }
                catch
                {
                }
            }

            return null;
        }

        private static string ResolveCodexExecutable()
        {
            Process[] processes = null;
            try
            {
                processes = Process.GetProcessesByName("codex");
                foreach (var process in processes)
                {
                    try
                    {
                        var path = process.MainModule == null ? null : process.MainModule.FileName;
                        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
            finally
            {
                if (processes != null)
                {
                    foreach (var process in processes) process.Dispose();
                }
            }

            try
            {
                var binRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
                if (Directory.Exists(binRoot))
                {
                    var candidate = new DirectoryInfo(binRoot)
                        .EnumerateFiles("codex.exe", SearchOption.AllDirectories)
                        .OrderByDescending(file => file.LastWriteTimeUtc)
                        .FirstOrDefault();
                    if (candidate != null) return candidate.FullName;
                }
            }
            catch
            {
            }

            return null;
        }

        private string ReadPlanTypeFromAuth()
        {
            try
            {
                var path = Path.Combine(_codexHome, "auth.json");
                if (!File.Exists(path)) return null;

                string json;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, false))
                {
                    json = reader.ReadToEnd();
                }

                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = serializer.DeserializeObject(json) as Dictionary<string, object>;
                var tokens = AsDictionary(Get(root, "tokens"));
                var idToken = AsString(Get(tokens, "id_token", "idToken")) ?? AsString(Get(root, "id_token", "idToken"));
                return TryReadPlanTypeFromIdToken(idToken);
            }
            catch
            {
                return null;
            }
        }

        internal static string TryReadPlanTypeFromIdToken(string idToken)
        {
            if (string.IsNullOrWhiteSpace(idToken)) return null;

            try
            {
                var parts = idToken.Split('.');
                if (parts.Length < 2) return null;

                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                var remainder = payload.Length % 4;
                if (remainder == 1) return null;
                if (remainder > 0) payload = payload.PadRight(payload.Length + 4 - remainder, '=');

                var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var claims = serializer.DeserializeObject(json) as Dictionary<string, object>;
                var authClaims = AsDictionary(Get(claims, "https://api.openai.com/auth"));
                return AsString(Get(authClaims, "chatgpt_plan_type", "chatgptPlanType"));
            }
            catch
            {
                return null;
            }
        }

        private static QuotaSnapshot ReadLatestFromFile(string path)
        {
            QuotaSnapshot latest = null;
            foreach (var line in ReadTailLines(path))
            {
                QuotaSnapshot candidate;
                if (TryParseTokenCountLine(line, out candidate) &&
                    (latest == null || candidate.CapturedAt > latest.CapturedAt))
                {
                    latest = candidate;
                }
            }

            return latest;
        }

        private static IEnumerable<string> ReadTailLines(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var offset = Math.Max(0, stream.Length - TailBytes);
                stream.Seek(offset, SeekOrigin.Begin);

                using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, false))
                {
                    if (offset > 0)
                    {
                        reader.ReadLine();
                    }

                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        yield return line;
                    }
                }
            }
        }

        internal static bool TryParseTokenCountLine(string line, out QuotaSnapshot snapshot)
        {
            snapshot = null;
            if (string.IsNullOrWhiteSpace(line) || line.IndexOf("token_count", StringComparison.Ordinal) < 0)
            {
                return false;
            }

            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = serializer.DeserializeObject(line) as Dictionary<string, object>;
                if (root == null || !StringEquals(Get(root, "type"), "event_msg"))
                {
                    return false;
                }

                var payload = AsDictionary(Get(root, "payload"));
                if (payload == null || !StringEquals(Get(payload, "type"), "token_count"))
                {
                    return false;
                }

                var rateLimits = AsDictionary(Get(payload, "rate_limits", "rateLimits"));
                if (rateLimits == null)
                {
                    return false;
                }

                var parsed = new QuotaSnapshot
                {
                    CapturedAt = ParseTimestamp(Get(root, "timestamp")),
                    LimitId = AsString(Get(rateLimits, "limit_id", "limitId")) ?? "codex",
                    PlanType = AsString(Get(rateLimits, "plan_type", "planType"))
                };

                var buckets = AsDictionary(Get(rateLimits, "rate_limits_by_limit_id", "rateLimitsByLimitId"));
                var rootIsReserve = IsReserveBucket(parsed.LimitId, rateLimits);
                if (!rootIsReserve && (AsDictionary(Get(rateLimits, "primary")) != null || AsDictionary(Get(rateLimits, "secondary")) != null))
                {
                    AddBucket(parsed, rateLimits, parsed.LimitId);
                }
                else if (buckets != null && buckets.Count > 0)
                {
                    object ordinary;
                    if (!buckets.TryGetValue(parsed.LimitId, out ordinary)) buckets.TryGetValue("codex", out ordinary);
                    AddBucket(parsed, AsDictionary(ordinary), parsed.LimitId);
                }
                else if (!rootIsReserve)
                {
                    AddBucket(parsed, rateLimits, parsed.LimitId);
                }

                AddReserveFromBuckets(parsed, buckets);
                if (rootIsReserve) SetReserveBucket(parsed, rateLimits, parsed.LimitId);

                if (parsed.Windows.Count == 0)
                {
                    return false;
                }

                snapshot = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryParseAppServerRateLimitsResponse(string line, out QuotaSnapshot snapshot)
        {
            snapshot = null;
            if (string.IsNullOrWhiteSpace(line)) return false;

            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = serializer.DeserializeObject(line) as Dictionary<string, object>;
                var result = AsDictionary(Get(root, "result"));
                var buckets = AsDictionary(Get(result, "rateLimitsByLimitId", "rate_limits_by_limit_id"));
                var rateLimits = AsDictionary(Get(result, "rateLimits", "rate_limits"));
                if (rateLimits == null)
                {
                    object ordinary;
                    if (buckets == null || !buckets.TryGetValue("codex", out ordinary)) return false;
                    rateLimits = AsDictionary(ordinary);
                }
                if (rateLimits == null) return false;

                var parsed = new QuotaSnapshot
                {
                    CapturedAt = DateTimeOffset.Now,
                    LimitId = AsString(Get(rateLimits, "limitId", "limit_id")) ?? "codex",
                    PlanType = AsString(Get(rateLimits, "planType", "plan_type"))
                };
                bool ordinaryUsageAllowed;
                if (TryBool(Get(result, "ordinaryUsageAllowed", "ordinary_usage_allowed"), out ordinaryUsageAllowed))
                    parsed.OrdinaryUsageAllowed = ordinaryUsageAllowed;
                AddBucket(parsed, rateLimits, parsed.LimitId);
                AddReserveFromBuckets(parsed, buckets);
                if (parsed.Windows.Count == 0) return false;

                snapshot = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryParseTokenUsageLine(string line, out TokenActivitySample usage)
        {
            usage = null;
            if (string.IsNullOrWhiteSpace(line) || line.IndexOf("token_count", StringComparison.Ordinal) < 0)
            {
                return false;
            }

            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = serializer.DeserializeObject(line) as Dictionary<string, object>;
                if (root == null || !StringEquals(Get(root, "type"), "event_msg"))
                {
                    return false;
                }

                var payload = AsDictionary(Get(root, "payload"));
                if (payload == null || !StringEquals(Get(payload, "type"), "token_count"))
                {
                    return false;
                }

                var info = AsDictionary(Get(payload, "info"));
                var totalUsage = AsDictionary(Get(info, "total_token_usage", "totalTokenUsage"));
                long totalTokens;
                if (!TryLong(Get(totalUsage, "total_tokens", "totalTokens"), out totalTokens) || totalTokens < 0)
                {
                    return false;
                }

                var capturedAt = ParseTimestamp(Get(root, "timestamp"));
                if (capturedAt == DateTimeOffset.MinValue)
                {
                    return false;
                }

                usage = new TokenActivitySample
                {
                    CapturedAt = capturedAt,
                    Tokens = totalTokens
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void AddBucket(QuotaSnapshot snapshot, Dictionary<string, object> bucket, string fallbackName)
        {
            if (bucket == null)
            {
                return;
            }

            var bucketName = AsString(Get(bucket, "limit_name", "limitName"));
            if (string.IsNullOrWhiteSpace(bucketName))
            {
                bucketName = fallbackName;
            }

            AddWindow(snapshot, AsDictionary(Get(bucket, "primary")), bucketName, false);
            AddWindow(snapshot, AsDictionary(Get(bucket, "secondary")), bucketName, true);
        }

        private static bool IsReserveBucket(string key, Dictionary<string, object> bucket)
        {
            var name = AsString(Get(bucket, "limit_name", "limitName"));
            return string.Equals(key, "base_model_inference", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "gpt-reserve", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddReserveFromBuckets(QuotaSnapshot snapshot, Dictionary<string, object> buckets)
        {
            if (buckets == null) return;
            object reserve;
            if (buckets.TryGetValue("base_model_inference", out reserve))
            {
                SetReserveBucket(snapshot, AsDictionary(reserve), "gpt-reserve");
                return;
            }

            foreach (var pair in buckets)
            {
                var bucket = AsDictionary(pair.Value);
                if (!IsReserveBucket(pair.Key, bucket)) continue;
                SetReserveBucket(snapshot, bucket, "gpt-reserve");
                return;
            }
        }

        private static void SetReserveBucket(QuotaSnapshot snapshot, Dictionary<string, object> bucket, string fallbackName)
        {
            if (bucket == null) return;
            var holder = new QuotaSnapshot();
            AddBucket(holder, bucket, fallbackName);
            var reserve = holder.Windows.OrderByDescending(window => window.WindowMinutes).FirstOrDefault();
            if (reserve == null) return;
            reserve.Name = "gpt-reserve · 备用额度";
            reserve.Key = "gpt-reserve:" + reserve.WindowMinutes.ToString(CultureInfo.InvariantCulture);
            snapshot.ReserveWindow = reserve;
        }

        private static void AddWindow(
            QuotaSnapshot snapshot,
            Dictionary<string, object> source,
            string bucketName,
            bool secondary)
        {
            if (source == null)
            {
                return;
            }

            double usedPercent;
            if (!TryDouble(Get(source, "used_percent", "usedPercent"), out usedPercent))
            {
                return;
            }

            int windowMinutes;
            TryInt(Get(source, "window_minutes", "windowDurationMins"), out windowMinutes);

            long resetsAt;
            DateTimeOffset? resetTime = null;
            if (TryLong(Get(source, "resets_at", "resetsAt"), out resetsAt) && resetsAt > 0)
            {
                resetTime = DateTimeOffset.FromUnixTimeSeconds(resetsAt);
            }

            snapshot.Windows.Add(new QuotaWindow
            {
                Name = BuildWindowName(bucketName, windowMinutes, secondary),
                Key = (string.IsNullOrWhiteSpace(bucketName) ? "codex" : bucketName.Trim().ToLowerInvariant()) + ":" + windowMinutes,
                Kind = ClassifyWindow(windowMinutes),
                UsedPercent = usedPercent,
                WindowMinutes = windowMinutes,
                ResetsAt = resetTime
            });
        }

        private static string BuildWindowName(string bucketName, int minutes, bool secondary)
        {
            string windowName;
            if (minutes == 300)
            {
                windowName = "5小时额度";
            }
            else if (minutes >= 10080)
            {
                windowName = "每周额度";
            }
            else if (minutes >= 1440)
            {
                windowName = "长期额度";
            }
            else if (minutes > 0)
            {
                windowName = "短期额度";
            }
            else
            {
                windowName = secondary ? "次要额度" : "主要额度";
            }

            if (!string.IsNullOrWhiteSpace(bucketName) &&
                !string.Equals(bucketName, "codex", StringComparison.OrdinalIgnoreCase))
            {
                return bucketName + " · " + windowName;
            }

            return windowName;
        }

        internal static QuotaWindowKind ClassifyWindow(int minutes)
        {
            if (minutes == 300) return QuotaWindowKind.FiveHour;
            if (minutes == 10080) return QuotaWindowKind.Weekly;
            return QuotaWindowKind.Other;
        }

        private static object Get(Dictionary<string, object> source, params string[] keys)
        {
            if (source == null)
            {
                return null;
            }

            foreach (var key in keys)
            {
                object value;
                if (source.TryGetValue(key, out value))
                {
                    return value;
                }
            }

            return null;
        }

        private static Dictionary<string, object> AsDictionary(object value)
        {
            return value as Dictionary<string, object>;
        }

        private static string AsString(object value)
        {
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static bool StringEquals(object value, string expected)
        {
            return string.Equals(AsString(value), expected, StringComparison.Ordinal);
        }

        private static DateTimeOffset ParseTimestamp(object value)
        {
            DateTimeOffset parsed;
            if (DateTimeOffset.TryParse(AsString(value), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed))
            {
                return parsed;
            }

            return DateTimeOffset.MinValue;
        }

        private static bool TryDouble(object value, out double parsed)
        {
            return double.TryParse(AsString(value), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed);
        }

        private static bool TryInt(object value, out int parsed)
        {
            return int.TryParse(AsString(value), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed);
        }

        private static bool TryLong(object value, out long parsed)
        {
            return long.TryParse(AsString(value), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed);
        }

        private static bool TryBool(object value, out bool parsed)
        {
            return bool.TryParse(AsString(value), out parsed);
        }
    }
}
