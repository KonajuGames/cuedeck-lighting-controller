using System.Text;
using Cuedeck.Dmx;

namespace Cuedeck.Tests.Dmx;

/// <summary>
/// The packets put on the wire, laid out as sLight's artnet_universe.gd and sacn.gd built them.
/// </summary>
public class PacketTests
{
    [Fact]
    public void An_ArtDmx_packet()
    {
        var dmx = new byte[512];
        dmx[0] = 11;
        dmx[511] = 99;
        var p = ArtNetPacket.Dmx(0x1234, dmx, 7);
        Assert.Equal(18 + 512, p.Length);
        Assert.Equal("Art-Net\0", Encoding.ASCII.GetString(p, 0, 8));
        Assert.Equal([0x00, 0x50], p[8..10]); // OpDmx, low byte first
        Assert.Equal([0, 14], p[10..12]); // protocol version 14
        Assert.Equal(7, p[12]); // sequence
        Assert.Equal([0x34, 0x12], p[14..16]); // universe, low byte first
        Assert.Equal([0x02, 0x00], p[16..18]); // length, high byte first
        Assert.Equal((11, 99), (p[18], p[^1]));
        Assert.Equal(18 + 2, ArtNetPacket.Dmx(1, new byte[3], 1).Length); // an odd length loses its last byte
    }

    [Fact]
    public void An_E131_data_packet()
    {
        var dmx = new byte[512];
        dmx[0] = 42;
        var cid = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var p = Sacn.DataPacket(300, dmx, cid, "Cuedeck", 250, 0x1FF);
        Assert.Equal(638, p.Length);
        Assert.Equal([0x00, 0x10, 0x00, 0x00], p[0..4]);
        Assert.Equal("ASC-E1.17", Encoding.ASCII.GetString(p, 4, 9));
        Assert.Equal([0x72, 0x6E], p[16..18]); // flags and the length from here: 622
        Assert.Equal([0, 0, 0, 4], p[18..22]);
        Assert.Equal(cid, p[22..38]);
        Assert.Equal([0x72, 0x58], p[38..40]); // 600
        Assert.Equal([0, 0, 0, 2], p[40..44]);
        Assert.Equal("Cuedeck", Encoding.UTF8.GetString(p, 44, 7));
        Assert.Equal(0, p[51]);
        Assert.Equal(200, p[108]); // priority, capped
        Assert.Equal(0xFF, p[111]); // sequence wraps
        Assert.Equal([0x01, 0x2C], p[113..115]); // universe 300
        Assert.Equal([0x72, 0x0B], p[115..117]); // 523
        Assert.Equal([0x02, 0xA1], p[117..119]);
        Assert.Equal([0x02, 0x01], p[123..125]); // 513 values
        Assert.Equal((0, 42), (p[125], p[126])); // start code, then slot 1
        Assert.Equal("239.255.1.44", Sacn.MulticastIp(300));
    }

    [Fact]
    public void A_random_cid_is_a_version_4_uuid()
    {
        var cid = Sacn.RandomCid();
        Assert.Equal(16, cid.Length);
        Assert.Equal(0x40, cid[6] & 0xF0);
        Assert.Equal(0x80, cid[8] & 0xC0);
    }

    [Fact]
    public void New_universes_take_the_first_free_number_and_the_last_one_stays()
    {
        using var dmx = new DmxOutput();
        dmx.AddUniverse();
        dmx.AddUniverse();
        dmx.Universes[1].Number = 5;
        dmx.RemoveUniverse(2);
        Assert.Equal(1, dmx.AddUniverse()!.Number);
        dmx.SetUniverseCount(1);
        dmx.RemoveUniverse(0);
        Assert.Equal(1, dmx.UniverseCount);
        dmx.SetUniverseCount(20);
        Assert.Equal(DmxOutput.MaxUniverses, dmx.UniverseCount);
        Assert.Null(dmx.AddUniverse());
    }
}
