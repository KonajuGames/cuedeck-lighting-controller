using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Fixtures;
using Cuedeck.Show;

namespace Cuedeck.Tests.Golden;

/// <summary>
/// A show and a script of things to do to it, played frame by frame both here and in sLight
/// (tools/slight_frames.gd), so the two apps' DMX output can be compared byte for byte.
/// <para>
/// The show is built here and saved as scenario.slight, which sLight then loads too. To make it again after changing
/// <see cref="Build"/>, run the tests with CUEDECK_WRITE_GOLDEN=1, then record sLight's frames again (see
/// tools/slight_frames.gd).
/// </para>
/// </summary>
static class GoldenScenario
{
    public const double FrameSeconds = 0.025; // 40 steps a second: any steady step does, as both apps are driven by it
    public const int Frames = 320;

    /// <summary>
    /// How many channels of each universe are recorded: the scenario's fixtures are all within them.
    /// </summary>
    public const int Channels = 64;

    public const double GrandMaster = 0.8;

    public static string Folder => Path.Combine(AppContext.BaseDirectory, "Golden");

    /// <summary>
    /// The scenario's source folder, for writing the show file afresh.
    /// </summary>
    static string SourceFolder([System.Runtime.CompilerServices.CallerFilePath] string here = "") => Path.GetDirectoryName(here)!;

    /// <summary>
    /// What happens at which frame, by name: the harness in sLight has the same list.
    /// </summary>
    public static readonly (int Frame, string Action)[] Script =
    [
        (0, "go"),
        (40, "effect Wave"),
        (50, "effect Pick"),
        (55, "effect Sweep"),
        (60, "chase Bounce"),
        (70, "chase Pairs"),
        (100, "go"),
        (150, "fader 1 0.3"),
        (180, "fader 0 0.9"),
        (220, "flash white"),
        (240, "flash release white"),
        (260, "back"),
        (290, "fader 1 0.0"),
        (300, "blackout"),
    ];

    /// <summary>
    /// Does <paramref name="action"/> to <paramref name="show"/>.
    /// </summary>
    public static void Do(LightingShow show, string action)
    {
        var words = action.Split(' ');
        switch (words[0])
        {
            case "go":
                show.Cues.Go();
                break;
            case "back":
                show.Cues.GoBack();
                break;
            case "effect":
                show.SetEffectRunning(words[1], true);
                break;
            case "chase":
                var chase = show.Effects.FindChase(words[1])!;
                chase.Reset();
                chase.Running = true;
                break;
            case "fader":
                show.Faders.SetLevel(int.Parse(words[1]), double.Parse(words[2], System.Globalization.CultureInfo.InvariantCulture));
                break;
            case "flash" when words[1] == "release":
                show.Flash.Release(FlashKind.White);
                break;
            case "flash":
                show.Flash.Press(FlashKind.White);
                break;
            case "blackout":
                show.BlackoutAll();
                break;
            default:
                throw new ArgumentException(action);
        }
    }

    static JsonObject Values(params (int Channel, int Value)[] values)
    {
        var o = new JsonObject();
        foreach (var (c, v) in values)
            o[c.ToString()] = v;
        return o;
    }

    /// <summary>
    /// The scenario's show: two universes of pars (without dimmers, so with virtual dimmers), a 16-bit spot, a dimmer,
    /// a two-head bar and a moving head; block and tracking cues with a delay, a follow and a palette; two chases; three
    /// effects (one capped as a motor, one picking up the live level, one on a group); a cue submaster and a group
    /// master.
    /// </summary>
    public static LightingShow Build()
    {
        var show = new LightingShow();
        show.Patch.AddUniverse();
        var builtIn = FixtureProfile.BuiltIn();
        FixtureProfile Profile(string id) => builtIn.First(p => p.Id == id).Clone();
        PatchedFixture Add(int universe, FixtureProfile profile, int start, string uid, string name, int mode = 0)
        {
            var f = show.Patch.Add(universe, profile, start, mode, name);
            f.Uid = uid;
            return f;
        }

        var a1 = Add(0, Profile("rgbw4"), 0, "a1a1a1a1a1a1a1a1", "Par 1");
        var a2 = Add(0, Profile("rgbw4"), 4, "a2a2a2a2a2a2a2a2", "Par 2");
        Add(0, Profile("mh_spot"), 10, "s1s1s1s1s1s1s1s1", "Spot", mode: 1);
        Add(0, Profile("dimmer1"), 30, "d1d1d1d1d1d1d1d1", "Dimmer");
        var bar = Profiles.Simple("bar2", "2-head bar", ("Red", "RED"), ("Green", "GREEN"), ("Blue", "BLUE"), ("Red", "RED"), ("Green", "GREEN"), ("Blue", "BLUE"));
        bar.Manufacturer = "Test";
        Add(1, bar, 0, "b1b1b1b1b1b1b1b1", "Bar");
        Add(1, Profile("mh_rgbw_pt7"), 10, "m1m1m1m1m1m1m1m1", "Mover");

        // Live levels: the spot pointed and dimmed, the dimmer up, par 2's virtual dimmer at half.
        var u0 = show.Dmx.Universes[0];
        u0.SetChannel(10, 100);
        u0.SetChannel(11, 30);
        u0.SetChannel(15, 120);
        u0.SetChannel(30, 180);
        show.Patch.SetVirtualDimmer(a2, 0.5);

        show.Groups.Add(new FixtureGroup { Name = "Pars", Members = { a1.Uid, a2.Uid } });

        // A teal palette for par 1.
        show.Palettes.FromJson(
            new JsonObject
            {
                ["palettes"] = new JsonArray(
                    new JsonObject { ["id"] = "p1", ["name"] = "Teal", ["kind"] = "colour", ["values"] = new JsonObject { [a1.Uid] = Values((0, 0), (1, 200), (2, 180), (3, 40)) } }
                ),
                ["next_id"] = 2,
            }
        );

        var cue1 = new Cue("Open", 1.0, 0.5);
        cue1.SetLevels([new Dictionary<int, int> { [0] = 255, [6] = 255, [15] = 255, [30] = 200 }, new Dictionary<int, int> { [12] = 255, [13] = 255 }]);
        var cue2 = new Cue("Green", 0.8, 0.8) { Tracking = true, Delay = 0.5, Follow = true, FollowWait = 0.3 };
        cue2.Levels = new Look([new() { [0] = 0, [1] = 255, [30] = 0 }, new() { [10] = 200, [11] = 90 }]);
        var cue3 = new Cue("Teal", 0.4, 1.2) { Tracking = true };
        cue3.Levels = new Look([new(), new() { [0] = 255, [2] = 128, [3] = 60, [5] = 255 }]);
        cue3.Refs[a1.Uid] = new Dictionary<string, string> { ["colour"] = "p1" };
        show.Cues.Cues.AddRange([cue1, cue2, cue3]);

        var bounce = new Chase { Name = "Bounce", Bpm = 150, Crossfade = 0.5, Direction = ChaseDirection.Bounce };
        bounce.Steps.Add(new Look([new(), new() { [0] = 255, [3] = 40 }]));
        bounce.Steps.Add(new Look([new(), new() { [1] = 255, [4] = 90 }]));
        bounce.Steps.Add(new Look([new(), new() { [2] = 255, [5] = 200 }]));
        var pairs = new Chase { Name = "Pairs", Bpm = 100 };
        pairs.Steps.Add(new Look([new() { [2] = 255, [7] = 255 }]));
        pairs.Steps.Add(new Look([new() { [3] = 255, [6] = 100 }]));
        show.Effects.Chases.AddRange([bounce, pairs]);

        show.Effects.Effects.Add(new WaveEffect { Name = "Wave", Role = "DIMMER", Bpm = 90, Size = 200, Center = 128, FanDeg = 90 });
        show.Effects.Effects.Add(new WaveEffect { Name = "Pick", Role = "RED", Group = "Pars", Waveform = Waveform.Triangle, BaseMode = WaveBase.Pickup, Bpm = 45, Size = 60 });
        show.Effects.Effects.Add(new WaveEffect { Name = "Sweep", Role = "PAN", Universe = 1, Waveform = Waveform.Square, Bpm = 200, Size = 80, Center = 120 });

        show.Faders.FromJson(
            new JsonObject
            {
                ["faders"] = new JsonArray(
                    new JsonObject { ["name"] = "Teal sub", ["kind"] = "cue", ["target"] = "3", ["level"] = 0.5 },
                    new JsonObject { ["name"] = "Pars master", ["kind"] = "group", ["target"] = "Pars", ["level"] = 0.6 }
                ),
            }
        );
        // No starter reactors: the scenario runs in Cue Mode, where they don't play anyway.
        show.Effects.Reactors.Clear();
        show.Dmx.SacnCid = new byte[16];
        return show;
    }

    /// <summary>
    /// Writes scenario.slight into the source folder, when CUEDECK_WRITE_GOLDEN is set.
    /// </summary>
    public static void WriteShowIfAsked()
    {
        if (Environment.GetEnvironmentVariable("CUEDECK_WRITE_GOLDEN") != "1")
            return;
        using var show = Build();
        File.WriteAllText(Path.Combine(SourceFolder(), "scenario.slight"), Json.StringifyIndented(ShowFile.ToJson(show)));
    }
}
