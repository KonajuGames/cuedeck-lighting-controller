using System.Numerics;
using Cuedeck.Common;
using Cuedeck.Fixtures;
using Cuedeck.Visualizer;

namespace Cuedeck.Tests.Visualizer;

/// <summary>
/// A fixture's rig in the 3D view (sLight's tests/fixture_view_test.gd): which fixtures hang in a yoke that follows
/// their pan and tilt, how far those turn, and the beam's spread.
/// </summary>
public sealed class FixtureRigTests
{
    static readonly string[] Moving = ["PAN", "TILT", "DIMMER", "RED", "GREEN", "BLUE"];

    /// <summary>
    /// A profile with <paramref name="roles"/> and physical category <paramref name="category"/> ("" to guess).
    /// </summary>
    static FixtureProfile P(string id, string[] roles, string category)
    {
        var p = Profiles.Simple(id, id, [.. roles.Select(r => (r, r))]);
        p.Physical.Category = category;
        return p;
    }

    /// <summary>
    /// Runs the rig for <paramref name="frames"/> one-second frames of <paramref name="output"/> (patched at channel
    /// 1).
    /// </summary>
    static void Run(FixtureRig rig, FixtureProfile p, byte[] output, int frames)
    {
        for (var i = 0; i < frames; i++)
            rig.Update(DmxRender.Evaluate(p, 0, output, 0), 1.0);
    }

    [Theory]
    [InlineData("auto", "", true)]
    [InlineData("moving head", "moving_head", true)]
    [InlineData("beam", "beam", true)]
    [InlineData("wash", "wash", true)]
    public void Fixtures_with_pan_and_tilt_hang_in_a_yoke(string _, string category, bool yoked) =>
        Assert.Equal(yoked, new FixtureRig(P("x", Moving, category), 0).Yoked);

    [Fact]
    public void Fixtures_without_are_fixed()
    {
        Assert.False(new FixtureRig(P("fbeam", ["DIMMER"], "beam"), 0).Yoked); // a beam without pan / tilt
        Assert.False(new FixtureRig(P("par", ["DIMMER", "RED", "GREEN", "BLUE"], "par"), 0).Yoked);
    }

    [Fact]
    public void A_beam_follows_its_pan_and_tilt()
    {
        var p = P("beam", Moving, "beam");
        var rig = new FixtureRig(p, 0);
        var output = new byte[512];
        output[0] = output[1] = 128;
        Run(rig, p, output, 10);
        var (pan0, tilt0) = (rig.Pan, rig.Tilt);
        // Centred (128 / 128) it aims along the pan axis, out of the yoke.
        var aim = Vector3.Normalize(rig.HeadAim(0, Matrix4x4.Identity));
        var panAxis = Vector3.Normalize(rig.PanAxis(Matrix4x4.Identity));
        Assert.True(Vector3.Dot(aim, panAxis) > 0.99f, $"aim {aim}, pan axis {panAxis}");
        output[0] = output[1] = 255;
        Run(rig, p, output, 10);
        Assert.True(rig.Pan > pan0 + 1.0 && rig.Tilt > tilt0 + 0.5, $"pan {pan0} -> {rig.Pan}, tilt {tilt0} -> {rig.Tilt}");
        // A moving wash is still wide; a beam narrow.
        Assert.True(new FixtureRig(P("mwash", Moving, "wash"), 0).HalfAngle >= 15);
        Assert.True(rig.HalfAngle < 15);
    }

    [Fact]
    public void The_profiles_ranges_and_beam_angle_set_the_travel_and_spread()
    {
        var p = P("lim", Moving, "moving_head");
        p.Physical.PanRange = 180;
        p.Physical.TiltRange = 90;
        p.Physical.BeamDeg = 40;
        var rig = new FixtureRig(p, 0);
        var output = new byte[512];
        output[0] = output[1] = 255;
        Run(rig, p, output, 20);
        // Full pan and tilt reach half the profile's range.
        Assert.Equal(90.0, Gd.Snapped(rig.Pan * 180 / Math.PI, 0.1), 6);
        Assert.Equal(45.0, Gd.Snapped(rig.Tilt * 180 / Math.PI, 0.1), 6);
        Assert.Equal(20.0, rig.HalfAngle, 6); // the beam spreads to the profile's angle
    }

    [Fact]
    public void Heads_take_their_colour_and_level()
    {
        var p = Profiles.Simple("bar", "bar", ("Dimmer", "DIMMER"), ("Strobe", "STROBE"), ("Red", "RED"), ("Green", "GREEN"), ("Blue", "BLUE"), ("Red", "RED"), ("Green", "GREEN"), ("Blue", "BLUE"));
        p.Physical.Category = "bar";
        var rig = new FixtureRig(p, 0);
        Assert.Equal(2, rig.Heads.Count);
        Assert.False(rig.Yoked);
        var output = new byte[512];
        output[0] = 255;
        output[2] = 255; // head 1 red
        output[6] = 255; // head 2 green
        rig.Update(DmxRender.Evaluate(p, 0, output, 0), 0.01);
        Assert.Equal((1f, 0f), (rig.Heads[0].Colour.R, rig.Heads[0].Colour.G));
        Assert.Equal((0f, 1f), (rig.Heads[1].Colour.R, rig.Heads[1].Colour.G));
        Assert.True(rig.Heads[0].Level > 0.99);
        // The heads sit side by side along the bar.
        Assert.True(rig.Heads[0].Offset.X < rig.Heads[1].Offset.X);
    }
}
