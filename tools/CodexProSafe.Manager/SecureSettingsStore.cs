using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexProSafeManager
{
    internal sealed class SecureSettingsStore
    {
        private const AccessControlSections PreservedSecuritySections =
            AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
        private const string SettingsMutexName = @"Local\CodexProSafe.Manager.Settings";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CodexProSafe.Manager.settings.v1");
        private readonly string settingsDirectory;
        private readonly string settingsPath;
        private readonly bool manageStartupPreference;

        public SecureSettingsStore()
            : this(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexProSafe Manager"), true)
        {
        }

        internal SecureSettingsStore(string directory, bool manageStartup)
        {
            settingsDirectory = Path.GetFullPath(directory);
            settingsPath = Path.Combine(settingsDirectory, "settings.dat");
            manageStartupPreference = manageStartup;
        }

        public string SettingsPath { get { return settingsPath; } }
        public bool Exists { get { return File.Exists(settingsPath); } }

        public AppSettings Load()
        {
            if (!File.Exists(settingsPath)) return AppSettings.CreateDefaults();
            return LoadExisting();
        }

        public AppSettings LoadExisting()
        {
            if (!File.Exists(settingsPath))
                throw new InvalidOperationException("The encrypted manager settings are unavailable.");
            byte[] encrypted = null;
            byte[] clear = null;
            try
            {
                encrypted = File.ReadAllBytes(settingsPath);
                clear = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                string json = Encoding.UTF8.GetString(clear);
                AppSettings settings = new JavaScriptSerializer().Deserialize<AppSettings>(json);
                if (settings == null) throw new InvalidOperationException("The encrypted manager settings are invalid.");
                settings.ApplyMissingDefaults();
                if (settings.CodexDiagnosticReadMode != "off" && settings.CodexDiagnosticReadMode != "read")
                    throw new InvalidOperationException("The encrypted manager settings are invalid.");
                return settings;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("The encrypted manager settings could not be read.", exception);
            }
            finally
            {
                Clear(encrypted);
                Clear(clear);
            }
        }

        public void Save(AppSettings settings, string executablePath)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            string json = new JavaScriptSerializer().Serialize(settings);
            using (AcquireSettingsLock()) WriteEncryptedJsonAtomically(json);
            if (manageStartupPreference) ApplyStartupPreference(settings.StartWithWindows, executablePath);
        }

        public void UpdateCodexDiagnosticMode(string mode, string managerExecutable)
        {
            if (mode != "off" && mode != "read") throw new InvalidOperationException("Unsupported diagnostic mode.");
            if (!File.Exists(settingsPath)) throw new InvalidOperationException("The encrypted manager settings are unavailable.");

            byte[] encrypted = null;
            byte[] clear = null;
            try
            {
                using (AcquireSettingsLock())
                {
                    encrypted = File.ReadAllBytes(settingsPath);
                    clear = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                    string json = Encoding.UTF8.GetString(clear);
                    JavaScriptSerializer serializer = new JavaScriptSerializer();
                    IDictionary<string, object> values = serializer.DeserializeObject(json) as IDictionary<string, object>;
                    AppSettings validation = serializer.Deserialize<AppSettings>(json);
                    if (values == null || validation == null)
                        throw new InvalidOperationException("The encrypted manager settings are invalid.");
                    validation.ApplyMissingDefaults();
                    if (validation.CodexDiagnosticReadMode != "off" && validation.CodexDiagnosticReadMode != "read")
                        throw new InvalidOperationException("The encrypted manager settings are invalid.");

                    if (mode == "read")
                    {
                        using (DiagnosticHelperLock ignored = DiagnosticHelperTrust.OpenVerifiedLock(validation, managerExecutable))
                        {
                            values["CodexDiagnosticReadMode"] = mode;
                            WriteEncryptedJsonAtomically(serializer.Serialize(values));
                        }
                    }
                    else
                    {
                        values["CodexDiagnosticReadMode"] = mode;
                        WriteEncryptedJsonAtomically(serializer.Serialize(values));
                    }
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("The encrypted manager settings could not be updated.", exception);
            }
            finally
            {
                Clear(encrypted);
                Clear(clear);
            }
        }

        internal void SaveSyntheticForSelfTest(AppSettings settings, IDictionary<string, object> additionalValues)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            IDictionary<string, object> values = serializer.DeserializeObject(serializer.Serialize(settings)) as IDictionary<string, object>;
            if (values == null) throw new InvalidOperationException("Synthetic settings could not be prepared.");
            if (additionalValues != null)
            {
                foreach (KeyValuePair<string, object> pair in additionalValues) values[pair.Key] = pair.Value;
            }
            using (AcquireSettingsLock()) WriteEncryptedJsonAtomically(serializer.Serialize(values));
        }

        internal bool SyntheticPropertyEqualsForSelfTest(string name, string expected)
        {
            byte[] encrypted = null;
            byte[] clear = null;
            try
            {
                encrypted = File.ReadAllBytes(settingsPath);
                clear = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                IDictionary<string, object> values = new JavaScriptSerializer().DeserializeObject(Encoding.UTF8.GetString(clear)) as IDictionary<string, object>;
                object value;
                return values != null && values.TryGetValue(name, out value) && String.Equals(Convert.ToString(value), expected, StringComparison.Ordinal);
            }
            finally
            {
                Clear(encrypted);
                Clear(clear);
            }
        }

        private void WriteEncryptedJsonAtomically(string json)
        {
            Directory.CreateDirectory(settingsDirectory);
            byte[] clear = null;
            byte[] encrypted = null;
            string temporaryPath = Path.Combine(settingsDirectory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
            FileSecurity existingSecurity = null;
            try
            {
                if (File.Exists(settingsPath))
                {
                    existingSecurity = File.GetAccessControl(settingsPath, PreservedSecuritySections);
                }

                clear = Encoding.UTF8.GetBytes(json);
                encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
                using (FileStream stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                {
                    stream.Write(encrypted, 0, encrypted.Length);
                    stream.Flush(true);
                }

                if (existingSecurity != null) ApplyPreservedSecurity(temporaryPath, existingSecurity);
                if (File.Exists(settingsPath)) File.Replace(temporaryPath, settingsPath, null, false);
                else File.Move(temporaryPath, settingsPath);

                if (existingSecurity != null)
                {
                    FileSecurity actualSecurity = File.GetAccessControl(settingsPath, PreservedSecuritySections);
                    if (!SecurityEquivalent(existingSecurity, actualSecurity))
                    {
                        ApplyPreservedSecurity(settingsPath, existingSecurity);
                        actualSecurity = File.GetAccessControl(settingsPath, PreservedSecuritySections);
                        if (!SecurityEquivalent(existingSecurity, actualSecurity))
                            throw new InvalidOperationException(
                                "The encrypted manager settings security descriptor could not be preserved: " +
                                SecurityDifference(existingSecurity, actualSecurity) + ".");
                    }
                }
            }
            finally
            {
                Clear(clear);
                Clear(encrypted);
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch { }
            }
        }

        internal static bool SecurityEquivalent(FileSecurity expected, FileSecurity actual)
        {
            return SecurityDifference(expected, actual) == "none";
        }

        internal static string SecurityDifference(FileSecurity expected, FileSecurity actual)
        {
            if (expected == null || actual == null) return "missing";
            SecurityIdentifier expectedOwner = expected.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            SecurityIdentifier actualOwner = actual.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            SecurityIdentifier expectedGroup = expected.GetGroup(typeof(SecurityIdentifier)) as SecurityIdentifier;
            SecurityIdentifier actualGroup = actual.GetGroup(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (!Object.Equals(expectedOwner, actualOwner)) return "owner";
            if (!Object.Equals(expectedGroup, actualGroup)) return "group";
            if (expected.AreAccessRulesProtected != actual.AreAccessRulesProtected) return "protection";

            List<string> expectedRules = AccessRuleSignatures(expected);
            List<string> actualRules = AccessRuleSignatures(actual);
            if (expectedRules == null || actualRules == null) return "invalid-rule";
            if (expectedRules.Count != actualRules.Count) return "rule-count";
            for (int index = 0; index < expectedRules.Count; index++)
                if (!String.Equals(expectedRules[index], actualRules[index], StringComparison.Ordinal)) return "rule-set";
            return "none";
        }

        private static void ApplyPreservedSecurity(string path, FileSecurity expected)
        {
            FileSecurity replacement = File.GetAccessControl(path, PreservedSecuritySections);
            replacement.SetOwner(expected.GetOwner(typeof(SecurityIdentifier)));
            replacement.SetGroup(expected.GetGroup(typeof(SecurityIdentifier)));
            replacement.SetAccessRuleProtection(expected.AreAccessRulesProtected, false);

            AuthorizationRuleCollection replacementRules = replacement.GetAccessRules(true, false, typeof(SecurityIdentifier));
            foreach (AuthorizationRule authorizationRule in replacementRules)
            {
                FileSystemAccessRule rule = authorizationRule as FileSystemAccessRule;
                if (rule == null) throw new InvalidOperationException("The replacement settings ACL contained an unsupported rule.");
                replacement.RemoveAccessRuleSpecific(rule);
            }

            AuthorizationRuleCollection expectedRules = expected.GetAccessRules(true, false, typeof(SecurityIdentifier));
            foreach (AuthorizationRule authorizationRule in expectedRules)
            {
                FileSystemAccessRule rule = authorizationRule as FileSystemAccessRule;
                if (rule == null) throw new InvalidOperationException("The existing settings ACL contained an unsupported rule.");
                replacement.AddAccessRule(rule);
            }
            File.SetAccessControl(path, replacement);
        }

        private static List<string> AccessRuleSignatures(FileSecurity security)
        {
            Dictionary<string, int> effectiveRights = new Dictionary<string, int>(StringComparer.Ordinal);
            AuthorizationRuleCollection rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier));
            foreach (AuthorizationRule authorizationRule in rules)
            {
                FileSystemAccessRule rule = authorizationRule as FileSystemAccessRule;
                SecurityIdentifier identity = rule == null ? null : rule.IdentityReference as SecurityIdentifier;
                if (rule == null || identity == null) return null;
                string key = String.Join("|", new[]
                {
                    identity.Value,
                    rule.AccessControlType.ToString(),
                    rule.InheritanceFlags.ToString(),
                    rule.PropagationFlags.ToString(),
                    rule.IsInherited ? "inherited" : "explicit"
                });
                int existing;
                effectiveRights.TryGetValue(key, out existing);
                effectiveRights[key] = existing | (int)rule.FileSystemRights;
            }
            List<string> values = new List<string>();
            foreach (KeyValuePair<string, int> pair in effectiveRights)
                values.Add(pair.Key + "|" + pair.Value.ToString());
            values.Sort(StringComparer.Ordinal);
            return values;
        }

        private static IDisposable AcquireSettingsLock()
        {
            Mutex mutex = new Mutex(false, SettingsMutexName);
            bool acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(5000); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new InvalidOperationException("The encrypted manager settings are busy.");
                return new SettingsLock(mutex);
            }
            catch
            {
                if (!acquired) mutex.Dispose();
                throw;
            }
        }

        private static void Clear(byte[] value)
        {
            if (value != null) Array.Clear(value, 0, value.Length);
        }

        private static void ApplyStartupPreference(bool enabled, string executablePath)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (key == null) throw new InvalidOperationException("Windows startup registry key is unavailable.");
                const string valueName = "CodexProSafe Manager";
                if (enabled)
                    key.SetValue(valueName, "\"" + executablePath + "\" --startup", RegistryValueKind.String);
                else
                    key.DeleteValue(valueName, false);
            }
        }

        private sealed class SettingsLock : IDisposable
        {
            private Mutex mutex;

            internal SettingsLock(Mutex value) { mutex = value; }

            public void Dispose()
            {
                if (mutex == null) return;
                mutex.ReleaseMutex();
                mutex.Dispose();
                mutex = null;
            }
        }
    }
}
