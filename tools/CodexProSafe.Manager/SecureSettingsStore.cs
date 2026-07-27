using Microsoft.Win32;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexProSafeManager
{
    internal sealed class SecureSettingsStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CodexProSafe.Manager.settings.v1");
        private readonly string settingsDirectory;
        private readonly string settingsPath;

        public SecureSettingsStore()
        {
            settingsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexProSafe Manager");
            settingsPath = Path.Combine(settingsDirectory, "settings.dat");
        }

        public string SettingsPath { get { return settingsPath; } }

        public AppSettings Load()
        {
            if (!File.Exists(settingsPath)) return AppSettings.CreateDefaults();
            try
            {
                byte[] encrypted = File.ReadAllBytes(settingsPath);
                byte[] clear = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                string json = Encoding.UTF8.GetString(clear);
                AppSettings settings = new JavaScriptSerializer().Deserialize<AppSettings>(json);
                if (settings == null) settings = AppSettings.CreateDefaults();
                settings.ApplyMissingDefaults();
                Array.Clear(clear, 0, clear.Length);
                return settings;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "The encrypted manager settings could not be read. Delete " + settingsPath +
                    " to reset them. Details: " + exception.Message, exception);
            }
        }

        public void Save(AppSettings settings, string executablePath)
        {
            Directory.CreateDirectory(settingsDirectory);
            string json = new JavaScriptSerializer().Serialize(settings);
            byte[] clear = Encoding.UTF8.GetBytes(json);
            byte[] encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(settingsPath, encrypted);
            Array.Clear(clear, 0, clear.Length);
            ApplyStartupPreference(settings.StartWithWindows, executablePath);
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
    }
}
