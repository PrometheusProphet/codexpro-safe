using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexProSafeDiagnosticHelper
{
    internal sealed class MaintenanceIdentity
    {
        internal ulong VolumeSerial;
        internal byte[] FileId;
        internal uint Attributes;
        internal long Length;
        internal long LastWriteFileTime;
        internal long ChangeFileTime;
        internal uint LinkCount;
        internal uint FileType;

        internal bool IsDirectory { get { return (Attributes & MaintenanceNativeMethods.FileAttributeDirectory) != 0; } }
        internal bool IsReparse { get { return (Attributes & MaintenanceNativeMethods.FileAttributeReparsePoint) != 0; } }

        internal MaintenanceIdentity Clone()
        {
            return new MaintenanceIdentity
            {
                VolumeSerial = VolumeSerial,
                FileId = FileId == null ? null : (byte[])FileId.Clone(),
                Attributes = Attributes,
                Length = Length,
                LastWriteFileTime = LastWriteFileTime,
                ChangeFileTime = ChangeFileTime,
                LinkCount = LinkCount,
                FileType = FileType
            };
        }
    }

    internal sealed class MaintenanceDirectoryEntry
    {
        internal string Name;
        internal MaintenanceIdentity Identity;
    }

    internal static class MaintenanceNativeMethods
    {
        internal const uint FileListDirectory = 0x0001;
        internal const uint FileReadData = 0x0001;
        internal const uint FileReadAttributes = 0x0080;
        internal const uint Synchronize = 0x00100000;
        internal const uint FileShareRead = 0x00000001;
        internal const uint FileShareWrite = 0x00000002;
        internal const uint FileShareDelete = 0x00000004;
        internal const uint OpenExisting = 3;
        internal const uint FileFlagBackupSemantics = 0x02000000;
        internal const uint FileFlagOpenReparsePoint = 0x00200000;
        internal const uint FileAttributeReadOnly = 0x00000001;
        internal const uint FileAttributeHidden = 0x00000002;
        internal const uint FileAttributeSystem = 0x00000004;
        internal const uint FileAttributeDirectory = 0x00000010;
        internal const uint FileAttributeArchive = 0x00000020;
        internal const uint FileAttributeDevice = 0x00000040;
        internal const uint FileAttributeReparsePoint = 0x00000400;
        internal const uint DiskFileType = 1;
        internal const int ErrorFileNotFound = 2;
        internal const int ErrorPathNotFound = 3;
        internal const int ErrorNoMoreFiles = 18;

        private const uint ObjCaseInsensitive = 0x00000040;
        private const uint FileOpen = 1;
        private const uint FileDirectoryFile = 0x00000001;
        private const uint FileNonDirectoryFile = 0x00000040;
        private const uint FileSynchronousIoNonAlert = 0x00000020;
        private const uint FileOpenReparsePoint = 0x00200000;
        private const int DirectoryBufferSize = 64 * 1024;
        private const int MaximumNameCharacters = 255;

        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeString
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ObjectAttributes
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatusBlock
        {
            public IntPtr Status;
            public UIntPtr Information;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, IntPtr information, uint size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeInformationByHandleW")]
        private static extern bool GetVolumeInformationByHandle(SafeFileHandle handle, StringBuilder volumeName, uint volumeNameSize,
            out uint serial, out uint maximumComponentLength, out uint filesystemFlags, StringBuilder filesystemName, uint filesystemNameSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetFileType(SafeFileHandle handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetDriveTypeW")]
        private static extern uint GetDriveType(string rootPath);

        [DllImport("ntdll.dll")]
        private static extern int NtCreateFile(out IntPtr fileHandle, uint desiredAccess, ref ObjectAttributes objectAttributes,
            out IoStatusBlock ioStatusBlock, IntPtr allocationSize, uint fileAttributes, uint shareAccess,
            uint createDisposition, uint createOptions, IntPtr eaBuffer, uint eaLength);

        internal static SafeFileHandle OpenRoot(string canonicalPath)
        {
            SafeFileHandle handle = CreateFile(
                canonicalPath,
                FileListDirectory | FileReadAttributes | Synchronize,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new IOException();
            }
            return handle;
        }

        internal static bool IsFixedLocalDrive(string canonicalPath)
        {
            return canonicalPath != null && canonicalPath.Length >= 3 && GetDriveType(canonicalPath.Substring(0, 3)) == 3;
        }

        internal static SafeFileHandle OpenRelativeDirectory(SafeFileHandle parent, string basename)
        {
            return OpenRelative(parent, basename, FileListDirectory | FileReadAttributes | Synchronize, FileDirectoryFile);
        }

        internal static SafeFileHandle OpenRelativeFile(SafeFileHandle parent, string basename, bool readContent)
        {
            uint access = FileReadAttributes | Synchronize;
            if (readContent) access |= FileReadData;
            return OpenRelative(parent, basename, access, FileNonDirectoryFile);
        }

        private static SafeFileHandle OpenRelative(SafeFileHandle parent, string basename, uint access, uint typeOption)
        {
            MaintenanceFilesystemProvider.ValidateBasename(basename);
            IntPtr nameBuffer = IntPtr.Zero;
            IntPtr unicodePointer = IntPtr.Zero;
            try
            {
                nameBuffer = Marshal.StringToHGlobalUni(basename);
                UnicodeString unicode = new UnicodeString
                {
                    Length = checked((ushort)(basename.Length * 2)),
                    MaximumLength = checked((ushort)((basename.Length + 1) * 2)),
                    Buffer = nameBuffer
                };
                unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(UnicodeString)));
                Marshal.StructureToPtr(unicode, unicodePointer, false);
                ObjectAttributes attributes = new ObjectAttributes
                {
                    Length = Marshal.SizeOf(typeof(ObjectAttributes)),
                    RootDirectory = parent.DangerousGetHandle(),
                    ObjectName = unicodePointer,
                    Attributes = ObjCaseInsensitive
                };
                IntPtr raw;
                IoStatusBlock statusBlock;
                int status = NtCreateFile(
                    out raw,
                    access,
                    ref attributes,
                    out statusBlock,
                    IntPtr.Zero,
                    0,
                    FileShareRead | FileShareWrite | FileShareDelete,
                    FileOpen,
                    typeOption | FileSynchronousIoNonAlert | FileOpenReparsePoint,
                    IntPtr.Zero,
                    0);
                if (status < 0 || raw == IntPtr.Zero || raw == new IntPtr(-1)) throw new IOException();
                return new SafeFileHandle(raw, true);
            }
            finally
            {
                if (unicodePointer != IntPtr.Zero) Marshal.FreeHGlobal(unicodePointer);
                if (nameBuffer != IntPtr.Zero) Marshal.FreeHGlobal(nameBuffer);
            }
        }

        internal static MaintenanceIdentity CaptureIdentity(SafeFileHandle handle)
        {
            uint attributes;
            uint tag;
            GetAttributeTag(handle, out attributes, out tag);
            ulong volume;
            byte[] fileId;
            GetFileId(handle, out volume, out fileId);
            long length;
            uint links;
            GetStandardInfo(handle, out length, out links);
            long lastWrite;
            long changeTime;
            uint basicAttributes;
            GetBasicInfo(handle, out lastWrite, out changeTime, out basicAttributes);
            if (attributes != basicAttributes || (attributes & FileAttributeDevice) != 0) throw new IOException();
            return new MaintenanceIdentity
            {
                VolumeSerial = volume,
                FileId = fileId,
                Attributes = attributes,
                Length = length,
                LastWriteFileTime = lastWrite,
                ChangeFileTime = changeTime,
                LinkCount = links,
                FileType = GetFileType(handle)
            };
        }

        internal static string FinalPath(SafeFileHandle handle)
        {
            StringBuilder value = new StringBuilder(32768);
            try
            {
                uint length = GetFinalPathNameByHandle(handle, value, (uint)value.Capacity, 0);
                if (length == 0 || length >= value.Capacity) throw new IOException();
                return value.ToString();
            }
            finally
            {
                for (int index = 0; index < value.Length; index++) value[index] = '\0';
            }
        }

        internal static string FilesystemName(SafeFileHandle handle)
        {
            StringBuilder filesystem = new StringBuilder(64);
            try
            {
                uint serial;
                uint maximumComponentLength;
                uint flags;
                if (!GetVolumeInformationByHandle(handle, null, 0, out serial,
                    out maximumComponentLength, out flags, filesystem, (uint)filesystem.Capacity)) throw new IOException();
                if (maximumComponentLength == 0 || maximumComponentLength > MaximumNameCharacters) throw new IOException();
                return filesystem.ToString();
            }
            finally
            {
                for (int index = 0; index < filesystem.Length; index++) filesystem[index] = '\0';
            }
        }

        internal static List<MaintenanceDirectoryEntry> Enumerate(SafeFileHandle directory, int maximumEntries,
            Func<bool> expired, out int visitedEntries, out string limitation)
        {
            visitedEntries = 0;
            limitation = null;
            IntPtr buffer = Marshal.AllocHGlobal(DirectoryBufferSize);
            try
            {
                List<MaintenanceDirectoryEntry> entries = new List<MaintenanceDirectoryEntry>();
                bool restart = true;
                while (true)
                {
                    if (expired()) { limitation = "duration"; return null; }
                    bool ok = GetFileInformationByHandleEx(directory, restart ? 20 : 19, buffer, (uint)DirectoryBufferSize);
                    restart = false;
                    if (!ok)
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == ErrorNoMoreFiles) break;
                        throw new IOException();
                    }
                    int offset = 0;
                    while (true)
                    {
                        if (expired()) { limitation = "duration"; return null; }
                        if (offset < 0 || offset > DirectoryBufferSize - 88) throw new IOException();
                        int next = Marshal.ReadInt32(buffer, offset);
                        long lastWrite = Marshal.ReadInt64(buffer, offset + 24);
                        long changeTime = Marshal.ReadInt64(buffer, offset + 32);
                        long length = Marshal.ReadInt64(buffer, offset + 40);
                        uint attributes = unchecked((uint)Marshal.ReadInt32(buffer, offset + 56));
                        int nameBytes = Marshal.ReadInt32(buffer, offset + 60);
                        if (nameBytes < 2 || (nameBytes & 1) != 0 || nameBytes > MaximumNameCharacters * 2 || offset + 88 + nameBytes > DirectoryBufferSize)
                            throw new IOException();
                        string name = Marshal.PtrToStringUni(IntPtr.Add(buffer, offset + 88), nameBytes / 2);
                        if (name != "." && name != "..")
                        {
                            if (visitedEntries >= maximumEntries) { limitation = "entries"; return null; }
                            MaintenanceFilesystemProvider.ValidateBasename(name);
                            byte[] fileId = new byte[16];
                            Marshal.Copy(IntPtr.Add(buffer, offset + 72), fileId, 0, fileId.Length);
                            entries.Add(new MaintenanceDirectoryEntry
                            {
                                Name = name,
                                Identity = new MaintenanceIdentity
                                {
                                    FileId = fileId,
                                    Attributes = attributes,
                                    Length = Math.Max(0, length),
                                    LastWriteFileTime = lastWrite,
                                    ChangeFileTime = changeTime
                                }
                            });
                            visitedEntries++;
                        }
                        if (next == 0) break;
                        if (next < 88 || offset + next <= offset || offset + next >= DirectoryBufferSize) throw new IOException();
                        offset += next;
                    }
                }
                return entries;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static void GetAttributeTag(SafeFileHandle handle, out uint attributes, out uint tag)
        {
            IntPtr buffer = Marshal.AllocHGlobal(8);
            try
            {
                if (!GetFileInformationByHandleEx(handle, 9, buffer, 8)) throw new IOException();
                attributes = unchecked((uint)Marshal.ReadInt32(buffer, 0));
                tag = unchecked((uint)Marshal.ReadInt32(buffer, 4));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static void GetFileId(SafeFileHandle handle, out ulong volume, out byte[] fileId)
        {
            IntPtr buffer = Marshal.AllocHGlobal(24);
            try
            {
                if (!GetFileInformationByHandleEx(handle, 18, buffer, 24)) throw new IOException();
                volume = unchecked((ulong)Marshal.ReadInt64(buffer, 0));
                fileId = new byte[16];
                Marshal.Copy(IntPtr.Add(buffer, 8), fileId, 0, fileId.Length);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static void GetStandardInfo(SafeFileHandle handle, out long length, out uint links)
        {
            IntPtr buffer = Marshal.AllocHGlobal(24);
            try
            {
                if (!GetFileInformationByHandleEx(handle, 1, buffer, 24)) throw new IOException();
                length = Marshal.ReadInt64(buffer, 8);
                links = unchecked((uint)Marshal.ReadInt32(buffer, 16));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static void GetBasicInfo(SafeFileHandle handle, out long lastWrite, out long changeTime, out uint attributes)
        {
            IntPtr buffer = Marshal.AllocHGlobal(40);
            try
            {
                if (!GetFileInformationByHandleEx(handle, 0, buffer, 40)) throw new IOException();
                lastWrite = Marshal.ReadInt64(buffer, 16);
                changeTime = Marshal.ReadInt64(buffer, 24);
                attributes = unchecked((uint)Marshal.ReadInt32(buffer, 32));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
}
