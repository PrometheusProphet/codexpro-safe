using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CodexProSafeMaintenanceFsLauncher
{
    internal sealed class FileIdentity
    {
        internal uint Volume;
        internal uint IndexHigh;
        internal uint IndexLow;
        internal uint Attributes;
        internal uint Links;
        internal long Size;
        internal long WriteTime;

        internal bool Same(FileIdentity other)
        {
            return other != null && Volume == other.Volume && IndexHigh == other.IndexHigh && IndexLow == other.IndexLow &&
                Attributes == other.Attributes && Links == other.Links && Size == other.Size && WriteTime == other.WriteTime;
        }
    }

    internal sealed class PackageLock : IDisposable
    {
        internal const string HelperName = "CodexProSafe.DiagnosticHelper.exe";
        internal const string DiagnosticProtocol = "codexpro-diagnostic-v1";
        internal const string MaintenanceProtocol = "codexpro-maintenance-fs-v1";

        private SafeFileHandle directory;
        private FileStream manifest;
        private FileStream helper;
        private readonly FileIdentity manifestIdentity;
        private readonly FileIdentity helperIdentity;
        private readonly FileIdentity directoryIdentity;

        internal string DirectoryPath { get; private set; }
        internal string HelperPath { get; private set; }
        internal SafeFileHandle HelperHandle { get { return helper.SafeFileHandle; } }

        private PackageLock(SafeFileHandle directory, FileStream manifest, FileStream helper, string directoryPath, string helperPath, FileIdentity directoryIdentity, FileIdentity manifestIdentity, FileIdentity helperIdentity)
        {
            this.directory = directory;
            this.manifest = manifest;
            this.helper = helper;
            DirectoryPath = directoryPath;
            HelperPath = helperPath;
            this.manifestIdentity = manifestIdentity;
            this.helperIdentity = helperIdentity;
            this.directoryIdentity = directoryIdentity;
        }

        internal static PackageLock Open(string manifestPath, string expectedManifestSha256, string expectedMaintenanceProtocol)
        {
            string canonicalManifest = NativeFiles.CanonicalLocalPath(manifestPath);
            if (!NativeFiles.ValidDigest(expectedManifestSha256) || expectedMaintenanceProtocol != MaintenanceProtocol) throw new InvalidDataException();
            string parent = Path.GetDirectoryName(canonicalManifest);
            string basename = Path.GetFileName(canonicalManifest);
            if (String.IsNullOrEmpty(parent) || String.IsNullOrEmpty(basename)) throw new InvalidDataException();

            SafeFileHandle directory = null;
            FileStream manifest = null;
            FileStream helper = null;
            try
            {
                directory = NativeFiles.OpenDirectory(parent);
                FileIdentity directoryIdentity = NativeFiles.Identity(directory);
                NativeFiles.RequireFixedNtfs(parent);
                manifest = NativeFiles.OpenRelative(directory, basename);
                FileIdentity manifestIdentity = NativeFiles.ValidateRegularFile(manifest.SafeFileHandle, canonicalManifest, 1, 64 * 1024);
                byte[] manifestBytes = NativeFiles.ReadAll(manifest, 64 * 1024);
                Dictionary<string, object> contract;
                try
                {
                    if (!NativeFiles.FixedTimeEquals(NativeFiles.Hash(manifestBytes), expectedManifestSha256)) throw new InvalidDataException();
                    contract = StrictJson.ParseObject(manifestBytes);
                }
                finally { Array.Clear(manifestBytes, 0, manifestBytes.Length); }
                if (!NativeFiles.ExactKeys(contract, "protocolVersion", "maintenanceFsProtocolVersion", "executable", "sha256") ||
                    !Object.Equals(contract["protocolVersion"], DiagnosticProtocol) ||
                    !Object.Equals(contract["maintenanceFsProtocolVersion"], MaintenanceProtocol) ||
                    !Object.Equals(contract["executable"], HelperName) ||
                    !(contract["sha256"] is string) || !NativeFiles.ValidDigest((string)contract["sha256"])) throw new InvalidDataException();

                string helperPath = Path.Combine(parent, HelperName);
                helper = NativeFiles.OpenRelative(directory, HelperName);
                FileIdentity helperIdentity = NativeFiles.ValidateRegularFile(helper.SafeFileHandle, helperPath, 1, 128L * 1024 * 1024);
                string helperHash = NativeFiles.Hash(helper);
                if (!NativeFiles.FixedTimeEquals(helperHash, (string)contract["sha256"])) throw new InvalidDataException();
                NativeFiles.Revalidate(manifest.SafeFileHandle, manifestIdentity);
                NativeFiles.Revalidate(helper.SafeFileHandle, helperIdentity);
                return new PackageLock(directory, manifest, helper, parent, helperPath, directoryIdentity, manifestIdentity, helperIdentity);
            }
            catch
            {
                if (helper != null) helper.Dispose();
                if (manifest != null) manifest.Dispose();
                if (directory != null) directory.Dispose();
                throw;
            }
        }

        internal void Revalidate()
        {
            NativeFiles.Revalidate(directory, directoryIdentity);
            NativeFiles.Revalidate(manifest.SafeFileHandle, manifestIdentity);
            NativeFiles.Revalidate(helper.SafeFileHandle, helperIdentity);
        }

        public void Dispose()
        {
            if (helper != null) { helper.Dispose(); helper = null; }
            if (manifest != null) { manifest.Dispose(); manifest = null; }
            if (directory != null) { directory.Dispose(); directory = null; }
        }
    }

    internal static class NativeFiles
    {
        private const uint GenericRead = 0x80000000;
        private const uint Synchronize = 0x00100000;
        private const uint ReadAttributes = 0x00000080;
        private const uint ShareRead = 1;
        private const uint ShareWrite = 2;
        private const uint OpenExisting = 3;
        private const uint BackupSemantics = 0x02000000;
        private const uint OpenReparsePoint = 0x00200000;
        private const uint FileOpen = 1;
        private const uint DirectoryFile = 1;
        private const uint NonDirectoryFile = 0x40;
        private const uint SynchronousNonAlert = 0x20;
        private const uint AttributeReparsePoint = 0x400;
        private const uint AttributeDirectory = 0x10;
        private const uint ObjectCaseInsensitive = 0x40;
        private const uint DriveFixed = 3;

        [StructLayout(LayoutKind.Sequential)] private struct IoStatusBlock { internal IntPtr Status; internal IntPtr Information; }
        [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { internal ushort Length; internal ushort MaximumLength; internal IntPtr Buffer; }
        [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes
        {
            internal int Length; internal IntPtr RootDirectory; internal IntPtr ObjectName; internal uint Attributes;
            internal IntPtr SecurityDescriptor; internal IntPtr SecurityQualityOfService;
        }
        [StructLayout(LayoutKind.Sequential)] private struct ByHandleFileInformation
        {
            internal uint Attributes; internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME AccessTime; internal System.Runtime.InteropServices.ComTypes.FILETIME WriteTime;
            internal uint VolumeSerial; internal uint SizeHigh; internal uint SizeLow; internal uint Links; internal uint IndexHigh; internal uint IndexLow;
        }

        internal static string CanonicalLocalPath(string value)
        {
            if (String.IsNullOrEmpty(value) || value.IndexOf('\0') >= 0 || value.StartsWith("\\\\", StringComparison.Ordinal) ||
                value.StartsWith("\\\\?\\", StringComparison.Ordinal) || value.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
                value.Length < 4 || !Char.IsLetter(value[0]) || value[1] != ':' || (value[2] != '\\' && value[2] != '/')) throw new InvalidDataException();
            if (value.IndexOf(':', 2) >= 0) throw new InvalidDataException();
            string full = Path.GetFullPath(value).Replace('/', '\\');
            if (!String.Equals(full, value.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase) || full.Length > 32767) throw new InvalidDataException();
            return full;
        }

        internal static SafeFileHandle OpenDirectory(string path)
        {
            SafeFileHandle handle = CreateFileW(path, GenericRead | ReadAttributes | Synchronize, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw new IOException(); }
            FileIdentity identity = Identity(handle);
            if ((identity.Attributes & AttributeDirectory) == 0 || (identity.Attributes & AttributeReparsePoint) != 0 || GetFileType(handle) != 1) { handle.Dispose(); throw new IOException(); }
            string final = FinalPath(handle);
            if (!String.Equals(final, path, StringComparison.OrdinalIgnoreCase)) { handle.Dispose(); throw new IOException(); }
            return handle;
        }

        internal static FileStream OpenRelative(SafeFileHandle directory, string basename)
        {
            if (String.IsNullOrEmpty(basename) || basename == "." || basename == ".." || basename.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) throw new InvalidDataException();
            IntPtr nameBuffer = Marshal.StringToHGlobalUni(basename);
            IntPtr unicodePointer = IntPtr.Zero;
            try
            {
                UnicodeString name = new UnicodeString { Length = checked((ushort)(basename.Length * 2)), MaximumLength = checked((ushort)((basename.Length + 1) * 2)), Buffer = nameBuffer };
                unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(UnicodeString)));
                Marshal.StructureToPtr(name, unicodePointer, false);
                ObjectAttributes attributes = new ObjectAttributes
                {
                    Length = Marshal.SizeOf(typeof(ObjectAttributes)), RootDirectory = directory.DangerousGetHandle(), ObjectName = unicodePointer, Attributes = ObjectCaseInsensitive
                };
                IoStatusBlock status;
                SafeFileHandle file;
                int result = NtCreateFile(out file, GenericRead | ReadAttributes | Synchronize, ref attributes, out status, IntPtr.Zero, 0,
                    ShareRead, FileOpen, NonDirectoryFile | SynchronousNonAlert | OpenReparsePoint, IntPtr.Zero, 0);
                if (result < 0 || file == null || file.IsInvalid) { if (file != null) file.Dispose(); throw new IOException(); }
                return new FileStream(file, FileAccess.Read, 4096, false);
            }
            finally
            {
                if (unicodePointer != IntPtr.Zero) Marshal.FreeHGlobal(unicodePointer);
                Marshal.FreeHGlobal(nameBuffer);
            }
        }

        internal static FileIdentity ValidateRegularFile(SafeFileHandle handle, string expectedPath, uint links, long maxSize)
        {
            FileIdentity identity = Identity(handle);
            if (GetFileType(handle) != 1 || (identity.Attributes & (AttributeDirectory | AttributeReparsePoint)) != 0 || identity.Links != links || identity.Size <= 0 || identity.Size > maxSize) throw new IOException();
            if (!String.Equals(FinalPath(handle), expectedPath, StringComparison.OrdinalIgnoreCase)) throw new IOException();
            return identity;
        }

        internal static void Revalidate(SafeFileHandle handle, FileIdentity expected)
        {
            if (!expected.Same(Identity(handle))) throw new IOException();
        }

        internal static FileIdentity Identity(SafeFileHandle handle)
        {
            ByHandleFileInformation value;
            if (!GetFileInformationByHandle(handle, out value)) throw new IOException();
            long write = ((long)value.WriteTime.dwHighDateTime << 32) | (uint)value.WriteTime.dwLowDateTime;
            long size = ((long)value.SizeHigh << 32) | value.SizeLow;
            return new FileIdentity { Volume = value.VolumeSerial, IndexHigh = value.IndexHigh, IndexLow = value.IndexLow, Attributes = value.Attributes, Links = value.Links, Size = size, WriteTime = write };
        }

        internal static string FinalPath(SafeFileHandle handle)
        {
            StringBuilder value = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandleW(handle, value, value.Capacity, 0);
            if (length == 0 || length >= value.Capacity) throw new IOException();
            string path = value.ToString();
            if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)) path = path.Substring(4);
            if (path.StartsWith("UNC\\", StringComparison.OrdinalIgnoreCase)) throw new IOException();
            return path;
        }

        internal static byte[] ReadAll(FileStream stream, int maximum)
        {
            stream.Position = 0;
            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] chunk = new byte[4096];
                int total = 0;
                while (true)
                {
                    int read = stream.Read(chunk, 0, chunk.Length);
                    if (read == 0) break;
                    total = checked(total + read);
                    if (total > maximum) throw new IOException();
                    buffer.Write(chunk, 0, read);
                }
                Array.Clear(chunk, 0, chunk.Length);
                return buffer.ToArray();
            }
        }

        internal static string Hash(FileStream stream)
        {
            stream.Position = 0;
            using (SHA256 hash = SHA256.Create()) return Hex(hash.ComputeHash(stream));
        }

        internal static string Hash(byte[] bytes) { using (SHA256 hash = SHA256.Create()) return Hex(hash.ComputeHash(bytes)); }
        private static string Hex(byte[] bytes) { StringBuilder text = new StringBuilder(64); foreach (byte value in bytes) text.Append(value.ToString("x2")); Array.Clear(bytes, 0, bytes.Length); return text.ToString(); }
        internal static bool ValidDigest(string value) { if (value == null || value.Length != 64) return false; foreach (char c in value) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false; return true; }
        internal static bool FixedTimeEquals(string left, string right) { if (left == null || right == null || left.Length != right.Length) return false; int difference = 0; for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i]; return difference == 0; }
        internal static bool ExactKeys(Dictionary<string, object> value, params string[] keys) { if (value.Count != keys.Length) return false; foreach (string key in keys) if (!value.ContainsKey(key)) return false; return true; }

        internal static void RequireFixedNtfs(string path)
        {
            string root = Path.GetPathRoot(path);
            if (GetDriveTypeW(root) != DriveFixed) throw new IOException();
            StringBuilder fileSystem = new StringBuilder(32);
            if (!GetVolumeInformationW(root, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, fileSystem, fileSystem.Capacity) || !String.Equals(fileSystem.ToString(), "NTFS", StringComparison.Ordinal)) throw new IOException();
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("ntdll.dll")] private static extern int NtCreateFile(out SafeFileHandle fileHandle, uint desiredAccess, ref ObjectAttributes objectAttributes, out IoStatusBlock ioStatusBlock, IntPtr allocationSize, uint fileAttributes, uint shareAccess, uint createDisposition, uint createOptions, IntPtr eaBuffer, uint eaLength);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, int length, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(SafeFileHandle file);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetDriveTypeW(string root);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetVolumeInformationW(string root, StringBuilder volumeName, int volumeNameSize, IntPtr serial, IntPtr maximumComponentLength, IntPtr flags, StringBuilder fileSystemName, int fileSystemNameSize);
    }
}
