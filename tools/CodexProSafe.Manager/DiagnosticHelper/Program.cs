using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace CodexProSafeDiagnosticHelper
{
    internal static class Program
    {
        internal const string Protocol = "codexpro-diagnostic-v1";
        private const int RequestLimit = 4096;
        private const int ResponseLimit = 48 * 1024 * 1024;

        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--self-test") return NativeBoundarySelfTest.Run();
            if (args.Length != 1 || args[0] != "--serve") return 2;
            try
            {
                JavaScriptSerializer serializer = Serializer();
                Stream input = Console.OpenStandardInput();
                Stream output = Console.OpenStandardOutput();
                while (true)
                {
                    byte[] header = ReadExact(input, 4, true);
                    if (header == null) return 0;
                    int length = BitConverter.ToInt32(header, 0);
                    if (length <= 0 || length > RequestLimit) return 3;
                    byte[] body = ReadExact(input, length, false);
                    Request request;
                    try { request = serializer.Deserialize<Request>(Encoding.UTF8.GetString(body)); }
                    catch { return 3; }
                    Response response = NativeBoundary.Execute(request, null, null);
                    byte[] encoded = Encoding.UTF8.GetBytes(serializer.Serialize(response));
                    if (encoded.Length <= 0 || encoded.Length > ResponseLimit) return 4;
                    output.Write(BitConverter.GetBytes(encoded.Length), 0, 4);
                    output.Write(encoded, 0, encoded.Length);
                    output.Flush();
                }
            }
            catch { return 5; }
        }

        internal static JavaScriptSerializer Serializer()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = ResponseLimit;
            serializer.RecursionLimit = 16;
            return serializer;
        }

        private static byte[] ReadExact(Stream stream, int length, bool allowCleanEof)
        {
            byte[] value = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = stream.Read(value, offset, length - offset);
                if (read == 0)
                {
                    if (allowCleanEof && offset == 0) return null;
                    throw new EndOfStreamException();
                }
                offset += read;
            }
            return value;
        }
    }

    internal sealed class Request
    {
        public string protocol { get; set; }
        public string operation { get; set; }
        public string familyKind { get; set; }
    }

    internal sealed class Response
    {
        public string protocol { get; set; }
        public string helperVersion { get; set; }
        public string status { get; set; }
        public int matches { get; set; }
        public List<EntryResponse> entries { get; set; }
        public List<FileResponse> files { get; set; }
        public FileResponse database { get; set; }
        public List<string> sidecars { get; set; }

        public static Response Status(string status)
        {
            return new Response { protocol = Program.Protocol, helperVersion = Program.Protocol, status = status };
        }
    }

    internal sealed class EntryResponse
    {
        public string name { get; set; }
        public bool isDirectory { get; set; }
        public bool isReparsePoint { get; set; }
        public long bytes { get; set; }
        public string modifiedUtc { get; set; }
    }

    internal sealed class FileResponse
    {
        public string name { get; set; }
        public string status { get; set; }
        public long bytes { get; set; }
        public string modifiedUtc { get; set; }
        public string contentBase64 { get; set; }
    }

    internal sealed class BoundaryHooks
    {
        public Action afterRootOpen;
        public Action beforeSelectedOpen;
    }

    internal sealed class NativeEntry
    {
        public string Name;
        public uint Attributes;
        public long Length;
        public long LastWriteFileTime;
        public byte[] FileId;
        public bool IsDirectory { get { return (Attributes & NativeMethods.FileAttributeDirectory) != 0; } }
        public bool IsReparsePoint { get { return (Attributes & NativeMethods.FileAttributeReparsePoint) != 0; } }
    }

    internal sealed class RootContext : IDisposable
    {
        public SafeFileHandle Handle;
        public ulong VolumeSerial;
        public List<NativeEntry> Entries;
        public void Dispose() { if (Handle != null) Handle.Dispose(); }
    }

    internal static class NativeBoundary
    {
        private const int MaxEntries = 512;
        private const int MaxNameChars = 255;
        private const int MaxConfigBytes = 64 * 1024;
        private const int MaxDatabaseBytes = 32 * 1024 * 1024;
        private static readonly string[] ConfigFiles = { "config.toml", "config.toml.bak", "config.toml.backup" };
        private static readonly Regex DatabasePattern = new Regex("^(?:logs|state|memories|goals)_[A-Za-z0-9_-]+\\.sqlite$", RegexOptions.CultureInvariant);

        public static Response Execute(Request request, string rootOverride, BoundaryHooks hooks)
        {
            if (request == null || request.protocol != Program.Protocol) return Response.Status("unavailable");
            if (request.operation == "handshake") return Response.Status("ok");
            string root = rootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            try
            {
                using (RootContext context = OpenRoot(root))
                {
                    if (context == null) return Response.Status("missing");
                    if (hooks != null && hooks.afterRootOpen != null) hooks.afterRootOpen();
                    context.Entries = Enumerate(context.Handle);
                    if (request.operation == "inventory") return Inventory(context);
                    if (request.operation == "configuration") return Configuration(context, hooks);
                    if (request.operation == "database") return Database(context, request.familyKind, hooks);
                    return Response.Status("unavailable");
                }
            }
            catch { return Response.Status("unavailable"); }
        }

        private static RootContext OpenRoot(string root)
        {
            SafeFileHandle handle = NativeMethods.CreateFile(
                root,
                NativeMethods.FileListDirectory | NativeMethods.FileReadAttributes | NativeMethods.Synchronize,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (error == NativeMethods.ErrorFileNotFound || error == NativeMethods.ErrorPathNotFound) return null;
                throw new IOException();
            }
            try
            {
                uint attributes;
                uint tag;
                NativeMethods.GetAttributeTag(handle, out attributes, out tag);
                if ((attributes & NativeMethods.FileAttributeDirectory) == 0 || (attributes & NativeMethods.FileAttributeReparsePoint) != 0)
                    throw new IOException();
                string expected = NormalizePath(Path.GetFullPath(root));
                StringBuilder finalName = new StringBuilder(1024);
                uint result = NativeMethods.GetFinalPathNameByHandle(handle, finalName, (uint)finalName.Capacity, 0);
                if (result == 0 || result >= finalName.Capacity || !String.Equals(expected, NormalizePath(finalName.ToString()), StringComparison.OrdinalIgnoreCase))
                    throw new IOException();
                ulong volume;
                byte[] fileId;
                NativeMethods.GetFileId(handle, out volume, out fileId);
                return new RootContext { Handle = handle, VolumeSerial = volume };
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private static string NormalizePath(string value)
        {
            string normalized = value;
            if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal)) normalized = normalized.Substring(4);
            return normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static List<NativeEntry> Enumerate(SafeFileHandle root)
        {
            const int bufferSize = 64 * 1024;
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                List<NativeEntry> entries = new List<NativeEntry>();
                bool restart = true;
                while (true)
                {
                    bool ok = NativeMethods.GetFileInformationByHandleEx(root, restart ? 20 : 19, buffer, (uint)bufferSize);
                    restart = false;
                    if (!ok)
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == NativeMethods.ErrorNoMoreFiles) break;
                        throw new IOException();
                    }
                    int offset = 0;
                    while (true)
                    {
                        if (offset < 0 || offset > bufferSize - 88) throw new IOException();
                        int next = Marshal.ReadInt32(buffer, offset);
                        long lastWrite = Marshal.ReadInt64(buffer, offset + 24);
                        long length = Marshal.ReadInt64(buffer, offset + 40);
                        uint attributes = unchecked((uint)Marshal.ReadInt32(buffer, offset + 56));
                        int nameBytes = Marshal.ReadInt32(buffer, offset + 60);
                        if (nameBytes < 2 || (nameBytes & 1) != 0 || nameBytes > MaxNameChars * 2 || offset + 88 + nameBytes > bufferSize)
                            throw new IOException();
                        string name = Marshal.PtrToStringUni(IntPtr.Add(buffer, offset + 88), nameBytes / 2);
                        if (name != "." && name != "..")
                        {
                            ValidateBasename(name);
                            byte[] fileId = new byte[16];
                            Marshal.Copy(IntPtr.Add(buffer, offset + 72), fileId, 0, fileId.Length);
                            entries.Add(new NativeEntry { Name = name, Attributes = attributes, Length = Math.Max(0, length), LastWriteFileTime = lastWrite, FileId = fileId });
                            if (entries.Count > MaxEntries) throw new IOException();
                        }
                        if (next == 0) break;
                        if (next < 88 || offset + next <= offset || offset + next >= bufferSize) throw new IOException();
                        offset += next;
                    }
                }
                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (NativeEntry entry in entries) if (!names.Add(entry.Name)) throw new IOException();
                return entries;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static void ValidateBasename(string name)
        {
            if (String.IsNullOrEmpty(name) || name.Length > MaxNameChars || name.IndexOf('\0') >= 0 ||
                name.IndexOf('\\') >= 0 || name.IndexOf('/') >= 0 || name.IndexOf(':') >= 0 || name == "." || name == "..")
                throw new IOException();
        }

        private static Response Inventory(RootContext context)
        {
            Response response = Response.Status("ok");
            response.entries = new List<EntryResponse>();
            foreach (NativeEntry entry in context.Entries)
            {
                if (!ApprovedInventoryName(entry.Name)) continue;
                response.entries.Add(new EntryResponse
                {
                    name = entry.Name,
                    isDirectory = entry.IsDirectory,
                    isReparsePoint = entry.IsReparsePoint,
                    bytes = entry.Length,
                    modifiedUtc = Utc(entry.LastWriteFileTime)
                });
            }
            return response;
        }

        private static bool ApprovedInventoryName(string name)
        {
            foreach (string category in new[] { "skills", "plugins", "logs", "sessions" })
                if (String.Equals(name, category, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string config in ConfigFiles)
                if (String.Equals(name, config, StringComparison.OrdinalIgnoreCase)) return true;
            if (DatabasePattern.IsMatch(name)) return true;
            if (name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) || name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase))
                return DatabasePattern.IsMatch(name.Substring(0, name.Length - 4));
            return false;
        }

        private static Response Configuration(RootContext context, BoundaryHooks hooks)
        {
            Response response = Response.Status("ok");
            response.files = new List<FileResponse>();
            foreach (string name in ConfigFiles)
            {
                NativeEntry entry = Find(context.Entries, name);
                if (entry == null)
                {
                    response.files.Add(new FileResponse { name = name, status = "absent", bytes = 0, modifiedUtc = "1970-01-01T00:00:00.000Z" });
                    continue;
                }
                if (entry.IsDirectory || entry.IsReparsePoint || entry.Length > MaxConfigBytes) return Response.Status("unavailable");
                if (hooks != null && hooks.beforeSelectedOpen != null) { Action action = hooks.beforeSelectedOpen; hooks.beforeSelectedOpen = null; action(); }
                byte[] bytes = ReadSelected(context, entry, MaxConfigBytes);
                response.files.Add(new FileResponse { name = name, status = "present", bytes = bytes.Length, modifiedUtc = Utc(entry.LastWriteFileTime), contentBase64 = Convert.ToBase64String(bytes) });
                Array.Clear(bytes, 0, bytes.Length);
            }
            return response;
        }

        private static Response Database(RootContext context, string familyKind, BoundaryHooks hooks)
        {
            if (!Regex.IsMatch(familyKind ?? String.Empty, "^(?:logs|state|memories|goals)$", RegexOptions.CultureInvariant)) return Response.Status("unavailable");
            Regex family = new Regex("^" + Regex.Escape(familyKind) + "_[A-Za-z0-9_-]+\\.sqlite$", RegexOptions.CultureInvariant);
            List<NativeEntry> matches = context.Entries.FindAll(delegate(NativeEntry entry) { return family.IsMatch(entry.Name); });
            if (matches.Count == 0) return Response.Status("missing");
            if (matches.Count > 1) { Response ambiguous = Response.Status("ambiguous"); ambiguous.matches = matches.Count; return ambiguous; }
            NativeEntry selected = matches[0];
            if (selected.IsDirectory || selected.IsReparsePoint) return Response.Status("unavailable");
            List<string> sidecars = Sidecars(context.Entries, selected.Name);
            if (selected.Length > MaxDatabaseBytes)
            {
                Response oversized = Response.Status("oversized");
                oversized.database = Metadata(selected);
                oversized.sidecars = sidecars;
                return oversized;
            }
            if (hooks != null && hooks.beforeSelectedOpen != null) { Action action = hooks.beforeSelectedOpen; hooks.beforeSelectedOpen = null; action(); }
            byte[] bytes = ReadSelected(context, selected, MaxDatabaseBytes);
            Response response = Response.Status("ok");
            response.database = Metadata(selected);
            response.database.contentBase64 = Convert.ToBase64String(bytes);
            response.sidecars = sidecars;
            Array.Clear(bytes, 0, bytes.Length);
            return response;
        }

        private static FileResponse Metadata(NativeEntry entry)
        {
            return new FileResponse { name = entry.Name, bytes = entry.Length, modifiedUtc = Utc(entry.LastWriteFileTime) };
        }

        private static List<string> Sidecars(List<NativeEntry> entries, string database)
        {
            List<string> sidecars = new List<string>();
            foreach (string kind in new[] { "wal", "shm" })
            {
                NativeEntry entry = Find(entries, database + "-" + kind);
                if (entry != null && !entry.IsDirectory && !entry.IsReparsePoint) sidecars.Add(kind);
            }
            return sidecars;
        }

        private static NativeEntry Find(List<NativeEntry> entries, string name)
        {
            return entries.Find(delegate(NativeEntry entry) { return String.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase); });
        }

        private static byte[] ReadSelected(RootContext context, NativeEntry expected, int maximum)
        {
            using (SafeFileHandle child = NativeMethods.OpenRelative(context.Handle, expected.Name))
            {
                uint attributes;
                uint tag;
                NativeMethods.GetAttributeTag(child, out attributes, out tag);
                if ((attributes & NativeMethods.FileAttributeDirectory) != 0 || (attributes & NativeMethods.FileAttributeReparsePoint) != 0 || NativeMethods.GetFileType(child) != 1)
                    throw new IOException();
                ulong volume;
                byte[] fileId;
                NativeMethods.GetFileId(child, out volume, out fileId);
                if (volume != context.VolumeSerial || !EqualBytes(fileId, expected.FileId)) throw new IOException();
                long length;
                uint links;
                NativeMethods.GetStandardInfo(child, out length, out links);
                if (links != 1 || length != expected.Length || length < 0 || length > maximum) throw new IOException();
                long lastWrite;
                uint currentAttributes;
                NativeMethods.GetBasicInfo(child, out lastWrite, out currentAttributes);
                if (lastWrite != expected.LastWriteFileTime || currentAttributes != expected.Attributes) throw new IOException();
                using (FileStream stream = new FileStream(child, FileAccess.Read, 65536, false))
                using (MemoryStream output = new MemoryStream((int)length))
                {
                    byte[] buffer = new byte[65536];
                    int total = 0;
                    while (true)
                    {
                        int read = stream.Read(buffer, 0, buffer.Length);
                        if (read == 0) break;
                        total += read;
                        if (total > maximum || total > length) throw new IOException();
                        output.Write(buffer, 0, read);
                    }
                    if (total != length) throw new IOException();
                    NativeMethods.GetFileId(child, out volume, out fileId);
                    NativeMethods.GetStandardInfo(child, out length, out links);
                    if (volume != context.VolumeSerial || !EqualBytes(fileId, expected.FileId) || links != 1 || length != total) throw new IOException();
                    return output.ToArray();
                }
            }
        }

        private static bool EqualBytes(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            int difference = 0;
            for (int index = 0; index < left.Length; index++) difference |= left[index] ^ right[index];
            return difference == 0;
        }

        private static string Utc(long fileTime)
        {
            try { return DateTime.FromFileTimeUtc(fileTime).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"); }
            catch { throw new IOException(); }
        }
    }

    internal static class NativeMethods
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
        internal const uint FileAttributeDirectory = 0x00000010;
        internal const uint FileAttributeReparsePoint = 0x00000400;
        internal const int ErrorFileNotFound = 2;
        internal const int ErrorPathNotFound = 3;
        internal const int ErrorNoMoreFiles = 18;
        private const uint ObjCaseInsensitive = 0x00000040;
        private const uint FileOpen = 1;
        private const uint FileNonDirectoryFile = 0x00000040;
        private const uint FileSynchronousIoNonAlert = 0x00000020;
        private const uint FileOpenReparsePoint = 0x00200000;

        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
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
        private struct IoStatusBlock { public IntPtr Status; public UIntPtr Information; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, IntPtr information, uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
        internal static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint GetFileType(SafeFileHandle handle);
        [DllImport("ntdll.dll")]
        private static extern int NtCreateFile(out IntPtr fileHandle, uint desiredAccess, ref ObjectAttributes objectAttributes,
            out IoStatusBlock ioStatusBlock, IntPtr allocationSize, uint fileAttributes, uint shareAccess,
            uint createDisposition, uint createOptions, IntPtr eaBuffer, uint eaLength);

        internal static SafeFileHandle OpenRelative(SafeFileHandle root, string basename)
        {
            IntPtr nameBuffer = IntPtr.Zero;
            IntPtr unicodePointer = IntPtr.Zero;
            try
            {
                nameBuffer = Marshal.StringToHGlobalUni(basename);
                UnicodeString unicode = new UnicodeString { Length = checked((ushort)(basename.Length * 2)), MaximumLength = checked((ushort)((basename.Length + 1) * 2)), Buffer = nameBuffer };
                unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(UnicodeString)));
                Marshal.StructureToPtr(unicode, unicodePointer, false);
                ObjectAttributes attributes = new ObjectAttributes
                {
                    Length = Marshal.SizeOf(typeof(ObjectAttributes)),
                    RootDirectory = root.DangerousGetHandle(),
                    ObjectName = unicodePointer,
                    Attributes = ObjCaseInsensitive
                };
                IntPtr raw;
                IoStatusBlock statusBlock;
                int status = NtCreateFile(out raw, FileReadData | FileReadAttributes | Synchronize, ref attributes, out statusBlock,
                    IntPtr.Zero, 0, FileShareRead, FileOpen, FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint, IntPtr.Zero, 0);
                if (status < 0 || raw == IntPtr.Zero || raw == new IntPtr(-1)) throw new IOException();
                return new SafeFileHandle(raw, true);
            }
            finally
            {
                if (unicodePointer != IntPtr.Zero) Marshal.FreeHGlobal(unicodePointer);
                if (nameBuffer != IntPtr.Zero) Marshal.FreeHGlobal(nameBuffer);
            }
        }

        internal static void GetAttributeTag(SafeFileHandle handle, out uint attributes, out uint tag)
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

        internal static void GetFileId(SafeFileHandle handle, out ulong volume, out byte[] fileId)
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

        internal static void GetStandardInfo(SafeFileHandle handle, out long length, out uint links)
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

        internal static void GetBasicInfo(SafeFileHandle handle, out long lastWrite, out uint attributes)
        {
            IntPtr buffer = Marshal.AllocHGlobal(40);
            try
            {
                if (!GetFileInformationByHandleEx(handle, 0, buffer, 40)) throw new IOException();
                lastWrite = Marshal.ReadInt64(buffer, 16);
                attributes = unchecked((uint)Marshal.ReadInt32(buffer, 32));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    internal static class NativeBoundarySelfTest
    {
        public static int Run()
        {
            string temp = Path.Combine(Path.GetTempPath(), "CodexProSafe.DiagnosticHelper." + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temp);
                string root = Path.Combine(temp, ".codex");
                string external = Path.Combine(temp, "external");
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(external);
                Directory.CreateDirectory(Path.Combine(root, "skills"));
                Directory.CreateDirectory(Path.Combine(root, "plugins"));
                Directory.CreateDirectory(Path.Combine(root, "sessions"));
                Directory.CreateDirectory(Path.Combine(root, "logs"));
                File.WriteAllText(Path.Combine(root, "config.toml"), "[general]\ntelemetry = true\nsecret = \"unchanged\"\n");
                File.WriteAllText(Path.Combine(root, "logs_test.sqlite"), "synthetic-database-bytes");
                File.WriteAllText(Path.Combine(root, "sessions", "private-session.jsonl"), "private-session-content");
                Directory.CreateDirectory(Path.Combine(root, "skills", "private-extension", "1.0.0"));
                File.WriteAllText(Path.Combine(external, "config.toml"), "external-config");
                File.WriteAllText(Path.Combine(external, "logs_test.sqlite"), "external-database");
                File.WriteAllText(Path.Combine(external, "goals_outside.sqlite"), "external-inventory");

                byte[] configBefore = File.ReadAllBytes(Path.Combine(root, "config.toml"));
                byte[] databaseBefore = File.ReadAllBytes(Path.Combine(root, "logs_test.sqlite"));
                Response inventory = Execute(root, "inventory", null, null);
                Assert(inventory.status == "ok", "inventory");
                string inventoryJson = Program.Serializer().Serialize(inventory);
                Assert(!inventoryJson.Contains("private-session") && !inventoryJson.Contains("private-extension") && !inventoryJson.Contains("1.0.0"), "private nested names");

                bool rootSwapBlocked = false;
                BoundaryHooks enumerationHooks = new BoundaryHooks();
                enumerationHooks.afterRootOpen = delegate
                {
                    try { Directory.Move(root, root + ".swap"); }
                    catch (IOException) { rootSwapBlocked = true; }
                    catch (UnauthorizedAccessException) { rootSwapBlocked = true; }
                };
                Response swappedInventory = Execute(root, "inventory", null, enumerationHooks);
                Assert(swappedInventory.status == "ok" && rootSwapBlocked, "root enumeration swap");
                Assert(!Program.Serializer().Serialize(swappedInventory).Contains("goals_outside.sqlite"), "external inventory exclusion");

                bool configRootSwapBlocked = false;
                BoundaryHooks configRootHooks = new BoundaryHooks();
                configRootHooks.beforeSelectedOpen = delegate
                {
                    try { Directory.Move(root, root + ".config-swap"); }
                    catch (IOException) { configRootSwapBlocked = true; }
                    catch (UnauthorizedAccessException) { configRootSwapBlocked = true; }
                };
                Assert(Execute(root, "configuration", null, configRootHooks).status == "ok" && configRootSwapBlocked, "root config swap");

                string configPath = Path.Combine(root, "config.toml");
                string configBackup = Path.Combine(root, "config.original");
                BoundaryHooks configChildHooks = new BoundaryHooks();
                configChildHooks.beforeSelectedOpen = delegate { File.Move(configPath, configBackup); File.Copy(Path.Combine(external, "config.toml"), configPath); };
                Assert(Execute(root, "configuration", null, configChildHooks).status == "unavailable", "config child replacement");
                File.Delete(configPath);
                File.Move(configBackup, configPath);

                bool databaseRootSwapBlocked = false;
                BoundaryHooks databaseRootHooks = new BoundaryHooks();
                databaseRootHooks.beforeSelectedOpen = delegate
                {
                    try { Directory.Move(root, root + ".database-swap"); }
                    catch (IOException) { databaseRootSwapBlocked = true; }
                    catch (UnauthorizedAccessException) { databaseRootSwapBlocked = true; }
                };
                Assert(Execute(root, "database", "logs", databaseRootHooks).status == "ok" && databaseRootSwapBlocked, "root database swap");

                string databasePath = Path.Combine(root, "logs_test.sqlite");
                string databaseBackup = Path.Combine(root, "database.original");
                BoundaryHooks databaseChildHooks = new BoundaryHooks();
                databaseChildHooks.beforeSelectedOpen = delegate { File.Move(databasePath, databaseBackup); File.Copy(Path.Combine(external, "logs_test.sqlite"), databasePath); };
                Assert(Execute(root, "database", "logs", databaseChildHooks).status == "unavailable", "database child replacement");
                File.Delete(databasePath);
                File.Move(databaseBackup, databasePath);

                File.Copy(databasePath, Path.Combine(root, "logs_second.sqlite"));
                Assert(Execute(root, "database", "logs", null).status == "ambiguous", "ambiguous family");
                File.Delete(Path.Combine(root, "logs_second.sqlite"));

                using (FileStream large = new FileStream(Path.Combine(root, "logs_large.sqlite"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    large.SetLength(33L * 1024L * 1024L);
                File.Move(databasePath, Path.Combine(root, "state_test.sqlite"));
                Response oversizedInventory = Execute(root, "inventory", null, null);
                Assert(Program.Serializer().Serialize(oversizedInventory).Contains("logs_large.sqlite"), "oversized inventory");
                Assert(Execute(root, "database", "logs", null).status == "oversized", "oversized content");
                File.Move(Path.Combine(root, "state_test.sqlite"), databasePath);
                File.Delete(Path.Combine(root, "logs_large.sqlite"));

                string junction = Path.Combine(temp, "root-junction");
                CreateJunction(junction, root);
                Assert(Execute(junction, "inventory", null, null).status == "unavailable", "root junction");
                Directory.Delete(junction);
                string childJunction = Path.Combine(root, "config.toml.bak");
                CreateJunction(childJunction, external);
                Assert(Execute(root, "configuration", null, null).status == "unavailable", "child junction");
                Directory.Delete(childJunction);

                string hardLink = Path.Combine(root, "config.toml.bak");
                Assert(CreateHardLink(hardLink, configPath, IntPtr.Zero), "hard-link creation");
                Assert(Execute(root, "configuration", null, null).status == "unavailable", "hard-link ambiguity");
                File.Delete(hardLink);

                Assert(Equal(configBefore, File.ReadAllBytes(configPath)), "configuration unchanged");
                Assert(Equal(databaseBefore, File.ReadAllBytes(databasePath)), "database unchanged");
                return 0;
            }
            catch { return 1; }
            finally
            {
                try { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
                catch { }
            }
        }

        private static Response Execute(string root, string operation, string family, BoundaryHooks hooks)
        {
            return NativeBoundary.Execute(new Request { protocol = Program.Protocol, operation = operation, familyKind = family }, root, hooks);
        }

        private static void Assert(bool value, string name) { if (!value) throw new InvalidOperationException(name); }
        private static bool Equal(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (int index = 0; index < left.Length; index++) if (left[index] != right[index]) return false;
            return true;
        }

        private static void CreateJunction(string junction, string target)
        {
            Directory.CreateDirectory(junction);
            using (SafeFileHandle handle = NativeMethods.CreateFile(junction, 0x40000000, NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
                IntPtr.Zero, NativeMethods.OpenExisting, NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new IOException();
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
                if (!DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out returned, IntPtr.Zero)) throw new IOException();
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
        private static extern bool CreateHardLink(string newName, string existingName, IntPtr securityAttributes);
    }
}
