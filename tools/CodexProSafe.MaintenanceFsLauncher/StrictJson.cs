using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CodexProSafeMaintenanceFsLauncher
{
    internal sealed class StrictJson
    {
        private readonly string source;
        private int offset;

        private StrictJson(string source) { this.source = source; }

        internal static Dictionary<string, object> ParseObject(byte[] utf8)
        {
            string text = new UTF8Encoding(false, true).GetString(utf8);
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            StrictJson parser = new StrictJson(text);
            Dictionary<string, object> result = parser.ReadObject();
            parser.White();
            if (parser.offset != text.Length) throw new FormatException();
            return result;
        }

        private Dictionary<string, object> ReadObject()
        {
            White(); Need('{'); White();
            Dictionary<string, object> value = new Dictionary<string, object>(StringComparer.Ordinal);
            if (Take('}')) return value;
            while (true)
            {
                string key = ReadString(); White(); Need(':'); White();
                if (value.ContainsKey(key)) throw new FormatException();
                value.Add(key, ReadPrimitive()); White();
                if (Take('}')) return value;
                Need(','); White();
            }
        }

        private object ReadPrimitive()
        {
            if (offset >= source.Length) throw new FormatException();
            if (source[offset] == '"') return ReadString();
            if (Match("true")) return true;
            if (Match("false")) return false;
            if (Match("null")) return null;
            if (source[offset] == '-' || Char.IsDigit(source[offset])) return ReadInteger();
            throw new FormatException();
        }

        private long ReadInteger()
        {
            int start = offset;
            Take('-');
            if (offset >= source.Length) throw new FormatException();
            if (Take('0'))
            {
                if (offset < source.Length && Char.IsDigit(source[offset])) throw new FormatException();
            }
            else
            {
                if (source[offset] < '1' || source[offset] > '9') throw new FormatException();
                while (offset < source.Length && Char.IsDigit(source[offset])) offset++;
            }
            if (offset < source.Length && ".eE+".IndexOf(source[offset]) >= 0) throw new FormatException();
            long result;
            if (!Int64.TryParse(source.Substring(start, offset - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result)) throw new FormatException();
            return result;
        }

        private string ReadString()
        {
            Need('"');
            StringBuilder result = new StringBuilder();
            while (offset < source.Length)
            {
                char current = source[offset++];
                if (current == '"') return result.ToString();
                if (current < 0x20) throw new FormatException();
                if (current != '\\') { result.Append(current); continue; }
                if (offset >= source.Length) throw new FormatException();
                char escaped = source[offset++];
                if (escaped == '"' || escaped == '\\' || escaped == '/') result.Append(escaped);
                else if (escaped == 'b') result.Append('\b');
                else if (escaped == 'f') result.Append('\f');
                else if (escaped == 'n') result.Append('\n');
                else if (escaped == 'r') result.Append('\r');
                else if (escaped == 't') result.Append('\t');
                else if (escaped == 'u')
                {
                    if (offset + 4 > source.Length) throw new FormatException();
                    int code;
                    if (!Int32.TryParse(source.Substring(offset, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) throw new FormatException();
                    offset += 4;
                    char decoded = (char)code;
                    if (Char.IsHighSurrogate(decoded))
                    {
                        if (offset + 6 > source.Length || source[offset] != '\\' || source[offset + 1] != 'u') throw new FormatException();
                        int low;
                        if (!Int32.TryParse(source.Substring(offset + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out low) || !Char.IsLowSurrogate((char)low)) throw new FormatException();
                        result.Append(decoded); result.Append((char)low); offset += 6;
                    }
                    else if (Char.IsLowSurrogate(decoded)) throw new FormatException();
                    else result.Append(decoded);
                }
                else throw new FormatException();
            }
            throw new FormatException();
        }

        private void White() { while (offset < source.Length && " \t\r\n".IndexOf(source[offset]) >= 0) offset++; }
        private bool Take(char value) { if (offset < source.Length && source[offset] == value) { offset++; return true; } return false; }
        private void Need(char value) { if (!Take(value)) throw new FormatException(); }
        private bool Match(string value)
        {
            if (offset + value.Length > source.Length || String.CompareOrdinal(source, offset, value, 0, value.Length) != 0) return false;
            offset += value.Length;
            return true;
        }
    }
}
