using System.Buffers.Binary;
using System.Text;

namespace Cuedeck.Triggers;

/// <summary>
/// An OSC message: its address and arguments (int, float, string, bool, or null for nil and impulse). sLight's
/// <c>OscMessage</c>: enough of OSC 1.0 for GO-style triggers, with bundles unwrapped and no wildcard matching
/// (bindings compare the address literally).
/// </summary>
sealed record OscMessage(string Address, IReadOnlyList<object?> Args)
{
    /// <summary>
    /// The messages in one UDP packet: one, or a bundle's several (none if it can't be read).
    /// </summary>
    public static List<OscMessage> Parse(ReadOnlySpan<byte> data)
    {
        var output = new List<OscMessage>();
        Parse(data, output);
        return output;
    }

    static void Parse(ReadOnlySpan<byte> data, List<OscMessage> output)
    {
        if (data.Length < 4)
            return;
        var (head, p) = ReadString(data, 0);
        if (head == "#bundle")
        {
            // "#bundle\0", an 8-byte time tag, then [int32 size][element]...
            p = 16;
            while (p + 4 <= data.Length)
            {
                var size = ReadInt(data, p);
                p += 4;
                if (size <= 0 || p + size > data.Length)
                    break;
                Parse(data.Slice(p, size), output);
                p += size;
            }
            return;
        }

        var args = new List<object?>();
        if (p < data.Length && data[p] == (byte)',')
        {
            var (tags, after) = ReadString(data, p);
            p = after;
            for (var i = 1; i < tags.Length; i++)
            {
                var stop = false;
                switch (tags[i])
                {
                    case 'i' or 'r' or 'c':
                        args.Add(ReadInt(data, p));
                        p += 4;
                        break;
                    case 'f':
                        args.Add(p + 4 <= data.Length ? BinaryPrimitives.ReadSingleBigEndian(data[p..]) : 0f);
                        p += 4;
                        break;
                    case 's' or 'S':
                        var (s, next) = ReadString(data, p);
                        args.Add(s);
                        p = next;
                        break;
                    case 'T':
                        args.Add(true);
                        break;
                    case 'F':
                        args.Add(false);
                        break;
                    case 'N' or 'I':
                        args.Add(null);
                        break;
                    case 'b':
                        // A blob: its size, then its bytes padded; skipped so later arguments stay aligned.
                        p += 4 + Pad4(Math.Max(ReadInt(data, p), 0));
                        break;
                    case 'h' or 't' or 'd':
                        p += 8;
                        break;
                    default:
                        stop = true;
                        break;
                }
                if (stop)
                    break;
            }
        }
        output.Add(new OscMessage(head, args));
    }

    /// <summary>
    /// A message as a packet. Arguments may be int, float or string.
    /// </summary>
    public static byte[] Encode(string address, params object[] args)
    {
        using var output = new MemoryStream();
        WriteString(output, address);
        var tags = new StringBuilder(",");
        using var body = new MemoryStream();
        Span<byte> word = stackalloc byte[4];
        foreach (var a in args)
        {
            switch (a)
            {
                case int i:
                    tags.Append('i');
                    BinaryPrimitives.WriteInt32BigEndian(word, i);
                    body.Write(word);
                    break;
                case float f:
                    tags.Append('f');
                    BinaryPrimitives.WriteSingleBigEndian(word, f);
                    body.Write(word);
                    break;
                case double d:
                    tags.Append('f');
                    BinaryPrimitives.WriteSingleBigEndian(word, (float)d);
                    body.Write(word);
                    break;
                default:
                    tags.Append('s');
                    WriteString(body, a?.ToString() ?? "");
                    break;
            }
        }
        WriteString(output, tags.ToString());
        body.WriteTo(output);
        return output.ToArray();
    }

    static void WriteString(Stream to, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        to.Write(bytes);
        to.Write(new byte[Pad4(bytes.Length + 1) - bytes.Length]);
    }

    /// <summary>
    /// An OSC string (nul-ended, padded with nuls to four bytes) and where the next thing starts.
    /// </summary>
    static (string Text, int Next) ReadString(ReadOnlySpan<byte> data, int at)
    {
        var end = at;
        while (end < data.Length && data[end] != 0)
            end++;
        return (Encoding.UTF8.GetString(data[at..end]), at + Pad4(end - at + 1));
    }

    static int ReadInt(ReadOnlySpan<byte> data, int at) => at + 4 <= data.Length ? BinaryPrimitives.ReadInt32BigEndian(data[at..]) : 0;

    static int Pad4(int n) => (n + 3) & ~3;
}
