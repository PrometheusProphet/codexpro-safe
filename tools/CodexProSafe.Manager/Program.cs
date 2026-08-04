using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace CodexProSafeManager
{
    internal static class Program
    {
        private const string MutexName = @"Local\CodexProSafe.Manager.Singleton";

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Any(delegate(string value) { return String.Equals(value, "--self-test", StringComparison.OrdinalIgnoreCase); }))
                return SelfTestProgram.Run();
            if (args.Any(delegate(string value) { return String.Equals(value, "--seal-helper-trust", StringComparison.OrdinalIgnoreCase); }))
                return SealHelperTrust();

            bool created;
            using (Mutex mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    MessageBox.Show(
                        "CodexPro-Safe Manager is already running in the notification area.",
                        "CodexPro-Safe Manager",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                SecureSettingsStore store = new SecureSettingsStore();
                AppSettings settings;
                try
                {
                    settings = store.Load();
                }
                catch (Exception exception)
                {
                    MessageBox.Show(exception.Message, "Settings error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 2;
                }

                bool startup = args.Any(delegate(string value) { return String.Equals(value, "--startup", StringComparison.OrdinalIgnoreCase); });
                Application.Run(new MainForm(settings, store, startup));
                GC.KeepAlive(mutex);
                return 0;
            }
        }

        private static int SealHelperTrust()
        {
            try
            {
                SecureSettingsStore store = new SecureSettingsStore();
                AppSettings settings = store.Load();
                string executable = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                DiagnosticHelperTrust.SealInstalledPackage(settings, executable);
                store.Save(settings, executable);
                return 0;
            }
            catch { return 3; }
        }
    }
}
