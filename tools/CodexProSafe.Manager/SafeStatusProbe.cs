using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexProSafeManager
{
    internal sealed class ManagerStatusObservation
    {
        public string HelperTrust { get; set; }
        public bool ConnectorLocalHealthy { get; set; }
        public bool TunnelLocalProcessHealthy { get; set; }
        public bool TunnelAuthenticatedReady { get; set; }
        public bool RestartRequired { get; set; }
    }

    internal interface IManagerStatusProbe
    {
        ManagerStatusObservation Observe(AppSettings settings, string managerExecutable);
    }

    internal sealed class FixedManagerStatusProbe : IManagerStatusProbe
    {
        public ManagerStatusObservation Observe(AppSettings settings, string managerExecutable)
        {
            bool connectorHealthy = LocalHealthProbes.ProbeAsync("http://127.0.0.1:8787/healthz").GetAwaiter().GetResult();
            bool tunnelLocalHealthy = LocalHealthProbes.ProbeAsync("http://127.0.0.1:8080/healthz").GetAwaiter().GetResult();
            bool tunnelAuthenticated = LocalHealthProbes.ProbeTunnelReadyAsync(settings).GetAwaiter().GetResult();
            string effectiveMode = connectorHealthy
                ? LocalHealthProbes.ProbeConnectorDiagnosticModeAsync().GetAwaiter().GetResult()
                : "unavailable";
            return new ManagerStatusObservation
            {
                HelperTrust = DiagnosticHelperTrust.GetTrustState(settings, managerExecutable),
                ConnectorLocalHealthy = connectorHealthy,
                TunnelLocalProcessHealthy = tunnelLocalHealthy,
                TunnelAuthenticatedReady = tunnelAuthenticated,
                RestartRequired = connectorHealthy &&
                    !String.Equals(effectiveMode, settings.CodexDiagnosticReadMode, StringComparison.Ordinal)
            };
        }
    }

    internal static class LocalHealthProbes
    {
        private const int ConnectorStatusLimitBytes = 4096;
        private const int TunnelStatusLimitBytes = 524288;
        private const int TunnelProfileLimitBytes = 65536;
        private const int RequestDeadlineMilliseconds = 1500;

        internal static async Task<bool> ProbeAsync(string fixedProductEndpoint)
        {
            try
            {
                HttpWebRequest request = CreateRequest(fixedProductEndpoint);
                using (WebResponse response = await GetResponseBoundedAsync(request))
                {
                    HttpWebResponse http = response as HttpWebResponse;
                    return http != null && (int)http.StatusCode >= 200 && (int)http.StatusCode < 300;
                }
            }
            catch { return false; }
        }

        internal static async Task<string> ProbeConnectorDiagnosticModeAsync()
        {
            try
            {
                string body = await ReadBoundedTextAsync(
                    "http://127.0.0.1:8787/manager-safe-status-v1",
                    ConnectorStatusLimitBytes);
                return ParseConnectorDiagnosticMode(body);
            }
            catch { return "unavailable"; }
        }

        internal static async Task<bool> ProbeTunnelReadyAsync(AppSettings settings)
        {
            if (!await ProbeAsync("http://127.0.0.1:8080/readyz")) return false;
            try
            {
                string body = await ReadBoundedTextAsync(
                    "http://127.0.0.1:8080/api/status",
                    TunnelStatusLimitBytes);
                object parsed = new JavaScriptSerializer().DeserializeObject(body);
                IDictionary<string, object> root = parsed as IDictionary<string, object>;
                if (root == null) return false;

                string controlPlaneTunnelId = DictionaryString(root, "control_plane_tunnel_id");
                IDictionary<string, object> metadata = DictionaryValue(root, "tunnel_metadata");
                string metadataTunnelId = metadata == null ? String.Empty : DictionaryString(metadata, "ID");
                if (String.IsNullOrWhiteSpace(metadataTunnelId) ||
                    !String.Equals(controlPlaneTunnelId, metadataTunnelId, StringComparison.Ordinal))
                    return false;

                string expectedTunnelId = ReadExpectedTunnelId(settings.TunnelProfile);
                if (!String.IsNullOrWhiteSpace(expectedTunnelId) &&
                    !String.Equals(expectedTunnelId, metadataTunnelId, StringComparison.Ordinal))
                    return false;

                object channelsValue;
                object[] channels = root.TryGetValue("channels", out channelsValue) ? channelsValue as object[] : null;
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

        private static string ReadExpectedTunnelId(string profileName)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(profileName) ||
                    !Regex.IsMatch(profileName, @"^[A-Za-z0-9_.-]{1,100}$")) return String.Empty;
                string profilePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "tunnel-client",
                    profileName + ".yaml");
                string content = ReadBoundedLocalText(profilePath, TunnelProfileLimitBytes);
                Match match = Regex.Match(
                    content,
                    @"(?m)^\s*tunnel_id\s*:\s*[""']?(tunnel_[A-Za-z0-9]+)[""']?\s*$");
                return match.Success ? match.Groups[1].Value : String.Empty;
            }
            catch { return String.Empty; }
        }

        private static HttpWebRequest CreateRequest(string fixedProductEndpoint)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(fixedProductEndpoint);
            request.Method = "GET";
            request.Timeout = 1500;
            request.ReadWriteTimeout = 1500;
            return request;
        }

        private static async Task<string> ReadBoundedTextAsync(string fixedProductEndpoint, int maximumBytes)
        {
            HttpWebRequest request = CreateRequest(fixedProductEndpoint);
            using (WebResponse response = await GetResponseBoundedAsync(request))
            {
                if (response.ContentLength > maximumBytes)
                    throw new InvalidOperationException("Local status response exceeded its bound.");
                using (Stream stream = response.GetResponseStream())
                    return ReadBoundedStream(stream, maximumBytes);
            }
        }

        private static string ReadBoundedLocalText(string path, int maximumBytes)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return ReadBoundedStream(stream, maximumBytes);
        }

        private static string ReadBoundedStream(Stream stream, int maximumBytes)
        {
            byte[] buffer = new byte[8192];
            byte[] content = null;
            try
            {
                using (MemoryStream collected = new MemoryStream())
                {
                    while (true)
                    {
                        int read = stream.Read(buffer, 0, Math.Min(buffer.Length, maximumBytes + 1 - (int)collected.Length));
                        if (read <= 0) break;
                        collected.Write(buffer, 0, read);
                        if (collected.Length > maximumBytes)
                            throw new InvalidOperationException("Local status response exceeded its bound.");
                    }
                    content = collected.ToArray();
                    return Encoding.UTF8.GetString(content);
                }
            }
            finally
            {
                Array.Clear(buffer, 0, buffer.Length);
                if (content != null) Array.Clear(content, 0, content.Length);
            }
        }

        internal static string ParseConnectorDiagnosticMode(string body)
        {
            try
            {
                IDictionary<string, object> root = new JavaScriptSerializer().DeserializeObject(body) as IDictionary<string, object>;
                if (root == null || root.Count != 2 ||
                    !String.Equals(DictionaryString(root, "schema"), "codexpro-manager-connector-status-v1", StringComparison.Ordinal))
                    return "unavailable";
                string mode = DictionaryString(root, "diagnosticMode");
                return mode == "off" || mode == "read" ? mode : "unavailable";
            }
            catch { return "unavailable"; }
        }

        internal static string ReadBoundedStreamForSelfTest(Stream stream, int maximumBytes)
        {
            return ReadBoundedStream(stream, maximumBytes);
        }

        internal static Task<T> AwaitWithAbortDeadlineForSelfTest<T>(Task<T> operation, Action abort, int timeoutMilliseconds)
        {
            return AwaitWithAbortDeadlineAsync(operation, abort, timeoutMilliseconds);
        }

        private static Task<WebResponse> GetResponseBoundedAsync(HttpWebRequest request)
        {
            return AwaitWithAbortDeadlineAsync(request.GetResponseAsync(), request.Abort, RequestDeadlineMilliseconds);
        }

        private static async Task<T> AwaitWithAbortDeadlineAsync<T>(Task<T> operation, Action abort, int timeoutMilliseconds)
        {
            Task completed = await Task.WhenAny(operation, Task.Delay(timeoutMilliseconds));
            if (completed == operation) return await operation;

            abort();
            Task faultObserver = operation.ContinueWith(
                task => { Exception ignored = task.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            GC.KeepAlive(faultObserver);
            throw new TimeoutException("The local status request exceeded its deadline.");
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
    }
}
