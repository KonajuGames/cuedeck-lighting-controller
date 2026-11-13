using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Dmx;
using Cuedeck.Show;

namespace Cuedeck.Tests.Dmx;

/// <summary>
/// sLight's virtual_dimmer_test.gd: a dimmer for fixtures without one, applied as the frame is built.
/// </summary>
public class VirtualDimmerTests
{
    [Fact]
    public void Without_a_dimmer_channel_it_scales_the_colour_and_white_channels_not_the_strobe()
    {
        Assert.Equal([0, 1, 2, 3], Profiles.Rgbw().VirtualDimmerChannels(0));
        Assert.Empty(Profiles.DimmedRgb().VirtualDimmerChannels(0));
        Assert.Empty(Profiles.Simple("pt", "Pan/tilt", ("Pan", "PAN"), ("Tilt", "TILT")).VirtualDimmerChannels(0));
    }

    [Fact]
    public void It_is_applied_when_the_frame_is_built()
    {
        using var u = new DmxUniverse();
        for (var i = 0; i < 5; i++)
            u.SetChannel(10 + i, 200);
        u.SetVirtualDimmer(1, [10, 11, 12, 13]);
        Assert.True(u.HasVirtualDimmer(1));
        Assert.Equal(1.0, u.VirtualDimmerLevel(1));
        u.ComputeOutput();
        Assert.Equal((200, 200), (u.Output[10], u.Output[13]));

        u.SetVirtualDimmerLevel(1, 0.5);
        u.ComputeOutput();
        Assert.Equal((100, 100), (u.Output[10], u.Output[13]));
        Assert.Equal(200, u.Output[14]); // outside it
        Assert.Equal((200, 200), (u.Data[10], u.GetChannel(13))); // stored colour unchanged

        u.ComputeOutput(0.5);
        Assert.Equal((50, 100), (u.Output[10], u.Output[14])); // the grand master on top

        u.ComputeOutput(1.0, new Dictionary<int, int> { [11] = 80 });
        Assert.Equal(40, u.Output[11]); // effect and chase overrides scaled too

        u.SetVirtualDimmer(1, [10, 11, 12, 13]);
        Assert.Equal(0.5, u.VirtualDimmerLevel(1)); // registering again keeps the level

        u.RemoveVirtualDimmer(1);
        u.ComputeOutput();
        Assert.Equal(200, u.Output[10]);
        Assert.Equal(1.0, u.VirtualDimmerLevel(1));
    }

    [Fact]
    public void Patched_fixtures_get_one_when_they_need_one()
    {
        using var show = new LightingShow();
        var u = show.Dmx.Universes[0];
        var a = show.Patch.Add(0, Profiles.Rgbw(), 0, name: "Par A");
        var b = show.Patch.Add(0, Profiles.DimmedRgb(), 10, name: "Par B");
        Assert.True(u.HasVirtualDimmer(a.Id));
        Assert.False(u.HasVirtualDimmer(b.Id));
        Assert.Equal([0, 1, 2, 3], u.VirtualDimmers[a.Id].Channels);

        u.SetChannel(0, 255);
        u.SetChannel(3, 128);
        show.Patch.SetVirtualDimmer(a, 51 / 255.0); // 20 %
        u.ComputeOutput();
        Assert.True(Gd.ApproxEqual(u.VirtualDimmerLevel(a.Id), 0.2));
        Assert.Equal((255, 128), (u.Data[0], u.Data[3])); // the actual colour kept
        Assert.Equal((51, 26), (u.Output[0], u.Output[3])); // the frame dimmed

        show.Patch.LoadLook(0, [10, 20, 30, 40]);
        Assert.Equal(1.0, u.VirtualDimmerLevel(a.Id)); // a look loaded opens it
        Assert.Equal(10, u.Data[0]);

        show.Patch.Remove(a);
        Assert.False(u.HasVirtualDimmer(a.Id));

        show.Patch.ApplyPatch(0, new JsonObject { ["fixtures"] = new JsonArray(new JsonObject { ["name"] = "Par C", ["start"] = 20, ["profile"] = Profiles.Rgbw().ToJson() }) });
        Assert.Single(u.VirtualDimmers);
        Assert.False(u.HasVirtualDimmer(b.Id));
        Assert.Equal([20, 21, 22, 23], u.VirtualDimmers.Values.Single().Channels);
    }
}
