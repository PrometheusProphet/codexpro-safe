using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexProSafeMaintenanceFsLauncher
{
    internal static class Program
    {
        internal const string LauncherProtocol = "codexpro-maintenance-fs-launcher-v1";
        private const int BootstrapLimit = 8192;
        private const int RequestLimit = 8192;
        private const int ResponseLimit = 4 * 1024 * 1024;
        private const int ResponseTimeoutMilliseconds = 6000;

        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--serve") return Serve();
            if (args.Length == 1 && args[0] == "--self-test") return SelfTest.Run();
            return 2;
        }

        private static int Serve()
        {
            try
            {
                Stream parentInput = Console.OpenStandardInput(); Stream parentOutput = Console.OpenStandardOutput();
                byte[] bootstrapBytes = ReadFrame(parentInput, BootstrapLimit, false);
                if (HasQueuedInput()) return 3;
                Bootstrap bootstrap;
                try { bootstrap = Bootstrap.Parse(bootstrapBytes); }
                finally { Array.Clear(bootstrapBytes, 0, bootstrapBytes.Length); }

                using (PackageLock package = PackageLock.Open(bootstrap.ManifestPath, bootstrap.ExpectedManifestSha256, bootstrap.ExpectedMaintenanceProtocol))
                using (NativeChild child = NativeChild.Start(package))
                {
                    byte[] bind = Serialize(new Dictionary<string, object> { { "protocol", PackageLock.MaintenanceProtocol }, { "operation", "bind_root" }, { "root", bootstrap.Root } }, RequestLimit);
                    try { WriteFrame(child.Input, bind); }
                    finally { Array.Clear(bind, 0, bind.Length); bootstrap.Clear(); }
                    byte[] bindResponse = ReadChildFrame(child, ResponseLimit);
                    ValidateBindResponse(bindResponse);
                    WriteFrame(parentOutput, bindResponse); Array.Clear(bindResponse, 0, bindResponse.Length);

                    while (true)
                    {
                        byte[] request = ReadFrame(parentInput, RequestLimit, true);
                        if (request == null) { child.Terminate(); return 0; }
                        if (HasQueuedInput()) { Array.Clear(request, 0, request.Length); return 3; }
                        bool close = IsClose(request);
                        WriteFrame(child.Input, request); Array.Clear(request, 0, request.Length);
                        byte[] response = ReadChildFrame(child, ResponseLimit);
                        WriteFrame(parentOutput, response); Array.Clear(response, 0, response.Length);
                        if (child.StderrInvalid) return 5;
                        if (close) return child.Wait(3000) ? 0 : 5;
                    }
                }
            }
            catch { return 5; }
        }

        private static void ValidateBindResponse(byte[] value)
        {
            Dictionary<string, object> response = StrictJson.ParseObject(value);
            if (!NativeFiles.ExactKeys(response, "protocol", "operation", "status") || !Object.Equals(response["protocol"], PackageLock.MaintenanceProtocol) ||
                !Object.Equals(response["operation"], "bind_root") || !Object.Equals(response["status"], "ok")) throw new InvalidDataException();
        }

        private static bool IsClose(byte[] value)
        {
            try
            {
                Dictionary<string, object> request = StrictJson.ParseObject(value);
                return NativeFiles.ExactKeys(request, "protocol", "operation") && Object.Equals(request["protocol"], PackageLock.MaintenanceProtocol) && Object.Equals(request["operation"], "close");
            }
            catch { return false; }
        }

        internal static byte[] ReadFrame(Stream input, int limit, bool cleanEof)
        {
            byte[] header = ReadExact(input, 4, cleanEof); if (header == null) return null;
            int length = BitConverter.ToInt32(header, 0); Array.Clear(header, 0, header.Length);
            if (length <= 0 || length > limit) throw new InvalidDataException();
            return ReadExact(input, length, false);
        }

        private static byte[] ReadChildFrame(NativeChild child, int limit)
        {
            byte[] result = null; Exception failure = null;
            using (ManualResetEvent completed = new ManualResetEvent(false))
            {
                Thread reader = new Thread(new ThreadStart(delegate
                {
                    try { result = ReadFrame(child.Output, limit, false); }
                    catch (Exception error) { failure = error; }
                    finally { completed.Set(); }
                })) { IsBackground = true, Name = "maintenance-launcher-response" };
                reader.Start();
                Stopwatch deadline = Stopwatch.StartNew();
                while (!completed.WaitOne(50))
                {
                    bool disconnected, queued;
                    ParentPipeState(out disconnected, out queued);
                    if (disconnected || queued || deadline.ElapsedMilliseconds >= ResponseTimeoutMilliseconds)
                    {
                        child.Terminate();
                        Environment.Exit(5);
                        throw new IOException();
                    }
                }
            }
            if (failure != null || result == null) throw new IOException();
            return result;
        }

        internal static void WriteFrame(Stream output, byte[] body)
        {
            byte[] header = BitConverter.GetBytes(body.Length); output.Write(header, 0, header.Length); output.Write(body, 0, body.Length); output.Flush(); Array.Clear(header, 0, header.Length);
        }

        private static byte[] ReadExact(Stream input, int length, bool cleanEof)
        {
            byte[] result = new byte[length]; int offset = 0;
            while (offset < length)
            {
                int read = input.Read(result, offset, length - offset);
                if (read == 0) { if (cleanEof && offset == 0) return null; Array.Clear(result, 0, result.Length); throw new EndOfStreamException(); }
                offset += read;
            }
            return result;
        }

        private static byte[] Serialize(object value, int limit)
        {
            byte[] body = new UTF8Encoding(false, true).GetBytes(new JavaScriptSerializer { MaxJsonLength = limit, RecursionLimit = 8 }.Serialize(value));
            if (body.Length == 0 || body.Length > limit) { Array.Clear(body, 0, body.Length); throw new InvalidDataException(); }
            return body;
        }

        private static bool HasQueuedInput()
        {
            bool disconnected, queued; ParentPipeState(out disconnected, out queued); return queued;
        }

        private static void ParentPipeState(out bool disconnected, out bool queued)
        {
            uint available; IntPtr input = GetStdHandle(-10);
            if (input == IntPtr.Zero || input == new IntPtr(-1)) throw new IOException();
            if (!PeekNamedPipe(input, IntPtr.Zero, 0, IntPtr.Zero, out available, IntPtr.Zero))
            {
                if (Marshal.GetLastWin32Error() == 109) { disconnected = true; queued = false; return; }
                throw new IOException();
            }
            disconnected = false; queued = available != 0;
        }

        [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int number);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PeekNamedPipe(IntPtr pipe, IntPtr buffer, uint size, IntPtr read, out uint available, IntPtr remaining);
    }

    internal sealed class Bootstrap
    {
        internal string ManifestPath; internal string ExpectedManifestSha256; internal string ExpectedMaintenanceProtocol; internal string Root;
        internal static Bootstrap Parse(byte[] bytes)
        {
            Dictionary<string, object> value = StrictJson.ParseObject(bytes);
            if (!NativeFiles.ExactKeys(value, "protocol", "operation", "manifestPath", "expectedManifestSha256", "expectedMaintenanceProtocol", "root") ||
                !Object.Equals(value["protocol"], Program.LauncherProtocol) || !Object.Equals(value["operation"], "bootstrap")) throw new InvalidDataException();
            string manifest = value["manifestPath"] as string, hash = value["expectedManifestSha256"] as string, protocol = value["expectedMaintenanceProtocol"] as string, root = value["root"] as string;
            if (manifest == null || hash == null || protocol == null || String.IsNullOrEmpty(root) || root.Length > 32767 || root.IndexOf('\0') >= 0) throw new InvalidDataException();
            return new Bootstrap { ManifestPath = NativeFiles.CanonicalLocalPath(manifest), ExpectedManifestSha256 = hash, ExpectedMaintenanceProtocol = protocol, Root = root };
        }
        internal void Clear() { ManifestPath = null; ExpectedManifestSha256 = null; ExpectedMaintenanceProtocol = null; Root = null; }
    }

    internal static class SelfTest
    {
        internal static int Run()
        {
            string package = null;
            try
            {
                byte[] valid = Encoding.UTF8.GetBytes("{\"protocol\":\"codexpro-maintenance-fs-launcher-v1\",\"operation\":\"bootstrap\",\"manifestPath\":\"C:\\\\package\\\\CodexProSafe.DiagnosticHelper.json\",\"expectedManifestSha256\":\"" + new string('0', 64) + "\",\"expectedMaintenanceProtocol\":\"codexpro-maintenance-fs-v1\",\"root\":\"C:\\\\root\"}");
                Bootstrap parsed = Bootstrap.Parse(valid); parsed.Clear(); Array.Clear(valid, 0, valid.Length);
                foreach (string invalid in new[] { "{}", "{\"a\":1,\"a\":2}", "[]", "{\"protocol\":\"wrong\"}" })
                {
                    bool rejected = false; try { Bootstrap.Parse(Encoding.UTF8.GetBytes(invalid)); } catch { rejected = true; }
                    if (!rejected) return 6;
                }

                string launcher = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                string sourceHelper = Path.Combine(Path.GetDirectoryName(launcher), PackageLock.HelperName);
                if (!File.Exists(sourceHelper)) return 6;
                package = Path.Combine(Path.GetTempPath(), "codexpro-launcher-selftest-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(package);
                string helper = Path.Combine(package, PackageLock.HelperName);
                string manifest = Path.Combine(package, "CodexProSafe.DiagnosticHelper.json");
                string root = Path.Combine(package, "root"); Directory.CreateDirectory(root);
                File.Copy(sourceHelper, helper);
                string helperHash;
                using (FileStream file = new FileStream(helper, FileMode.Open, FileAccess.Read, FileShare.Read)) helperHash = NativeFiles.Hash(file);
                File.WriteAllText(manifest, "{\"protocolVersion\":\"codexpro-diagnostic-v1\",\"maintenanceFsProtocolVersion\":\"codexpro-maintenance-fs-v1\",\"executable\":\"CodexProSafe.DiagnosticHelper.exe\",\"sha256\":\"" + helperHash + "\"}", new UTF8Encoding(false));
                string manifestHash;
                using (FileStream file = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read)) manifestHash = NativeFiles.Hash(file);

                using (PackageLock locked = PackageLock.Open(manifest, manifestHash, PackageLock.MaintenanceProtocol))
                {
                    bool hookRan = false;
                    SelfTestHooks.BeforeCreateProcess = delegate
                    {
                        hookRan = true;
                        ExpectBlocked(delegate { File.WriteAllText(helper, "replacement"); });
                        ExpectBlocked(delegate { File.Move(helper, helper + ".moved"); });
                        ExpectBlocked(delegate { File.Move(manifest, manifest + ".moved"); });
                        ExpectBlocked(delegate { Directory.Move(package, package + ".moved"); });
                    };
                    SelfTestHooks.ForceImageMismatch = true;
                    bool mismatchRejected = false;
                    try { using (NativeChild ignored = NativeChild.Start(locked)) { } }
                    catch { mismatchRejected = true; }
                    if (!mismatchRejected || !hookRan) return 6;
                    SelfTestHooks.Reset();

                    using (NativeChild child = NativeChild.Start(locked))
                    {
                        byte[] bind = Encoding.UTF8.GetBytes("{\"protocol\":\"codexpro-maintenance-fs-v1\",\"operation\":\"bind_root\",\"root\":\"" + Escape(root) + "\"}");
                        Program.WriteFrame(child.Input, bind); Array.Clear(bind, 0, bind.Length);
                        byte[] response = Program.ReadFrame(child.Output, 4 * 1024 * 1024, false); Array.Clear(response, 0, response.Length);
                        ExpectBlocked(delegate { File.Delete(helper); });
                        byte[] close = Encoding.UTF8.GetBytes("{\"protocol\":\"codexpro-maintenance-fs-v1\",\"operation\":\"close\"}");
                        Program.WriteFrame(child.Input, close); Array.Clear(close, 0, close.Length);
                        response = Program.ReadFrame(child.Output, 4 * 1024 * 1024, false); Array.Clear(response, 0, response.Length);
                        if (!child.Wait(3000)) return 6;
                    }
                }
                File.Move(helper, helper + ".moved"); File.Move(helper + ".moved", helper);
                Directory.Move(package, package + ".moved"); Directory.Move(package + ".moved", package);

                string hardlink = Path.Combine(package, "helper-link.exe");
                if (!CreateHardLinkW(hardlink, helper, IntPtr.Zero)) return 6;
                bool hardlinkRejected = false;
                try { using (PackageLock ignored = PackageLock.Open(manifest, manifestHash, PackageLock.MaintenanceProtocol)) { } }
                catch { hardlinkRejected = true; }
                if (!hardlinkRejected) return 6;
                File.Delete(hardlink);
                Console.Out.WriteLine("maintenance launcher self-test passed");
                return 0;
            }
            catch { return 6; }
            finally
            {
                SelfTestHooks.Reset();
                if (package != null) try { Directory.Delete(package, true); } catch { }
            }
        }

        private static void ExpectBlocked(Action action) { bool blocked = false; try { action(); } catch (IOException) { blocked = true; } catch (UnauthorizedAccessException) { blocked = true; } if (!blocked) throw new InvalidOperationException(); }
        private static string Escape(string value) { return value.Replace("\\", "\\\\").Replace("\"", "\\\""); }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);
    }
}
