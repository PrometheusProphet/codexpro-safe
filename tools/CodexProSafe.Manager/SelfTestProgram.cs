using System;
using System.IO;

namespace CodexProSafeManager
{
    internal static class SelfTestProgram
    {
        public static int Run()
        {
            try
            {
                AppSettings settings = AppSettings.CreateDefaults();
                Assert(settings.CodexDiagnosticReadMode == "off", "diagnostic default");
                settings.RepositoryPath = @"C:\repo with spaces\codexpro-safe";
                settings.WorkspaceRoot = @"C:\Users\test\Projects";
                settings.AllowedRoot = @"C:\Users\test\Projects";
                settings.TunnelProfile = "codexpro-safe-local";
                settings.CodexDiagnosticReadMode = "read";

                string connector = ProcessSupervisor.BuildConnectorArguments(settings);
                Assert(connector.Contains("\"C:\\repo with spaces\\codexpro-safe\\scripts\\codexpro.mjs\""), "connector script quoting");
                Assert(connector.Contains("--mode handoff"), "handoff mode");
                Assert(connector.Contains("--bash off"), "bash mode");
                Assert(connector.Contains("--write handoff"), "write mode");
                Assert(connector.Contains("--codex-diagnostic-read read"), "diagnostic read mode");
                Assert(ProcessSupervisor.BuildTunnelArguments(settings) == "run --profile \"codexpro-safe-local\"", "tunnel profile");
                Assert(ProcessSupervisor.ContainsArgument(
                    "node scripts\\codexpro.mjs --root \"C:\\Users\\test\\Projects\" --allow-root \"C:\\Users\\test\\Projects\" --tunnel none --mode handoff --bash off --write handoff --codex-diagnostic-read read",
                    "--root",
                    @"C:\Users\test\Projects"), "takeover root matching");
                Assert(ProcessSupervisor.ContainsArgument(connector, "--codex-diagnostic-read", "read"), "takeover diagnostic matching");
                Assert(!ProcessSupervisor.ContainsArgument(connector, "--codex-diagnostic-read", "off"), "takeover diagnostic mismatch");

                string secret = "fake-redaction-secret-value-123456789";
                string sanitized = LogWriter.Sanitize("api_key=" + secret + " Authorization: Bearer " + secret);
                Assert(!sanitized.Contains(secret), "secret redaction");
                Assert(sanitized.Contains("<redacted>") || sanitized.Contains("<redacted-key>"), "redaction marker");

                string reportPath = Path.Combine(Path.GetTempPath(), "CodexProSafe.Manager.self-test.txt");
                File.WriteAllText(reportPath, "PASS " + DateTimeOffset.Now.ToString("O"));
                return 0;
            }
            catch (Exception exception)
            {
                string reportPath = Path.Combine(Path.GetTempPath(), "CodexProSafe.Manager.self-test.txt");
                File.WriteAllText(reportPath, "FAIL " + exception);
                return 1;
            }
        }

        private static void Assert(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Self-test failed: " + name);
        }
    }
}
