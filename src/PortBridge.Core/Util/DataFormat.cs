using System.Globalization;
using System.Text;

namespace PortBridge.Core.Util;

public static class DataFormat
{
    /// <summary>Turns C-style escapes (\r \n \t \0 \\ \xHH) into bytes. Other characters are Latin-1.</summary>
    public static byte[] Unescape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var bytes = new List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\\' || i == text.Length - 1)
            {
                AppendChar(bytes, c);
                continue;
            }
            var n = text[++i];
            switch (n)
            {
                case 'r': bytes.Add(0x0D); break;
                case 'n': bytes.Add(0x0A); break;
                case 't': bytes.Add(0x09); break;
                case '0': bytes.Add(0x00); break;
                case 'e': bytes.Add(0x1B); break;
                case '\\': bytes.Add((byte)'\\'); break;
                case 'x' when i + 2 < text.Length &&
                              byte.TryParse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, null, out var hx):
                    bytes.Add(hx);
                    i += 2;
                    break;
                default:
                    bytes.Add((byte)'\\');
                    AppendChar(bytes, n);
                    break;
            }
        }
        return bytes.ToArray();
    }

    private static void AppendChar(List<byte> bytes, char c)
    {
        if (c <= 0xFF) bytes.Add((byte)c);
        else bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
    }

    /// <summary>Parses "01 02 ff", "0102FF", "0x01,0x02" etc.</summary>
    public static bool TryParseHex(string? text, out byte[] bytes)
    {
        bytes = [];
        if (text is null) return false;
        var clean = new StringBuilder(text.Length);
        var s = text.Replace("0x", " ", StringComparison.OrdinalIgnoreCase);
        foreach (var c in s)
        {
            if (Uri.IsHexDigit(c)) clean.Append(c);
            else if (!char.IsWhiteSpace(c) && c is not (',' or '-' or ':' or ';')) return false;
        }
        if (clean.Length % 2 != 0) return false;
        bytes = Convert.FromHexString(clean.ToString());
        return true;
    }

    public static string ToHex(ReadOnlySpan<byte> data, int max = int.MaxValue)
    {
        var len = Math.Min(data.Length, max);
        var sb = new StringBuilder(len * 3);
        for (var i = 0; i < len; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        if (data.Length > len) sb.Append(" …");
        return sb.ToString();
    }

    /// <summary>Printable ASCII with control characters shown as escapes, for logs.</summary>
    public static string ToEscapedText(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length);
        foreach (var b in data)
        {
            switch (b)
            {
                case 0x0D: sb.Append("\\r"); break;
                case 0x0A: sb.Append("\\n"); break;
                case 0x09: sb.Append("\\t"); break;
                case (byte)'\\': sb.Append("\\\\"); break;
                case >= 0x20 and < 0x7F: sb.Append((char)b); break;
                default: sb.Append("\\x").Append(b.ToString("X2")); break;
            }
        }
        return sb.ToString();
    }
}
