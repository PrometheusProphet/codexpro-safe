using System;
using System.IO;
using System.Text.RegularExpressions;

namespace CodexProSafeManager
{
    internal static class LogWriter
    {
        private static readonly object Gate = new object();
        private static readonly Regex Bearer = new Regex(
            @"(?i)(authorization\s*[:=]\s*bearer\s+)[^\s""']+",
            RegexOptions.Compiled);
        private static readonly Regex SecretField = new Regex(
            @"(?i)((?:api[_-]?key|token|secret)\s*[""']?\s*[:=]\s*[""']?)[^,\s""'}]+",
            RegexOptions.Compiled);
        private static readonly Regex OpenAiKey = new Regex(
            @"\b(?:sk|sess)-[A-Za-z0-9_-]{12,}\b",
            RegexOptions.Compiled);

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

        public static string Sanitize(string text)
        {
            if (String.IsNullOrEmpty(text)) return String.Empty;
            string sanitized = Bearer.Replace(text, "$1<redacted>");
            sanitized = SecretField.Replace(sanitized, "$1<redacted>");
            sanitized = OpenAiKey.Replace(sanitized, "<redacted-key>");
            return sanitized;
        }

        public static void Append(string source, string message)
        {
            string line = String.Format(
                "{0:O} [{1}] {2}",
                DateTimeOffset.Now,
                source,
                Sanitize(message).TrimEnd());
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                File.AppendAllText(CurrentLogPath, line + Environment.NewLine);
            }
        }
    }
}
