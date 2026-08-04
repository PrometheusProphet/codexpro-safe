using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Runtime.InteropServices;

namespace CodexProSafeDiagnosticHelper
{
    internal sealed class MaintenanceEntry
    {
        public string entryId { get; set; }
        public string relativePath { get; set; }
        public string kind { get; set; }
        public long byteSize { get; set; }
        public string modifiedUtc { get; set; }
        public string attributes { get; set; }
    }

    internal sealed class MaintenanceWalkRequest
    {
        public int maxDepth { get; set; }
        public int maxEntries { get; set; }
        public long maxObservedBytes { get; set; }
        public int maxResponseBytes { get; set; }
        public int maxDurationMs { get; set; }
    }

    internal sealed class MaintenanceWalkResponse
    {
        public string protocol { get; set; }
        public string operation { get; set; }
        public string status { get; set; }
        public bool complete { get; set; }
        public string limitation { get; set; }
        public int returnedEntries { get; set; }
        public long observedFileBytes { get; set; }
        public List<MaintenanceEntry> entries { get; set; }
    }

    internal sealed class MaintenanceFileResponse
    {
        public string protocol { get; set; }
        public string operation { get; set; }
        public string status { get; set; }
        public string entryId { get; set; }
        public long byteCount { get; set; }
        public string sha256 { get; set; }
        public string contentBase64 { get; set; }
    }

    internal static class MaintenanceProtocolServer
    {
        internal const string Protocol = "codexpro-maintenance-fs-v1";
        private const int RequestLimit = 8192;
        private const int ResponseLimit = MaintenanceFilesystemProvider.MaximumResponseBytes;

        internal static int Run()
        {
            try
            {
                using (MaintenanceFilesystemProvider provider = new MaintenanceFilesystemProvider())
                {
                    Stream input = Console.OpenStandardInput();
                    Stream output = Console.OpenStandardOutput();
                    bool first = true;
                    while (true)
                    {
                        byte[] header = ReadExact(input, 4, true);
                        if (header == null) return 0;
                        int length = BitConverter.ToInt32(header, 0);
                        if (length <= 0 || length > RequestLimit) return 3;
                        byte[] body = ReadExact(input, length, false);
                        if (HasQueuedInput()) return 3;
                        Dictionary<string, object> request;
                        try
                        {
                            string json = new UTF8Encoding(false, true).GetString(body);
                            request = StrictJsonObject.Parse(json);
                        }
                        catch { return 3; }
                        finally { Array.Clear(body, 0, body.Length); }
                        object response;
                        bool close;
                        if (!Dispatch(provider, request, first, out response, out close)) return 3;
                        first = false;
                        WriteResponse(output, response);
                        if (close) return 0;
                    }
                }
            }
            catch { return 5; }
        }

        internal static int EstimateWalkResponseBytes(MaintenanceWalkResponse current, MaintenanceEntry candidate)
        {
            current.entries.Add(candidate);
            try { return checked(Serialize(current, ResponseLimit + (4 * 1024 * 1024)).Length + 4 + 256); }
            finally { current.entries.RemoveAt(current.entries.Count - 1); }
        }

        private static bool Dispatch(MaintenanceFilesystemProvider provider, Dictionary<string, object> request, bool first, out object response, out bool close)
        {
            response = null;
            close = false;
            string protocol;
            string operation;
            if (!TryString(request, "protocol", out protocol) || protocol != Protocol || !TryString(request, "operation", out operation)) return false;
            if (first && operation != "bind_root") return false;
            if (operation == "bind_root")
            {
                if (!ExactKeys(request, "protocol", "operation", "root")) return false;
                string root;
                if (!TryString(request, "root", out root)) return false;
                string status = provider.Bind(root);
                response = Status("bind_root", status);
                close = status != "ok";
                return true;
            }
            if (!provider.IsBound) return false;
            if (operation == "handshake")
            {
                if (!ExactKeys(request, "protocol", "operation")) return false;
                response = new Dictionary<string, object>
                {
                    { "protocol", Protocol }, { "operation", "handshake" }, { "status", "ok" },
                    { "maxDepth", MaintenanceFilesystemProvider.MaximumDepth }, { "maxEntries", MaintenanceFilesystemProvider.MaximumEntries },
                    { "maxObservedBytes", MaintenanceFilesystemProvider.MaximumObservedBytes }, { "maxResponseBytes", MaintenanceFilesystemProvider.MaximumResponseBytes },
                    { "maxDurationMs", MaintenanceFilesystemProvider.MaximumDurationMs }, { "maxHashBytes", MaintenanceFilesystemProvider.MaximumHashBytes },
                    { "maxTextBytes", MaintenanceFilesystemProvider.MaximumTextBytes }, { "filesystem", "NTFS" }
                };
                return true;
            }
            if (operation == "walk")
            {
                if (!ExactKeys(request, "protocol", "operation", "maxDepth", "maxEntries", "maxObservedBytes", "maxResponseBytes", "maxDurationMs")) return false;
                long depth, entries, observed, responseBytes, duration;
                if (!TryInteger(request, "maxDepth", out depth) || !TryInteger(request, "maxEntries", out entries) ||
                    !TryInteger(request, "maxObservedBytes", out observed) || !TryInteger(request, "maxResponseBytes", out responseBytes) ||
                    !TryInteger(request, "maxDurationMs", out duration) || depth > Int32.MaxValue || entries > Int32.MaxValue || responseBytes > Int32.MaxValue || duration > Int32.MaxValue)
                    return false;
                response = provider.Walk(new MaintenanceWalkRequest
                {
                    maxDepth = (int)depth, maxEntries = (int)entries, maxObservedBytes = observed,
                    maxResponseBytes = (int)responseBytes, maxDurationMs = (int)duration
                });
                return true;
            }
            if (operation == "hash_file" || operation == "read_text_file")
            {
                if (!ExactKeys(request, "protocol", "operation", "entryId", "maxBytes")) return false;
                string entryId;
                long maximum;
                if (!TryString(request, "entryId", out entryId) || !ValidEntryId(entryId) || !TryInteger(request, "maxBytes", out maximum)) return false;
                response = operation == "hash_file" ? provider.HashFile(entryId, maximum) : provider.ReadTextFile(entryId, maximum);
                return true;
            }
            if (operation == "close")
            {
                if (!ExactKeys(request, "protocol", "operation")) return false;
                response = Status("close", "ok");
                close = true;
                return true;
            }
            return false;
        }

        private static Dictionary<string, object> Status(string operation, string status)
        {
            return new Dictionary<string, object> { { "protocol", Protocol }, { "operation", operation }, { "status", status } };
        }

        private static bool ValidEntryId(string value)
        {
            if (value == null || value.Length != 18 || value[0] != 's' || value[9] != 'e') return false;
            for (int index = 1; index < value.Length; index++)
            {
                if (index == 9) continue;
                char current = value[index];
                if (!((current >= '0' && current <= '9') || (current >= 'a' && current <= 'f'))) return false;
            }
            return true;
        }

        private static bool ExactKeys(Dictionary<string, object> value, params string[] keys)
        {
            if (value.Count != keys.Length) return false;
            foreach (string key in keys) if (!value.ContainsKey(key)) return false;
            return true;
        }

        private static bool TryString(Dictionary<string, object> value, string key, out string result)
        {
            object raw;
            result = null;
            if (!value.TryGetValue(key, out raw)) return false;
            result = raw as string;
            return result != null;
        }

        private static bool TryInteger(Dictionary<string, object> value, string key, out long result)
        {
            object raw;
            result = 0;
            return value.TryGetValue(key, out raw) && raw is long && (result = (long)raw) >= 0;
        }

        private static byte[] Serialize(object response)
        {
            return Serialize(response, ResponseLimit);
        }

        private static byte[] Serialize(object response, int limit)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = limit, RecursionLimit = 16 };
            return new UTF8Encoding(false, true).GetBytes(serializer.Serialize(response));
        }

        private static void WriteResponse(Stream output, object response)
        {
            byte[] body = Serialize(response);
            try
            {
                if (body.Length <= 0 || body.Length > ResponseLimit) throw new IOException();
                byte[] header = BitConverter.GetBytes(body.Length);
                output.Write(header, 0, header.Length);
                output.Write(body, 0, body.Length);
                output.Flush();
            }
            finally { Array.Clear(body, 0, body.Length); }
        }

        private static byte[] ReadExact(Stream input, int length, bool allowCleanEof)
        {
            byte[] value = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = input.Read(value, offset, length - offset);
                if (read == 0)
                {
                    if (allowCleanEof && offset == 0) return null;
                    throw new EndOfStreamException();
                }
                offset += read;
            }
            return value;
        }

        private static bool HasQueuedInput()
        {
            uint available;
            IntPtr input = GetStdHandle(-10);
            if (input == IntPtr.Zero || input == new IntPtr(-1) || !PeekNamedPipe(input, IntPtr.Zero, 0, IntPtr.Zero, out available, IntPtr.Zero))
                throw new IOException();
            return available != 0;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int standardHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool PeekNamedPipe(IntPtr pipe, IntPtr buffer, uint bufferSize, IntPtr bytesRead, out uint totalBytesAvailable, IntPtr bytesLeftThisMessage);
    }

    internal sealed class StrictJsonObject
    {
        private readonly string value;
        private int offset;

        private StrictJsonObject(string value) { this.value = value; }

        internal static Dictionary<string, object> Parse(string value)
        {
            if (String.IsNullOrEmpty(value)) throw new FormatException();
            StrictJsonObject parser = new StrictJsonObject(value);
            Dictionary<string, object> result = parser.Object();
            parser.White();
            if (parser.offset != value.Length) throw new FormatException();
            return result;
        }

        private Dictionary<string, object> Object()
        {
            White(); Need('{');
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            White();
            if (Take('}')) return result;
            while (true)
            {
                White(); string key = StringValue(); White(); Need(':'); White();
                if (result.ContainsKey(key)) throw new FormatException();
                result.Add(key, Primitive());
                White();
                if (Take('}')) return result;
                Need(',');
            }
        }

        private object Primitive()
        {
            if (offset >= value.Length) throw new FormatException();
            if (value[offset] == '"') return StringValue();
            if (value[offset] == '-' || (value[offset] >= '0' && value[offset] <= '9')) return Integer();
            if (Match("true")) return true;
            if (Match("false")) return false;
            if (Match("null")) return null;
            throw new FormatException();
        }

        private long Integer()
        {
            int start = offset;
            if (Take('-')) { }
            if (offset >= value.Length) throw new FormatException();
            if (Take('0'))
            {
                if (offset < value.Length && Char.IsDigit(value[offset])) throw new FormatException();
            }
            else
            {
                if (value[offset] < '1' || value[offset] > '9') throw new FormatException();
                while (offset < value.Length && Char.IsDigit(value[offset])) offset++;
            }
            if (offset < value.Length && (value[offset] == '.' || value[offset] == 'e' || value[offset] == 'E' || value[offset] == '+')) throw new FormatException();
            long parsed;
            if (!Int64.TryParse(value.Substring(start, offset - start), System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out parsed)) throw new FormatException();
            return parsed;
        }

        private string StringValue()
        {
            Need('"');
            StringBuilder result = new StringBuilder();
            while (offset < value.Length)
            {
                char current = value[offset++];
                if (current == '"') return result.ToString();
                if (current < 0x20) throw new FormatException();
                if (current != '\\') { result.Append(current); continue; }
                if (offset >= value.Length) throw new FormatException();
                char escaped = value[offset++];
                if (escaped == '"' || escaped == '\\' || escaped == '/') result.Append(escaped);
                else if (escaped == 'b') result.Append('\b');
                else if (escaped == 'f') result.Append('\f');
                else if (escaped == 'n') result.Append('\n');
                else if (escaped == 'r') result.Append('\r');
                else if (escaped == 't') result.Append('\t');
                else if (escaped == 'u')
                {
                    if (offset + 4 > value.Length) throw new FormatException();
                    int code;
                    if (!Int32.TryParse(value.Substring(offset, 4), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out code)) throw new FormatException();
                    char decoded = (char)code;
                    offset += 4;
                    if (Char.IsHighSurrogate(decoded))
                    {
                        if (offset + 6 > value.Length || value[offset] != '\\' || value[offset + 1] != 'u') throw new FormatException();
                        int low;
                        if (!Int32.TryParse(value.Substring(offset + 2, 4), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out low) || !Char.IsLowSurrogate((char)low)) throw new FormatException();
                        result.Append(decoded); result.Append((char)low); offset += 6;
                    }
                    else if (Char.IsLowSurrogate(decoded)) throw new FormatException();
                    else result.Append(decoded);
                }
                else throw new FormatException();
            }
            throw new FormatException();
        }

        private void White() { while (offset < value.Length && (value[offset] == ' ' || value[offset] == '\t' || value[offset] == '\r' || value[offset] == '\n')) offset++; }
        private bool Take(char expected) { if (offset < value.Length && value[offset] == expected) { offset++; return true; } return false; }
        private void Need(char expected) { if (!Take(expected)) throw new FormatException(); }
        private bool Match(string expected)
        {
            if (offset + expected.Length > value.Length || String.CompareOrdinal(value, offset, expected, 0, expected.Length) != 0) return false;
            offset += expected.Length; return true;
        }
    }
}
