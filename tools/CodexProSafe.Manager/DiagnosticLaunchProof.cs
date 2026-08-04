using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexProSafeManager
{
    internal sealed class DiagnosticLaunchContract
    {
        public string protocol { get; set; }
        public string status { get; set; }
        public string instanceId { get; set; }
        public int managerPid { get; set; }
        public int launcherPid { get; set; }
        public int serverPid { get; set; }
        public int verifierPid { get; set; }
        public string issuedUtc { get; set; }
        public string expiresUtc { get; set; }
        public DiagnosticLaunchHelperContract helper { get; set; }
    }

    internal sealed class DiagnosticLaunchHelperContract
    {
        public string executablePath { get; set; }
        public string protocolVersion { get; set; }
        public string sha256 { get; set; }
    }

    internal sealed class DiagnosticLaunchBroker : IDisposable
    {
        internal const string ProtocolVersion = "codexpro-manager-launch-v1";
        private const int FrameLimit = 4096;
        private readonly NamedPipeServerStream server;
        private readonly NamedPipeServerStream gate;
        private readonly SafeFileHandle job;
        private readonly AppSettings settings;
        private readonly string managerExecutable;
        private readonly string nodeExecutable;
        private readonly string expectedServerScript;
        private readonly string launchCapability;
        private readonly DateTime expiresUtc;
        private readonly int ancestorDepth;
        private Process connector;
        private DateTime connectorStartUtc;
        private Thread worker;
        private Thread gateWorker;
        private bool disposed;

        internal DiagnosticLaunchBroker(
            AppSettings settings,
            string managerExecutable,
            string nodeExecutable,
            string repositoryPath,
            int lifetimeMilliseconds,
            int ancestorDepth)
        {
            if (settings == null || settings.CodexDiagnosticReadMode != "read")
                throw new InvalidOperationException("The diagnostic launch broker requires read mode.");
            if (ancestorDepth < 1 || ancestorDepth > 2)
                throw new InvalidOperationException("The diagnostic launch ancestry depth is invalid.");
            this.settings = settings;
            this.managerExecutable = Path.GetFullPath(managerExecutable);
            this.nodeExecutable = Path.GetFullPath(nodeExecutable);
            this.expectedServerScript = Path.GetFullPath(Path.Combine(repositoryPath, @"dist\http.js"));
            this.ancestorDepth = ancestorDepth;
            expiresUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(50, Math.Min(20000, lifetimeMilliseconds)));
            launchCapability = RandomHex(32);
            PipeName = "codexpro-safe-diagnostic-" + RandomHex(16);
            GateName = "codexpro-safe-diagnostic-gate-" + RandomHex(16);
            server = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.None,
                FrameLimit,
                FrameLimit);
            gate = new NamedPipeServerStream(
                GateName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.None,
                64,
                64);
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == null || job.IsInvalid)
            {
                if (job != null) job.Dispose();
                gate.Dispose();
                server.Dispose();
                throw new InvalidOperationException("The Manager diagnostic launch job could not be created.");
            }
        }

        public string PipeName { get; private set; }
        public string GateName { get; private set; }

        internal void BindConnector(Process value)
        {
            if (value == null || value.HasExited) throw new InvalidOperationException("The diagnostic launch connector is unavailable.");
            if (connector != null) throw new InvalidOperationException("The diagnostic launch broker is already bound.");
            connector = value;
            connectorStartUtc = value.StartTime.ToUniversalTime();
            if (!AssignProcessToJobObject(job, value.Handle))
                throw new InvalidOperationException("The connector could not be assigned to the Manager diagnostic launch job.");
            worker = new Thread(ServeOne);
            worker.IsBackground = true;
            worker.Name = "CodexProSafe diagnostic launch proof";
            worker.Start();
            gateWorker = new Thread(ReleaseGate);
            gateWorker.IsBackground = true;
            gateWorker.Name = "CodexProSafe diagnostic launch gate";
            gateWorker.Start();
        }

        private void ReleaseGate()
        {
            try
            {
                gate.WaitForConnection();
                if (disposed || DateTime.UtcNow > expiresUtc || connector == null) return;
                uint clientPid;
                if (!GetNamedPipeClientProcessId(gate.SafePipeHandle, out clientPid) || clientPid != connector.Id) return;
                using (Process current = Process.GetProcessById(connector.Id))
                {
                    if (current.HasExited || current.StartTime.ToUniversalTime() != connectorStartUtc || !IsJobMember(connector.Id)) return;
                }
                byte[] capability = Encoding.ASCII.GetBytes(launchCapability);
                gate.Write(capability, 0, capability.Length);
                gate.Flush();
            }
            catch { }
            finally
            {
                try { gate.Dispose(); }
                catch { }
            }
        }

        private void ServeOne()
        {
            try
            {
                server.WaitForConnection();
                if (disposed || DateTime.UtcNow > expiresUtc || connector == null) return;
                uint clientPid;
                if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out clientPid) || clientPid == 0 || clientPid > Int32.MaxValue) return;
                ProcessIdentity client;
                ProcessIdentity serverNode;
                if (!ValidateClient((int)clientPid, out client, out serverNode)) return;
                byte[] suppliedCapability = ReadExact(server, 64);
                if (!ConstantTimeEquals(Encoding.ASCII.GetBytes(launchCapability), suppliedCapability)) return;

                DateTime issuedUtc = DateTime.UtcNow;
                DiagnosticLaunchContract contract = new DiagnosticLaunchContract
                {
                    protocol = ProtocolVersion,
                    status = "ok",
                    instanceId = PipeName,
                    managerPid = Process.GetCurrentProcess().Id,
                    launcherPid = connector.Id,
                    serverPid = serverNode.ProcessId,
                    verifierPid = client.ProcessId,
                    issuedUtc = issuedUtc.ToString("O"),
                    expiresUtc = expiresUtc.ToString("O"),
                    helper = new DiagnosticLaunchHelperContract
                    {
                        executablePath = settings.DiagnosticHelperPath,
                        protocolVersion = settings.DiagnosticHelperProtocolVersion,
                        sha256 = settings.DiagnosticHelperSha256.ToLowerInvariant()
                    }
                };
                byte[] body = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(contract));
                if (body.Length <= 0 || body.Length > FrameLimit) return;
                byte[] header = BitConverter.GetBytes(body.Length);
                server.Write(header, 0, header.Length);
                server.Write(body, 0, body.Length);
                server.Flush();
            }
            catch { }
            finally
            {
                try { server.Dispose(); }
                catch { }
            }
        }

        private bool ValidateClient(int clientPid, out ProcessIdentity client, out ProcessIdentity serverNode)
        {
            client = ReadIdentity(clientPid);
            serverNode = null;
            if (client == null || !PathEquals(client.ExecutablePath, managerExecutable)) return false;
            if (client.StartUtc < connectorStartUtc || client.StartUtc > expiresUtc) return false;
            if (!IsJobMember(clientPid)) return false;

            ProcessIdentity ancestor = client;
            for (int depth = 0; depth < ancestorDepth; depth++)
            {
                ancestor = ReadIdentity(ancestor.ParentProcessId);
                if (ancestor == null) return false;
                if (depth == 0) serverNode = ancestor;
            }
            if (ancestor.ProcessId != connector.Id || ancestor.StartUtc != connectorStartUtc) return false;
            if (!IsJobMember(ancestor.ProcessId) || serverNode == null || !IsJobMember(serverNode.ProcessId)) return false;
            if (ancestorDepth == 2)
            {
                if (serverNode == null || !PathEquals(serverNode.ExecutablePath, nodeExecutable)) return false;
                if (!CommandContainsPath(serverNode.CommandLine, expectedServerScript)) return false;
                if (serverNode.StartUtc < connectorStartUtc || client.StartUtc < serverNode.StartUtc) return false;
            }
            try
            {
                using (Process current = Process.GetProcessById(connector.Id))
                    if (current.HasExited || current.StartTime.ToUniversalTime() != connectorStartUtc) return false;
            }
            catch { return false; }
            return true;
        }

        private bool IsJobMember(int pid)
        {
            using (SafeFileHandle process = OpenProcess(0x1000, false, pid))
            {
                if (process == null || process.IsInvalid) return false;
                bool member;
                return IsProcessInJob(process, job, out member) && member;
            }
        }

        private static bool CommandContainsPath(string commandLine, string expected)
        {
            if (String.IsNullOrWhiteSpace(commandLine)) return false;
            return commandLine.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0 ||
                commandLine.IndexOf(expected.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static ProcessIdentity ReadIdentity(int pid)
        {
            if (pid <= 0) return null;
            string query = "SELECT ProcessId, ParentProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE ProcessId=" + pid;
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(query))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject item in results)
                    {
                        DateTime startUtc;
                        using (Process process = Process.GetProcessById(pid)) startUtc = process.StartTime.ToUniversalTime();
                        return new ProcessIdentity
                        {
                            ProcessId = Convert.ToInt32(item["ProcessId"]),
                            ParentProcessId = Convert.ToInt32(item["ParentProcessId"]),
                            ExecutablePath = Convert.ToString(item["ExecutablePath"]),
                            CommandLine = Convert.ToString(item["CommandLine"]),
                            StartUtc = startUtc
                        };
                    }
                }
            }
            catch { }
            return null;
        }

        private static string RandomHex(int bytes)
        {
            byte[] value = new byte[bytes];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(value);
            return BitConverter.ToString(value).Replace("-", String.Empty).ToLowerInvariant();
        }

        private static byte[] ReadExact(Stream stream, int length)
        {
            byte[] value = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = stream.Read(value, offset, length - offset);
                if (read <= 0) throw new EndOfStreamException();
                offset += read;
            }
            return value;
        }

        private static bool ConstantTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            int difference = 0;
            for (int index = 0; index < left.Length; index++) difference |= left[index] ^ right[index];
            return difference == 0;
        }

        private static bool PathEquals(string left, string right)
        {
            if (String.IsNullOrWhiteSpace(left) || String.IsNullOrWhiteSpace(right)) return false;
            try { return String.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        public void Dispose()
        {
            disposed = true;
            try { server.Dispose(); }
            catch { }
            try { gate.Dispose(); }
            catch { }
            try { job.Dispose(); }
            catch { }
        }

        private sealed class ProcessIdentity
        {
            public int ProcessId;
            public int ParentProcessId;
            public string ExecutablePath;
            public string CommandLine;
            public DateTime StartUtc;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr securityAttributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsProcessInJob(SafeFileHandle process, SafeFileHandle job, out bool result);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle OpenProcess(uint access, bool inheritHandle, int processId);
    }

    internal static class DiagnosticLaunchProofClient
    {
        private const int FrameLimit = 4096;
        private static readonly Regex PipePattern = new Regex("^codexpro-safe-diagnostic-[a-f0-9]{32}$", RegexOptions.CultureInvariant);
        private static Guid LocalAppData = new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");

        internal static int Run(string pipeName, bool requireInstalledPath, int connectTimeoutMilliseconds)
        {
            try
            {
                if (!PipePattern.IsMatch(pipeName ?? String.Empty)) return 3;
                string launchCapability = ReadLaunchCapability(Console.OpenStandardInput());
                if (launchCapability == null) return 3;
                string ownExecutable = Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName);
                if (requireInstalledPath && !String.Equals(ownExecutable, InstalledManagerPath(), StringComparison.OrdinalIgnoreCase)) return 3;

                using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None))
                {
                    pipe.Connect(connectTimeoutMilliseconds);
                    uint managerPid;
                    if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out managerPid) || managerPid == 0 || managerPid > Int32.MaxValue) return 3;
                    string managerExecutable;
                    using (Process manager = Process.GetProcessById((int)managerPid)) managerExecutable = manager.MainModule.FileName;
                    if (!String.Equals(Path.GetFullPath(managerExecutable), ownExecutable, StringComparison.OrdinalIgnoreCase)) return 3;
                    byte[] capabilityBytes = Encoding.ASCII.GetBytes(launchCapability);
                    pipe.Write(capabilityBytes, 0, capabilityBytes.Length);
                    pipe.Flush();

                    byte[] header = ReadExact(pipe, 4);
                    int length = BitConverter.ToInt32(header, 0);
                    if (length <= 0 || length > FrameLimit) return 3;
                    byte[] body = ReadExact(pipe, length);
                    DiagnosticLaunchContract contract = new JavaScriptSerializer().Deserialize<DiagnosticLaunchContract>(Encoding.UTF8.GetString(body));
                    if (!ValidContract(contract, pipeName, (int)managerPid, ownExecutable)) return 3;
                    Stream output = Console.OpenStandardOutput();
                    output.Write(header, 0, header.Length);
                    output.Write(body, 0, body.Length);
                    output.Flush();
                    return 0;
                }
            }
            catch { return 3; }
        }

        private static bool ValidContract(DiagnosticLaunchContract value, string pipeName, int managerPid, string ownExecutable)
        {
            DateTime issued;
            DateTime expires;
            if (value == null || value.protocol != DiagnosticLaunchBroker.ProtocolVersion || value.status != "ok" ||
                value.instanceId != pipeName || value.managerPid != managerPid || value.verifierPid != Process.GetCurrentProcess().Id ||
                value.launcherPid <= 0 || value.serverPid <= 0 || value.helper == null ||
                !DateTime.TryParse(value.issuedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out issued) ||
                !DateTime.TryParse(value.expiresUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out expires) ||
                issued.ToUniversalTime() > DateTime.UtcNow.AddSeconds(2) || DateTime.UtcNow > expires.ToUniversalTime() ||
                expires.ToUniversalTime() - issued.ToUniversalTime() > TimeSpan.FromSeconds(20) ||
                value.helper.protocolVersion != DiagnosticHelperTrust.ProtocolVersion || !ValidHash(value.helper.sha256)) return false;
            string expectedHelper = Path.Combine(Path.GetDirectoryName(ownExecutable), DiagnosticHelperTrust.HelperFileName);
            try { return String.Equals(Path.GetFullPath(value.helper.executablePath), expectedHelper, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static string InstalledManagerPath()
        {
            IntPtr value;
            int result = SHGetKnownFolderPath(ref LocalAppData, 0, IntPtr.Zero, out value);
            if (result != 0 || value == IntPtr.Zero) throw new InvalidOperationException();
            try
            {
                return Path.Combine(Marshal.PtrToStringUni(value), "Programs", "CodexProSafe Manager", "CodexProSafe.Manager.exe");
            }
            finally { Marshal.FreeCoTaskMem(value); }
        }

        private static byte[] ReadExact(Stream stream, int length)
        {
            byte[] value = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = stream.Read(value, offset, length - offset);
                if (read <= 0) throw new EndOfStreamException();
                offset += read;
            }
            return value;
        }

        internal static string ReadLaunchCapability(Stream stream)
        {
            try
            {
                string value = Encoding.ASCII.GetString(ReadExact(stream, 64));
                return Regex.IsMatch(value, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant) ? value : null;
            }
            catch { return null; }
        }

        private static bool ValidHash(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length != 64) return false;
            foreach (char character in value)
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))) return false;
            return true;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);
    }

    internal static class DiagnosticLaunchProofSelfTest
    {
        internal static void Run(AppSettings settings, string executable)
        {
            string repository = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(executable))));
            string expectedServerScript = Path.Combine(repository, @"dist\http.js");
            using (DiagnosticLaunchBroker valid = new DiagnosticLaunchBroker(settings, executable, executable, repository, 3000, 2))
            {
                ProcessStartInfo launcherStart = ChildStart(
                    executable,
                    "--diagnostic-launch-test-launcher " + valid.PipeName + " " + valid.GateName + " " + Quote(expectedServerScript));
                using (Process launcher = Process.Start(launcherStart))
                {
                    valid.BindConnector(launcher);
                    Assert(launcher.WaitForExit(5000) && launcher.ExitCode == 0, "managed launch proof valid production-depth instance");
                }
                Assert(RunClient(executable, valid.PipeName, new string('0', 64)) != 0, "managed launch proof replay rejection");
            }

            using (DiagnosticLaunchBroker stale = new DiagnosticLaunchBroker(settings, executable, executable, repository, 50, 1))
            {
                stale.BindConnector(Process.GetCurrentProcess());
                Thread.Sleep(100);
                Assert(RunClient(executable, stale.PipeName, new string('0', 64)) != 0, "managed launch proof staleness rejection");
            }

            ProcessStartInfo sleeperStart = ChildStart(executable, "--diagnostic-launch-test-sleep");
            using (Process sleeper = Process.Start(sleeperStart))
            using (DiagnosticLaunchBroker mismatch = new DiagnosticLaunchBroker(settings, executable, executable, repository, 1000, 1))
            {
                try
                {
                    mismatch.BindConnector(sleeper);
                    Assert(RunClient(executable, mismatch.PipeName, new string('0', 64)) != 0, "managed launch proof parent and instance mismatch rejection");
                }
                finally
                {
                    if (!sleeper.HasExited) sleeper.Kill();
                    sleeper.WaitForExit(2000);
                }
            }

            using (DiagnosticLaunchBroker wrongCapability = new DiagnosticLaunchBroker(settings, executable, executable, repository, 1000, 1))
            {
                wrongCapability.BindConnector(Process.GetCurrentProcess());
                Assert(RunClient(executable, wrongCapability.PipeName, new string('0', 64)) != 0,
                    "managed launch proof private capability rejection");
            }
        }

        internal static int RunLauncher(string pipeName, string gateName, string expectedServerScript, string executable)
        {
            string capability = WaitForGate(gateName);
            if (capability == null) return -1;
            ProcessStartInfo start = ChildStart(
                executable,
                "--diagnostic-launch-test-server " + pipeName + " " + Quote(expectedServerScript));
            start.RedirectStandardInput = true;
            using (Process server = Process.Start(start))
            {
                server.StandardInput.Write(capability);
                server.StandardInput.Close();
                if (!server.WaitForExit(5000)) { server.Kill(); return -1; }
                return server.ExitCode;
            }
        }

        internal static int RunServer(string pipeName, string expectedServerScript, string executable)
        {
            if (String.IsNullOrWhiteSpace(expectedServerScript)) return -1;
            string capability = DiagnosticLaunchProofClient.ReadLaunchCapability(Console.OpenStandardInput());
            return capability == null ? -1 : RunClient(executable, pipeName, capability);
        }

        private static string WaitForGate(string gateName)
        {
            try
            {
                using (NamedPipeClientStream gate = new NamedPipeClientStream(".", gateName, PipeDirection.In, PipeOptions.None))
                {
                    gate.Connect(1000);
                    return DiagnosticLaunchProofClient.ReadLaunchCapability(gate);
                }
            }
            catch { return null; }
        }

        private static int RunClient(string executable, string pipeName, string capability)
        {
            ProcessStartInfo start = ChildStart(executable, "--diagnostic-launch-test-client " + pipeName);
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            using (Process child = Process.Start(start))
            {
                child.StandardInput.Write(capability);
                child.StandardInput.Close();
                using (MemoryStream output = new MemoryStream()) child.StandardOutput.BaseStream.CopyTo(output);
                child.StandardError.ReadToEnd();
                if (!child.WaitForExit(5000)) { child.Kill(); return -1; }
                return child.ExitCode;
            }
        }

        private static ProcessStartInfo ChildStart(string executable, string arguments)
        {
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = executable;
            start.Arguments = arguments;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            return start;
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? String.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static void Assert(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Self-test failed: " + name);
        }
    }
}
