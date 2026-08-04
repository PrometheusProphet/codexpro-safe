using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal static class MaintenanceFsClientFixture
{
    private const string Protocol = "codexpro-maintenance-fs-v1";

    private static int Main(string[] args)
    {
        if (args.Length != 1 || args[0] != "--serve-maintenance-fs") return 2;
        Stream input = Console.OpenStandardInput();
        Stream output = Console.OpenStandardOutput();
#if UNSOLICITED
        Write(output, "{\"protocol\":\"" + Protocol + "\",\"operation\":\"walk\",\"status\":\"ok\"}");
        Thread.Sleep(30000);
        return 7;
#endif
        int count = 0;
        while (true)
        {
            byte[] header = Read(input, 4, true);
            if (header == null) return 0;
            int length = BitConverter.ToInt32(header, 0);
            if (length <= 0 || length > 8192) return 3;
            byte[] body = Read(input, length, false);
            Dictionary<string, object> request = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(body));
            Array.Clear(body, 0, body.Length);
            count++;
#if TIMEOUT
            Thread.Sleep(30000);
            return 4;
#else
            string operation = request["operation"] as string;
            if (count == 1)
            {
                if (operation != "bind_root" || !request.ContainsKey("root") || args[0].Contains(request["root"] as string)) return 5;
                foreach (System.Collections.DictionaryEntry item in Environment.GetEnvironmentVariables())
                    if (String.Equals(item.Value as string, request["root"] as string, StringComparison.Ordinal)) return 6;
#if OVERSIZED
                byte[] oversized = BitConverter.GetBytes(4194305);
                output.Write(oversized, 0, oversized.Length); output.Flush(); Thread.Sleep(30000); return 8;
#elif INCOMPLETE
                byte[] incomplete = BitConverter.GetBytes(100);
                output.Write(incomplete, 0, incomplete.Length); output.WriteByte((byte)'{'); output.WriteByte((byte)'}'); output.Flush(); return 9;
#elif CONCATENATED
                byte[] valid = Frame("{\"protocol\":\"" + Protocol + "\",\"operation\":\"bind_root\",\"status\":\"ok\"}");
                output.Write(valid, 0, valid.Length); output.Write(valid, 0, valid.Length); output.Flush(); Array.Clear(valid, 0, valid.Length); Thread.Sleep(30000); return 10;
#elif SCHEMA
                Write(output, "{\"protocol\":\"" + Protocol + "\",\"operation\":\"bind_root\",\"status\":\"ok\",\"extra\":true}");
#elif VERSION
                Write(output, "{\"protocol\":\"wrong\",\"operation\":\"bind_root\",\"status\":\"ok\"}");
#else
                Write(output, "{\"protocol\":\"" + Protocol + "\",\"operation\":\"bind_root\",\"status\":\"ok\"}");
#endif
            }
            else if (operation == "handshake")
            {
                Write(output, "{\"protocol\":\"" + Protocol + "\",\"operation\":\"handshake\",\"status\":\"ok\",\"maxDepth\":64,\"maxEntries\":4096,\"maxObservedBytes\":4294967296,\"maxResponseBytes\":4194304,\"maxDurationMs\":5000,\"maxHashBytes\":67108864,\"maxTextBytes\":1048576,\"filesystem\":\"NTFS\"}");
            }
            else if (operation == "walk")
            {
#if MISMATCH
                Write(output, "{\"protocol\":\"" + Protocol + "\",\"operation\":\"walk\",\"status\":\"ok\",\"complete\":true,\"limitation\":\"none\",\"returnedEntries\":2,\"observedFileBytes\":2,\"entries\":[]}");
#else
                Write(output, "{\"protocol\":\"" + Protocol + "\",\"operation\":\"walk\",\"status\":\"ok\",\"complete\":true,\"limitation\":\"none\",\"returnedEntries\":1,\"observedFileBytes\":1,\"entries\":[{\"entryId\":\"s00000001e00000001\",\"relativePath\":\"file.txt\",\"kind\":\"file\",\"byteSize\":1,\"modifiedUtc\":\"2000-01-01T00:00:00.0000000Z\",\"attributes\":\"none\"}]}");
#endif
            }
            else if (operation == "hash_file")
            {
                Write(output, "{\"protocol\":\"" + Protocol + "\",\"operation\":\"hash_file\",\"status\":\"ok\",\"entryId\":\"s00000001e00000002\",\"byteCount\":2,\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"contentBase64\":null}");
            }
            else if (operation == "close")
            {
                Write(output, "{\"protocol\":\"" + Protocol + "\",\"operation\":\"close\",\"status\":\"ok\"}");
                return 0;
            }
#endif
        }
    }

    private static byte[] Read(Stream stream, int length, bool cleanEof)
    {
        byte[] value = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = stream.Read(value, offset, length - offset);
            if (read == 0)
            {
                if (cleanEof && offset == 0) return null;
                throw new EndOfStreamException();
            }
            offset += read;
        }
        return value;
    }

    private static void Write(Stream stream, string json)
    {
        byte[] frame = Frame(json);
        stream.Write(frame, 0, frame.Length);
        stream.Flush();
        Array.Clear(frame, 0, frame.Length);
    }

    private static byte[] Frame(string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        byte[] frame = new byte[body.Length + 4];
        Array.Copy(BitConverter.GetBytes(body.Length), frame, 4);
        Array.Copy(body, 0, frame, 4, body.Length);
        Array.Clear(body, 0, body.Length);
        return frame;
    }
}
