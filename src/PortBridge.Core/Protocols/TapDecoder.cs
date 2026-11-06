using PortBridge.Core.Engine;
using PortBridge.Core.Util;

namespace PortBridge.Core.Protocols;

/// <summary>
/// Explains TAP (Telocator Alphanumeric Protocol, alphanumeric paging) traffic in words for the Monitor and the traffic
/// logs: the entry device's wake-up, logon, pages and logoff, and the paging terminal's prompt, replies and message codes.
/// One instance per bridge, fed every chunk in order. A message can arrive in several reads (some USB serial adapters
/// deliver byte by byte), so the unfinished end of a chunk is kept per direction and explained when it is complete.
/// </summary>
public sealed class TapDecoder
{
    private const byte STX = 0x02, ETX = 0x03, EOT = 0x04, ACK = 0x06, LF = 0x0A, CR = 0x0D, XON = 0x11, XOFF = 0x13,
        NAK = 0x15, ETB = 0x17, ESC = 0x1B, RS = 0x1E, US = 0x1F;
    private const int MaxBlock = 300;     // a TAP block is at most 256 characters
    private const int MaxLine = 256;      // message sequences, logon strings
    private const int MaxPending = 1024;

    private enum Role { Unknown, Entry, Terminal }
    private enum Awaiting { None, Logon, Page, Block, Logoff }

    private sealed class Side
    {
        public byte[] Pending = [];
        public Role Role;
        public bool InTransaction;      // a page continues in the next block (the last block ended in ETB/US)
        public bool NextInTransaction;  // what InTransaction becomes once the terminal accepts the last block
    }

    private readonly Side[] _sides = [new(), new()];
    private readonly Lock _gate = new();
    private Awaiting _awaiting;

    private static readonly (int Length, string? Text, Role Role) Incomplete = (0, null, Role.Unknown);

    /// <summary>What the TAP messages completed by this chunk mean, e.g. "Logon accepted · Go ahead: ready for pages";
    /// null when the chunk completes none.</summary>
    public string? Decode(TrafficDirection direction, ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            var side = _sides[(int)direction];
            var other = _sides[1 - (int)direction];
            byte[] buf = side.Pending.Length == 0 ? data.ToArray() : [.. side.Pending, .. data];
            var parts = new List<string>();
            var i = 0;
            while (i < buf.Length)
            {
                var (length, text, role) = Next(buf, i, side, other);
                if (length == 0) break; // unfinished: wait for the next chunk
                if (role != Role.Unknown && side.Role == Role.Unknown && other.Role != role)
                {
                    side.Role = role;
                    other.Role = role == Role.Entry ? Role.Terminal : Role.Entry;
                }
                if (text is not null) parts.Add(text);
                i += length;
            }
            var left = buf.Length - i;
            side.Pending = left is > 0 and <= MaxPending ? buf[i..] : [];
            return Join(parts);
        }
    }

    private (int Length, string? Text, Role Role) Next(byte[] buf, int i, Side side, Side other)
    {
        var rest = buf.Length - i;
        switch (buf[i])
        {
            case STX:
                return Block(buf, i, side);

            case ESC:
                return Escape(buf, i, side, other);

            case EOT:
                if (rest < 2) return Incomplete;
                if (buf[i + 1] != CR) return (1, null, Role.Unknown);
                _awaiting = Awaiting.Logoff;
                return (2, "Log off: no more pages", Role.Entry);

            case CR:
                if (rest >= 2 && buf[i + 1] is ACK or NAK or RS)
                    return rest < 3 ? Incomplete : buf[i + 2] == CR ? Reply(buf[i + 1], 3, other) : (1, null, Role.Unknown);
                if (rest >= 2 && buf[i + 1] == ESC) return (1, null, Role.Unknown); // <CR><ESC><EOT><CR>: forced disconnect
                if (side.Role == Role.Terminal) return rest == 1 ? Incomplete : (1, null, Role.Unknown); // maybe <CR><ACK>…
                return (1, "Wake-up", Role.Unknown);

            case ACK or NAK or RS: // some terminals send <RS><CR> without the leading <CR>
                if (rest < 2) return Incomplete;
                return buf[i + 1] == CR ? Reply(buf[i], 2, other) : (1, null, Role.Unknown);

            case LF or XON or XOFF:
                return (1, null, Role.Unknown);
        }

        if (StartsWith(buf, i, "ID="u8))
        {
            var n = 3;
            if (i + n < buf.Length && buf[i + n] == CR) n++;
            if (i + n < buf.Length && buf[i + n] == LF) n++;
            return (n, "Logon prompt (ID=)", Role.Terminal);
        }
        if (IsPrefixAtEnd(buf, i, "ID="u8)) return Incomplete;

        // A line of text up to <CR>: a message sequence ("115", "211 Page sent") or other text (manual mode, banners)
        var cr = Array.IndexOf(buf, CR, i, Math.Min(rest, MaxLine + 1));
        if (cr < 0) return rest <= MaxLine ? Incomplete : (1, null, Role.Unknown);
        var line = buf.AsSpan(i, cr - i);
        foreach (var b in line) if (b < 0x20 || b >= 0x7F) return (1, null, Role.Unknown); // not a text line
        var len = cr - i + 1;
        if (len < buf.Length - i && buf[cr + 1] == LF) len++;
        if (line.Length >= 3 && char.IsAsciiDigit((char)line[0]) && char.IsAsciiDigit((char)line[1]) && char.IsAsciiDigit((char)line[2]) &&
            (line.Length == 3 || line[3] == (byte)' '))
        {
            var code = (line[0] - '0') * 100 + (line[1] - '0') * 10 + (line[2] - '0');
            return (len, MessageCode(code, DataFormat.ToReadableText(line[3..]).Trim()), Role.Terminal);
        }
        return (len, side.Role == Role.Terminal ? $"Message: \"{DataFormat.ToReadableText(line)}\"" : null, Role.Unknown);
    }

    /// <summary>&lt;STX&gt; field &lt;CR&gt; field &lt;CR&gt; … &lt;ETX|ETB|US&gt; 3-character checksum &lt;CR&gt;</summary>
    private (int Length, string? Text, Role Role) Block(byte[] buf, int i, Side side)
    {
        var end = -1;
        for (var j = i + 1; j < buf.Length && j - i <= MaxBlock; j++)
            if (buf[j] is ETX or ETB or US) { end = j; break; }
        if (end < 0) return buf.Length - i <= MaxBlock ? Incomplete : (1, null, Role.Unknown);
        if (end + 4 >= buf.Length) return Incomplete;

        var sum = 0;
        for (var j = i; j <= end; j++) sum += buf[j] & 0x7F;
        sum &= 0xFFF;
        string expected = $"{(char)(0x30 + ((sum >> 8) & 0xF))}{(char)(0x30 + ((sum >> 4) & 0xF))}{(char)(0x30 + (sum & 0xF))}";
        var got = DataFormat.ToReadableText(buf.AsSpan(end + 1, 3));
        var check = got == expected ? "checksum OK" : $"checksum WRONG: {got}, should be {expected}";

        // Fields end with <CR>; a block ending in <US> stops in the middle of a field.
        var fields = new List<string>();
        var start = i + 1;
        for (var j = start; j < end; j++)
        {
            if (buf[j] != CR) continue;
            fields.Add(DataFormat.ToReadableText(buf.AsSpan(start, j - start)));
            start = j + 1;
        }
        if (start < end) fields.Add(DataFormat.ToReadableText(buf.AsSpan(start, end - start)));

        string text;
        if (!side.InTransaction)
        {
            var pager = fields.Count > 0 ? fields[0] : "";
            text = fields.Count < 2 ? $"Page to {pager}"
                : fields[1].Length == 0 ? $"Page to {pager} (tone only)"
                : $"Page to {pager}: \"{fields[1]}\"";
            if (fields.Count > 2) text += $" (+{fields.Count - 2} more fields)";
        }
        else
        {
            text = $"Page continued: \"{string.Join(" | ", fields)}\"";
        }
        var continues = buf[end] is ETB or US;
        side.NextInTransaction = continues;
        _awaiting = continues ? Awaiting.Block : Awaiting.Page;
        text += continues ? $" – continues in the next block ({check})" : $" ({check})";

        var length = end + 5 - i;
        if (buf[end + 4] != CR) { length = end + 4 - i; text += ", no <CR> after the checksum"; }
        return (length, text, Role.Entry);
    }

    private (int Length, string? Text, Role Role) Escape(byte[] buf, int i, Side side, Side other)
    {
        var rest = buf.Length - i;
        if (rest < 2) return Incomplete;
        var next = buf[i + 1];

        if (next == EOT) // <ESC><EOT><CR>: the terminal disconnects
        {
            if (rest < 3) return Incomplete;
            var n = buf[i + 2] == CR ? 3 : 2;
            _awaiting = Awaiting.None;
            side.InTransaction = other.InTransaction = false;
            return (n, "Disconnect", Role.Terminal);
        }
        if (next == (byte)'[') // <ESC>[p<CR>: message go-ahead
        {
            if (rest < 4) return Incomplete;
            return buf[i + 2] == (byte)'p' && buf[i + 3] == CR ? (4, "Go ahead: ready for pages", Role.Terminal) : (1, null, Role.Unknown);
        }
        if (next is >= (byte)'A' and <= (byte)'Z') // <ESC> service (2) category (1) [password] <CR>: logon
        {
            var cr = Array.IndexOf(buf, CR, i, Math.Min(rest, 17));
            if (cr < 0) return rest <= 16 ? Incomplete : (1, null, Role.Unknown);
            if (cr - i < 4) return (1, null, Role.Unknown);
            var service = DataFormat.ToReadableText(buf.AsSpan(i + 1, 2));
            var category = (char)buf[i + 3];
            var text = (service == "PG" ? "Log on: paging" : $"Log on: service {service}") + $", category {category}";
            if (cr - i > 4) text += ", with password"; // never shown
            _awaiting = Awaiting.Logon;
            side.InTransaction = false;
            return (cr - i + 1, text, Role.Entry);
        }
        return (1, null, Role.Unknown);
    }

    /// <summary>The terminal's answer (ACK / NAK / RS) to the entry device's last logon, block or logoff.</summary>
    private (int Length, string? Text, Role Role) Reply(byte code, int length, Side entry)
    {
        string text;
        switch (code)
        {
            case ACK:
                text = _awaiting switch
                {
                    Awaiting.Logon => "Logon accepted",
                    Awaiting.Page => "Page accepted",
                    Awaiting.Block => "Block accepted, send the next one",
                    _ => "Accepted (ACK)",
                };
                entry.InTransaction = entry.NextInTransaction;
                _awaiting = Awaiting.None;
                break;
            case NAK:
                text = _awaiting is Awaiting.Logon
                    ? "Logon not accepted, try again (NAK)"
                    : "Not accepted: checksum or transmission error, send the block again (NAK)";
                break;
            default:
                text = "Page abandoned (RS): refused, e.g. an invalid pager ID";
                entry.InTransaction = entry.NextInTransaction = false;
                _awaiting = Awaiting.None;
                break;
        }
        return (length, text, Role.Terminal);
    }

    private static string MessageCode(int code, string text)
    {
        var meaning = code switch
        {
            110 => "TAP version",
            111 => "processing, please wait",
            112 => "maximum pages for this session reached",
            113 => "maximum time for this session reached",
            114 => "welcome",
            115 => "goodbye",
            211 => "page(s) sent",
            212 => "long message truncated and sent",
            213 => "message accepted, held for later delivery",
            214 => "message truncated to the character limit and sent",
            501 => "error: time-out waiting for input",
            >= 100 and < 200 => "information",
            >= 200 and < 300 => "success",
            >= 500 and < 600 => "error",
            _ => "message",
        };
        return text.Length == 0 ? $"{code} {meaning}" : $"{code} {meaning}: \"{text}\"";
    }

    private static bool StartsWith(byte[] buf, int i, ReadOnlySpan<byte> s) =>
        buf.Length - i >= s.Length && buf.AsSpan(i, s.Length).SequenceEqual(s);

    private static bool IsPrefixAtEnd(byte[] buf, int i, ReadOnlySpan<byte> s) =>
        buf.Length - i < s.Length && s.StartsWith(buf.AsSpan(i));

    /// <summary>Joins with " · ", folding repeats: "Wake-up ×3".</summary>
    private static string? Join(List<string> parts)
    {
        if (parts.Count == 0) return null;
        var outList = new List<string>();
        for (var k = 0; k < parts.Count;)
        {
            var n = 1;
            while (k + n < parts.Count && parts[k + n] == parts[k]) n++;
            outList.Add(n == 1 ? parts[k] : $"{parts[k]} ×{n}");
            k += n;
        }
        return string.Join(" · ", outList);
    }
}
