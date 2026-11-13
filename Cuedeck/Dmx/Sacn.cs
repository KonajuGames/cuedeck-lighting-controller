using System.Security.Cryptography;
using System.Text;

namespace Cuedeck.Dmx;

/// <summary>
/// E1.31 (streaming ACN): DMX over Ethernet, the ANSI-standard cousin of Art-Net. Multicast by default
/// (239.255.hi.lo, UDP 5568); a receiver subscribes to the universe's group. Builds one E1.31 Data packet: a 0x00
/// start code and 512 slots.
/// </summary>
static class Sacn
{
    public const int Port = 5568;
    const string AcnPid = "ASC-E1.17";
    const uint VectorRootData = 0x00000004;
    const uint VectorFramingData = 0x00000002;
    const byte VectorDmpSetProperty = 0x02;
    const int Slots = 512;

    /// <summary>
    /// A full packet's length: 126 bytes of headers, then the slots.
    /// </summary>
    public const int PacketLength = 126 + Slots;

    /// <summary>
    /// The multicast group for a universe: 239.255.(universe >> 8).(universe &amp; 255).
    /// </summary>
    public static string MulticastIp(int universe) => $"239.255.{(universe >> 8) & 0xFF}.{universe & 0xFF}";

    /// <summary>
    /// A random 16-byte CID (a version 4 UUID). Kept in the show file, so a receiver sees one source across restarts.
    /// </summary>
    public static byte[] RandomCid()
    {
        var b = RandomNumberGenerator.GetBytes(16);
        b[6] = (byte)((b[6] & 0x0F) | 0x40);
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        return b;
    }

    /// <summary>
    /// One E1.31 Data packet of <paramref name="dmx"/>'s 512 slot values (the start code's added here).
    /// <paramref name="sequence"/> wraps at 256, bumped once per packet per universe.
    /// </summary>
    public static byte[] DataPacket(int universe, ReadOnlySpan<byte> dmx, ReadOnlySpan<byte> cid, string sourceName, int priority, int sequence)
    {
        var p = new byte[PacketLength];

        // Root layer (0 to 37).
        U16(p, 0, 0x0010); // preamble size
        U16(p, 2, 0x0000); // post-amble size
        Encoding.ASCII.GetBytes(AcnPid).CopyTo(p, 4); // 4 to 15, the rest already 0
        FlagsAndLength(p, 16, PacketLength - 16);
        U32(p, 18, VectorRootData);
        for (var i = 0; i < 16; i++)
            p[22 + i] = i < cid.Length ? cid[i] : (byte)0;

        // Framing layer (38 to 114).
        FlagsAndLength(p, 38, PacketLength - 38);
        U32(p, 40, VectorFramingData);
        var name = Encoding.UTF8.GetBytes(sourceName);
        Array.Copy(name, 0, p, 44, Math.Min(name.Length, 63)); // 44 to 107, null-padded
        p[108] = (byte)Math.Clamp(priority, 0, 200);
        U16(p, 109, 0); // synchronization address
        p[111] = (byte)(sequence & 0xFF);
        p[112] = 0; // options
        U16(p, 113, universe);

        // DMP layer (115 to 637).
        FlagsAndLength(p, 115, PacketLength - 115);
        p[117] = VectorDmpSetProperty;
        p[118] = 0xA1; // address and data type
        U16(p, 119, 0x0000); // first property address
        U16(p, 121, 0x0001); // address increment
        U16(p, 123, Slots + 1); // property value count: the start code and the slots
        p[125] = 0x00; // DMX start code
        for (var i = 0; i < Slots; i++)
            p[126 + i] = i < dmx.Length ? dmx[i] : (byte)0;
        return p;
    }

    static void U16(byte[] p, int offset, int v)
    {
        p[offset] = (byte)((v >> 8) & 0xFF);
        p[offset + 1] = (byte)(v & 0xFF);
    }

    static void U32(byte[] p, int offset, uint v)
    {
        p[offset] = (byte)((v >> 24) & 0xFF);
        p[offset + 1] = (byte)((v >> 16) & 0xFF);
        p[offset + 2] = (byte)((v >> 8) & 0xFF);
        p[offset + 3] = (byte)(v & 0xFF);
    }

    /// <summary>
    /// A PDU's "flags and length": the top nibble 0x7, then a 12-bit length counting from this field to the end.
    /// </summary>
    static void FlagsAndLength(byte[] p, int offset, int length)
    {
        p[offset] = (byte)(0x70 | ((length >> 8) & 0x0F));
        p[offset + 1] = (byte)(length & 0xFF);
    }
}

/// <summary>
/// Art-Net's ArtDMX packet, which any Art-Net node, gateway or lighting program (QLC+, say) can receive.
/// </summary>
static class ArtNetPacket
{
    public const int Port = 6454;

    /// <summary>
    /// One ArtDMX packet of <paramref name="dmx"/> (an even number of bytes is sent; an odd last one is dropped) for
    /// universe <paramref name="universe"/> (15-bit), with <paramref name="sequence"/> from 1 to 255.
    /// </summary>
    public static byte[] Dmx(int universe, ReadOnlySpan<byte> dmx, int sequence)
    {
        var length = dmx.Length - dmx.Length % 2;
        var p = new byte[18 + length];
        // "Art-Net" and a null: the protocol ID.
        Encoding.ASCII.GetBytes("Art-Net").CopyTo(p, 0);
        p[7] = 0;
        // OpDmx (0x5000), low byte first.
        p[8] = 0x00;
        p[9] = 0x50;
        // Protocol version 14, high byte first.
        p[10] = 0;
        p[11] = 14;
        p[12] = (byte)sequence;
        p[13] = 0; // physical port, informational only
        // Universe, low byte first.
        p[14] = (byte)(universe & 0xFF);
        p[15] = (byte)((universe >> 8) & 0xFF);
        // Data length, high byte first.
        p[16] = (byte)((length >> 8) & 0xFF);
        p[17] = (byte)(length & 0xFF);
        dmx[..length].CopyTo(p.AsSpan(18));
        return p;
    }
}
