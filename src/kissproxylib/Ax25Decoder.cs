using System.Text;

namespace kissproxylib;

/// <summary>
/// Stateless AX.25 (and NET/ROM) decoder producing one-line monitor text in the
/// style of the BPQ / QtTermTCP monitor, e.g.
/// <code>
///   GB7WIN>M0LTE &lt;RR C P R3&gt;
///   M0LTE>GB7WIN &lt;I C S2 R3&gt; pid=F0 len=12: BYE\r
///   GB7WIN>GB7RDG &lt;XID C P&gt; N1=256 k=7
/// </code>
/// Ported in spirit from packet.net's monitor decode (MonitorEventFactory), which
/// needs .NET 10 and so can't run on the 32-bit Bullseye nodes kissproxy targets.
/// Being stateless it assumes modulo-8 sequence numbers; a modulo-128 (SABME)
/// session's I and S frames will decode with the wrong N(S)/N(R).
/// </summary>
public static class Ax25Decoder
{
    private const int MaxTextChars = 80;

    /// <summary>
    /// Decodes a complete KISS frame (FEND-delimited, still escaped). Returns null
    /// for KISS commands that aren't AX.25 data (TxDelay etc.).
    /// </summary>
    public static string? DescribeKissFrame(byte[] kissFrame)
    {
        var payload = Unescape(kissFrame);
        if (payload.Count == 0)
            return null;

        int port = payload[0] >> 4;
        int command = payload[0] & 0x0F;
        string portPrefix = port != 0 ? $"p{port} " : "";

        // Modem to host ACKMODE ack: just the two sequence bytes, sent once the frame is on air.
        if (command == KissFrameBuilder.CMD_ACKMODE && payload.Count == 3)
            return $"{portPrefix}ACKMODE ack seq={payload[1]:X2}{payload[2]:X2}";

        var data = UnkissData(payload);
        if (data == null)
            return null;

        string ackPrefix = data.AckSeq is { } seq ? $"ACKMODE seq={seq:X4} " : "";
        return portPrefix + ackPrefix + Describe(data.Ax25);
    }

    /// <summary>
    /// The AX.25 frame carried by a KISS data or ACKMODE frame, or null for anything else
    /// (parameter commands, ACKMODE acks).
    /// </summary>
    public static KissData? UnkissData(byte[] kissFrame) => UnkissData(Unescape(kissFrame));

    private static KissData? UnkissData(List<byte> payload)
    {
        if (payload.Count < 2)
            return null;

        int port = payload[0] >> 4;
        int command = payload[0] & 0x0F;

        if (command == KissFrameBuilder.CMD_DATAFRAME)
            return new KissData(port, payload.Skip(1).ToArray(), null);

        if (command == KissFrameBuilder.CMD_ACKMODE && payload.Count > 3)
            return new KissData(port, payload.Skip(3).ToArray(), (payload[1] << 8) | payload[2]);

        return null;
    }

    /// <summary>
    /// Parses the AX.25 header fields, or returns null if the address field is malformed.
    /// Stateless, so it assumes modulo-8 sequence numbers.
    /// </summary>
    public static Ax25Fields? Parse(byte[] frame)
    {
        // Address field: destination, source, then up to 8 digipeaters,
        // terminated by the extension bit in the last SSID byte.
        int addrEnd = -1;
        for (int i = 6; i < frame.Length && i < 70; i += 7)
        {
            if ((frame[i] & 0x01) != 0)
            {
                addrEnd = i + 1;
                break;
            }
        }

        if (addrEnd < 14 || addrEnd >= frame.Length)
            return null;

        var digis = new List<string>();
        for (int offset = 14; offset < addrEnd; offset += 7)
            digis.Add(Callsign(frame, offset) + ((frame[offset + 6] & 0x80) != 0 ? "*" : ""));

        bool destC = (frame[6] & 0x80) != 0;
        bool srcC = (frame[13] & 0x80) != 0;
        char? cr = destC && !srcC ? 'C' : !destC && srcC ? 'R' : null;

        byte ctrl = frame[addrEnd];
        bool pf = (ctrl & 0x10) != 0;
        int infoStart = addrEnd + 1;

        string type;
        int? ns = null, nr = null, pid = null;
        if ((ctrl & 0x01) == 0)
        {
            type = "I";
            ns = (ctrl >> 1) & 7;
            nr = (ctrl >> 5) & 7;
        }
        else if ((ctrl & 0x03) == 0x01)
        {
            type = (ctrl & 0x0C) switch
            {
                0x00 => "RR",
                0x04 => "RNR",
                0x08 => "REJ",
                _ => "SREJ",
            };
            nr = (ctrl >> 5) & 7;
        }
        else
        {
            type = (ctrl & 0xEF) switch
            {
                0x2F => "SABM",
                0x6F => "SABME",
                0x43 => "DISC",
                0x63 => "UA",
                0x0F => "DM",
                0x87 => "FRMR",
                0xAF => "XID",
                0xE3 => "TEST",
                0x03 => "UI",
                _ => $"U{ctrl:X2}",
            };
        }

        if ((type == "I" || type == "UI") && infoStart < frame.Length)
        {
            pid = frame[infoStart];
            infoStart++;
        }

        return new Ax25Fields(Callsign(frame, 7), Callsign(frame, 0), digis, type, cr, pf, ns, nr, pid,
            infoStart, Math.Max(0, frame.Length - infoStart));
    }

    /// <summary>Decodes a raw AX.25 frame (no KISS framing, no FCS).</summary>
    public static string Describe(byte[] frame)
    {
        var f = Parse(frame);
        if (f == null)
            return $"undecodable len={frame.Length}: {Hex(frame, 0, Math.Min(frame.Length, 24))}";

        var sb = new StringBuilder();
        sb.Append(f.Source).Append('>').Append(f.Dest);
        foreach (var digi in f.Digis)
            sb.Append(',').Append(digi);

        string crText = f.CommandResponse is { } cr ? $" {cr}" : "";
        string pfText = f.PollFinal ? (f.CommandResponse == 'R' ? " F" : " P") : "";

        sb.Append(" <").Append(f.Type).Append(crText).Append(pfText);
        if (f.Ns is { } ns)
            sb.Append($" S{ns}");
        if (f.Nr is { } nr)
            sb.Append($" R{nr}");
        sb.Append('>');

        switch (f.Type)
        {
            case "I" or "UI" when f.Pid != null:
                AppendPidAndInfo(sb, frame, f.InfoStart - 1, ui: f.Type == "UI");
                break;
            case "I" or "UI":
                break;
            case "XID":
                AppendXid(sb, frame, f.InfoStart);
                break;
            default:
                if (f.InfoLength > 0)
                    sb.Append($" len={f.InfoLength}: {Hex(frame, f.InfoStart, Math.Min(f.InfoLength, 16))}");
                break;
        }

        return sb.ToString();
    }

    private static void AppendPidAndInfo(StringBuilder sb, byte[] frame, int pidIndex, bool ui)
    {
        if (pidIndex >= frame.Length)
            return;

        byte pid = frame[pidIndex];
        int infoStart = pidIndex + 1;
        int infoLen = frame.Length - infoStart;

        if (pid == 0xCF)
        {
            sb.Append(' ').Append(DescribeNetRom(frame, infoStart, ui));
            return;
        }

        sb.Append($" pid={pid:X2} len={infoLen}");
        if (infoLen > 0 && pid == 0xF0)
            sb.Append(": ").Append(Text(frame, infoStart, infoLen));
    }

    private static string DescribeNetRom(byte[] frame, int start, bool ui)
    {
        int len = frame.Length - start;

        // NODES broadcast: 0xFF, sender alias (6), then 21-byte route entries.
        if (ui && len >= 7 && frame[start] == 0xFF)
        {
            string alias = Encoding.ASCII.GetString(frame, start + 1, 6).Trim();
            int routes = (len - 7) / 21;
            return $"NET/ROM NODES {alias} routes={routes}";
        }

        // L3 header (origin, dest, TTL) then the 5-byte L4 header.
        if (len < 20)
            return $"NET/ROM len={len}";

        string origin = Callsign(frame, start);
        string dest = Callsign(frame, start + 7);
        int ttl = frame[start + 14];
        int l4 = start + 15;
        byte index = frame[l4], id = frame[l4 + 1], txSeq = frame[l4 + 2], rxSeq = frame[l4 + 3], op = frame[l4 + 4];
        int dataStart = l4 + 5;
        int dataLen = frame.Length - dataStart;

        var flags = new StringBuilder();
        if ((op & 0x80) != 0) flags.Append(" CHOKE");
        if ((op & 0x40) != 0) flags.Append(" NAK");
        if ((op & 0x20) != 0) flags.Append(" MORE");

        string circuit = $"{index:X2}/{id:X2}";
        string l4Text = (op & 0x0F) switch
        {
            0x01 when dataLen >= 15 => $"CREQ {circuit} w={frame[dataStart]} user={Callsign(frame, dataStart + 1)} node={Callsign(frame, dataStart + 8)}",
            0x01 => $"CREQ {circuit}",
            0x02 => $"CACK {circuit} my={txSeq:X2}/{rxSeq:X2}" + (dataLen >= 1 ? $" w={frame[dataStart]}" : ""),
            0x03 => $"DREQ {circuit}",
            0x04 => $"DACK {circuit}",
            0x05 => $"INFO {circuit} S{txSeq} R{rxSeq}",
            0x06 => $"IACK {circuit} R{rxSeq}",
            0x07 => $"RSET {circuit}",
            0x08 => $"CRQX {circuit}",
            0x00 => $"PID {circuit}",
            _ => $"OP{op & 0x0F:X} {circuit}",
        };

        var result = $"NET/ROM {origin}>{dest} ttl={ttl} <{l4Text}{flags}>";
        if ((op & 0x0F) == 0x05 && dataLen > 0)
            result += $" len={dataLen}: {Text(frame, dataStart, dataLen)}";
        return result;
    }

    private static void AppendXid(StringBuilder sb, byte[] frame, int start)
    {
        // FI (0x82), GI (0x80), 2-byte group length, then PI/PL/PV parameters.
        if (frame.Length - start < 4 || frame[start] != 0x82 || frame[start + 1] != 0x80)
        {
            if (frame.Length > start)
                sb.Append($" len={frame.Length - start}");
            return;
        }

        int end = Math.Min(frame.Length, start + 4 + ((frame[start + 2] << 8) | frame[start + 3]));
        int i = start + 4;
        while (i + 1 < end)
        {
            int pi = frame[i], pl = frame[i + 1];
            if (i + 2 + pl > end)
                break;

            long value = 0;
            for (int j = 0; j < pl; j++)
                value = (value << 8) | frame[i + 2 + j];

            string? text = pi switch
            {
                5 => $"N1tx={value / 8}",
                6 => $"N1={value / 8}",
                7 => $"ktx={value}",
                8 => $"k={value}",
                9 => $"T1={value}ms",
                10 => $"N2={value}",
                _ => null,
            };
            if (text != null)
                sb.Append(' ').Append(text);

            i += 2 + pl;
        }
    }

    private static string Callsign(byte[] frame, int offset)
    {
        var sb = new StringBuilder(9);
        for (int i = 0; i < 6; i++)
        {
            char c = (char)((frame[offset + i] >> 1) & 0x7F);
            if (c == ' ')
                continue;
            sb.Append(c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' ? c : '?');
        }

        int ssid = (frame[offset + 6] >> 1) & 0x0F;
        if (ssid != 0)
            sb.Append('-').Append(ssid);
        return sb.ToString();
    }

    private static string Text(byte[] frame, int start, int length)
    {
        var sb = new StringBuilder();
        for (int i = start; i < start + length; i++)
        {
            if (sb.Length >= MaxTextChars)
            {
                sb.Append("...");
                break;
            }

            byte b = frame[i];
            if (b == 0x0D)
                sb.Append("\\r");
            else if (b == 0x0A)
                sb.Append("\\n");
            else
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        }
        return sb.ToString();
    }

    private static string Hex(byte[] frame, int start, int length) =>
        string.Join(" ", frame.Skip(start).Take(length).Select(b => b.ToString("X2")));

    private static List<byte> Unescape(byte[] kissFrame)
    {
        var result = new List<byte>(kissFrame.Length);
        bool escaped = false;
        foreach (byte b in kissFrame)
        {
            if (b == KissFrameBuilder.FEND)
                continue;

            if (escaped)
            {
                result.Add(b switch
                {
                    KissFrameBuilder.TFEND => KissFrameBuilder.FEND,
                    KissFrameBuilder.TFESC => KissFrameBuilder.FESC,
                    _ => b,
                });
                escaped = false;
            }
            else if (b == KissFrameBuilder.FESC)
            {
                escaped = true;
            }
            else
            {
                result.Add(b);
            }
        }
        return result;
    }
}

/// <summary>The AX.25 frame inside a KISS data or ACKMODE frame.</summary>
public sealed record KissData(int Port, byte[] Ax25, int? AckSeq);

/// <summary>Header fields of an AX.25 frame, as decoded by <see cref="Ax25Decoder.Parse"/>.</summary>
/// <param name="CommandResponse">'C' or 'R', or null for an AX.25 v1 frame that doesn't say</param>
/// <param name="InfoStart">Offset of the information field (after the PID, if any)</param>
public sealed record Ax25Fields(
    string Source,
    string Dest,
    IReadOnlyList<string> Digis,
    string Type,
    char? CommandResponse,
    bool PollFinal,
    int? Ns,
    int? Nr,
    int? Pid,
    int InfoStart,
    int InfoLength)
{
    public bool IsCommand => CommandResponse != 'R';
}
