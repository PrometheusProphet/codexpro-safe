using Microsoft.Win32.SafeHandles;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexProSafeManager
{
    internal sealed class DiagnosticHelperLock : IDisposable
    {
        private readonly SafeFileHandle directoryHandle;
        private readonly FileStream helperStream;

        internal DiagnosticHelperLock(SafeFileHandle directoryHandle, FileStream helperStream)
        {
            this.directoryHandle = directoryHandle;
            this.helperStream = helperStream;
        }

        public long Length { get { return helperStream.Length; } }

        public void Dispose()
        {
            helperStream.Dispose();
            directoryHandle.Dispose();
        }
    }

    internal static class DiagnosticHelperTrust
    {
        internal const string ProtocolVersion = "codexpro-diagnostic-v1";
        internal const string HelperFileName = "CodexProSafe.DiagnosticHelper.exe";
        internal const string ManifestFileName = "CodexProSafe.DiagnosticHelper.json";
        private const uint GenericRead = 0x80000000;
        private const uint FileReadAttributes = 0x00000080;
        private const uint Synchronize = 0x00100000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const uint FileAttributeDirectory = 0x00000010;
        private const uint FileAttributeReparsePoint = 0x00000400;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;

        private sealed class HelperManifest
        {
            public string protocolVersion { get; set; }
            public string executable { get; set; }
            public string sha256 { get; set; }
        }

        public static void SealInstalledPackage(AppSettings settings, string managerExecutable)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(managerExecutable));
            string helper = Path.Combine(directory, HelperFileName);
            string manifestPath = Path.Combine(directory, ManifestFileName);
            if (!File.Exists(manifestPath)) throw new InvalidOperationException("The Manager diagnostic helper package is incomplete.");
            HelperManifest manifest = new JavaScriptSerializer().Deserialize<HelperManifest>(File.ReadAllText(manifestPath));
            if (manifest == null || manifest.protocolVersion != ProtocolVersion || manifest.executable != HelperFileName || !ValidHash(manifest.sha256))
                throw new InvalidOperationException("The Manager diagnostic helper manifest is invalid.");
            using (DiagnosticHelperLock packageLock = OpenPackageLock(directory, helper, manifest.sha256))
            {
                settings.DiagnosticHelperPath = helper;
                settings.DiagnosticHelperProtocolVersion = ProtocolVersion;
                settings.DiagnosticHelperSha256 = manifest.sha256.ToLowerInvariant();
            }
        }

        public static DiagnosticHelperLock OpenVerifiedLock(AppSettings settings, string managerExecutable)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(managerExecutable));
            string expected = Path.Combine(directory, HelperFileName);
            string savedPath;
            try { savedPath = Path.GetFullPath(settings.DiagnosticHelperPath ?? String.Empty); }
            catch { throw new InvalidOperationException("The saved diagnostic helper path is invalid. Reinstall the Manager."); }
            if (!String.Equals(savedPath, expected, StringComparison.OrdinalIgnoreCase) ||
                settings.DiagnosticHelperProtocolVersion != ProtocolVersion || !ValidHash(settings.DiagnosticHelperSha256))
                throw new InvalidOperationException("The saved diagnostic helper trust contract is missing or invalid. Reinstall the Manager before enabling diagnostics.");
            return OpenPackageLock(directory, expected, settings.DiagnosticHelperSha256);
        }

        private static DiagnosticHelperLock OpenPackageLock(string directory, string helper, string expectedHash)
        {
            SafeFileHandle directoryHandle = OpenHandle(directory, FileReadAttributes | Synchronize,
                FileShareRead | FileShareWrite, FileFlagBackupSemantics | FileFlagOpenReparsePoint);
            try
            {
                VerifyHandle(directoryHandle, directory, true, false);
                SafeFileHandle helperHandle = OpenHandle(helper, GenericRead, FileShareRead, FileFlagOpenReparsePoint);
                try
                {
                    VerifyHandle(helperHandle, helper, false, true);
                    FileStream stream = new FileStream(helperHandle, FileAccess.Read, 65536, false);
                    try
                    {
                        string actual = HashStream(stream);
                        if (!String.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("The diagnostic helper fingerprint changed. Reinstall the Manager before enabling diagnostics.");
                        stream.Position = 0;
                        return new DiagnosticHelperLock(directoryHandle, stream);
                    }
                    catch
                    {
                        stream.Dispose();
                        throw;
                    }
                }
                catch
                {
                    helperHandle.Dispose();
                    throw;
                }
            }
            catch
            {
                directoryHandle.Dispose();
                throw;
            }
        }

        private static SafeFileHandle OpenHandle(string path, uint access, uint share, uint flags)
        {
            SafeFileHandle handle = CreateFile(path, access, share, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new InvalidOperationException("The diagnostic helper package could not be opened safely.");
            }
            return handle;
        }

        private static void VerifyHandle(SafeFileHandle handle, string expectedPath, bool requireDirectory, bool requireSingleLink)
        {
            uint attributes;
            uint tag;
            GetAttributeTag(handle, out attributes, out tag);
            bool directory = (attributes & FileAttributeDirectory) != 0;
            if (directory != requireDirectory || (attributes & FileAttributeReparsePoint) != 0)
                throw new InvalidOperationException("The diagnostic helper package contains a reparse point or unexpected object type.");
            if (requireSingleLink)
            {
                uint links = GetLinkCount(handle);
                if (links != 1) throw new InvalidOperationException("The diagnostic helper executable has ambiguous hard-link identity.");
            }
            StringBuilder finalName = new StringBuilder(1024);
            uint result = GetFinalPathNameByHandle(handle, finalName, (uint)finalName.Capacity, 0);
            if (result == 0 || result >= finalName.Capacity ||
                !String.Equals(NormalizePath(finalName.ToString()), NormalizePath(Path.GetFullPath(expectedPath)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The diagnostic helper package resolved outside its expected app-local path.");
        }

        private static string NormalizePath(string value)
        {
            string normalized = value;
            if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal)) normalized = normalized.Substring(4);
            return normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static void GetAttributeTag(SafeFileHandle handle, out uint attributes, out uint tag)
        {
            IntPtr buffer = Marshal.AllocHGlobal(8);
            try
            {
                if (!GetFileInformationByHandleEx(handle, 9, buffer, 8)) throw new InvalidOperationException("The diagnostic helper object attributes are unavailable.");
                attributes = unchecked((uint)Marshal.ReadInt32(buffer, 0));
                tag = unchecked((uint)Marshal.ReadInt32(buffer, 4));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static uint GetLinkCount(SafeFileHandle handle)
        {
            IntPtr buffer = Marshal.AllocHGlobal(24);
            try
            {
                if (!GetFileInformationByHandleEx(handle, 1, buffer, 24)) throw new InvalidOperationException("The diagnostic helper identity is unavailable.");
                return unchecked((uint)Marshal.ReadInt32(buffer, 16));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static bool ValidHash(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length != 64) return false;
            foreach (char character in value)
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f') || (character >= 'A' && character <= 'F'))) return false;
            return true;
        }

        private static string HashStream(Stream stream)
        {
            using (SHA256 algorithm = SHA256.Create())
            {
                byte[] hash = algorithm.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", String.Empty).ToLowerInvariant();
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, IntPtr information, uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    }
}
