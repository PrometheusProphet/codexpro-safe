using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

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
                DiagnosticHelperTrust.SealInstalledPackage(settings, System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);

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
                string syntheticPipe = "codexpro-safe-diagnostic-0123456789abcdef0123456789abcdef";
                string syntheticGate = "codexpro-safe-diagnostic-gate-0123456789abcdef0123456789abcdef";
                System.Collections.Generic.IDictionary<string, string> helperEnvironment = ProcessSupervisor.BuildConnectorEnvironment(settings, syntheticPipe, syntheticGate);
                Assert(helperEnvironment.Count == 2 && helperEnvironment["CODEXPRO_DIAGNOSTIC_MANAGER_PIPE"] == syntheticPipe &&
                    helperEnvironment["CODEXPRO_DIAGNOSTIC_MANAGER_GATE"] == syntheticGate, "manager proof environment locators only");
                Assert(!helperEnvironment.ContainsKey("CODEXPRO_DIAGNOSTIC_HELPER_PATH"), "helper path not transported by environment");
                using (DiagnosticHelperLock helperLock = DiagnosticHelperTrust.OpenVerifiedLock(settings, System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName))
                {
                    Assert(helperLock.Length > 0, "helper verified lock");
                    DiagnosticLaunchProofSelfTest.Run(settings, System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
                    System.Diagnostics.ProcessStartInfo helperStart = new System.Diagnostics.ProcessStartInfo();
                    helperStart.FileName = settings.DiagnosticHelperPath;
                    helperStart.Arguments = "--self-test";
                    helperStart.UseShellExecute = false;
                    helperStart.CreateNoWindow = true;
                    using (System.Diagnostics.Process helperProcess = System.Diagnostics.Process.Start(helperStart))
                    {
                        Assert(helperProcess.WaitForExit(15000) && helperProcess.ExitCode == 0, "helper launch while locked");
                    }
                }
                string sealedHash = settings.DiagnosticHelperSha256;
                settings.DiagnosticHelperSha256 = new string('0', 64);
                AssertThrows(delegate { using (DiagnosticHelperLock ignored = DiagnosticHelperTrust.OpenVerifiedLock(settings, System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName)) { } }, "helper fingerprint mismatch");
                settings.DiagnosticHelperSha256 = sealedHash;
                settings.DiagnosticHelperProtocolVersion = "wrong-protocol";
                AssertThrows(delegate { using (DiagnosticHelperLock ignored = DiagnosticHelperTrust.OpenVerifiedLock(settings, System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName)) { } }, "helper protocol mismatch");
                settings.DiagnosticHelperProtocolVersion = DiagnosticHelperTrust.ProtocolVersion;

                string reparseTest = Path.Combine(Path.GetTempPath(), "CodexProSafe.Manager.reparse." + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(reparseTest);
                try
                {
                    string managerDirectory = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
                    string parentJunction = Path.Combine(reparseTest, "package-link");
                    CreateJunction(parentJunction, managerDirectory);
                    AppSettings parentReparse = AppSettings.CreateDefaults();
                    parentReparse.DiagnosticHelperPath = Path.Combine(parentJunction, DiagnosticHelperTrust.HelperFileName);
                    parentReparse.DiagnosticHelperProtocolVersion = DiagnosticHelperTrust.ProtocolVersion;
                    parentReparse.DiagnosticHelperSha256 = sealedHash;
                    AssertThrows(delegate { using (DiagnosticHelperLock ignored = DiagnosticHelperTrust.OpenVerifiedLock(parentReparse, Path.Combine(parentJunction, "CodexProSafe.Manager.exe"))) { } }, "helper parent reparse rejection");
                    Directory.Delete(parentJunction);

                    string normalPackage = Path.Combine(reparseTest, "normal-package");
                    Directory.CreateDirectory(normalPackage);
                    string helperJunction = Path.Combine(normalPackage, DiagnosticHelperTrust.HelperFileName);
                    CreateJunction(helperJunction, managerDirectory);
                    AppSettings helperReparse = AppSettings.CreateDefaults();
                    helperReparse.DiagnosticHelperPath = helperJunction;
                    helperReparse.DiagnosticHelperProtocolVersion = DiagnosticHelperTrust.ProtocolVersion;
                    helperReparse.DiagnosticHelperSha256 = sealedHash;
                    AssertThrows(delegate { using (DiagnosticHelperLock ignored = DiagnosticHelperTrust.OpenVerifiedLock(helperReparse, Path.Combine(normalPackage, "CodexProSafe.Manager.exe"))) { } }, "helper object reparse rejection");
                    Directory.Delete(helperJunction);
                }
                finally
                {
                    if (Directory.Exists(reparseTest)) Directory.Delete(reparseTest, true);
                }

                string secret = "fake-redaction-secret-value-123456789";
                string sanitized = LogWriter.Sanitize("api_key=" + secret + " Authorization: Bearer " + secret);
                Assert(!sanitized.Contains(secret), "secret redaction");
                Assert(sanitized.Contains("<redacted>") || sanitized.Contains("<redacted-key>"), "redaction marker");

                OperationalPrivacySelfTest.Run(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);

                string reportPath = Path.Combine(Path.GetTempPath(), "CodexProSafe.Manager.self-test.txt");
                File.WriteAllText(reportPath, "PASS " + DateTimeOffset.Now.ToString("O"));
                return 0;
            }
            catch
            {
                string reportPath = Path.Combine(Path.GetTempPath(), "CodexProSafe.Manager.self-test.txt");
                File.WriteAllText(reportPath, "FAIL " + (OperationalPrivacySelfTest.LastStage ?? "core"));
                return 1;
            }
        }

        private static void Assert(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Self-test failed: " + name);
        }

        private static void AssertThrows(Action action, string name)
        {
            try { action(); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Self-test failed: " + name);
        }

        private static void CreateJunction(string junction, string target)
        {
            Directory.CreateDirectory(junction);
            using (SafeFileHandle handle = CreateFile(junction, 0x40000000, 0x00000001 | 0x00000002 | 0x00000004,
                IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new InvalidOperationException("Self-test could not create junction handle.");
                string substitute = @"\??\" + Path.GetFullPath(target);
                string print = Path.GetFullPath(target);
                byte[] substituteBytes = Encoding.Unicode.GetBytes(substitute);
                byte[] printBytes = Encoding.Unicode.GetBytes(print);
                int dataLength = 8 + substituteBytes.Length + 2 + printBytes.Length + 2;
                byte[] buffer = new byte[8 + dataLength];
                Array.Copy(BitConverter.GetBytes(0xA0000003u), 0, buffer, 0, 4);
                Array.Copy(BitConverter.GetBytes((ushort)dataLength), 0, buffer, 4, 2);
                Array.Copy(BitConverter.GetBytes((ushort)0), 0, buffer, 8, 2);
                Array.Copy(BitConverter.GetBytes((ushort)substituteBytes.Length), 0, buffer, 10, 2);
                Array.Copy(BitConverter.GetBytes((ushort)(substituteBytes.Length + 2)), 0, buffer, 12, 2);
                Array.Copy(BitConverter.GetBytes((ushort)printBytes.Length), 0, buffer, 14, 2);
                Array.Copy(substituteBytes, 0, buffer, 16, substituteBytes.Length);
                Array.Copy(printBytes, 0, buffer, 18 + substituteBytes.Length, printBytes.Length);
                uint returned;
                if (!DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out returned, IntPtr.Zero))
                    throw new InvalidOperationException("Self-test could not create junction.");
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out uint returned, IntPtr overlapped);
    }
}
