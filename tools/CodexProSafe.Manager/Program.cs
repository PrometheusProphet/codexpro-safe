using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace CodexProSafeManager
{
    internal static class Program
    {
        internal const string MutexName = @"Local\CodexProSafe.Manager.Singleton";

        [STAThread]
        private static int Main(string[] args)
        {
            string executable = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            if (args.Length > 0 && String.Equals(args[0], "--set-codex-diagnostics", StringComparison.Ordinal))
                return OperationalCommands.SetCodexDiagnostics(
                    args, new SecureSettingsStore(), executable, Console.Out);
            if (args.Length > 0 && String.Equals(args[0], "--safe-status", StringComparison.Ordinal))
                return OperationalCommands.SafeStatus(
                    args, new SecureSettingsStore(), executable, new FixedManagerStatusProbe(), Console.Out);
            if (args.Length == 2 && String.Equals(args[0], "--diagnostic-launch-proof-client", StringComparison.Ordinal))
                return DiagnosticLaunchProofClient.Run(args[1], true, 3500);
            if (args.Length == 2 && String.Equals(args[0], "--diagnostic-launch-test-client", StringComparison.Ordinal))
                return DiagnosticLaunchProofClient.Run(args[1], false, 400);
            if (args.Length == 4 && String.Equals(args[0], "--diagnostic-launch-test-launcher", StringComparison.Ordinal))
                return DiagnosticLaunchProofSelfTest.RunLauncher(args[1], args[2], args[3], System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
            if (args.Length == 3 && String.Equals(args[0], "--diagnostic-launch-test-server", StringComparison.Ordinal))
                return DiagnosticLaunchProofSelfTest.RunServer(args[1], args[2], System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
            if (args.Length == 1 && String.Equals(args[0], "--diagnostic-launch-test-sleep", StringComparison.Ordinal))
            {
                System.Threading.Thread.Sleep(1500);
                return 0;
            }
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
