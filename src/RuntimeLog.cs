using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexQuotaTray
{
    // Diagnostic events only. Never pass quota values, session paths, tokens, or exception messages.
    // Two bounded local files make a disappearing window distinguishable from a process crash.
    internal static class RuntimeLog
    {
        private const long MaximumFileBytes = 128 * 1024;
        private static readonly object Gate = new object();
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private static string _path;
        internal static string LogPath => _path ?? @"%LOCALAPPDATA%\ClaudeMeter\logs\runtime.log";

        internal static void Start()
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClaudeMeter", "logs");
                lock (Gate)
                {
                    Directory.CreateDirectory(directory);
                    _path = Path.Combine(directory, "runtime.log");
                }
                var assembly = Assembly.GetEntryAssembly()?.GetName();
                Write("startup", "version=" + assembly?.Version +
                    " pid=" + Environment.ProcessId +
                    " architecture=" + RuntimeInformation.ProcessArchitecture +
                    " runtime=" + Environment.Version +
                    " os=" + Environment.OSVersion.Version);
            }
            catch
            {
                // Diagnostics must never prevent startup, even for a read-only or full profile disk.
            }
        }

        internal static void Event(string stage)
        {
            Write(stage, null);
        }

        // Details must be app-owned text, such as the fixed quota failure messages.
        internal static void Event(string stage, string details)
        {
            Write(stage, details?.Replace('\r', ' ').Replace('\n', ' '));
        }

        internal static void Exception(string stage, Exception exception)
        {
            try { Write(stage, DescribeException(exception)); }
            catch { }
        }

        // Type, native error code, and method names are sufficient to locate failures. Omitting
        // Message, Data, Source, TargetSite parameter values, and source filenames avoids leaking
        // the user's files, quota JSON, authentication responses, and personal profile paths.
        internal static string DescribeException(Exception exception)
        {
            var output = new StringBuilder();
            for (int depth = 0; exception != null && depth < 4; depth++, exception = exception.InnerException)
            {
                if (depth > 0) output.Append(" | inner=");
                output.Append(exception.GetType().FullName);
                output.Append(" hresult=0x").Append(exception.HResult.ToString("X8"));
                if (exception is Win32Exception native)
                    output.Append(" native=").Append(native.NativeErrorCode);
                var frames = new StackTrace(exception, false).GetFrames();
                if (frames == null) continue;
                int count = Math.Min(frames.Length, 16);
                for (int index = 0; index < count; index++)
                {
                    var method = frames[index].GetMethod();
                    if (method == null) continue;
                    output.Append(" <- ").Append(method.DeclaringType?.FullName).Append('.').Append(method.Name);
                }
            }
            return output.ToString();
        }

        private static void Write(string stage, string details)
        {
            try
            {
                // Stages are developer-owned identifiers, not arbitrary incoming data.
                var label = new StringBuilder();
                foreach (var character in stage ?? "unknown")
                {
                    if (label.Length >= 72) break;
                    label.Append(char.IsAsciiLetterOrDigit(character) || character == '.' || character == '-' || character == '_'
                        ? character : '_');
                }
                var line = DateTimeOffset.UtcNow.ToString("O") + " " + label +
                    (string.IsNullOrEmpty(details) ? string.Empty : " " + details) + Environment.NewLine;
                if (line.Length > 12000) line = line.Substring(0, 12000) + Environment.NewLine;
                lock (Gate)
                {
                    if (_path == null) return;
                    if (File.Exists(_path) && new FileInfo(_path).Length + Utf8.GetByteCount(line) > MaximumFileBytes)
                        File.Move(_path, _path + ".previous", true);
                    File.AppendAllText(_path, line, Utf8);
                }
            }
            catch
            {
                // Logging is best-effort. No recursive diagnostics or user-facing exception dialogs.
            }
        }
    }
}
