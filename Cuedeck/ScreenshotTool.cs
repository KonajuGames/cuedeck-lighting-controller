using System.Net.Sockets;
using System.Numerics;
using System.Text.Json.Nodes;
using Brutal.Numerics;
using Cuedeck.Common;
using Cuedeck.Dj;
using Cuedeck.Fixtures;
using Cuedeck.Music;
using Cuedeck.Perform;
using Cuedeck.Rendering;
using Cuedeck.Show;
using Cuedeck.Timelines;
using Cuedeck.Triggers;
using Cuedeck.UI;
using Cuedeck.UI.Console;
using Cuedeck.UI.DjEdit;

namespace Cuedeck;

// The show's Cue, not the framework's Cue namespace (a name in the global namespace beats a using's).
using Cue = Cuedeck.Show.Cue;

/// <summary>
/// Retakes the user guide's and the README's screenshots from the running app (sLight's
/// <c>tools/capture_screenshots.gd</c>): a demo rig is patched and programmed, each scene set up through the screen as
/// someone would, and the frame the GPU drew saved as a PNG named as <c>docs/img</c>'s. Run as
/// <c>Cuedeck --capture &lt;folder&gt; [name ...]</c>; names pick which to take (all of them when there are none).
/// Nothing of the user's is read or touched: the app's folder is a scratch one for the run (<see cref="UseScratch"/>),
/// and only the window's own picture is saved, never the screen's. <c>Cuedeck --tour</c> plays through the main
/// features as a video instead (see ScreenshotTool.Tour.cs).
/// </summary>
sealed partial class ScreenshotTool
{
    /// <summary>
    /// The window's size for every shot but the README's.
    /// </summary>
    public static readonly int2 Size = new(1600, 900);

    public static readonly string[] Shots =
    [
        "01-patch-tab",
        "02-fixture-library",
        "03-cues-tab",
        "04-chases-tab",
        "05-effects-tab",
        "06-sound-tab",
        "07-palettes-tab",
        "08-groups-tab",
        "09-visualizer",
        "10-triggers-dialog",
        "11-dj-edit-library",
        "12-dj-edit-timeline",
        "13-position-editor",
        "14-dj-perform",
        "15-mute-solo-collapse",
        "screenshot",
    ];

    /// <summary>
    /// What the tool drives: the app as <c>Program</c> built it.
    /// </summary>
    public sealed record App(
        Brutal.GlfwApi.GlfwWindow Window,
        Renderer Renderer,
        Root Ui,
        MainScreen Screen,
        LightingShow Show,
        DjPerformer Performer,
        DjEditView DjEdit,
        ShowSession Session,
        Action<Brutal.GlfwApi.GlfwKey, Brutal.GlfwApi.GlfwKeyAction, Brutal.GlfwApi.GlfwModifier> Key
    );

    /// <summary>
    /// What the command line asks for: the folder to write to, and which shots, or the tour (made with the ffmpeg at
    /// <c>Ffmpeg</c>).
    /// </summary>
    public sealed record Request(string Folder, string[] Names, bool Tour = false, string Ffmpeg = "ffmpeg");

    abstract record Step;

    /// <summary>
    /// A pause of some frames.
    /// </summary>
    sealed record Wait(int Frames) : Step;

    /// <summary>
    /// A pause until <c>Ready</c>, for no more than <c>MaxFrames</c> frames.
    /// </summary>
    sealed record Until(Func<bool> Ready, int MaxFrames, string What) : Step;

    /// <summary>
    /// A shot (its top <c>Height</c> pixels only, when set).
    /// </summary>
    sealed record Shot(string Name, int Height = 0) : Step;

    /// <summary>
    /// Something done at once, before the next step.
    /// </summary>
    sealed record Do(Action Act) : Step;

    /// <summary>
    /// A wait of so many seconds, by the clock rather than frames.
    /// </summary>
    sealed record Hold(double Seconds) : Step;

    /// <summary>
    /// Something done a little each frame over so many seconds, told how far through it is, from 0 to 1.
    /// </summary>
    sealed record Over(double Seconds, Action<double> Progress) : Step;

    readonly App _app;
    readonly string _out;
    readonly IEnumerator<Step> _script;
    int _waiting;
    Until? _until;
    Over? _over;
    double _overStarted;
    double _holdUntil;
    Shot? _taking;
    readonly bool _tour;
    readonly string _ffmpeg;
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    readonly List<string> _uids = [];

    /// <summary>
    /// What the command line asks for; null without <c>--capture</c> or <c>--tour</c>. <c>--capture [folder]
    /// [name ...]</c> takes the screenshots; <c>--tour [folder] [--ffmpeg path]</c> records the tour.
    /// </summary>
    public static Request? Parse(string[] args)
    {
        var tourAt = Array.IndexOf(args, "--tour");
        var at = tourAt >= 0 ? tourAt : Array.IndexOf(args, "--capture");
        if (at < 0)
            return null;
        var ffmpegAt = Array.IndexOf(args, "--ffmpeg");
        var ffmpeg = ffmpegAt >= 0 && ffmpegAt + 1 < args.Length ? args[ffmpegAt + 1] : "ffmpeg";
        var rest = args.Skip(at + 1).TakeWhile(arg => !arg.StartsWith("--", StringComparison.Ordinal)).ToArray();
        var folder = rest.Length > 0 ? Path.GetFullPath(rest[0]) : Path.Combine(Environment.CurrentDirectory, tourAt >= 0 ? "tour" : "screenshots");
        return new Request(folder, rest.Length > 1 ? rest[1..] : Shots, tourAt >= 0, ffmpeg);
    }

    /// <summary>
    /// Points the app's folder at an empty scratch one, so the run starts from nothing and leaves the user's show,
    /// settings, profiles and folders alone. Call before anything reads <see cref="AppPaths.Folder"/>.
    /// </summary>
    public static void UseScratch()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "cuedeck-capture");
        try
        {
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
        }
        catch (IOException) { }
        Directory.CreateDirectory(scratch);
        AppPaths.UseFolder(scratch);
    }

    public ScreenshotTool(App app, Request request)
    {
        _app = app;
        _out = request.Folder;
        _tour = request.Tour;
        _ffmpeg = request.Ffmpeg;
        Directory.CreateDirectory(_out);
        _script = (_tour ? Tour() : Run(request.Names)).GetEnumerator();
    }

    /// <summary>
    /// Whether every shot's been taken.
    /// </summary>
    public bool Done { get; private set; }

    /// <summary>
    /// Each frame, after it's drawn: the script moved on, or the shot it asked for saved.
    /// </summary>
    public void Frame()
    {
        if (Done)
            return;
        if (_tour)
            TourFrame();
        if (_taking is { } shot)
        {
            if (_app.Renderer.TakeWindowCapture() is not { } picture)
            {
                _app.Renderer.CaptureWindow = true;
                return;
            }
            Save(shot, picture);
            _taking = null;
        }
        if (_until is { } until)
        {
            if (!until.Ready() && _waiting-- > 0)
                return;
            if (!until.Ready())
                Console.Error.WriteLine($"gave up waiting for {until.What}");
            (_until, _waiting) = (null, 0);
        }
        if (_over is { } over)
        {
            var through = Math.Min(1, (_clock.Elapsed.TotalSeconds - _overStarted) / Math.Max(over.Seconds, 1e-6));
            over.Progress(through);
            if (through < 1)
                return;
            _over = null;
        }
        if (_clock.Elapsed.TotalSeconds < _holdUntil)
            return;
        if (_waiting > 0)
        {
            _waiting--;
            return;
        }
        // Steps done at once run on to the next, so a run of them all happens in the one frame.
        while (true)
        {
            if (!_script.MoveNext())
            {
                if (_tour)
                    FinishTour();
                Done = true;
                return;
            }
            switch (_script.Current)
            {
                case Wait w:
                    _waiting = w.Frames;
                    return;
                case Until next:
                    (_until, _waiting) = (next, next.MaxFrames);
                    return;
                case Shot take:
                    _taking = take;
                    _app.Renderer.CaptureWindow = true;
                    return;
                case Hold hold:
                    _holdUntil = _clock.Elapsed.TotalSeconds + hold.Seconds;
                    return;
                case Over next:
                    (_over, _overStarted) = (next, _clock.Elapsed.TotalSeconds);
                    next.Progress(0);
                    return;
                case Do act:
                    act.Act();
                    break;
            }
        }
    }

    void Save(Shot shot, (int Width, int Height, byte[] Rgba) picture)
    {
        var (w, h, rgba) = picture;
        if (shot.Height > 0 && shot.Height < h)
        {
            rgba = rgba[..(w * shot.Height * 4)];
            h = shot.Height;
        }
        var path = Path.Combine(_out, shot.Name + ".png");
        File.WriteAllBytes(path, Png.Encode(new Png.Image(w, h, rgba)));
        Console.WriteLine($"saved {path} ({w}x{h})");
    }

    // ------------------------------------------------------------------------------------------------- the script

    IEnumerable<Step> Run(string[] names)
    {
        yield return new Wait(5);
        PatchRig();
        ProgramShow();
        yield return new Wait(5);
        foreach (var name in names)
        {
            var steps = name switch
            {
                "01-patch-tab" => PatchTabShot(),
                "02-fixture-library" => LibraryShot(),
                "03-cues-tab" => CuesShot(),
                "04-chases-tab" => ChasesShot(),
                "05-effects-tab" => EffectsShot(),
                "06-sound-tab" => SoundShot(),
                "07-palettes-tab" => PalettesShot(),
                "08-groups-tab" => GroupsShot(),
                "09-visualizer" => VisualizerShot(),
                "10-triggers-dialog" => TriggersShot(),
                "11-dj-edit-library" => DjLibraryShot(),
                "12-dj-edit-timeline" => DjTimelineShot(),
                "13-position-editor" => PositionEditorShot(),
                "14-dj-perform" => DjPerformShot(),
                "15-mute-solo-collapse" => MuteSoloShot(),
                "screenshot" => ReadmeShot(),
                _ => Unknown(name),
            };
            foreach (var step in steps)
                yield return step;
        }
    }

    static IEnumerable<Step> Unknown(string name)
    {
        Console.WriteLine("no such screenshot: " + name);
        yield break;
    }

    LightingShow Show => _app.Show;

    /// <summary>
    /// Two moving heads, two washes and a spot, hung on a bar over the stage.
    /// </summary>
    void PatchRig()
    {
        var builtIn = FixtureProfile.BuiltIn();
        FixtureProfile Profile(string id) => builtIn.First(p => p.Id == id).Clone();
        (string Id, int Start, string Name, float X)[] rig =
        [
            ("mh_rgbw_pt7", 0, "Mover L", -1.5f),
            ("mh_rgbw_pt7", 10, "Mover R", 1.5f),
            ("rgbw4", 20, "Wash L", -4f),
            ("rgbw4", 30, "Wash R", 4f),
            ("mh_spot", 40, "Spot", 0f),
        ];
        foreach (var (id, start, name, x) in rig)
            _uids.Add(Show.Patch.Add(0, Profile(id), start, 0, name, new Vector3(x, 4, -3), new Vector3(90, 0, 0)).Uid);
        Look(Warm);
    }

    /// <summary>
    /// Sets the live levels, as the patch's controls would.
    /// </summary>
    void Look(Dictionary<int, int> levels)
    {
        var u = Show.Dmx.Universes[0];
        for (var ch = 0; ch < 512; ch++)
            u.SetChannel(ch, levels.GetValueOrDefault(ch));
    }

    /// <summary>
    /// Movers amber and blue aimed in, washes warm, the spot open.
    /// </summary>
    static readonly Dictionary<int, int> Warm = new()
    {
        [0] = 100, [1] = 150, [2] = 255, [3] = 255, [4] = 130, [5] = 0, [6] = 30,
        [10] = 156, [11] = 150, [12] = 255, [13] = 30, [14] = 90, [15] = 255, [16] = 30,
        [20] = 255, [21] = 110, [22] = 20, [23] = 60, [30] = 255, [31] = 110, [32] = 20, [33] = 60,
        [40] = 128, [41] = 110, [42] = 255, [43] = 12, [44] = 0,
    };

    static readonly Dictionary<int, int> Cool = new()
    {
        [0] = 80, [1] = 120, [2] = 255, [3] = 0, [4] = 120, [5] = 255, [6] = 0,
        [10] = 176, [11] = 120, [12] = 255, [13] = 0, [14] = 200, [15] = 255, [16] = 0,
        [20] = 0, [21] = 60, [22] = 255, [23] = 0, [30] = 0, [31] = 60, [32] = 255, [33] = 0,
        [40] = 128, [41] = 90, [42] = 200, [43] = 12, [44] = 40,
    };

    /// <summary>
    /// Cues, a chase, an effect, a group, palettes, faders and a couple of triggers.
    /// </summary>
    void ProgramShow()
    {
        foreach (var (label, up, down, look) in new[] { ("Warm Wash", 3.0, 2.0, Warm), ("Cool Chase Standby", 2.0, 2.0, Cool), ("Blackout", 1.5, 1.5, new Dictionary<int, int>()) })
        {
            var cue = new Cue(label, up, down);
            cue.SetLevels([look]);
            Show.Cues.Cues.Add(cue);
        }

        var wipe = new Chase { Name = "Colour Wipe", Bpm = 120, Crossfade = 0.3 };
        foreach (var (r, g, b) in new[] { (255, 0, 0), (0, 255, 0), (0, 0, 255) })
            wipe.Steps.Add(new Look([new() { [20] = r, [21] = g, [22] = b, [30] = r, [31] = g, [32] = b }]));
        Show.Effects.Chases.Add(wipe);

        Show.Groups.Add(new FixtureGroup { Name = "Movers", Members = { _uids[0], _uids[1] } });
        Show.Groups.Add(new FixtureGroup { Name = "Washes", Members = { _uids[2], _uids[3] } });
        Show.Effects.Effects.Add(new WaveEffect { Name = "Pan Sweep", Role = "PAN", Group = "Movers", Bpm = 20, Size = 120, Center = 128, FanDeg = 90 });

        Show.Triggers.Triggers.Add(new Trigger { Name = "Pad 1 — GO", MidiKind = TriggerMidiKind.Note, MidiNumber = 36, Action = TriggerAction.CueGo });
        Show.Triggers.Triggers.Add(new Trigger { Name = "Fader 8 — Strobe", MidiKind = TriggerMidiKind.PitchBend, MidiChannel = 7, Action = TriggerAction.FlashStrobe });

        Show.Faders.FromJson(
            new JsonObject
            {
                ["faders"] = new JsonArray(
                    new JsonObject { ["name"] = "Warm", ["kind"] = "cue", ["target"] = "1", ["level"] = 0.8, ["midi_cc"] = 7, ["midi_channel"] = 0 },
                    new JsonObject { ["name"] = "Movers", ["kind"] = "group", ["target"] = "Movers", ["level"] = 1.0 },
                    new JsonObject { ["name"] = "Sweep", ["kind"] = "effect", ["target"] = "Pan Sweep", ["level"] = 0.0, ["midi_bend"] = true, ["midi_channel"] = 1 }
                ),
            }
        );
        Show.Visualizer.Haze = 0.5;
        _app.Screen.Log("");
    }

    // --------------------------------------------------------------------------------------------- driving the UI

    T? Find<T>(Func<T, bool> match)
        where T : Element => _app.Ui.Elements.Where(e => e.Visible).SelectMany(All).OfType<T>().FirstOrDefault(match);

    static IEnumerable<Element> All(Element e)
    {
        yield return e;
        if (e is Panel p)
            foreach (var child in p.Children.Where(c => c.Visible))
                foreach (var d in All(child))
                    yield return d;
    }

    /// <summary>
    /// Clicks <paramref name="e"/> in the middle, then takes the pointer away so nothing shows hovered.
    /// </summary>
    void Click(Element? e)
    {
        if (e is null)
            return;
        _app.Ui.MouseMove(e.ScreenPosition + e.Size / 2);
        _app.Ui.MouseButton(MouseButton.Left, down: true);
        _app.Ui.MouseButton(MouseButton.Left, down: false);
        Away();
    }

    void Away() => _app.Ui.MouseMove(new float2(Size.X - 2, Size.Y - 2));

    void Tab(string name) => Click(Find<Button>(b => b.Text == name));

    void Type(string text)
    {
        foreach (var rune in text.EnumerateRunes())
            _app.Ui.TextInput(rune);
    }

    /// <summary>
    /// Opens the patch's fixtures by their places in it, closing the rest.
    /// </summary>
    void Expand(params int[] which)
    {
        var rows = _app.Ui.Elements.SelectMany(All).OfType<FixtureRow>().ToList();
        for (var i = 0; i < rows.Count; i++)
            rows[i].Expanded = which.Contains(i);
    }

    void SelectFirst<T>(ListView<T>? list)
        where T : class
    {
        if (list is not null)
            list.SelectedIndex = 0;
    }

    Step Settle(int frames = 5)
    {
        _app.Screen.Log("");
        Away();
        _app.Ui.Focus(null);
        return new Wait(frames);
    }

    void ToConsole()
    {
        _app.Screen.SetView(AppView.Console);
        _app.Screen.Console.Visualizer.UiVisible = true;
    }

    // -------------------------------------------------------------------------------------------------- the shots

    IEnumerable<Step> PatchTabShot()
    {
        ToConsole();
        Tab("Cues");
        Tab("Patch");
        Look(Warm);
        yield return new Wait(2);
        Expand(0, 1);
        yield return Settle();
        yield return new Shot("01-patch-tab");
        Expand();
    }

    IEnumerable<Step> LibraryShot()
    {
        ToConsole();
        Tab("Patch");
        yield return new Wait(2);
        Click(Find<Button>(b => b.Text == "Library…"));
        yield return new Wait(5);
        Click(Find<TextBox>(t => t.Placeholder.StartsWith("Search")));
        Type("moving head");
        yield return new Wait(10);
        if (_app.Ui.Elements.OfType<LibraryDialog>().FirstOrDefault() is { } dialog && dialog.Results.Items.Count > 2)
            dialog.Results.SelectedIndex = 2;
        Away();
        yield return new Wait(10);
        yield return new Shot("02-fixture-library");
        _app.Ui.Elements.OfType<LibraryDialog>().FirstOrDefault()?.Close();
    }

    IEnumerable<Step> CuesShot()
    {
        ToConsole();
        Tab("Cues");
        Tab("Patch");
        yield return new Wait(2);
        SelectFirst(_app.Screen.Console.Cues.List);
        yield return Settle();
        yield return new Shot("03-cues-tab");
    }

    IEnumerable<Step> ChasesShot()
    {
        ToConsole();
        Tab("Chases");
        Tab("3D Visualizer");
        Show.Effects.Chases[0].Running = true;
        yield return new Wait(2);
        SelectFirst(_app.Screen.Console.Chases.List);
        yield return Settle(30);
        yield return new Shot("04-chases-tab");
        Show.Effects.Chases[0].Running = false;
    }

    IEnumerable<Step> EffectsShot()
    {
        ToConsole();
        Tab("Effects");
        Tab("3D Visualizer");
        Show.Effects.Effects[0].Running = true;
        yield return new Wait(2);
        SelectFirst(_app.Screen.Console.Effects.List);
        yield return Settle(40);
        yield return new Shot("05-effects-tab");
        Show.Effects.Effects[0].Running = false;
    }

    IEnumerable<Step> SoundShot()
    {
        ToConsole();
        Tab("Sound");
        Tab("Patch");
        yield return new Wait(2);
        SelectFirst(_app.Screen.Console.Sound.List);
        yield return Settle();
        yield return new Shot("06-sound-tab");
    }

    IEnumerable<Step> PalettesShot()
    {
        ToConsole();
        Tab("Cues");
        Tab("Palettes");
        Look(Warm);
        yield return new Wait(2);
        foreach (var record in new[] { "Record Colour", "Record Position", "Record Beam" })
            Click(Find<Button>(b => b.Text == record));
        yield return new Wait(2);
        Look(Cool);
        Click(Find<Button>(b => b.Text == "Record Colour"));
        Look(Warm);
        yield return Settle();
        yield return new Shot("07-palettes-tab");
    }

    IEnumerable<Step> GroupsShot()
    {
        ToConsole();
        Tab("Groups");
        Tab("Faders");
        yield return new Wait(2);
        SelectFirst(_app.Screen.Console.Groups.List);
        yield return Settle();
        yield return new Shot("08-groups-tab");
    }

    IEnumerable<Step> VisualizerShot()
    {
        ToConsole();
        Tab("Cues");
        Tab("3D Visualizer");
        Look(Warm);
        yield return Settle(20);
        yield return new Shot("09-visualizer");
    }

    IEnumerable<Step> TriggersShot()
    {
        ToConsole();
        Tab("Groups");
        Tab("3D Visualizer");
        _app.Screen.Dialogs.Triggers.Show(); // Edit > Triggers…
        yield return new Wait(5);
        if (_app.Ui.Elements.OfType<TriggersDialog>().FirstOrDefault() is { } dialog)
            SelectFirst(dialog.List);
        Away();
        yield return new Wait(5);
        yield return new Shot("10-triggers-dialog");
        _app.Ui.Elements.OfType<TriggersDialog>().FirstOrDefault()?.Close();
    }

    IEnumerable<Step> DjPerformShot()
    {
        // A show with timelines for one song, and VirtualDJ's decks told to the performer as it would send them.
        var dir = Path.Combine(AppPaths.Folder, "songs");
        Directory.CreateDirectory(dir);
        string[] songs = [Path.Combine(dir, "01 Wake Me Up.mp3"), Path.Combine(dir, "02 You Make Me.mp3")];
        for (var i = 0; i < songs.Length; i++)
            File.WriteAllText(songs[i], $"not really audio {i}");
        var store = new TimelineStore(Path.Combine(AppPaths.Folder, "show.timelines"));
        var tracks = new JsonObject
        {
            ["master"] = new JsonObject
            {
                ["dimmer"] = new JsonArray(new JsonArray(0.0, 0.2), new JsonArray(60.0, 0.9), new JsonArray(120.0, 0.4), new JsonArray(240.0, 1.0)),
                ["colour"] = new JsonArray(new JsonArray(0.0, 240.0, "ff8800", "0044ff")),
            },
        };
        foreach (var uid in _uids)
            tracks[uid] = new JsonObject { ["dimmer"] = new JsonArray(new JsonArray(0.0, 0.5), new JsonArray(240.0, 1.0)) };
        store.SaveSong(songs[0], new JsonObject { ["tracks"] = tracks });
        var perf = _app.Performer;
        var (storeWas, timelineWas, lookupWas) = (perf.StoreProvider, perf.Timeline, perf.SongLookup);
        perf.StoreProvider = () => store;
        perf.Timeline = null; // deck 2's song: no temporary show for the shot
        perf.SongLookup = null;
        _app.Screen.SetView(AppView.DjPerform);
        yield return new Wait(5);

        // A client on the OS2L port, as VirtualDJ would be.
        using var client = new TcpClient();
        if (perf.Os2l?.Port is int port and > 0)
        {
            try
            {
                client.Connect("127.0.0.1", port);
            }
            catch (SocketException) { }
        }
        for (var i = 0; i < 60 && perf.ClientCount == 0; i++)
            yield return new Wait(1);
        void Vdj(string trigger, JsonNode? value) =>
            perf.Os2l?.HandleMessage(new JsonObject { ["evt"] = "subscribed", ["trigger"] = trigger, ["value"] = value });
        Vdj("get_decks", 2);
        Vdj("deck 1 get_filepath", songs[0]);
        Vdj("deck 1 level", 1.0);
        Vdj("deck 1 play", "on");
        Vdj("deck 1 get_time elapsed absolute", 95000.0);
        Vdj("deck 2 get_filepath", songs[1]);
        Vdj("deck 2 level", 0.0);
        Vdj("deck 2 play", "off");
        Vdj("deck 2 get_time elapsed absolute", 31000.0);
        for (var i = 0; i < 300 && !perf.LoadsDone(); i++)
            yield return new Wait(1);
        yield return Settle(20);
        yield return new Shot("14-dj-perform", 405);
        client.Close();
        ToConsole();
        (perf.StoreProvider, perf.Timeline, perf.SongLookup) = (storeWas, timelineWas, lookupWas);
    }

    // ------------------------------------------------------------------------------------------------ DJ Edit Mode

    string _music = "";
    bool _songReady;

    /// <summary>
    /// The music folder: made-up songs in a folder of their own, the only one the browser shows, with their details as
    /// a DJ library would know them; the show saved beside it, so the songs' timelines have somewhere to go; and some
    /// named positions for the movers.
    /// </summary>
    void SetUpMusic()
    {
        if (_music != "")
            return;
        _music = Path.Combine(AppPaths.Folder, "Music");
        var songs = DemoSongs.Write(_music);
        var dj = _app.DjEdit;
        dj.Drives = () => [("Music", DemoSongs.Key(_music))];
        dj.Browser.ShownPath = path => path.Replace(DemoSongs.Key(_music), "Music");
        dj.Browser.LoadLibrary = () => new DjLibrary(path => songs.GetValueOrDefault(DemoSongs.Key(path)), [], "VirtualDJ");
        dj.ReloadLibrary();

        (string Name, float X, float Y)[] spots = [("Crowd", 0.5f, 0.15f), ("Stage Left", 0.3f, 0.55f), ("Stage Right", 0.7f, 0.55f), ("Ceiling", 0.5f, 0.95f), ("Mirror Ball", 0.5f, 0.75f)];
        foreach (var (name, x, y) in spots)
        {
            var p = Show.Positions.Add();
            Show.Positions.Rename(p.Id, name);
            for (var i = 0; i < _uids.Count; i++)
            {
                var (dx, dy) = i == 0 ? (-0.14f, 0.04f) : i == 1 ? (0.12f, -0.03f) : (0.02f, -0.12f);
                Show.Positions.SetPoint(p.Id, _uids[i], new Vector2(Math.Clamp(x + dx, 0, 1), Math.Clamp(y + dy, 0, 1)));
            }
        }
        _app.Session.Save(Path.Combine(AppPaths.Folder, "Demo Show.slight"));
    }

    /// <summary>
    /// The first song's timeline open, analysed, with a light show generated for it the first time.
    /// </summary>
    IEnumerable<Step> OpenSong()
    {
        SetUpMusic();
        var dj = _app.DjEdit;
        _app.Screen.SetView(AppView.DjEdit);
        dj.ShowFolder(DemoSongs.Key(_music));
        yield return new Wait(3);
        if (dj.Browser.Rows.Count == 0)
            yield break;
        dj.OpenTrack(dj.Browser.Rows[0]);
        var tl = dj.Timeline.Timelines;
        for (var i = 0; i < 1800 && (dj.Timeline.IsLoading || tl.IsAnalysing || tl.Structure is null); i++)
            yield return new Wait(1);
        if (!_songReady)
        {
            tl.GenerateShow();
            _songReady = true;
            yield return new Wait(5);
        }
    }

    double ChorusStart()
    {
        var a = _app.DjEdit.Timeline.Timelines.Structure;
        return a?.Sections.FirstOrDefault(s => s.Label == "Chorus")?.Start ?? (a?.Duration ?? 120) / 3;
    }

    IEnumerable<Step> DjLibraryShot()
    {
        foreach (var step in OpenSong())
            yield return step;
        var dj = _app.DjEdit;
        dj.Timelines.Flush();
        dj.ShowFolder(DemoSongs.Key(_music));
        yield return new Wait(10);
        yield return Settle(10);
        yield return new Shot("11-dj-edit-library");
    }

    IEnumerable<Step> DjTimelineShot()
    {
        foreach (var step in OpenSong())
            yield return step;
        var tv = _app.DjEdit.Timeline;
        var tl = tv.Timelines;
        var bar = 240 / (tl.Structure?.Bpm ?? 124);
        var chorus = ChorusStart();
        tv.Wave.SetView(Math.Max(chorus - 24 * bar, 0), 40 * bar);
        tv.Seek(chorus - 6 * bar);
        yield return new Wait(3);
        // Four bars of the master selected, across the build into the chorus.
        int Row(TimelineRow row) => tv.RowIndexAt(tv.PointAt(tl.Master, row, chorus).Y);
        tl.SelectPress(Row(TimelineRow.Position), chorus - 2 * bar);
        tl.SelectRelease(Row(TimelineRow.Dimmer), chorus + 2 * bar);
        yield return Settle(10);
        yield return new Shot("12-dj-edit-timeline");
        tl.Selection.Clear();
    }

    IEnumerable<Step> PositionEditorShot()
    {
        SetUpMusic();
        var dj = _app.DjEdit;
        _app.Screen.SetView(AppView.DjEdit);
        dj.EnsureLoaded();
        if (Show.Positions.Positions.FirstOrDefault(p => p.Name == "Stage Left") is { } left)
        {
            dj.OpenPosition(left.Id);
            yield return new Wait(3);
            dj.Editor.SelectUids(_uids.Take(2));
        }
        yield return Settle(10);
        yield return new Shot("13-position-editor");
        dj.Editor.SelectUids([]);
        dj.ShowTrackList();
        yield return new Wait(3);
    }

    IEnumerable<Step> MuteSoloShot()
    {
        foreach (var step in OpenSong())
            yield return step;
        var tl = _app.DjEdit.Timeline.Timelines;
        var tracks = tl.FixtureTracks;
        if (tracks.Count >= 4)
        {
            tl.SetSolo(tracks[0], true);
            tl.SetMute(tracks[1], true);
            tl.SetCollapsed(tracks[2], true);
            tl.SetCollapsed(tracks[3], true);
        }
        _app.DjEdit.Timeline.Seek(ChorusStart() + 1);
        yield return Settle(10);
        yield return new Shot("15-mute-solo-collapse");
        if (tracks.Count >= 4)
        {
            tl.SetSolo(tracks[0], false);
            tl.SetMute(tracks[1], false);
            tl.SetCollapsed(tracks[2], false);
            tl.SetCollapsed(tracks[3], false);
        }
    }

    IEnumerable<Step> ReadmeShot()
    {
        ToConsole();
        Tab("Cues");
        Tab("3D Visualizer");
        _app.Screen.Console.Visualizer.UiVisible = false;
        Look(Warm);
        _app.Screen.Console.Cues.List.SelectedIndex = -1;
        _app.Window.Size = new int2(1400, 800);
        yield return Settle(20);
        yield return new Shot("screenshot");
        _app.Screen.Console.Visualizer.UiVisible = true;
        _app.Window.Size = Size;
        yield return new Wait(5);
    }
}
