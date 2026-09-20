using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace CodexProSafeManager
{
    internal enum ProcessIdentityState
    {
        Missing,
        Matching,
        Reused,
        Unavailable
    }

    internal enum ProcessStopCommandState
    {
        Succeeded,
        Failed,
        TimedOut
    }

    internal enum ProcessStopFailure
    {
        None,
        AccessDenied,
        CommandFailed,
        CommandTimedOut,
        IdentityUnavailable,
        PidReused,
        LingeringDescendant,
        EndpointStillLive
    }

    internal sealed class VerifiedProcessIdentity
    {
        public int ProcessId { get; set; }
        public DateTime StartUtc { get; set; }
    }

    internal sealed class ProcessStopAssessment
    {
        public ProcessStopFailure Failure { get; set; }
        public bool AlreadyExited { get; set; }
        public bool CommandAnomalyRecovered { get; set; }

        public bool Succeeded { get { return Failure == ProcessStopFailure.None; } }

        internal static ProcessStopAssessment Classify(
            ProcessStopCommandState command,
            ProcessIdentityState target,
            bool accessDenied,
            bool descendantStillAlive,
            bool endpointStillLive)
        {
            ProcessStopAssessment result = new ProcessStopAssessment();
            if (target == ProcessIdentityState.Reused)
                result.Failure = ProcessStopFailure.PidReused;
            else if (target == ProcessIdentityState.Unavailable)
                result.Failure = ProcessStopFailure.IdentityUnavailable;
            else if (target == ProcessIdentityState.Matching && accessDenied)
                result.Failure = ProcessStopFailure.AccessDenied;
            else if (target == ProcessIdentityState.Matching && command == ProcessStopCommandState.TimedOut)
                result.Failure = ProcessStopFailure.CommandTimedOut;
            else if (target == ProcessIdentityState.Matching)
                result.Failure = ProcessStopFailure.CommandFailed;
            else if (descendantStillAlive)
                result.Failure = ProcessStopFailure.LingeringDescendant;
            else if (endpointStillLive)
                result.Failure = ProcessStopFailure.EndpointStillLive;
            else
            {
                result.Failure = ProcessStopFailure.None;
                result.AlreadyExited = command != ProcessStopCommandState.Succeeded;
                result.CommandAnomalyRecovered = command != ProcessStopCommandState.Succeeded;
            }
            return result;
        }
    }

    internal static class VerifiedProcessTreeTerminator
    {
        private const uint ProcessTerminate = 0x0001;
        private const uint Synchronize = 0x00100000;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const int ErrorAccessDenied = 5;

        internal static VerifiedProcessIdentity Capture(Process process)
        {
            if (process == null) throw new ArgumentNullException("process");
            return new VerifiedProcessIdentity
            {
                ProcessId = process.Id,
                StartUtc = process.StartTime.ToUniversalTime()
            };
        }

        internal static VerifiedProcessIdentity Capture(int processId, DateTime startUtc)
        {
            return new VerifiedProcessIdentity { ProcessId = processId, StartUtc = startUtc.ToUniversalTime() };
        }

        internal static ProcessStopAssessment Stop(VerifiedProcessIdentity target)
        {
            if (target == null || target.ProcessId <= 0)
                return Failed(ProcessStopFailure.IdentityUnavailable);

            ProcessIdentityState initial = Observe(target);
            if (initial == ProcessIdentityState.Missing)
                return ProcessStopAssessment.Classify(ProcessStopCommandState.Failed, initial, false, false, false);
            if (initial != ProcessIdentityState.Matching)
                return ProcessStopAssessment.Classify(ProcessStopCommandState.Failed, initial, false, false, false);

            using (SafeFileHandle rootHandle = OpenProcess(
                ProcessTerminate | Synchronize | ProcessQueryLimitedInformation,
                false,
                target.ProcessId))
            {
                if (rootHandle == null || rootHandle.IsInvalid)
                {
                    bool denied = Marshal.GetLastWin32Error() == ErrorAccessDenied;
                    return ProcessStopAssessment.Classify(
                        ProcessStopCommandState.Failed,
                        denied ? ProcessIdentityState.Matching : ProcessIdentityState.Unavailable,
                        denied,
                        false,
                        false);
                }

                // Keeping this handle open prevents the verified process object from
                // being discarded and its numeric PID from becoming a new kill target.
                ProcessIdentityState lockedIdentity = Observe(target);
                if (lockedIdentity != ProcessIdentityState.Matching)
                    return ProcessStopAssessment.Classify(ProcessStopCommandState.Failed, lockedIdentity, false, false, false);

                IList<VerifiedProcessIdentity> descendants;
                try { descendants = CaptureDescendants(target.ProcessId); }
                catch { return Failed(ProcessStopFailure.IdentityUnavailable); }

                ProcessStopCommandState command;
                try { command = RunTaskkill(target.ProcessId); }
                catch { command = ProcessStopCommandState.Failed; }

                ProcessIdentityState final = WaitForIdentityChange(target, 5000);
                bool descendantStillAlive = false;
                foreach (VerifiedProcessIdentity descendant in descendants)
                {
                    ProcessIdentityState state = Observe(descendant);
                    if (state == ProcessIdentityState.Matching || state == ProcessIdentityState.Unavailable)
                    {
                        descendantStillAlive = true;
                        break;
                    }
                }
                return ProcessStopAssessment.Classify(command, final, false, descendantStillAlive, false);
            }
        }

        internal static ProcessStopAssessment WithEndpointState(ProcessStopAssessment processResult, bool endpointStillLive)
        {
            if (processResult == null) return Failed(ProcessStopFailure.IdentityUnavailable);
            if (!processResult.Succeeded || !endpointStillLive) return processResult;
            return ProcessStopAssessment.Classify(
                ProcessStopCommandState.Succeeded,
                ProcessIdentityState.Missing,
                false,
                false,
                true);
        }

        internal static string FailureMessage(ProcessStopFailure failure)
        {
            switch (failure)
            {
                case ProcessStopFailure.AccessDenied:
                    return "Windows denied permission to stop the verified process tree (access_denied).";
                case ProcessStopFailure.CommandTimedOut:
                    return "Windows did not finish stopping the verified process tree within the bounded timeout (taskkill_timeout).";
                case ProcessStopFailure.PidReused:
                    return "The verified process exited and its PID was reused; no replacement process was stopped (pid_reused).";
                case ProcessStopFailure.LingeringDescendant:
                    return "A verified descendant remained after the process tree stop (lingering_descendant).";
                case ProcessStopFailure.EndpointStillLive:
                    return "The service endpoint remained available after its verified process tree stopped (endpoint_still_live).";
                case ProcessStopFailure.IdentityUnavailable:
                    return "Windows could not re-verify the process identity during shutdown (identity_unavailable).";
                default:
                    return "Windows could not stop the verified process tree (taskkill_failed).";
            }
        }

        private static ProcessStopAssessment Failed(ProcessStopFailure failure)
        {
            return new ProcessStopAssessment { Failure = failure };
        }

        private static ProcessStopCommandState RunTaskkill(int processId)
        {
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe");
            start.Arguments = "/PID " + processId + " /T /F";
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            using (Process killer = new Process())
            {
                killer.StartInfo = start;
                killer.Start();
                killer.BeginOutputReadLine();
                killer.BeginErrorReadLine();
                if (!killer.WaitForExit(8000))
                {
                    try { killer.Kill(); }
                    catch { }
                    try { killer.WaitForExit(1000); }
                    catch { }
                    return ProcessStopCommandState.TimedOut;
                }
                return killer.ExitCode == 0 ? ProcessStopCommandState.Succeeded : ProcessStopCommandState.Failed;
            }
        }

        private static ProcessIdentityState WaitForIdentityChange(VerifiedProcessIdentity target, int timeoutMilliseconds)
        {
            Stopwatch watch = Stopwatch.StartNew();
            ProcessIdentityState state;
            do
            {
                state = Observe(target);
                if (state != ProcessIdentityState.Matching) return state;
                Thread.Sleep(100);
            }
            while (watch.ElapsedMilliseconds < timeoutMilliseconds);
            return state;
        }

        private static ProcessIdentityState Observe(VerifiedProcessIdentity target)
        {
            try
            {
                using (Process current = Process.GetProcessById(target.ProcessId))
                {
                    DateTime startUtc = current.StartTime.ToUniversalTime();
                    return startUtc == target.StartUtc
                        ? ProcessIdentityState.Matching
                        : ProcessIdentityState.Reused;
                }
            }
            catch (ArgumentException) { return ProcessIdentityState.Missing; }
            catch (InvalidOperationException) { return ProcessIdentityState.Missing; }
            catch (Win32Exception error)
            {
                return error.NativeErrorCode == ErrorAccessDenied
                    ? ProcessIdentityState.Unavailable
                    : ProcessIdentityState.Missing;
            }
        }

        private static IList<VerifiedProcessIdentity> CaptureDescendants(int rootProcessId)
        {
            List<ProcessRecord> records = new List<ProcessRecord>();
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, CreationDate FROM Win32_Process"))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementObject item in results)
                {
                    string created = Convert.ToString(item["CreationDate"]);
                    if (String.IsNullOrWhiteSpace(created)) continue;
                    try
                    {
                        records.Add(new ProcessRecord
                        {
                            ProcessId = Convert.ToInt32(item["ProcessId"]),
                            ParentProcessId = Convert.ToInt32(item["ParentProcessId"]),
                            StartUtc = ManagementDateTimeConverter.ToDateTime(created).ToUniversalTime()
                        });
                    }
                    catch { }
                }
            }

            List<VerifiedProcessIdentity> found = new List<VerifiedProcessIdentity>();
            HashSet<int> parents = new HashSet<int>();
            parents.Add(rootProcessId);
            bool changed;
            do
            {
                changed = false;
                foreach (ProcessRecord record in records)
                {
                    if (parents.Contains(record.ParentProcessId) && !parents.Contains(record.ProcessId))
                    {
                        parents.Add(record.ProcessId);
                        found.Add(Capture(record.ProcessId, record.StartUtc));
                        changed = true;
                    }
                }
            }
            while (changed);
            return found;
        }

        private sealed class ProcessRecord
        {
            public int ProcessId;
            public int ParentProcessId;
            public DateTime StartUtc;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);
    }
}
