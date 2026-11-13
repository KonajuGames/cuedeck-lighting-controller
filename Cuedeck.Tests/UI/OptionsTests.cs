using Cuedeck.Input;
using Cuedeck.Triggers;
using Cuedeck.UI;
using Cuedeck.UI.Console;

namespace Cuedeck.Tests.UI;

/// <summary>
/// The Options dialog and its settings file, and the Triggers dialog's feedback settings, clicked through.
/// </summary>
public sealed class OptionsTests : ConsoleHarness
{
    OptionsDialog Options => Ui.Elements.OfType<OptionsDialog>().Single();

    T Field<T>(string name)
        where T : Element =>
        Find<Row>(r => r.Children.Count > 1 && r.Children[0] is FixedWidth { Inner: Label l } && l.Text == name).Children.OfType<T>().First();

    [Fact]
    public void Settings_default_to_virtualdj_and_read_sLights_file()
    {
        var settings = new AppSettings(Dir.File("none.json"));
        Assert.Equal((DjSoftware.VirtualDj, "VirtualDJ"), (settings.DjSoftware, settings.DjSoftwareName));
        Assert.False(File.Exists(Dir.File("none.json"))); // nothing written just for reading

        File.WriteAllText(Dir.File("slight.json"), """{ "dj_software": "cuepoint", "something_else": 3 }""");
        var theirs = new AppSettings(Dir.File("slight.json"));
        Assert.Equal(DjSoftware.Cuepoint, theirs.DjSoftware);
        theirs.DjSoftware = DjSoftware.VirtualDj;
        var text = File.ReadAllText(Dir.File("slight.json"));
        Assert.Contains("\"dj_software\": \"virtualdj\"", text);
        Assert.Contains("\"something_else\": 3", text); // what it doesn't know is kept

        File.WriteAllText(Dir.File("bad.json"), "not json");
        Assert.Equal(DjSoftware.VirtualDj, new AppSettings(Dir.File("bad.json")).DjSoftware);
        File.WriteAllText(Dir.File("odd.json"), """{ "dj_software": "serato" }""");
        Assert.Equal(DjSoftware.VirtualDj, new AppSettings(Dir.File("odd.json")).DjSoftware);
    }

    [Fact]
    public void The_cog_opens_the_options_and_a_choice_is_kept()
    {
        Click(Find<IconButton>(b => b.Tooltip == "Options"));
        Assert.True(Options.Visible);
        Assert.Contains(All(Options).OfType<Label>(), l => l.Text.Contains("VirtualDJ's databases", StringComparison.Ordinal));

        Choose(Find<Dropdown<DjSoftware>>(_ => true), "Cuepoint");
        Assert.Equal("Music library: Cuepoint.", Screen.StatusText);
        Assert.Equal(DjSoftware.Cuepoint, new AppSettings(Dir.File("settings.json")).DjSoftware);
        Assert.Contains(All(Options).OfType<Label>(), l => l.Text.Contains("Cuepoint's library files", StringComparison.Ordinal));

        Frame(); // focus back on the dialog once the menu's gone
        Ui.KeyDown(Key.Escape, Modifiers.None);
        Frame();
        Assert.False(Options.Visible);
    }

    [Fact]
    public void Feedback_is_set_up_in_the_triggers_dialog()
    {
        var sent = new List<(string Host, int Port, byte[] Bytes)>();
        Show.Triggers.SendUdp = (host, port, bytes) => sent.Add((host, port, bytes));
        Click(ButtonCalled("Triggers…"));

        // Where OSC feedback goes, then feedback on.
        var host = Find<TextBox>(b => b.Placeholder == "127.0.0.1");
        Click(host);
        host.SelectAll();
        Type("10.0.0.9");
        Click(Find<Checkbox>(c => c.Text == "Feedback (LEDs)"));
        Assert.True(Show.Triggers.FeedbackEnabled);
        Assert.Equal("10.0.0.9", Show.Triggers.OscOutHost);
        Assert.Equal("(none — UDP bridge :9010)", Find<Dropdown<string>>(d => d.Text("").StartsWith("(none", StringComparison.Ordinal)).Text(""));

        // A pad that follows Sending.
        Click(ButtonCalled("Add"));
        var t = Show.Triggers.Triggers.Single();
        t.Action = TriggerAction.None;
        Click(Find<Checkbox>(c => c.Text == "light this pad"));
        Assert.True(t.FeedbackEnabled);
        Choose(Field<Row>("  follows").Children.OfType<Dropdown<FeedbackWatch>>().Single(), "Sending is on");
        Assert.Equal(FeedbackWatch.Sending, t.FeedbackWatch);

        Show.Tick(1.0 / 30, false);
        Assert.Equal([0x90, 60, 127], sent[^1].Bytes); // Sending's on: lit
        Click(ButtonCalled("Close"));
        Click(ButtonCalled("Sending"));
        Show.Tick(1.0 / 30, false);
        Assert.Equal([0x90, 60, 0], sent[^1].Bytes);

        // Off again: the pad's put out.
        sent.Clear();
        Click(ButtonCalled("Triggers…"));
        Click(Find<Checkbox>(c => c.Text == "Feedback (LEDs)"));
        Assert.False(Show.Triggers.FeedbackEnabled);
        Assert.Equal([0x90, 60, 0], Assert.Single(sent).Bytes);
    }
}
