using System;
using System.IO;
using System.Text.RegularExpressions;

namespace CodexProSafeManager
{
    internal static class LogWriter
    {
        private const int MaximumAcceptedCharacters = 65536;
        private const int MaximumPersistedCharacters = 4096;
        private static readonly object Gate = new object();
        private static readonly Regex Bearer = new Regex(
            @"(?i)([""']?authorization[""']?\s*[:=]\s*[""']?\s*bearer\s+)[^,\s""'}]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex SensitiveField = new Regex(
            @"(?i)([""']?(?:api[_-]?key|access[_-]?token|refresh[_-]?token|token|secret|password|tunnel(?:[_-]?id)?|request(?:[_-]?id)?|session(?:[_-]?id)?|trace(?:[_-]?id)?|connection(?:[_-]?id)?|client(?:[_-]?(?:id|instance[_-]?id))?|correlation(?:[_-]?id)?|organization(?:[_-]?id)?|org(?:[_-]?id)?)[""']?\s*[:=]\s*[""']?)[^,\s""'}&]{4,}",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex OpenAiKey = new Regex(
            @"\b(?:sk|sess)[-_][A-Za-z0-9_-]{12,}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex IdentifierPrefix = new Regex(
            @"(?i)\b(?:req(?:uest)?|sess(?:ion)?|trace|tunnel|connection|conn|client(?:_instance)?|correlation|corr|org|cmd|rpc)_[A-Za-z0-9_-]{8,}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex CorrelationUuid = new Regex(
            @"(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex TraceParent = new Regex(
            @"(?i)\b[0-9a-f]{2}-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex UrlUserInfo = new Regex(
            @"(?i)\b(https?://)[^/\s:@]+:[^@\s/]+@",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex QueryValue = new Regex(
            @"([?&][A-Za-z0-9_.~-]{1,64}=)[^&#\s""']+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex WindowsPath = new Regex(
            @"(?i)\b[A-Z]:\\[^\r\n""']+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex UncPath = new Regex(
            @"\\\\[A-Za-z0-9.$_-]+\\[^\r\n""']+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static string DirectoryPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"CodexProSafe Manager\logs");
            }
        }

        public static string CurrentLogPath
        {
            get { return Path.Combine(DirectoryPath, "manager.log"); }
        }

        internal static string Prepare(string source, string message)
        {
            if (String.IsNullOrEmpty(message)) return String.Empty;
            if (message.Length > MaximumAcceptedCharacters) return "[suppressed oversized output]";
            string normalizedSource = NormalizeSource(source);
            if (normalizedSource == "tunnel") return SummarizeTunnelLine(message);
            return Sanitize(message);
        }

        public static string Sanitize(string text)
        {
            if (String.IsNullOrEmpty(text)) return String.Empty;
            if (text.Length > MaximumAcceptedCharacters) return "[suppressed oversized output]";
            string sanitized = Bearer.Replace(text, "$1<redacted>");
            sanitized = SensitiveField.Replace(sanitized, "$1<redacted>");
            sanitized = OpenAiKey.Replace(sanitized, "<redacted-key>");
            sanitized = IdentifierPrefix.Replace(sanitized, "<redacted-id>");
            sanitized = CorrelationUuid.Replace(sanitized, "<redacted-id>");
            sanitized = TraceParent.Replace(sanitized, "<redacted-trace>");
            sanitized = UrlUserInfo.Replace(sanitized, "$1<redacted-userinfo>@");
            sanitized = QueryValue.Replace(sanitized, "$1<redacted>");
            sanitized = WindowsPath.Replace(sanitized, "<redacted-path>");
            sanitized = UncPath.Replace(sanitized, "<redacted-path>");
            sanitized = sanitized.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (sanitized.Length > MaximumPersistedCharacters)
                sanitized = sanitized.Substring(0, MaximumPersistedCharacters) + " [truncated]";
            return sanitized;
        }

        public static void Append(string source, string message)
        {
            string prepared = Prepare(source, message);
            if (prepared == null) return;
            AppendPrepared(source, prepared);
        }

        internal static void AppendPrepared(string source, string prepared)
        {
            WritePrepared(CurrentLogPath, NormalizeSource(source), prepared);
        }

        internal static void AppendPreparedForSelfTest(string path, string source, string prepared)
        {
            WritePrepared(path, NormalizeSource(source), prepared);
        }

        private static void WritePrepared(string path, string source, string prepared)
        {
            string line = String.Format(
                "{0:O} [{1}] {2}",
                DateTimeOffset.Now,
                source,
                (prepared ?? String.Empty).TrimEnd());
            lock (Gate)
            {
                string directory = Path.GetDirectoryName(path);
                if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }

        private static string SummarizeTunnelLine(string message)
        {
            string value = message.Trim();
            if (value.Length == 0) return String.Empty;
            if (value.StartsWith("{", StringComparison.Ordinal) || value.StartsWith("[", StringComparison.Ordinal))
                return null;
            if (String.Equals(value, "Process exited.", StringComparison.OrdinalIgnoreCase))
                return "Process exited.";
            if (value.IndexOf("authenticated", StringComparison.OrdinalIgnoreCase) >= 0 &&
                value.IndexOf("ready", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Tunnel reported authenticated readiness.";
            if (value.IndexOf("starting", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Tunnel process reported startup activity.";
            if (value.IndexOf("waiting", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("health", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Tunnel process reported health-wait activity.";
            if (value.IndexOf("stopped", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("shutdown", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Tunnel process reported shutdown activity.";
            return null;
        }

        private static string NormalizeSource(string source)
        {
            if (String.Equals(source, "manager", StringComparison.OrdinalIgnoreCase)) return "manager";
            if (String.Equals(source, "connector", StringComparison.OrdinalIgnoreCase)) return "connector";
            if (String.Equals(source, "tunnel", StringComparison.OrdinalIgnoreCase)) return "tunnel";
            return "other";
        }
    }
}
