using System;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexProSafeManager
{
    internal static class OperationalCommands
    {
        internal const int SuccessExitCode = 0;
        internal const int InvalidRequestExitCode = 2;
        internal const int UpdateUnavailableExitCode = 3;
        internal const int StatusUnavailableExitCode = 4;

        internal static int SetCodexDiagnostics(
            string[] args,
            SecureSettingsStore store,
            string managerExecutable,
            TextWriter output)
        {
            return SetCodexDiagnostics(
                args, store, managerExecutable, output, new FixedManagerExclusiveLeaseProvider());
        }

        internal static int SetCodexDiagnostics(
            string[] args,
            SecureSettingsStore store,
            string managerExecutable,
            TextWriter output,
            IManagerExclusiveLeaseProvider leaseProvider,
            Action<Exception> failureObserver = null)
        {
            if (args == null || args.Length != 2 ||
                !String.Equals(args[0], "--set-codex-diagnostics", StringComparison.Ordinal) ||
                (args[1] != "off" && args[1] != "read"))
            {
                WriteModeEnvelope(output, "invalid_request", "unavailable", false);
                return InvalidRequestExitCode;
            }

            string mode = args[1];
            try
            {
                using (leaseProvider.Acquire()) store.UpdateCodexDiagnosticMode(mode, managerExecutable);
                WriteModeEnvelope(output, "ok", mode, true);
                return SuccessExitCode;
            }
            catch (Exception exception)
            {
                if (failureObserver != null) failureObserver(exception);
                WriteModeEnvelope(output, "unavailable", "unavailable", false);
                return UpdateUnavailableExitCode;
            }
        }

        internal static int SafeStatus(
            string[] args,
            SecureSettingsStore store,
            string managerExecutable,
            IManagerStatusProbe probe,
            TextWriter output)
        {
            if (args == null || args.Length != 1 ||
                !String.Equals(args[0], "--safe-status", StringComparison.Ordinal))
            {
                WriteStatusEnvelope(output, SafeStatusEnvelope.Unavailable());
                return InvalidRequestExitCode;
            }

            try
            {
                AppSettings settings = store.LoadExisting();
                ManagerStatusObservation observation = probe.Observe(settings, managerExecutable);
                SafeStatusEnvelope envelope = SafeStatusEnvelope.From(settings, observation);
                WriteStatusEnvelope(output, envelope);
                return envelope.overall == "unsafe" ? StatusUnavailableExitCode : SuccessExitCode;
            }
            catch
            {
                WriteStatusEnvelope(output, SafeStatusEnvelope.Unavailable());
                return StatusUnavailableExitCode;
            }
        }

        private static void WriteModeEnvelope(TextWriter output, string status, string mode, bool restartRequired)
        {
            output.WriteLine(
                "{\"schema\":\"codexpro-manager-command-v1\",\"command\":\"set-codex-diagnostics\",\"status\":\"" + status +
                "\",\"mode\":\"" + mode + "\",\"restartRequired\":" + (restartRequired ? "true" : "false") + "}");
            output.Flush();
        }

        private static void WriteStatusEnvelope(TextWriter output, SafeStatusEnvelope envelope)
        {
            output.WriteLine(new JavaScriptSerializer().Serialize(envelope));
            output.Flush();
        }
    }

    internal interface IManagerExclusiveLeaseProvider
    {
        IDisposable Acquire();
    }

    internal sealed class FixedManagerExclusiveLeaseProvider : IManagerExclusiveLeaseProvider
    {
        public IDisposable Acquire()
        {
            bool created;
            Mutex mutex = new Mutex(true, Program.MutexName, out created);
            if (!created)
            {
                mutex.Dispose();
                throw new InvalidOperationException("The lifecycle owner is active.");
            }
            return new OwnedManagerMutex(mutex);
        }

        private sealed class OwnedManagerMutex : IDisposable
        {
            private Mutex mutex;

            internal OwnedManagerMutex(Mutex value) { mutex = value; }

            public void Dispose()
            {
                if (mutex == null) return;
                mutex.ReleaseMutex();
                mutex.Dispose();
                mutex = null;
            }
        }
    }

    internal sealed class SafeStatusEnvelope
    {
        public string schema { get; set; }
        public string savedDiagnosticMode { get; set; }
        public string installedHelperTrust { get; set; }
        public bool connectorLocalHealthy { get; set; }
        public bool tunnelLocalProcessHealthy { get; set; }
        public bool tunnelAuthenticatedReady { get; set; }
        public bool restartRequired { get; set; }
        public string overall { get; set; }
        public string limitation { get; set; }

        internal static SafeStatusEnvelope Unavailable()
        {
            return new SafeStatusEnvelope
            {
                schema = "codexpro-manager-safe-status-v1",
                savedDiagnosticMode = "unavailable",
                installedHelperTrust = "unavailable",
                connectorLocalHealthy = false,
                tunnelLocalProcessHealthy = false,
                tunnelAuthenticatedReady = false,
                restartRequired = false,
                overall = "unavailable",
                limitation = "status_unavailable"
            };
        }

        internal static SafeStatusEnvelope From(AppSettings settings, ManagerStatusObservation observation)
        {
            string overall;
            string limitation;
            if (settings.CodexDiagnosticReadMode == "read" && observation.HelperTrust != "sealed")
            {
                overall = "unsafe";
                limitation = "helper_unavailable";
            }
            else if (!observation.ConnectorLocalHealthy || !observation.TunnelLocalProcessHealthy)
            {
                overall = "degraded";
                limitation = "local_service_unavailable";
            }
            else if (!observation.TunnelAuthenticatedReady)
            {
                overall = "degraded";
                limitation = "tunnel_not_authenticated";
            }
            else if (observation.RestartRequired)
            {
                overall = "restart_required";
                limitation = "restart_required";
            }
            else
            {
                overall = "ready";
                limitation = "none";
            }

            return new SafeStatusEnvelope
            {
                schema = "codexpro-manager-safe-status-v1",
                savedDiagnosticMode = settings.CodexDiagnosticReadMode,
                installedHelperTrust = observation.HelperTrust,
                connectorLocalHealthy = observation.ConnectorLocalHealthy,
                tunnelLocalProcessHealthy = observation.TunnelLocalProcessHealthy,
                tunnelAuthenticatedReady = observation.TunnelAuthenticatedReady,
                restartRequired = observation.RestartRequired,
                overall = overall,
                limitation = limitation
            };
        }
    }
}
