using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
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
            byte[] expectedSecurity = null;
            try
            {
                if (File.Exists(settingsPath))
                {
                    existingSecurity = File.GetAccessControl(settingsPath, PreservedSecuritySections);
                    expectedSecurity = existingSecurity.GetSecurityDescriptorBinaryForm();
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

                if (existingSecurity != null) File.SetAccessControl(temporaryPath, existingSecurity);
                if (File.Exists(settingsPath)) File.Replace(temporaryPath, settingsPath, null, false);
                else File.Move(temporaryPath, settingsPath);

                if (expectedSecurity != null)
                {
                    byte[] actualSecurity = File.GetAccessControl(settingsPath, PreservedSecuritySections).GetSecurityDescriptorBinaryForm();
                    if (!EqualBytes(expectedSecurity, actualSecurity))
                    {
                        File.SetAccessControl(settingsPath, existingSecurity);
                        actualSecurity = File.GetAccessControl(settingsPath, PreservedSecuritySections).GetSecurityDescriptorBinaryForm();
                        if (!EqualBytes(expectedSecurity, actualSecurity))
                            throw new InvalidOperationException("The encrypted manager settings security descriptor could not be preserved.");
                    }
                }
            }
            finally
            {
                Clear(clear);
                Clear(encrypted);
                Clear(expectedSecurity);
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch { }
            }
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

        private static bool EqualBytes(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            int difference = 0;
            for (int index = 0; index < left.Length; index++) difference |= left[index] ^ right[index];
            return difference == 0;
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
