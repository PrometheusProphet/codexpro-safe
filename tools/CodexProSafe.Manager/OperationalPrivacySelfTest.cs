using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;

namespace CodexProSafeManager
{
    internal static class OperationalPrivacySelfTest
    {
        internal static string LastStage { get; private set; }

        internal static void Run(string managerExecutable)
        {
            LastStage = "privacy-start";
            string root = Path.Combine(Path.GetTempPath(), "CodexProSafe.Manager.privacy." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                RunCommandAndSettingsTests(root, managerExecutable);
                RunLogAndAccessibilityTests(root);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private static void RunCommandAndSettingsTests(string root, string managerExecutable)
        {
            LastStage = "privacy-settings-prepare";
            const string syntheticSecret = "synthetic-private-value-987654321";
            const string syntheticOrganization = "org_synthetic_private_987654321";
            const string syntheticProfile = "synthetic-private-profile";
            const string futureValue = "future-synthetic-value";
            AppSettings settings = AppSettings.CreateDefaults();
            settings.RepositoryPath = @"C:\synthetic\repository";
            settings.WorkspaceRoot = @"C:\synthetic\workspace";
            settings.AllowedRoot = @"C:\synthetic";
            settings.NodePath = @"C:\synthetic\node.exe";
            settings.TunnelClientPath = @"C:\synthetic\tunnel-client.exe";
            settings.TunnelProfile = syntheticProfile;
            settings.ControlPlaneApiKey = syntheticSecret;
            settings.OrganizationId = syntheticOrganization;
            settings.StartWithWindows = true;
            settings.StartMinimized = true;
            settings.AutoStartServices = true;
            settings.RestartOnFailure = false;
            settings.CodexDiagnosticReadMode = "off";
            DiagnosticHelperTrust.SealInstalledPackage(settings, managerExecutable);

            string settingsRoot = Path.Combine(root, "settings");
            SecureSettingsStore store = new SecureSettingsStore(settingsRoot, false);
            store.SaveSyntheticForSelfTest(
                settings,
                new Dictionary<string, object> { { "FutureSyntheticSetting", futureValue } });
            IDictionary<string, object> beforeValues = SnapshotExceptMode(store.LoadExisting());
            AccessControlSections securitySections = AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
            byte[] beforeSecurity = File.GetAccessControl(store.SettingsPath, securitySections)
                .GetSecurityDescriptorBinaryForm();

            StringWriter modeOutput = new StringWriter();
            LastStage = "privacy-mode-update";
            int modeExit = OperationalCommands.SetCodexDiagnostics(
                new[] { "--set-codex-diagnostics", "read" },
                store,
                managerExecutable,
                modeOutput,
                new SyntheticManagerExclusiveLeaseProvider(),
                delegate(Exception exception) { LastStage = "privacy-mode-" + ClassifyModeFailure(exception); });
            Assert(modeExit == OperationalCommands.SuccessExitCode, "mode command success");
            LastStage = "privacy-mode-output";
            Assert(modeOutput.ToString().Trim() ==
                "{\"schema\":\"codexpro-manager-command-v1\",\"command\":\"set-codex-diagnostics\",\"status\":\"ok\",\"mode\":\"read\",\"restartRequired\":true}",
                "mode command fixed output");
            LastStage = "privacy-mode-redaction";
            AssertSafeOutput(
                modeOutput.ToString(), syntheticSecret, syntheticOrganization, syntheticProfile,
                settings.RepositoryPath, settings.WorkspaceRoot, settings.NodePath,
                settings.DiagnosticHelperPath, settings.DiagnosticHelperSha256);

            LastStage = "privacy-mode-load";
            AppSettings updated = store.LoadExisting();
            Assert(updated.CodexDiagnosticReadMode == "read", "mode changed only");
            LastStage = "privacy-mode-preserve";
            AssertEquivalent(beforeValues, SnapshotExceptMode(updated), "settings preserved");
            LastStage = "privacy-mode-unknown";
            Assert(store.SyntheticPropertyEqualsForSelfTest("FutureSyntheticSetting", futureValue), "unknown setting preserved");
            LastStage = "privacy-mode-acl-read";
            byte[] afterSecurity = File.GetAccessControl(store.SettingsPath, securitySections)
                .GetSecurityDescriptorBinaryForm();
            LastStage = "privacy-mode-acl-compare";
            Assert(EqualBytes(beforeSecurity, afterSecurity), "settings security descriptor preserved");
            LastStage = "privacy-mode-temp";
            AssertNoTemporaryFiles(settingsRoot);
            LastStage = "privacy-mode-ciphertext";
            Assert(!ContainsBytes(File.ReadAllBytes(store.SettingsPath), Encoding.UTF8.GetBytes(syntheticSecret)), "settings ciphertext hides secret");

            string unchangedHash = HashFile(store.SettingsPath);
            LastStage = "privacy-invalid-mode";
            StringWriter invalidOutput = new StringWriter();
            int invalidExit = OperationalCommands.SetCodexDiagnostics(
                new[] { "--set-codex-diagnostics", "unsafe" },
                store,
                managerExecutable,
                invalidOutput,
                new SyntheticManagerExclusiveLeaseProvider());
            Assert(invalidExit == OperationalCommands.InvalidRequestExitCode, "invalid mode rejected");
            Assert(HashFile(store.SettingsPath) == unchangedHash, "invalid mode made no change");
            AssertSafeOutput(
                invalidOutput.ToString(), syntheticSecret, syntheticOrganization, syntheticProfile,
                settings.RepositoryPath, settings.DiagnosticHelperPath, settings.DiagnosticHelperSha256);
            AssertNoTemporaryFiles(settingsRoot);

            FakeManagerStatusProbe probe = new FakeManagerStatusProbe
            {
                Observation = new ManagerStatusObservation
                {
                    HelperTrust = "sealed",
                    ConnectorLocalHealthy = true,
                    TunnelLocalProcessHealthy = true,
                    TunnelAuthenticatedReady = false,
                    RestartRequired = true
                }
            };
            string syntheticLogDirectory = Path.Combine(settingsRoot, "logs");
            LastStage = "privacy-safe-status";
            Directory.CreateDirectory(syntheticLogDirectory);
            string lockedLog = Path.Combine(syntheticLogDirectory, "manager.log");
            using (FileStream logLock = new FileStream(lockedLog, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                byte[] sentinel = Encoding.UTF8.GetBytes("synthetic locked log");
                logLock.Write(sentinel, 0, sentinel.Length);
                logLock.Flush();
                string statusHashBefore = HashFile(store.SettingsPath);
                StringWriter statusOutput = new StringWriter();
                int statusExit = OperationalCommands.SafeStatus(
                    new[] { "--safe-status" },
                    store,
                    managerExecutable,
                    probe,
                    statusOutput);
                Assert(statusExit == OperationalCommands.SuccessExitCode, "safe status completed");
                Assert(probe.CallCount == 1, "safe status used bounded probe once");
                Assert(HashFile(store.SettingsPath) == statusHashBefore, "safe status did not mutate settings");
                AssertSafeStatusSchema(statusOutput.ToString(), false, true);
                AssertSafeOutput(
                    statusOutput.ToString(), syntheticSecret, syntheticOrganization, syntheticProfile,
                    settings.RepositoryPath, settings.DiagnosticHelperPath, settings.DiagnosticHelperSha256);
            }
            Assert(LocalHealthProbes.ParseConnectorDiagnosticMode(
                "{\"schema\":\"codexpro-manager-connector-status-v1\",\"diagnosticMode\":\"read\"}") == "read",
                "connector effective mode fixed schema");
            Assert(LocalHealthProbes.ParseConnectorDiagnosticMode(
                "{\"schema\":\"codexpro-manager-connector-status-v1\",\"diagnosticMode\":\"read\",\"extra\":true}") == "unavailable",
                "connector effective mode rejects extra fields");
            bool boundedRejected = false;
            try
            {
                LocalHealthProbes.ReadBoundedStreamForSelfTest(new MemoryStream(new byte[17]), 16);
            }
            catch (InvalidOperationException) { boundedRejected = true; }
            Assert(boundedRejected, "safe status response bound enforced");
            bool abortCalled = false;
            bool deadlineRejected = false;
            TaskCompletionSource<string> stalledResponse = new TaskCompletionSource<string>();
            try
            {
                LocalHealthProbes.AwaitWithAbortDeadlineForSelfTest(
                    stalledResponse.Task,
                    delegate { abortCalled = true; },
                    25).GetAwaiter().GetResult();
            }
            catch (TimeoutException) { deadlineRejected = true; }
            Assert(deadlineRejected && abortCalled, "safe status stalled response deadline enforced");

            byte[] validCiphertext = File.ReadAllBytes(store.SettingsPath);
            LastStage = "privacy-failure-cleanup";
            File.WriteAllBytes(store.SettingsPath, Encoding.UTF8.GetBytes("synthetic corrupt ciphertext"));
            StringWriter failureOutput = new StringWriter();
            int failureExit = OperationalCommands.SetCodexDiagnostics(
                new[] { "--set-codex-diagnostics", "off" },
                store,
                managerExecutable,
                failureOutput,
                new SyntheticManagerExclusiveLeaseProvider());
            Assert(failureExit == OperationalCommands.UpdateUnavailableExitCode, "settings failure closed");
            Assert(failureOutput.ToString().Trim() ==
                "{\"schema\":\"codexpro-manager-command-v1\",\"command\":\"set-codex-diagnostics\",\"status\":\"unavailable\",\"mode\":\"unavailable\",\"restartRequired\":false}",
                "settings failure fixed output");
            AssertNoTemporaryFiles(settingsRoot);
            File.WriteAllBytes(store.SettingsPath, validCiphertext);
            Array.Clear(validCiphertext, 0, validCiphertext.Length);
        }

        private static void RunLogAndAccessibilityTests(string root)
        {
            LastStage = "privacy-log-policy";
            const string secret = "synthetic-secret-value-123456789";
            const string tunnelId = "tunnel_syntheticopaque123456789";
            const string requestId = "req_syntheticopaque123456789";
            const string sessionId = "session_syntheticopaque123456789";
            const string traceId = "trace_syntheticopaque123456789";
            const string organization = "org_syntheticopaque123456789";
            const string uuid = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
            const string traceParent = "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbb-01";
            string privateUrl = "https://synthetic-user:synthetic-pass@example.invalid/path?token=" + secret;
            string connectorLine =
                "{\"authorization\":\"Bearer " + secret + "\"} tunnel_id=" + tunnelId + " request_id=" + requestId +
                " session_id=" + sessionId + " trace_id=" + traceId + " organization_id=" + organization +
                " correlation=" + uuid + " traceparent=" + traceParent + " url=" + privateUrl + @" path=C:\synthetic\private\file.txt";
            string prepared = LogWriter.Prepare("connector", connectorLine);
            Assert(!String.IsNullOrEmpty(prepared), "connector log retained after redaction");
            AssertSafeOutput(prepared, secret, tunnelId, requestId, sessionId, traceId, organization, uuid, traceParent, privateUrl, @"C:\synthetic\private\file.txt");
            Assert(prepared.Contains("<redacted"), "connector log redaction marker");

            string tunnelPayload = "{\"request_id\":\"" + requestId + "\",\"tunnel_id\":\"" + tunnelId + "\"}";
            Assert(LogWriter.Prepare("tunnel", tunnelPayload) == null, "raw tunnel payload suppressed");
            Assert(LogWriter.Prepare("tunnel", "Process exited.") == "Process exited.", "tunnel exit summary retained");
            Assert(LogWriter.Prepare("tunnel", "authenticated and ready") == "Tunnel reported authenticated readiness.", "tunnel readiness summary retained");
            Assert(LogWriter.Prepare("manager", "Tunnel is authenticated and ready.") == "Tunnel is authenticated and ready.", "manager lifecycle message retained");

            string logPath = Path.Combine(root, "synthetic-manager.log");
            LogWriter.AppendPreparedForSelfTest(logPath, "connector", prepared);
            string persisted = File.ReadAllText(logPath);
            AssertSafeOutput(persisted, secret, tunnelId, requestId, sessionId, traceId, organization, uuid, traceParent, privateUrl, @"C:\synthetic\private\file.txt");

            using (PrivateLogView view = new PrivateLogView())
            using (Button restart = new Button { Text = "Restart All" })
            {
                LastStage = "privacy-accessibility";
                view.AppendLine(prepared);
                AssertSafeOutput(view.RenderedTextForSelfTest, secret, tunnelId, requestId, sessionId, traceId, organization, uuid, traceParent, privateUrl, @"C:\synthetic\private\file.txt");
                Assert(view.AccessibilityObject.Name == "Sanitized lifecycle activity", "log accessible name fixed");
                Assert(String.IsNullOrEmpty(view.AccessibilityObject.Value), "log accessible value empty");
                Assert(view.AccessibilityObject.GetChildCount() == 0, "log accessible children empty");
                Assert(!view.AccessibilityObject.Description.Contains(prepared), "log accessible description fixed");
                Assert(restart.AccessibilityObject.Name == "Restart All", "important controls remain accessible");

                view.CreateControl();
                restart.CreateControl();
                AutomationElement logElement = AutomationElement.FromHandle(view.Handle);
                AutomationElement restartElement = AutomationElement.FromHandle(restart.Handle);
                Assert(logElement != null, "log UI Automation element available");
                Assert(restartElement != null, "control UI Automation element available");
                Assert(logElement.Current.Name == "Sanitized lifecycle activity", "log UI Automation name fixed");
                object valuePattern;
                object textPattern;
                Assert(!logElement.TryGetCurrentPattern(ValuePattern.Pattern, out valuePattern), "log UI Automation value unavailable");
                Assert(!logElement.TryGetCurrentPattern(TextPattern.Pattern, out textPattern), "log UI Automation text unavailable");
                AutomationElementCollection descendants = logElement.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                Assert(descendants.Count == 0, "log UI Automation descendants empty");
                Assert(restartElement.Current.Name == "Restart All", "important control UI Automation name retained");
            }
        }

        private static void AssertSafeStatusSchema(string json, bool expectedAuthenticated, bool expectedRestart)
        {
            IDictionary<string, object> values = new JavaScriptSerializer().DeserializeObject(json) as IDictionary<string, object>;
            Assert(values != null && values.Count == 9, "safe status fixed field count");
            string[] names = new[]
            {
                "schema", "savedDiagnosticMode", "installedHelperTrust", "connectorLocalHealthy",
                "tunnelLocalProcessHealthy", "tunnelAuthenticatedReady", "restartRequired", "overall", "limitation"
            };
            foreach (string name in names) Assert(values.ContainsKey(name), "safe status field " + name);
            Assert(Convert.ToBoolean(values["tunnelLocalProcessHealthy"]), "local tunnel health represented");
            Assert(Convert.ToBoolean(values["tunnelAuthenticatedReady"]) == expectedAuthenticated, "authenticated readiness represented");
            Assert(Convert.ToBoolean(values["restartRequired"]) == expectedRestart, "restart requirement represented");
            Assert(Convert.ToString(values["limitation"]) == "tunnel_not_authenticated", "status limitation fixed enum");
        }

        private static IDictionary<string, object> SnapshotExceptMode(AppSettings settings)
        {
            Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (PropertyInfo property in typeof(AppSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.Name == "CodexDiagnosticReadMode") continue;
                values[property.Name] = property.GetValue(settings, null);
            }
            return values;
        }

        private static void AssertEquivalent(IDictionary<string, object> expected, IDictionary<string, object> actual, string name)
        {
            Assert(expected.Count == actual.Count, name + " count");
            foreach (KeyValuePair<string, object> pair in expected)
            {
                object found;
                Assert(actual.TryGetValue(pair.Key, out found) && Object.Equals(pair.Value, found), name + " " + pair.Key);
            }
        }

        private static void AssertSafeOutput(string value, params string[] forbidden)
        {
            foreach (string item in forbidden)
            {
                if (!String.IsNullOrEmpty(item)) Assert(value.IndexOf(item, StringComparison.OrdinalIgnoreCase) < 0, "private value absent");
            }
            Assert(value.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) < 0, "exception body absent");
        }

        private static void AssertNoTemporaryFiles(string directory)
        {
            Assert(Directory.GetFiles(directory, ".settings-*.tmp", SearchOption.TopDirectoryOnly).Length == 0, "no settings temp files");
        }

        private static string HashFile(string path)
        {
            using (SHA256 algorithm = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
                return Convert.ToBase64String(algorithm.ComputeHash(stream));
        }

        private static string ClassifyModeFailure(Exception exception)
        {
            Exception current = exception;
            while (current != null)
            {
                if (current is CryptographicException) return "crypto";
                if (current is UnauthorizedAccessException) return "access";
                if (current is IOException) return "io";
                if (current is System.ComponentModel.Win32Exception) return "win32";
                if (current is PlatformNotSupportedException || current is NotSupportedException) return "platform";
                if (current is ArgumentException) return "argument";
                if (current is InvalidOperationException)
                {
                    if (current.Message.IndexOf("diagnostic helper", StringComparison.OrdinalIgnoreCase) >= 0) return "trust";
                    if (current.Message.IndexOf("security descriptor", StringComparison.OrdinalIgnoreCase) >= 0) return "acl";
                    if (current.Message.IndexOf("settings are busy", StringComparison.OrdinalIgnoreCase) >= 0) return "busy";
                }
                current = current.InnerException;
            }
            return exception is InvalidOperationException ? "validation" : "unexpected";
        }

        private static bool ContainsBytes(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0 || haystack.Length < needle.Length) return false;
            for (int index = 0; index <= haystack.Length - needle.Length; index++)
            {
                int matched = 0;
                while (matched < needle.Length && haystack[index + matched] == needle[matched]) matched++;
                if (matched == needle.Length) return true;
            }
            return false;
        }

        private static bool EqualBytes(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            int difference = 0;
            for (int index = 0; index < left.Length; index++) difference |= left[index] ^ right[index];
            return difference == 0;
        }

        private static void Assert(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Operational privacy self-test failed: " + name);
        }

        private sealed class FakeManagerStatusProbe : IManagerStatusProbe
        {
            internal ManagerStatusObservation Observation { get; set; }
            internal int CallCount { get; private set; }

            public ManagerStatusObservation Observe(AppSettings settings, string managerExecutable)
            {
                CallCount++;
                return Observation;
            }
        }

        private sealed class SyntheticManagerExclusiveLeaseProvider : IManagerExclusiveLeaseProvider
        {
            public IDisposable Acquire() { return new SyntheticLease(); }

            private sealed class SyntheticLease : IDisposable
            {
                public void Dispose() { }
            }
        }
    }
}
