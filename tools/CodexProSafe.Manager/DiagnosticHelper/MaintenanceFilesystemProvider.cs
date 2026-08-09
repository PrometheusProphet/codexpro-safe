using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CodexProSafeDiagnosticHelper
{
    internal static class MaintenanceFilesystemTestHooks
    {
        internal static Action<string> BeforeEntryOpen;
        internal static Action<string> BeforeAncestorOpen;
        internal static Action<string> BeforeFileOpen;
        internal static Action<string> AfterReadChunk;
        internal static Action AfterEnumeration;

        internal static void Reset()
        {
            BeforeEntryOpen = null;
            BeforeAncestorOpen = null;
            BeforeFileOpen = null;
            AfterReadChunk = null;
            AfterEnumeration = null;
        }
    }

    internal sealed class MaintenanceSnapshotRecord
    {
        internal string EntryId;
        internal string RelativePath;
        internal string[] Components;
        internal MaintenanceIdentity[] Ancestors;
        internal MaintenanceIdentity Identity;
        internal MaintenanceIdentity RootIdentity;
        internal string Kind;
    }

    internal sealed class MaintenanceFilesystemProvider : IDisposable
    {
        internal const int MaximumDepth = 64;
        internal const int MaximumEntries = 4096;
        internal const long MaximumObservedBytes = 4L * 1024L * 1024L * 1024L;
        internal const int MaximumResponseBytes = 4 * 1024 * 1024;
        internal const int MaximumDurationMs = 5000;
        internal const long MaximumHashBytes = 64L * 1024L * 1024L;
        internal const int MaximumTextBytes = 1024 * 1024;

        private SafeFileHandle rootHandle;
        private MaintenanceIdentity rootIdentity;
        private Dictionary<string, MaintenanceSnapshotRecord> snapshot = new Dictionary<string, MaintenanceSnapshotRecord>(StringComparer.Ordinal);
        private long snapshotGeneration;
        private bool disposed;

        internal bool IsBound { get { return rootHandle != null && !rootHandle.IsInvalid && !rootHandle.IsClosed; } }

        internal string Bind(string suppliedRoot)
        {
            if (IsBound) return "already_bound";
            string canonical;
            try { canonical = ValidateRootPath(suppliedRoot); }
            catch { return "unsupported"; }

            SafeFileHandle candidate = null;
            try
            {
                if (!MaintenanceNativeMethods.IsFixedLocalDrive(canonical)) return "unsupported";
                candidate = MaintenanceNativeMethods.OpenRoot(canonical);
                MaintenanceIdentity identity = MaintenanceNativeMethods.CaptureIdentity(candidate);
                if (!identity.IsDirectory || identity.IsReparse || identity.FileType != MaintenanceNativeMethods.DiskFileType)
                    return "unsupported";
                string filesystem = MaintenanceNativeMethods.FilesystemName(candidate);
                if (!String.Equals(filesystem, "NTFS", StringComparison.OrdinalIgnoreCase)) return "unsupported";
                string finalPath = CanonicalHandlePath(MaintenanceNativeMethods.FinalPath(candidate));
                if (!String.Equals(finalPath, canonical, StringComparison.OrdinalIgnoreCase)) return "unsupported";
                rootHandle = candidate;
                candidate = null;
                rootIdentity = identity.Clone();
                snapshot.Clear();
                return "ok";
            }
            catch { return "unavailable"; }
            finally
            {
                if (candidate != null) candidate.Dispose();
                ClearString(ref canonical);
            }
        }

        internal MaintenanceWalkResponse Walk(MaintenanceWalkRequest request)
        {
            snapshot.Clear();
            MaintenanceWalkResponse response = new MaintenanceWalkResponse
            {
                protocol = MaintenanceProtocolServer.Protocol,
                operation = "walk",
                status = "ok",
                complete = true,
                limitation = "none",
                entries = new List<MaintenanceEntry>()
            };
            if (!IsBound) { response.status = "not_bound"; response.complete = false; return response; }
            if (!ValidWalkBudgets(request)) { response.status = "invalid_request"; response.complete = false; return response; }

            Dictionary<string, MaintenanceSnapshotRecord> next = new Dictionary<string, MaintenanceSnapshotRecord>(StringComparer.Ordinal);
            snapshot = next;
            snapshotGeneration = checked(snapshotGeneration + 1);
            Stopwatch clock = Stopwatch.StartNew();
            WalkState state = new WalkState(request, response, next, snapshotGeneration, clock);
            try
            {
                VerifyRoot();
                state.RootIdentity = MaintenanceNativeMethods.CaptureIdentity(rootHandle);
                WalkDirectory(rootHandle, new string[0], new MaintenanceIdentity[0], 0, state);
                if (state.Exhausted == null && clock.ElapsedMilliseconds >= request.maxDurationMs) state.Exhausted = "duration";
                if (state.Exhausted == null && state.DepthLimited) state.Exhausted = "depth";
                if (state.Exhausted != null)
                {
                    response.status = "budget_exhausted";
                    response.complete = false;
                    response.limitation = state.Exhausted;
                }
                response.observedFileBytes = state.ObservedBytes;
                response.returnedEntries = response.entries.Count;
                return response;
            }
            catch
            {
                snapshot.Clear();
                response.entries.Clear();
                response.status = "unavailable";
                response.complete = false;
                response.limitation = "none";
                response.observedFileBytes = 0;
                response.returnedEntries = 0;
                return response;
            }
        }

        internal MaintenanceFileResponse HashFile(string entryId, long callerMaximum)
        {
            if (callerMaximum < 0 || callerMaximum > MaximumHashBytes) return FileFailure("hash_file", entryId, "invalid_request");
            return ReadFile(entryId, callerMaximum, false);
        }

        internal MaintenanceFileResponse ReadTextFile(string entryId, long callerMaximum)
        {
            if (callerMaximum < 0 || callerMaximum > MaximumTextBytes) return FileFailure("read_text_file", entryId, "invalid_request");
            return ReadFile(entryId, callerMaximum, true);
        }

        private MaintenanceFileResponse ReadFile(string entryId, long maximum, bool text)
        {
            string operation = text ? "read_text_file" : "hash_file";
            if (!IsBound) return FileFailure(operation, entryId, "not_bound");
            MaintenanceSnapshotRecord record;
            if (String.IsNullOrEmpty(entryId) || !snapshot.TryGetValue(entryId, out record) || record.Kind != "file")
                return FileFailure(operation, entryId, "invalid_entry");
            if (record.Identity.LinkCount != 1) return FileFailure(operation, entryId, "unavailable");
            if (record.Identity.Length > maximum) return FileFailure(operation, entryId, "too_large");

            List<SafeFileHandle> opened = new List<SafeFileHandle>();
            byte[] content = null;
            try
            {
                VerifyRoot();
                if (!DirectoryIdentityEqual(MaintenanceNativeMethods.CaptureIdentity(rootHandle), record.RootIdentity))
                    return FileFailure(operation, entryId, "changed");
                SafeFileHandle parent = rootHandle;
                for (int index = 0; index < record.Components.Length - 1; index++)
                {
                    MaintenanceIdentity parentBefore = MaintenanceNativeMethods.CaptureIdentity(parent);
                    Action<string> ancestorHook = MaintenanceFilesystemTestHooks.BeforeAncestorOpen;
                    if (ancestorHook != null) ancestorHook(String.Join("/", record.Components, 0, index + 1));
                    SafeFileHandle child = MaintenanceNativeMethods.OpenRelativeDirectory(parent, record.Components[index]);
                    opened.Add(child);
                    MaintenanceIdentity current = MaintenanceNativeMethods.CaptureIdentity(child);
                    MaintenanceIdentity parentAfter = MaintenanceNativeMethods.CaptureIdentity(parent);
                    if (!DirectoryIdentityEqual(parentAfter, parentBefore)) return FileFailure(operation, entryId, "changed");
                    if (!DirectoryIdentityEqual(current, record.Ancestors[index]) || current.IsReparse || !current.IsDirectory)
                        return FileFailure(operation, entryId, "changed");
                    parent = child;
                }
                MaintenanceIdentity fileParentBefore = MaintenanceNativeMethods.CaptureIdentity(parent);
                Action<string> fileHook = MaintenanceFilesystemTestHooks.BeforeFileOpen;
                if (fileHook != null) fileHook(record.RelativePath);
                SafeFileHandle file = MaintenanceNativeMethods.OpenRelativeFile(parent, record.Components[record.Components.Length - 1], true);
                opened.Add(file);
                MaintenanceIdentity before = MaintenanceNativeMethods.CaptureIdentity(file);
                MaintenanceIdentity fileParentAfter = MaintenanceNativeMethods.CaptureIdentity(parent);
                if (!DirectoryIdentityEqual(fileParentAfter, fileParentBefore)) return FileFailure(operation, entryId, "changed");
                if (!IdentityEqual(before, record.Identity, true) || before.IsDirectory || before.IsReparse || before.FileType != MaintenanceNativeMethods.DiskFileType || before.LinkCount != 1)
                    return FileFailure(operation, entryId, "changed");
                if (before.Length > maximum || before.Length > Int32.MaxValue) return FileFailure(operation, entryId, "too_large");

                using (FileStream stream = new FileStream(file, FileAccess.Read, 64 * 1024, false))
                using (SHA256 algorithm = SHA256.Create())
                {
                    opened.Remove(file);
                    byte[] buffer = new byte[64 * 1024];
                    MemoryStream collected = text ? new MemoryStream((int)before.Length) : null;
                    long total = 0;
                    try
                    {
                        while (true)
                        {
                            int read = stream.Read(buffer, 0, buffer.Length);
                            if (read == 0) break;
                            total = checked(total + read);
                            if (total > maximum) return FileFailure(operation, entryId, "too_large");
                            algorithm.TransformBlock(buffer, 0, read, null, 0);
                            if (collected != null) collected.Write(buffer, 0, read);
                            Action<string> readHook = MaintenanceFilesystemTestHooks.AfterReadChunk;
                            if (readHook != null) readHook(entryId);
                        }
                        algorithm.TransformFinalBlock(new byte[0], 0, 0);
                        MaintenanceIdentity after = MaintenanceNativeMethods.CaptureIdentity(file);
                        MaintenanceIdentity parentAfterRead = MaintenanceNativeMethods.CaptureIdentity(parent);
                        if (!DirectoryIdentityEqual(parentAfterRead, fileParentAfter) || total != before.Length ||
                            !IdentityEqual(after, before, true) || !IdentityEqual(after, record.Identity, true))
                            return FileFailure(operation, entryId, "changed");
                        string digest = Hex(algorithm.Hash);
                        MaintenanceFileResponse response = new MaintenanceFileResponse
                        {
                            protocol = MaintenanceProtocolServer.Protocol,
                            operation = operation,
                            status = "ok",
                            entryId = entryId,
                            byteCount = total,
                            sha256 = digest
                        };
                        if (collected != null)
                        {
                            content = collected.ToArray();
                            if (!StrictUtf8(content)) return FileFailure(operation, entryId, "not_text");
                            response.contentBase64 = Convert.ToBase64String(content);
                        }
                        return response;
                    }
                    finally
                    {
                        Array.Clear(buffer, 0, buffer.Length);
                        if (collected != null) collected.Dispose();
                    }
                }
            }
            catch { return FileFailure(operation, entryId, "unavailable"); }
            finally
            {
                if (content != null) Array.Clear(content, 0, content.Length);
                for (int index = opened.Count - 1; index >= 0; index--) opened[index].Dispose();
            }
        }

        private void WalkDirectory(SafeFileHandle directory, string[] components, MaintenanceIdentity[] ancestors, int depth, WalkState state)
        {
            if (state.Exhausted != null) return;
            if (state.Clock.ElapsedMilliseconds >= state.Request.maxDurationMs) { state.Exhausted = "duration"; return; }
            MaintenanceIdentity namespaceIdentity = MaintenanceNativeMethods.CaptureIdentity(directory);
            int remaining = state.Request.maxEntries - state.VisitedEntries;
            if (remaining <= 0) { state.Exhausted = "entries"; return; }
            int visited;
            string enumerationLimitation;
            List<MaintenanceDirectoryEntry> children = MaintenanceNativeMethods.Enumerate(
                directory, remaining,
                delegate { return state.Clock.ElapsedMilliseconds >= state.Request.maxDurationMs; },
                out visited, out enumerationLimitation);
            state.VisitedEntries += visited;
            Action enumerationHook = MaintenanceFilesystemTestHooks.AfterEnumeration;
            if (enumerationHook != null) enumerationHook();
            if (enumerationLimitation != null) { state.Exhausted = enumerationLimitation; return; }
            if (state.Clock.ElapsedMilliseconds >= state.Request.maxDurationMs) { state.Exhausted = "duration"; return; }
            if (!DirectoryIdentityEqual(MaintenanceNativeMethods.CaptureIdentity(directory), namespaceIdentity)) throw new IOException();
            children.Sort(CompareEntries);
            for (int index = 1; index < children.Count; index++)
                if (String.Equals(children[index - 1].Name, children[index].Name, StringComparison.OrdinalIgnoreCase)) throw new IOException();

            foreach (MaintenanceDirectoryEntry child in children)
            {
                if (state.Clock.ElapsedMilliseconds >= state.Request.maxDurationMs) { state.Exhausted = "duration"; return; }
                if (state.Response.entries.Count >= state.Request.maxEntries) { state.Exhausted = "entries"; return; }
                string[] chain = Append(components, child.Name);
                string relative = String.Join("/", chain);
                MaintenanceIdentity actual;
                string kind;
                SafeFileHandle opened = null;
                try
                {
                    if (child.Identity.IsReparse)
                    {
                        kind = "reparse";
                        actual = child.Identity.Clone();
                        actual.VolumeSerial = rootIdentity.VolumeSerial;
                    }
                    else if (child.Identity.IsDirectory)
                    {
                        Action<string> entryHook = MaintenanceFilesystemTestHooks.BeforeEntryOpen;
                        if (entryHook != null) entryHook(relative);
                        opened = MaintenanceNativeMethods.OpenRelativeDirectory(directory, child.Name);
                        actual = MaintenanceNativeMethods.CaptureIdentity(opened);
                        if (!EnumerationIdentityEqual(child.Identity, actual, false) || !actual.IsDirectory || actual.IsReparse) throw new IOException();
                        kind = "directory";
                    }
                    else
                    {
                        Action<string> entryHook = MaintenanceFilesystemTestHooks.BeforeEntryOpen;
                        if (entryHook != null) entryHook(relative);
                        opened = MaintenanceNativeMethods.OpenRelativeFile(directory, child.Name, false);
                        actual = MaintenanceNativeMethods.CaptureIdentity(opened);
                        if (!EnumerationIdentityEqual(child.Identity, actual, true) || actual.IsDirectory || actual.IsReparse) throw new IOException();
                        kind = actual.FileType == MaintenanceNativeMethods.DiskFileType ? "file" : "other";
                    }
                    if (!DirectoryIdentityEqual(MaintenanceNativeMethods.CaptureIdentity(directory), namespaceIdentity)) throw new IOException();

                    if (kind == "file")
                    {
                        if (actual.Length < 0 || state.ObservedBytes > state.Request.maxObservedBytes - actual.Length)
                        { state.Exhausted = "observed_bytes"; return; }
                        state.ObservedBytes += actual.Length;
                    }
                    string id = "s" + state.Generation.ToString("x8", CultureInfo.InvariantCulture) + "e" + (state.Response.entries.Count + 1).ToString("x8", CultureInfo.InvariantCulture);
                    MaintenanceEntry entry = new MaintenanceEntry
                    {
                        entryId = id,
                        relativePath = relative,
                        kind = kind,
                        byteSize = kind == "file" ? actual.Length : 0,
                        modifiedUtc = DateTime.FromFileTimeUtc(actual.LastWriteFileTime).ToString("o", CultureInfo.InvariantCulture),
                        attributes = SanitizedAttributes(actual.Attributes)
                    };
                    int projected = MaintenanceProtocolServer.EstimateWalkResponseBytes(state.Response, entry);
                    if (state.Clock.ElapsedMilliseconds >= state.Request.maxDurationMs) { state.Exhausted = "duration"; return; }
                    if (projected > state.Request.maxResponseBytes) { state.Exhausted = "response_bytes"; return; }
                    MaintenanceIdentity[] nextAncestors = ancestors;
                    if (kind == "directory") nextAncestors = Append(ancestors, actual.Clone());
                    state.Response.entries.Add(entry);
                    state.Snapshot.Add(id, new MaintenanceSnapshotRecord
                    {
                        EntryId = id,
                        RelativePath = relative,
                        Components = chain,
                        Ancestors = kind == "directory" ? ancestors : ancestors,
                        Identity = actual.Clone(),
                        RootIdentity = state.RootIdentity.Clone(),
                        Kind = kind
                    });
                    if (kind == "directory")
                    {
                        if (depth + 1 >= state.Request.maxDepth) state.DepthLimited = true;
                        else
                        {
                            WalkDirectory(opened, chain, nextAncestors, depth + 1, state);
                            if (state.Exhausted != null) return;
                        }
                    }
                }
                finally { if (opened != null) opened.Dispose(); }
            }
        }

        private void VerifyRoot()
        {
            MaintenanceIdentity current = MaintenanceNativeMethods.CaptureIdentity(rootHandle);
            if (!IdentityEqual(current, rootIdentity, false) || !current.IsDirectory || current.IsReparse) throw new IOException();
        }

        internal static void ValidateBasename(string value)
        {
            if (String.IsNullOrEmpty(value) || value == "." || value == ".." || value.Length > 255) throw new IOException();
            if (value.IndexOf('\0') >= 0 || value.IndexOf('\\') >= 0 || value.IndexOf('/') >= 0 || value.IndexOf(':') >= 0) throw new IOException();
        }

        internal static string ValidateRootPath(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > 4096) throw new IOException();
            if (value.StartsWith("\\\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal) ||
                value.StartsWith("\\??\\", StringComparison.OrdinalIgnoreCase) || value.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase) || value.StartsWith("\\\\.\\", StringComparison.OrdinalIgnoreCase)) throw new IOException();
            if (value.Length < 3 || !Char.IsLetter(value[0]) || value[1] != ':' || (value[2] != '\\' && value[2] != '/')) throw new IOException();
            if (value.IndexOf(':', 2) >= 0 || value.IndexOf('\0') >= 0) throw new IOException();
            string full = Path.GetFullPath(value).TrimEnd('\\', '/');
            if (full.Length == 2 && full[1] == ':') full += "\\";
            if (!String.Equals(value.TrimEnd('\\', '/'), full.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) throw new IOException();
            return full;
        }

        private static string CanonicalHandlePath(string value)
        {
            if (value.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) throw new IOException();
            if (value.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase)) value = value.Substring(4);
            string full = Path.GetFullPath(value).TrimEnd('\\', '/');
            return full.Length == 2 && full[1] == ':' ? full + "\\" : full;
        }

        private static bool EnumerationIdentityEqual(MaintenanceIdentity listed, MaintenanceIdentity opened, bool regularFile)
        {
            return ByteEqual(listed.FileId, opened.FileId) && listed.Attributes == opened.Attributes &&
                (regularFile ? (listed.Length == opened.Length && listed.LastWriteFileTime == opened.LastWriteFileTime) :
                    true);
        }

        private static bool DirectoryIdentityEqual(MaintenanceIdentity left, MaintenanceIdentity right)
        {
            return IdentityEqual(left, right, false) && left.ChangeFileTime == right.ChangeFileTime;
        }

        private static bool IdentityEqual(MaintenanceIdentity left, MaintenanceIdentity right, bool fileStrict)
        {
            if (left == null || right == null || left.VolumeSerial != right.VolumeSerial || !ByteEqual(left.FileId, right.FileId) ||
                left.Attributes != right.Attributes || left.FileType != right.FileType) return false;
            return !fileStrict || (left.Length == right.Length && left.LastWriteFileTime == right.LastWriteFileTime &&
                left.ChangeFileTime == right.ChangeFileTime && left.LinkCount == right.LinkCount);
        }

        private static bool ByteEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            int difference = 0;
            for (int index = 0; index < left.Length; index++) difference |= left[index] ^ right[index];
            return difference == 0;
        }

        private static int CompareEntries(MaintenanceDirectoryEntry left, MaintenanceDirectoryEntry right)
        {
            int primary = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            return primary != 0 ? primary : StringComparer.Ordinal.Compare(left.Name, right.Name);
        }

        private static string[] Append(string[] values, string value)
        {
            string[] result = new string[values.Length + 1];
            Array.Copy(values, result, values.Length);
            result[values.Length] = value;
            return result;
        }

        private static MaintenanceIdentity[] Append(MaintenanceIdentity[] values, MaintenanceIdentity value)
        {
            MaintenanceIdentity[] result = new MaintenanceIdentity[values.Length + 1];
            Array.Copy(values, result, values.Length);
            result[values.Length] = value;
            return result;
        }

        private static string SanitizedAttributes(uint value)
        {
            List<string> names = new List<string>();
            if ((value & MaintenanceNativeMethods.FileAttributeReadOnly) != 0) names.Add("readonly");
            if ((value & MaintenanceNativeMethods.FileAttributeHidden) != 0) names.Add("hidden");
            if ((value & MaintenanceNativeMethods.FileAttributeSystem) != 0) names.Add("system");
            if ((value & MaintenanceNativeMethods.FileAttributeArchive) != 0) names.Add("archive");
            return names.Count == 0 ? "none" : String.Join(",", names.ToArray());
        }

        private static bool StrictUtf8(byte[] value)
        {
            for (int index = 0; index < value.Length; index++) if (value[index] == 0) return false;
            char[] characters = null;
            try
            {
                UTF8Encoding encoding = new UTF8Encoding(false, true);
                characters = new char[encoding.GetCharCount(value)];
                encoding.GetChars(value, 0, value.Length, characters, 0);
                return true;
            }
            catch { return false; }
            finally { if (characters != null) Array.Clear(characters, 0, characters.Length); }
        }

        private static string Hex(byte[] value) { return BitConverter.ToString(value).Replace("-", String.Empty).ToLowerInvariant(); }

        private static MaintenanceFileResponse FileFailure(string operation, string entryId, string status)
        {
            return new MaintenanceFileResponse { protocol = MaintenanceProtocolServer.Protocol, operation = operation, status = status, entryId = entryId };
        }

        private static bool ValidWalkBudgets(MaintenanceWalkRequest value)
        {
            return value != null && value.maxDepth >= 1 && value.maxDepth <= MaximumDepth && value.maxEntries >= 1 && value.maxEntries <= MaximumEntries &&
                value.maxObservedBytes >= 0 && value.maxObservedBytes <= MaximumObservedBytes && value.maxResponseBytes >= 512 && value.maxResponseBytes <= MaximumResponseBytes &&
                value.maxDurationMs >= 1 && value.maxDurationMs <= MaximumDurationMs;
        }

        private static void ClearString(ref string value) { value = null; }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            snapshot.Clear();
            if (rootHandle != null) rootHandle.Dispose();
            rootHandle = null;
            rootIdentity = null;
        }

        private sealed class WalkState
        {
            internal readonly MaintenanceWalkRequest Request;
            internal readonly MaintenanceWalkResponse Response;
            internal readonly Dictionary<string, MaintenanceSnapshotRecord> Snapshot;
            internal readonly long Generation;
            internal readonly Stopwatch Clock;
            internal long ObservedBytes;
            internal int VisitedEntries;
            internal string Exhausted;
            internal bool DepthLimited;
            internal MaintenanceIdentity RootIdentity;

            internal WalkState(MaintenanceWalkRequest request, MaintenanceWalkResponse response, Dictionary<string, MaintenanceSnapshotRecord> snapshot, long generation, Stopwatch clock)
            { Request = request; Response = response; Snapshot = snapshot; Generation = generation; Clock = clock; }
        }
    }
}
