using Brutal.Numerics;
using Cuedeck.Show;
using Cuedeck.UI;
using Cuedeck.UI.Console;

namespace Cuedeck.Tests.UI;

/// <summary>
/// The Cues tab, clicked through: recording, GO, the progress bar, choosing a cue and editing it, making it a block,
/// Load to Patch, Duplicate, Delete and double-clicking a cue to fire it.
/// </summary>
public sealed class CuesTabTests : ConsoleHarness
{
    CuesTab Tab => Screen.Console.Cues;

    int Level(int channel) => Show.Dmx.Universes[0].GetChannel(channel);

    void Run(double seconds)
    {
        for (var t = 0.0; t < seconds; t += 1.0 / 30)
            Show.Tick(1.0 / 30, transmit: false);
        Frame();
    }

    /// <summary>
    /// Clicks cue <paramref name="index"/>'s row in the list, twice to double-click it.
    /// </summary>
    void ClickRow(int index, int times = 1)
    {
        var list = Tab.List;
        ClickAt(list.ScreenPosition + new float2(60, index * list.RowHeight + list.RowHeight / 2), times);
    }

    /// <summary>
    /// The number box in the editor's row called <paramref name="name"/>.
    /// </summary>
    NumberBox Field(string name) =>
        Find<Row>(r => r.Children.Count > 1 && r.Children[0] is FixedWidth && All(r).OfType<NumberBox>().Any() && LabelOf(r) == name).Children.OfType<NumberBox>().First();

    static string LabelOf(Row row) => row.Children[0] is FixedWidth { Inner: Label l } ? l.Text : "";

    void RecordTwoCues()
    {
        Show.Dmx.Universes[0].SetChannel(0, 255);
        Click(ButtonCalled("Record Cue"));
        Show.Dmx.Universes[0].SetChannel(1, 100);
        Click(ButtonCalled("Record Cue"));
        Show.Dmx.Universes[0].SetAll(0);
    }

    [Fact]
    public void Recording_cues_lists_them_and_says_whats_next()
    {
        Assert.Contains(All(Tab).OfType<Label>(), l => l.Text.StartsWith("No cues", StringComparison.Ordinal));
        RecordTwoCues();
        Assert.Equal(2, Show.Cues.Cues.Count);
        Assert.Equal(2, Tab.List.Items.Count);
        Assert.Equal(1, Tab.Selected); // the one just recorded
        Assert.True(Show.Cues.Cues[1].Tracking); // tracking, as the Tracking box says
        Assert.Equal(new Dictionary<int, int> { [1] = 100 }, Show.Cues.Cues[1].Levels.Universes[0]); // just what it changed
        Assert.Contains(All(Tab).OfType<Label>(), l => l.Text.StartsWith("Next: cue 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Go_fades_to_the_cue_with_the_bar_following()
    {
        RecordTwoCues();
        Click(ButtonCalled("GO"));
        Assert.Equal(0, Show.Cues.Current);
        Run(1.5); // half its 3 second fade
        Assert.InRange(Level(0), 110, 145);
        var bar = All(Tab).OfType<ProgressBar>().Single();
        Assert.InRange(bar.Value, 0.4f, 0.6f);
        Run(2);
        Assert.Equal(255, Level(0));
        Assert.Equal(1, bar.Value);
        Assert.Contains(All(Tab).OfType<Label>(), l => l.Text.StartsWith("Next: cue 2", StringComparison.Ordinal));

        Click(ButtonCalled("GO"));
        Run(3.5);
        Assert.Equal((255, 100), (Level(0), Level(1))); // cue 2 tracks cue 1's level

        Click(ButtonCalled("Back"));
        Run(3.5);
        Assert.Equal((255, 0), (Level(0), Level(1)));

        Click(ButtonCalled("Clear"));
        Assert.Equal(-1, Show.Cues.Current);
        Run(3.5);
        Assert.Equal((0, 0), (Level(0), Level(1)));
        Assert.DoesNotContain(Tab.List.Items, c => Tab.List.Columns[0].Text(c) == "►"); // none marked live
        Assert.Contains(All(Tab).OfType<Label>(), l => l.Text.StartsWith("Next: cue 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Choosing_a_cue_shows_it_for_editing()
    {
        RecordTwoCues();
        ClickRow(0);
        Assert.Equal(0, Tab.Selected);
        Assert.Equal("Cue 1", Find<TextBox>(t => t.Text == "Cue 1").Text);

        // Typing a label.
        var label = Find<TextBox>(t => t.Text == "Cue 1");
        Click(label);
        foreach (var _ in "Cue 1")
            Ui.KeyDown(Cuedeck.Input.Key.Backspace, Cuedeck.Input.Modifiers.None);
        Type("Warm wash");
        Assert.Equal("Warm wash", Show.Cues.Cues[0].Label);

        // Its times, by the + buttons.
        var fadeUp = Field("Fade up (s)");
        Click(All(fadeUp).Single(e => e.Tooltip == "Step up"));
        Assert.Equal(3.1, Show.Cues.Cues[0].FadeUp, 6);
        var delay = Field("Delay (s)");
        Click(All(delay).Single(e => e.Tooltip == "Step up"));
        Assert.Equal(0.1, Show.Cues.Cues[0].Delay, 6);

        // Follow, and the wait that comes with it.
        Assert.DoesNotContain(All(Tab).OfType<NumberBox>(), n => n.Suffix == " s");
        Click(Find<Checkbox>(c => c.Text == "then GO next after"));
        Assert.True(Show.Cues.Cues[0].Follow);
        Assert.Contains(All(Tab).OfType<NumberBox>(), n => n.Suffix == " s");

        // Choosing the other cue shows its settings.
        ClickRow(1);
        Assert.Equal(3.0, Field("Fade up (s)").Value);
        Assert.False(Find<Checkbox>(c => c.Text == "then GO next after").Checked);
    }

    [Fact]
    public void A_cue_made_a_block_plays_the_same()
    {
        RecordTwoCues();
        ClickRow(1);
        var before = Show.Cues.StandingLook(2)!.Buffers(1)[0];
        var tracks = Find<Checkbox>(c => c.Text == "tracks from previous cue");
        Assert.True(tracks.Checked);
        Click(tracks);
        Assert.False(Show.Cues.Cues[1].Tracking);
        Assert.Equal(new Dictionary<int, int> { [0] = 255, [1] = 100 }, Show.Cues.Cues[1].Levels.Universes[0]); // the whole look
        Assert.Equal(before, Show.Cues.StandingLook(2)!.Buffers(1)[0]);
    }

    [Fact]
    public void Load_to_patch_duplicate_delete_and_double_click()
    {
        RecordTwoCues();
        ClickRow(1);
        Click(ButtonCalled("Load to Patch"));
        Assert.Equal((255, 100), (Level(0), Level(1)));

        Click(ButtonCalled("Duplicate"));
        Assert.Equal(3, Show.Cues.Cues.Count);
        Assert.Equal("Cue 2 copy", Show.Cues.Cues[2].Label);
        Assert.Equal(2, Tab.Selected);

        Click(ButtonCalled("Delete"));
        Assert.Equal(2, Show.Cues.Cues.Count);

        Show.Dmx.Universes[0].SetAll(0);
        ClickRow(1, times: 2);
        Assert.Equal(1, Show.Cues.Current); // fired
    }

    [Fact]
    public void Recording_blocks_when_tracking_is_off()
    {
        Click(Find<Checkbox>(c => c.Text == "Tracking"));
        Assert.False(Show.Cues.TrackingEnabled);
        Show.Dmx.Universes[0].SetChannel(3, 7);
        Click(ButtonCalled("Record Cue"));
        Assert.False(Show.Cues.Cues[0].Tracking);
    }
}
