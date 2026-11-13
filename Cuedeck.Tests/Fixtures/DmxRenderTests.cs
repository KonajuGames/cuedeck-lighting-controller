using Cuedeck.Common;
using Cuedeck.Fixtures;
using Cuedeck.Show;

namespace Cuedeck.Tests.Fixtures;

/// <summary>
/// sLight's dmx_render_test.gd: a fixture's live DMX turned into what the 3D view shows.
/// </summary>
public class DmxRenderTests
{
    static float Dimmer(FixtureProfile prof, int v) => DmxRender.Evaluate(prof, 0, [(byte)(v >> 8), (byte)(v & 0xFF), 0, 0], 0).Dimmer;

    // The fine channel carrying its coarse channel's role, as some profiles have it, and as GENERIC.
    public static TheoryData<string> FineRoles => ["DIMMER", "GENERIC"];

    static FixtureProfile Dimmer16(string fineRole) =>
        Profiles.Make("d16", "Dimmer 16", $$"""[{"name":"Dimmer","role":"DIMMER"},{"name":"Dimmer Fine","role":"{{fineRole}}","fine":true}]""");

    [Theory]
    [MemberData(nameof(FineRoles))]
    public void The_dimmer_only_rises_as_the_16_bit_level_does(string fineRole)
    {
        var prof = Dimmer16(fineRole);
        var last = -1.0f;
        for (var v = 0; v < 65536; v += 97)
        {
            var d = Dimmer(prof, v);
            Assert.True(d >= last - 0.0001f, $"{d} after {last} at {v}");
            last = d;
        }
    }

    [Theory]
    [MemberData(nameof(FineRoles))]
    public void Coarse_is_the_high_byte(string fineRole)
    {
        var prof = Dimmer16(fineRole);
        Assert.True(Gd.ApproxEqual(Dimmer(prof, 0x8000), 0x8000 / 65535.0));
        Assert.True(Gd.ApproxEqual(Dimmer(prof, 65535), 1.0));
    }

    [Fact]
    public void A_fine_channel_with_PANs_role_doesnt_take_over_the_pan()
    {
        var pan = Profiles.Make("p16", "Pan 16", """[{"name":"Pan","role":"PAN"},{"name":"Pan Fine","role":"PAN","fine":true}]""", """{"pan_range":540}""");
        var st = DmxRender.Evaluate(pan, 0, [0xC0, 0x00], 0);
        var expect = (0xC000 / 65535.0 - 0.5) * 540.0;
        Assert.True(Math.Abs(st.Pan - expect) < 0.01, $"{st.Pan}");
    }

    [Fact]
    public void An_effect_on_DIMMER_drives_the_coarse_channel_not_the_fine_one()
    {
        using var show = new LightingShow();
        show.Patch.Add(0, Dimmer16("DIMMER"), 10);
        var targets = show.Targets("DIMMER", -1, "");
        Assert.Equal([new ChannelTarget(0, 10)], targets);
    }

    [Fact]
    public void A_colour_wheel_tints_and_a_closed_shutter_blacks_out()
    {
        var spot = FixtureProfile.BuiltIn().First(p => p.Id == "mh_spot");
        // Pan, tilt, dimmer full, shutter open, wheel on red.
        var st = DmxRender.Evaluate(spot, 0, [128, 128, 255, 12, 15, 0, 0, 0], 0);
        Assert.Equal(1, st.Dimmer);
        Assert.False(st.Blackout);
        Assert.Equal(Rgb.FromString("#e01f1f", Rgb.White), st.Color);
        var closed = DmxRender.Evaluate(spot, 0, [128, 128, 255, 3, 15, 0, 0, 0], 0);
        Assert.True(closed.Blackout);
        Assert.Equal(0, closed.Dimmer);
    }
}
