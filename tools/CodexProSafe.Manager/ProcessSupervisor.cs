using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexProSafeManager
{
    internal sealed class ProcessSupervisor : IDisposable
    {
        private readonly SemaphoreSlim operationLock = new SemaphoreSlim(1, 1);
        private Process connector;
        private Process tunnel;
        private AppSettings settings;
        private bool disposed;
        private bool intentionalStop;
        private DateTime connectorRestartAfter = DateTime.MinValue;
        private DateTime tunnelRestartAfter = DateTime.MinValue;

        public event Action<string, string> LogLine;
        public event Action StateChanged;

        public ProcessSupervisor(AppSettings settings)
        {
            this.settings = settings;
        }

        public void UpdateSettings(AppSettings value)
        {
            settings = value;
        }

        public static string BuildConnectorArguments(AppSettings value)
        {
            string script = Path.Combine(value.RepositoryPath, @"scripts\codexpro.mjs");
            return String.Join(" ", new[]
            {
                Quote(script),
                "--root", Quote(value.WorkspaceRoot),
                "--allow-root", Quote(value.AllowedRoot),
                "--tunnel", "none",
                "--mode", "handoff",
                "--bash", "off",
                "--write", "handoff",
                "--codex-diagnostic-read", value.CodexDiagnosticReadMode
            });
        }

        public static string BuildTunnelArguments(AppSettings value)
        {
            return "run --profile " + Quote(value.TunnelProfile);
        }

        public static string Quote(string value)
        {
            return "\"" + (value ?? String.Empty).Replace("\"", "\\\"") + "\"";
        }

        public async Task<ServiceSnapshot> GetSnapshotAsync()
        {
            bool connectorHealthy = await ProbeAsync("http://127.0.0.1:8787/healthz");
            bool tunnelHealthy = await ProbeTunnelReadyAsync();
            bool tunnelLocalHealthy = await ProbeAsync("http://127.0.0.1:8080/healthz");
            bool connectorOwned = IsAlive(connector);
            bool tunnelOwned = IsAlive(tunnel);

            ServiceSnapshot snapshot = new ServiceSnapshot();
            snapshot.ConnectorHealthy = connectorHealthy;
            snapshot.TunnelHealthy = tunnelHealthy;
            snapshot.ConnectorState = connectorHealthy
                ? (connectorOwned ? ServiceState.RunningOwned : ServiceState.RunningExternal)
                : (connectorOwned ? ServiceState.Starting : ServiceState.Stopped);
            snapshot.TunnelState = tunnelHealthy
                ? (tunnelOwned ? ServiceState.RunningOwned : ServiceState.RunningExternal)
                : tunnelLocalHealthy
                    ? (tunnelOwned && ProcessAgeSeconds(tunnel) < 30
                        ? ServiceState.Starting
                        : ServiceState.Faulted)
                    : (tunnelOwned ? ServiceState.Starting : ServiceState.Stopped);
            snapshot.ConnectorDetail = connectorHealthy
                ? (connectorOwned ? "Healthy · managed here" : "Healthy · running outside manager")
                : (connectorOwned ? "Process started; waiting for health" : "Not running");
            snapshot.TunnelDetail = tunnelHealthy
                ? (tunnelOwned ? "Authenticated and ready · managed here" : "Authenticated and ready · running outside manager")
                : tunnelLocalHealthy
                    ? (tunnelOwned && ProcessAgeSeconds(tunnel) < 30
                        ? "Process started; waiting for authenticated tunnel metadata"
                        : "Local process is healthy, but the control plane is not authenticated")
                    : (tunnelOwned ? "Process started; waiting for local health" : "Not running");
            return snapshot;
        }

        public async Task StartAllAsync()
        {
            await operationLock.WaitAsync();
            try
            {
                intentionalStop = false;
                await StartConnectorCoreAsync();
                await StartTunnelCoreAsync();
            }
            finally
            {
                operationLock.Release();
                RaiseStateChanged();
            }
        }

        public async Task RestartAllAsync(bool allowExactTakeover)
        {
            await operationLock.WaitAsync();
            try
            {
                string connectorValidation = settings.ValidateForConnector();
                if (connectorValidation != null) throw new InvalidOperationException(connectorValidation);
                string tunnelValidation = settings.ValidateForTunnel();
                if (tunnelValidation != null) throw new InvalidOperationException(tunnelValidation);

                intentionalStop = true;
                await StopTunnelCoreAsync(allowExactTakeover);
                await StopConnectorCoreAsync(allowExactTakeover);
                intentionalStop = false;
                await StartConnectorCoreAsync();
                await StartTunnelCoreAsync();
            }
            finally
            {
                intentionalStop = false;
                operationLock.Release();
                RaiseStateChanged();
            }
        }

        public async Task StopAllAsync(bool allowExactTakeover)
        {
            await operationLock.WaitAsync();
            try
            {
                intentionalStop = true;
                await StopTunnelCoreAsync(allowExactTakeover);
                await StopConnectorCoreAsync(allowExactTakeover);
            }
            finally
            {
                intentionalStop = false;
                operationLock.Release();
                RaiseStateChanged();
            }
        }

        public async Task StartConnectorAsync()
        {
            await operationLock.WaitAsync();
            try { await StartConnectorCoreAsync(); }
            finally { operationLock.Release(); RaiseStateChanged(); }
        }

        public async Task StartTunnelAsync()
        {
            await operationLock.WaitAsync();
            try { await StartTunnelCoreAsync(); }
            finally { operationLock.Release(); RaiseStateChanged(); }
        }

        public async Task StopConnectorAsync(bool allowExactTakeover)
        {
            await operationLock.WaitAsync();
            try { await StopConnectorCoreAsync(allowExactTakeover); }
            finally { operationLock.Release(); RaiseStateChanged(); }
        }

        public async Task StopTunnelAsync(bool allowExactTakeover)
        {
            await operationLock.WaitAsync();
            try { await StopTunnelCoreAsync(allowExactTakeover); }
            finally { operationLock.Release(); RaiseStateChanged(); }
        }

        public async Task MonitorAndRecoverAsync()
        {
            if (!settings.RestartOnFailure || intentionalStop || operationLock.CurrentCount == 0) return;
            ServiceSnapshot snapshot = await GetSnapshotAsync();
            if (!snapshot.ConnectorHealthy && connector != null && connector.HasExited &&
                DateTime.UtcNow >= connectorRestartAfter)
            {
                connectorRestartAfter = DateTime.UtcNow.AddSeconds(15);
                Emit("manager", "Connector exited unexpectedly; restarting.");
                await StartConnectorAsync();
            }
            if (snapshot.ConnectorHealthy && !snapshot.TunnelHealthy && tunnel != null && tunnel.HasExited &&
                DateTime.UtcNow >= tunnelRestartAfter)
            {
                tunnelRestartAfter = DateTime.UtcNow.AddSeconds(15);
                Emit("manager", "Tunnel exited unexpectedly; restarting.");
                await StartTunnelAsync();
            }
        }

        private async Task StartConnectorCoreAsync()
        {
            if (await ProbeAsync("http://127.0.0.1:8787/healthz"))
            {
                Emit("manager", IsAlive(connector)
                    ? "Connector is already healthy."
                    : "Connector is already healthy and running outside the manager.");
                return;
            }
            string validation = settings.ValidateForConnector();
            if (validation != null) throw new InvalidOperationException(validation);

            connector = CreateProcess(settings.NodePath, BuildConnectorArguments(settings), settings.RepositoryPath, null);
            AttachProcess(connector, "connector");
            Emit("manager", "Starting connector on 127.0.0.1:8787.");
            connector.Start();
            connector.BeginOutputReadLine();
            connector.BeginErrorReadLine();
            if (!await WaitForProbeAsync("http://127.0.0.1:8787/healthz", 20000))
                throw new InvalidOperationException("Connector did not become healthy within 20 seconds. Open Logs for details.");
            Emit("manager", "Connector is healthy.");
        }

        private async Task StartTunnelCoreAsync()
        {
            if (await ProbeTunnelReadyAsync())
            {
                Emit("manager", IsAlive(tunnel)
                    ? "Tunnel is already authenticated and ready."
                    : "Tunnel is already authenticated and ready outside the manager.");
                return;
            }
            if (await ProbeAsync("http://127.0.0.1:8080/healthz"))
            {
                throw new InvalidOperationException(
                    "A tunnel process is listening locally but has not authenticated with the control plane. " +
                    "Correct its API key, organization, tunnel access, or tunnel ID, then use Restart All.");
            }
            if (!await ProbeAsync("http://127.0.0.1:8787/healthz"))
                throw new InvalidOperationException("The connector must be healthy before the tunnel can start.");
            string validation = settings.ValidateForTunnel();
            if (validation != null) throw new InvalidOperationException(validation);

            IDictionary<string, string> environment = new Dictionary<string, string>();
            environment["CONTROL_PLANE_API_KEY"] = settings.ControlPlaneApiKey;
            if (!String.IsNullOrWhiteSpace(settings.OrganizationId))
                environment["CONTROL_PLANE_ORGANIZATION_ID"] = settings.OrganizationId;

            tunnel = CreateProcess(
                settings.TunnelClientPath,
                BuildTunnelArguments(settings),
                Path.GetDirectoryName(settings.TunnelClientPath),
                environment);
            AttachProcess(tunnel, "tunnel");
            Emit("manager", "Starting tunnel profile " + settings.TunnelProfile + ".");
            tunnel.Start();
            tunnel.BeginOutputReadLine();
            tunnel.BeginErrorReadLine();
            if (!await WaitForTunnelReadyAsync(30000))
                throw new InvalidOperationException(
                    "Tunnel did not authenticate and fetch matching tunnel metadata within 30 seconds. Open Logs for details.");
            Emit("manager", "Tunnel is authenticated and ready.");
        }

        private async Task StopConnectorCoreAsync(bool allowExactTakeover)
        {
            if (IsAlive(connector))
            {
                int pid = connector.Id;
                KillTree(pid);
                await WaitForExitAsync(connector, 5000);
                connector.Dispose();
                connector = null;
                Emit("manager", "Stopped managed connector.");
                return;
            }
            if (!await ProbeAsync("http://127.0.0.1:8787/healthz")) return;
            if (!allowExactTakeover)
                throw new InvalidOperationException("Connector is running outside the manager. Use the confirmed exact-process takeover.");
            int portPid = FindListeningProcessId(8787);
            int ownerPid = FindMatchingConnectorOwner(portPid);
            if (ownerPid <= 0)
                throw new InvalidOperationException("Refused to stop the external connector because its exact owner could not be verified.");
            KillTree(ownerPid);
            await WaitForProbeToStopAsync("http://127.0.0.1:8787/healthz", 8000);
            Emit("manager", "Stopped the exact matching external connector.");
        }

        private async Task StopTunnelCoreAsync(bool allowExactTakeover)
        {
            if (IsAlive(tunnel))
            {
                int pid = tunnel.Id;
                KillTree(pid);
                await WaitForExitAsync(tunnel, 5000);
                tunnel.Dispose();
                tunnel = null;
                Emit("manager", "Stopped managed tunnel.");
                return;
            }
            if (!await ProbeAsync("http://127.0.0.1:8080/healthz")) return;
            if (!allowExactTakeover)
                throw new InvalidOperationException("Tunnel is running outside the manager. Use the confirmed exact-process takeover.");
            int portPid = FindListeningProcessId(8080);
            ProcessIdentity identity = GetIdentity(portPid);
            if (identity == null ||
                !PathEquals(identity.ExecutablePath, settings.TunnelClientPath) ||
                !ContainsArgument(identity.CommandLine, "--profile", settings.TunnelProfile))
            {
                throw new InvalidOperationException("Refused to stop the external tunnel because its executable and profile did not exactly match.");
            }
            KillTree(portPid);
            await WaitForProbeToStopAsync("http://127.0.0.1:8080/healthz", 8000);
            Emit("manager", "Stopped the exact matching external tunnel.");
        }

        private int FindMatchingConnectorOwner(int portPid)
        {
            if (portPid <= 0) return -1;
            ProcessIdentity child = GetIdentity(portPid);
            if (child == null || !String.Equals(child.Name, "node.exe", StringComparison.OrdinalIgnoreCase))
                return -1;
            ProcessIdentity parent = GetIdentity(child.ParentProcessId);
            string script = Path.Combine(settings.RepositoryPath, @"scripts\codexpro.mjs");
            if (parent == null || !String.Equals(parent.Name, "node.exe", StringComparison.OrdinalIgnoreCase))
                return -1;
            if (!ContainsPathOrRelativeScript(parent.CommandLine, script)) return -1;
            if (!ContainsArgument(parent.CommandLine, "--root", settings.WorkspaceRoot)) return -1;
            if (!ContainsArgument(parent.CommandLine, "--allow-root", settings.AllowedRoot)) return -1;
            if (!ContainsArgument(parent.CommandLine, "--tunnel", "none")) return -1;
            if (!ContainsArgument(parent.CommandLine, "--mode", "handoff")) return -1;
            if (!ContainsArgument(parent.CommandLine, "--bash", "off")) return -1;
            if (!ContainsArgument(parent.CommandLine, "--write", "handoff")) return -1;
            if (!ContainsArgument(parent.CommandLine, "--codex-diagnostic-read", settings.CodexDiagnosticReadMode)) return -1;
            return parent.ProcessId;
        }

        internal static bool ContainsArgument(string commandLine, string name, string value)
        {
            if (String.IsNullOrWhiteSpace(commandLine)) return false;
            string pattern = Regex.Escape(name) + @"\s+(?:""" + Regex.Escape(value) + @"""|" + Regex.Escape(value) + @")(?=\s|$)";
            return Regex.IsMatch(commandLine, pattern, RegexOptions.IgnoreCase);
        }

        private static bool ContainsPathOrRelativeScript(string commandLine, string fullPath)
        {
            if (String.IsNullOrWhiteSpace(commandLine)) return false;
            if (commandLine.IndexOf(fullPath, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return commandLine.IndexOf(@"scripts\codexpro.mjs", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   commandLine.IndexOf("scripts/codexpro.mjs", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool PathEquals(string left, string right)
        {
            if (String.IsNullOrWhiteSpace(left) || String.IsNullOrWhiteSpace(right)) return false;
            return String.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }

        private static Process CreateProcess(
            string fileName,
            string arguments,
            string workingDirectory,
            IDictionary<string, string> environment)
        {
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = fileName;
            start.Arguments = arguments;
            start.WorkingDirectory = workingDirectory;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.RedirectStandardInput = true;
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;
            if (environment != null)
            {
                foreach (KeyValuePair<string, string> pair in environment)
                    start.EnvironmentVariables[pair.Key] = pair.Value;
            }
            return new Process { StartInfo = start, EnableRaisingEvents = true };
        }

        private void AttachProcess(Process process, string source)
        {
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args)
            {
                if (args.Data != null) Emit(source, args.Data);
            };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args)
            {
                if (args.Data != null) Emit(source, args.Data);
            };
            process.Exited += delegate
            {
                Emit(source, "Process exited.");
                RaiseStateChanged();
            };
        }

        private void Emit(string source, string message)
        {
            string sanitized = LogWriter.Sanitize(message);
            LogWriter.Append(source, sanitized);
            Action<string, string> handler = LogLine;
            if (handler != null) handler(source, sanitized);
        }

        private void RaiseStateChanged()
        {
            Action handler = StateChanged;
            if (handler != null) handler();
        }

        private static async Task<bool> ProbeAsync(string url)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = 1500;
                request.ReadWriteTimeout = 1500;
                using (WebResponse response = await request.GetResponseAsync())
                {
                    HttpWebResponse http = response as HttpWebResponse;
                    return http != null && (int)http.StatusCode >= 200 && (int)http.StatusCode < 300;
                }
            }
            catch { return false; }
        }

        private static async Task<bool> WaitForProbeAsync(string url, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (await ProbeAsync(url)) return true;
                await Task.Delay(350);
            }
            return false;
        }

        private static async Task WaitForProbeToStopAsync(string url, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (!await ProbeAsync(url)) return;
                await Task.Delay(250);
            }
            throw new InvalidOperationException("Service endpoint remained available after its verified process was stopped.");
        }

        private static async Task WaitForExitAsync(Process process, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (!process.HasExited && watch.ElapsedMilliseconds < timeoutMs)
                await Task.Delay(100);
        }

        private static bool IsAlive(Process process)
        {
            try { return process != null && !process.HasExited; }
            catch { return false; }
        }

        private static double ProcessAgeSeconds(Process process)
        {
            try
            {
                if (!IsAlive(process)) return Double.MaxValue;
                return Math.Max(0, (DateTime.Now - process.StartTime).TotalSeconds);
            }
            catch { return Double.MaxValue; }
        }

        private async Task<bool> ProbeTunnelReadyAsync()
        {
            if (!await ProbeAsync("http://127.0.0.1:8080/readyz")) return false;
            try
            {
                string body = await GetTextAsync("http://127.0.0.1:8080/api/status");
                object parsed = new JavaScriptSerializer().DeserializeObject(body);
                IDictionary<string, object> root = parsed as IDictionary<string, object>;
                if (root == null) return false;

                string controlPlaneTunnelId = DictionaryString(root, "control_plane_tunnel_id");
                IDictionary<string, object> metadata = DictionaryValue(root, "tunnel_metadata");
                string metadataTunnelId = metadata == null ? String.Empty : DictionaryString(metadata, "ID");
                if (String.IsNullOrWhiteSpace(metadataTunnelId) ||
                    !String.Equals(controlPlaneTunnelId, metadataTunnelId, StringComparison.Ordinal))
                    return false;

                string expectedTunnelId = ReadExpectedTunnelId();
                if (!String.IsNullOrWhiteSpace(expectedTunnelId) &&
                    !String.Equals(expectedTunnelId, metadataTunnelId, StringComparison.Ordinal))
                    return false;

                object channelsValue;
                object[] channels = root.TryGetValue("channels", out channelsValue)
                    ? channelsValue as object[]
                    : null;
                if (channels == null) return false;
                foreach (object item in channels)
                {
                    IDictionary<string, object> channel = item as IDictionary<string, object>;
                    if (channel == null) continue;
                    if (String.Equals(DictionaryString(channel, "name"), "main", StringComparison.Ordinal) &&
                        String.Equals(DictionaryString(channel, "probe_status"), "ok", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            catch { return false; }
        }

        private async Task<bool> WaitForTunnelReadyAsync(int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (await ProbeTunnelReadyAsync()) return true;
                await Task.Delay(350);
            }
            return false;
        }

        private string ReadExpectedTunnelId()
        {
            try
            {
                string profilePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "tunnel-client",
                    settings.TunnelProfile + ".yaml");
                string content = File.ReadAllText(profilePath);
                Match match = Regex.Match(
                    content,
                    @"(?m)^\s*tunnel_id\s*:\s*[""']?(tunnel_[A-Za-z0-9]+)[""']?\s*$");
                return match.Success ? match.Groups[1].Value : String.Empty;
            }
            catch { return String.Empty; }
        }

        private static async Task<string> GetTextAsync(string url)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = 1500;
            request.ReadWriteTimeout = 1500;
            using (WebResponse response = await request.GetResponseAsync())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                return await reader.ReadToEndAsync();
        }

        private static IDictionary<string, object> DictionaryValue(IDictionary<string, object> value, string key)
        {
            object found;
            return value.TryGetValue(key, out found) ? found as IDictionary<string, object> : null;
        }

        private static string DictionaryString(IDictionary<string, object> value, string key)
        {
            object found;
            return value.TryGetValue(key, out found) && found != null ? Convert.ToString(found) : String.Empty;
        }

        private static void KillTree(int pid)
        {
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe");
            start.Arguments = "/PID " + pid + " /T /F";
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            using (Process killer = Process.Start(start))
            {
                if (!killer.WaitForExit(8000) || killer.ExitCode != 0)
                    throw new InvalidOperationException("Windows could not stop verified process tree " + pid + ".");
            }
        }

        internal static int FindListeningProcessId(int port)
        {
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netstat.exe");
            start.Arguments = "-ano -p tcp";
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            using (Process process = Process.Start(start))
            {
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                foreach (string raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string line = raw.Trim();
                    if (line.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string[] fields = Regex.Split(line, @"\s+");
                    if (fields.Length < 5) continue;
                    string local = fields[1];
                    int colon = local.LastIndexOf(':');
                    int parsedPort;
                    int parsedPid;
                    if (colon >= 0 &&
                        Int32.TryParse(local.Substring(colon + 1), out parsedPort) &&
                        parsedPort == port &&
                        Int32.TryParse(fields[fields.Length - 1], out parsedPid))
                        return parsedPid;
                }
            }
            return -1;
        }

        private static ProcessIdentity GetIdentity(int pid)
        {
            if (pid <= 0) return null;
            string query = "SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine FROM Win32_Process WHERE ProcessId=" + pid;
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(query))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementObject item in results)
                {
                    return new ProcessIdentity
                    {
                        ProcessId = Convert.ToInt32(item["ProcessId"]),
                        ParentProcessId = Convert.ToInt32(item["ParentProcessId"]),
                        Name = Convert.ToString(item["Name"]),
                        ExecutablePath = Convert.ToString(item["ExecutablePath"]),
                        CommandLine = Convert.ToString(item["CommandLine"])
                    };
                }
            }
            return null;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            operationLock.Dispose();
            if (connector != null) connector.Dispose();
            if (tunnel != null) tunnel.Dispose();
        }

        private sealed class ProcessIdentity
        {
            public int ProcessId { get; set; }
            public int ParentProcessId { get; set; }
            public string Name { get; set; }
            public string ExecutablePath { get; set; }
            public string CommandLine { get; set; }
        }
    }
}
