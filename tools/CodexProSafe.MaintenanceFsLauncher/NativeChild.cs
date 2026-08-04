using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CodexProSafeMaintenanceFsLauncher
{
    internal sealed class NativeChild : IDisposable
    {
        private const uint StartfUseStdHandles = 0x100;
        private const uint CreateNoWindow = 0x08000000;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint CreateSuspended = 0x00000004;
        private const uint HandleFlagInherit = 1;
        private const uint JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;
        private const uint TerminateExitCode = 70;

        [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { internal int Length; internal IntPtr SecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
        {
            internal int Size; internal string Reserved; internal string Desktop; internal string Title; internal uint X; internal uint Y; internal uint XSize; internal uint YSize;
            internal uint XCountChars; internal uint YCountChars; internal uint FillAttribute; internal uint Flags; internal ushort ShowWindow; internal ushort Reserved2;
            internal IntPtr Reserved2Pointer; internal IntPtr StdInput; internal IntPtr StdOutput; internal IntPtr StdError;
        }
        [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { internal StartupInfo StartupInfo; internal IntPtr AttributeList; }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { internal IntPtr Process; internal IntPtr Thread; internal uint ProcessId; internal uint ThreadId; }
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimitInformation
        {
            internal long PerProcessUserTimeLimit; internal long PerJobUserTimeLimit; internal uint LimitFlags;
            internal UIntPtr MinimumWorkingSetSize; internal UIntPtr MaximumWorkingSetSize; internal uint ActiveProcessLimit;
            internal IntPtr Affinity; internal uint PriorityClass; internal uint SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters
        {
            internal ulong ReadOperationCount; internal ulong WriteOperationCount; internal ulong OtherOperationCount;
            internal ulong ReadTransferCount; internal ulong WriteTransferCount; internal ulong OtherTransferCount;
        }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimitInformation
        {
            internal BasicLimitInformation BasicLimitInformation; internal IoCounters IoInfo;
            internal UIntPtr ProcessMemoryLimit; internal UIntPtr JobMemoryLimit; internal UIntPtr PeakProcessMemoryUsed; internal UIntPtr PeakJobMemoryUsed;
        }

        private IntPtr process;
        private IntPtr job;
        private readonly FileStream input;
        private readonly FileStream output;
        private readonly FileStream error;
        private readonly Thread errorDrain;
        private volatile bool stderrInvalid;

        private NativeChild(IntPtr process, IntPtr job, FileStream input, FileStream output, FileStream error)
        {
            this.process = process; this.job = job; this.input = input; this.output = output; this.error = error;
            errorDrain = new Thread(DrainError) { IsBackground = true, Name = "maintenance-launcher-stderr" };
            errorDrain.Start();
        }

        internal Stream Input { get { return input; } }
        internal Stream Output { get { return output; } }
        internal bool StderrInvalid { get { return stderrInvalid; } }

        internal static NativeChild Start(PackageLock package)
        {
            SecurityAttributes inheritable = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), InheritHandle = true };
            IntPtr childInput = IntPtr.Zero, parentInput = IntPtr.Zero, parentOutput = IntPtr.Zero, childOutput = IntPtr.Zero, parentError = IntPtr.Zero, childError = IntPtr.Zero;
            IntPtr process = IntPtr.Zero, thread = IntPtr.Zero, job = IntPtr.Zero;
            IntPtr attributeList = IntPtr.Zero, handleList = IntPtr.Zero;
            FileStream input = null, output = null, error = null;
            try
            {
                if (SelfTestHooks.BeforeCreateProcess != null) SelfTestHooks.BeforeCreateProcess();
                if (!CreatePipe(out childInput, out parentInput, ref inheritable, 0) || !SetHandleInformation(parentInput, HandleFlagInherit, 0)) throw new IOException();
                if (!CreatePipe(out parentOutput, out childOutput, ref inheritable, 0) || !SetHandleInformation(parentOutput, HandleFlagInherit, 0)) throw new IOException();
                if (!CreatePipe(out parentError, out childError, ref inheritable, 0) || !SetHandleInformation(parentError, HandleFlagInherit, 0)) throw new IOException();
                StartupInfoEx startup = new StartupInfoEx();
                startup.StartupInfo = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfoEx)), Flags = StartfUseStdHandles, StdInput = childInput, StdOutput = childOutput, StdError = childError };
                UIntPtr attributeSize = UIntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeSize);
                attributeList = Marshal.AllocHGlobal(checked((int)attributeSize.ToUInt64()));
                if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeSize)) throw new IOException();
                handleList = Marshal.AllocHGlobal(IntPtr.Size * 3);
                Marshal.WriteIntPtr(handleList, 0, childInput); Marshal.WriteIntPtr(handleList, IntPtr.Size, childOutput); Marshal.WriteIntPtr(handleList, IntPtr.Size * 2, childError);
                if (!UpdateProcThreadAttribute(attributeList, 0, new IntPtr(0x00020002), handleList, new UIntPtr((uint)(IntPtr.Size * 3)), IntPtr.Zero, IntPtr.Zero)) throw new IOException();
                startup.AttributeList = attributeList;
                job = CreateKillJob();
                ProcessInformation information;
                IntPtr environment = BuildEnvironment();
                try
                {
                    StringBuilder command = new StringBuilder("\"" + PackageLock.HelperName + "\" --serve-maintenance-fs");
                    if (!CreateProcessW(package.HelperPath, command, IntPtr.Zero, IntPtr.Zero, true, CreateNoWindow | CreateUnicodeEnvironment | ExtendedStartupInfoPresent | CreateSuspended, environment, package.DirectoryPath, ref startup, out information)) throw new IOException();
                }
                finally { Marshal.FreeHGlobal(environment); }
                DeleteProcThreadAttributeList(attributeList); Marshal.FreeHGlobal(attributeList); attributeList = IntPtr.Zero;
                Marshal.FreeHGlobal(handleList); handleList = IntPtr.Zero;
                process = information.Process; thread = information.Thread;
                CloseHandle(childInput); childInput = IntPtr.Zero;
                CloseHandle(childOutput); childOutput = IntPtr.Zero;
                CloseHandle(childError); childError = IntPtr.Zero;

                if (!AssignProcessToJobObject(job, process)) throw new IOException();
                VerifyProcessImage(process, package);
                package.Revalidate();
                if (ResumeThread(thread) == UInt32.MaxValue) throw new IOException();

                input = new FileStream(new SafeFileHandle(parentInput, true), FileAccess.Write, 4096, false); parentInput = IntPtr.Zero;
                output = new FileStream(new SafeFileHandle(parentOutput, true), FileAccess.Read, 4096, false); parentOutput = IntPtr.Zero;
                error = new FileStream(new SafeFileHandle(parentError, true), FileAccess.Read, 4096, false); parentError = IntPtr.Zero;
                CloseHandle(thread); thread = IntPtr.Zero;
                return new NativeChild(process, job, input, output, error);
            }
            catch
            {
                if (process != IntPtr.Zero) TerminateProcess(process, TerminateExitCode);
                if (input != null) input.Dispose(); if (output != null) output.Dispose(); if (error != null) error.Dispose();
                CloseIfValid(parentInput); CloseIfValid(parentOutput); CloseIfValid(parentError); CloseIfValid(childInput); CloseIfValid(childOutput); CloseIfValid(childError);
                CloseIfValid(thread); CloseIfValid(process); CloseIfValid(job);
                if (attributeList != IntPtr.Zero) { DeleteProcThreadAttributeList(attributeList); Marshal.FreeHGlobal(attributeList); }
                if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
                throw;
            }
        }

        internal bool Wait(int milliseconds) { return process != IntPtr.Zero && WaitForSingleObject(process, (uint)milliseconds) == 0; }
        internal void Terminate() { if (process != IntPtr.Zero) TerminateProcess(process, TerminateExitCode); }

        private void DrainError()
        {
            try
            {
                byte[] buffer = new byte[1024]; int total = 0;
                while (true)
                {
                    int read = error.Read(buffer, 0, buffer.Length); if (read == 0) break;
                    total = checked(total + read); Array.Clear(buffer, 0, buffer.Length);
                    if (total > 8192) { stderrInvalid = true; Terminate(); break; }
                }
                Array.Clear(buffer, 0, buffer.Length);
            }
            catch { if (process != IntPtr.Zero) stderrInvalid = true; }
        }

        public void Dispose()
        {
            try { input.Dispose(); } catch { }
            if (!Wait(2000)) Terminate();
            Wait(2000);
            try { output.Dispose(); } catch { }
            try { error.Dispose(); } catch { }
            if (errorDrain != null && errorDrain.IsAlive) errorDrain.Join(500);
            CloseIfValid(process); process = IntPtr.Zero;
            CloseIfValid(job); job = IntPtr.Zero;
        }

        private static void VerifyProcessImage(IntPtr process, PackageLock package)
        {
            if (SelfTestHooks.ForceImageMismatch) throw new IOException();
            StringBuilder path = new StringBuilder(32768); int size = path.Capacity;
            if (!QueryFullProcessImageNameW(process, 0, path, ref size) || !String.Equals(path.ToString(), package.HelperPath, StringComparison.OrdinalIgnoreCase)) throw new IOException();
            using (SafeFileHandle image = CreateFileW(path.ToString(), 0x80000000 | 0x80, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
            {
                if (image.IsInvalid) throw new IOException();
                FileIdentity expected = NativeFiles.Identity(package.HelperHandle);
                FileIdentity actual = NativeFiles.Identity(image);
                if (expected.Volume != actual.Volume || expected.IndexHigh != actual.IndexHigh || expected.IndexLow != actual.IndexLow) throw new IOException();
            }
        }

        private static IntPtr CreateKillJob()
        {
            IntPtr job = CreateJobObjectW(IntPtr.Zero, null); if (job == IntPtr.Zero) throw new IOException();
            ExtendedLimitInformation limits = new ExtendedLimitInformation(); limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            int size = Marshal.SizeOf(typeof(ExtendedLimitInformation)); IntPtr value = Marshal.AllocHGlobal(size);
            try { Marshal.StructureToPtr(limits, value, false); if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, value, (uint)size)) throw new IOException(); }
            catch { CloseHandle(job); throw; }
            finally { Marshal.FreeHGlobal(value); }
            return job;
        }

        private static IntPtr BuildEnvironment()
        {
            SortedDictionary<string, string> values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" }) { string value = Environment.GetEnvironmentVariable(name); if (!String.IsNullOrEmpty(value) && value.IndexOf('\0') < 0) values[name] = value; }
            StringBuilder block = new StringBuilder(); foreach (KeyValuePair<string, string> item in values) block.Append(item.Key).Append('=').Append(item.Value).Append('\0'); block.Append('\0');
            return Marshal.StringToHGlobalUni(block.ToString());
        }

        private static void CloseIfValid(IntPtr handle) { if (handle != IntPtr.Zero && handle != new IntPtr(-1)) CloseHandle(handle); }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SecurityAttributes attributes, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string currentDirectory, ref StartupInfoEx startup, out ProcessInformation information);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder path, ref int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref UIntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, UIntPtr size, IntPtr previousValue, IntPtr returnSize);
        [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObjectW(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, uint informationClass, IntPtr information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    }

    internal static class SelfTestHooks
    {
        internal static Action BeforeCreateProcess;
        internal static bool ForceImageMismatch;
        internal static void Reset() { BeforeCreateProcess = null; ForceImageMismatch = false; }
    }
}
