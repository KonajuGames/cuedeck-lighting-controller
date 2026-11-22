using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Cuedeck.UI;
using Cuedeck.UI.Console;

namespace Cuedeck;

/// <summary>
/// The feature tour: <c>Cuedeck --tour [folder] [--ffmpeg path]</c>. On the screenshots' scratch folder, demo rig
/// and made-up songs, it plays through the app's main features by itself, pressing its keys and moving and clicking
/// a pretend mouse, while every frame the GPU draws goes to ffmpeg as <c>tour-raw.mp4</c>. Nothing's played aloud,
/// and the window lets the real mouse through and doesn't take the focus, so the computer can be used meanwhile.
/// What was said and shown when — the captions, the boxes round what's being shown, the keys pressed, where the
/// mouse was, and which song played from where — goes in <c>tour-timeline.json</c>, for <c>docs/tour/annotate.py</c>
/// to draw over the video and put the music under it. Cuepoint's tour.
/// </summary>
sealed partial class ScreenshotTool
{
    /// <summary>
    /// The tour's window: 1920 wide, and short enough to fit a 1080 screen with its taskbar. The video's padded to
    /// 1080.
    /// </summary>
    public static readonly int2 TourSize = new(1920, 1050);

    const int FPS = 60;

    VideoWriter? _video;

    // The video's own clock: how long has been recorded, which stops while the tour skips a wait.
    double _recordedBefore;
    double _recordingSince = -1;

    double VideoTime => _recordedBefore + (_recordingSince >= 0 ? _clock.Elapsed.TotalSeconds - _recordingSince : 0);

    readonly List<string> _events = [];
    readonly StringBuilder _cursorTrack = new();
    float2 _mouse = new(1500, 760);
    bool _cursorShown;
    bool _mouseDown;

    // -------------------------------------------------------------------------------------------- recording frames

    /// <summary>
    /// Each frame while the tour records: the frame just drawn goes to the video, the next is asked for, and where
    /// the pretend mouse is goes down.
    /// </summary>
    void TourFrame()
    {
        if (_video is null || _recordingSince < 0)
            return;
        var t = VideoTime;
        if (_app.Renderer.TakeWindowCapture() is { } picture)
            _video.Add(picture.Rgba, t);
        _app.Renderer.CaptureWindow = true;
        if (_cursorShown)
            _cursorTrack.Append(CultureInfo.InvariantCulture, $"[{t:F3},{_mouse.X:F1},{_mouse.Y:F1},{(_mouseDown ? 1 : 0)}],");
    }

    void StartRecordingVideo()
    {
        var size = _app.Window.FramebufferSize;
        _video = new VideoWriter(_ffmpeg, Path.Combine(_out, "tour-raw.mp4"), size.X, size.Y, FPS, Path.Combine(_out, "tour-ffmpeg.log"));
        _recordingSince = _clock.Elapsed.TotalSeconds;
        _app.Renderer.CaptureWindow = true;
    }

    /// <summary>
    /// Stops the video, to skip a wait; <see cref="ResumeVideo"/> carries on from there.
    /// </summary>
    void PauseVideo()
    {
        _recordedBefore = VideoTime;
        _recordingSince = -1;
        _app.Renderer.TakeWindowCapture();
    }

    void ResumeVideo()
    {
        _recordingSince = _clock.Elapsed.TotalSeconds;
        _app.Renderer.CaptureWindow = true;
    }

    void FinishTour()
    {
        var end = VideoTime;
        _recordingSince = -1;
        _video?.Finish(end);
        Console.WriteLine($"{_video?.Captured} frames captured for {_video?.Written} written ({end:F1}s)");
        var size = _app.Window.FramebufferSize;
        var json = new StringBuilder();
        json.Append(CultureInfo.InvariantCulture, $"{{\"width\":{size.X},\"height\":{size.Y},\"fps\":{FPS},\"duration\":{end:F3},\n\"events\":[\n");
        json.AppendJoin(",\n", _events);
        json.Append("\n],\n\"cursor\":[").Append(_cursorTrack.ToString().TrimEnd(',')).Append("]}\n");
        File.WriteAllText(Path.Combine(_out, "tour-timeline.json"), json.ToString());
        Console.WriteLine($"tour written to {_out}");
    }

    // ---------------------------------------------------------------------------------------------- the timeline

    static string Json(string text) => System.Text.Json.JsonSerializer.Serialize(text);

    void Event(string type, string fields = "") =>
        _events.Add(string.Create(CultureInfo.InvariantCulture, $"{{\"t\":{VideoTime:F3},\"type\":\"{type}\"{(fields.Length > 0 ? "," + fields : "")}}}"));

    Step Caption(string title, string body) => new Do(() => Event("caption", $"\"title\":{Json(title)},\"body\":{Json(body)}"));

    Step NoCaption() => new Do(() => Event("captionOff"));

    Step Card(string title, string body) => new Do(() => Event("card", $"\"title\":{Json(title)},\"body\":{Json(body)}"));

    Step NoCard() => new Do(() => Event("cardOff"));

    /// <summary>
    /// The song at <paramref name="file"/> heard from <paramref name="from"/> seconds in, until <see cref="NoMusic"/>.
    /// </summary>
    void Music(string file, double from) => Event("music", string.Create(CultureInfo.InvariantCulture, $"\"file\":{Json(file)},\"from\":{from:F3}"));

    void NoMusic() => Event("musicOff");

    /// <summary>
    /// A box round what <paramref name="find"/> finds when it comes to it (nothing if it finds nothing), with an
    /// optional label.
    /// </summary>
    Step Highlight(Func<(float2 At, float2 Size)?> find, string label = "") =>
        new Do(() =>
        {
            if (find() is not var (at, size))
            {
                Console.Error.WriteLine($"nothing to highlight for \"{label}\"");
                return;
            }
            Event("highlight", string.Create(CultureInfo.InvariantCulture, $"\"x\":{at.X:F1},\"y\":{at.Y:F1},\"w\":{size.X:F1},\"h\":{size.Y:F1},\"label\":{Json(label)}"));
        });

    Step Highlight(Func<Element?> find, string label = "", float pad = 4) => Highlight(() => Box(find(), pad), label);

    Step NoHighlights() => new Do(() => Event("highlightsOff"));

    static (float2 At, float2 Size)? Box(Element? element, float pad = 4) =>
        element is null ? null : (element.ScreenPosition - new float2(pad, pad), element.Size + new float2(2 * pad, 2 * pad));

    static (float2 At, float2 Size)? Around(IEnumerable<Element?> elements, float pad = 4)
    {
        var found = elements.OfType<Element>().ToList();
        if (found.Count == 0)
            return null;
        var min = found.Select(e => e.ScreenPosition).Aggregate(float2.Min);
        var max = found.Select(e => e.ScreenPosition + e.Size).Aggregate(float2.Max);
        return (min - new float2(pad, pad), max - min + new float2(2 * pad, 2 * pad));
    }

    // ------------------------------------------------------------------------------------- finding what's on screen

    Element? FindAny(Func<Element, bool> match) => _app.Ui.Elements.Where(e => e.Visible).SelectMany(All).FirstOrDefault(match);

    Button? ButtonCalled(string text) => Find<Button>(b => b.Text == text);

    Element? WithTooltip(string tooltip) => FindAny(e => e.Tooltip == tooltip);

    Button? MenuTitle(string title) => _app.Screen.MenuBar.Children.OfType<Button>().FirstOrDefault(b => b.Text == title);

    // ----------------------------------------------------------------------------------------- the pretend mouse

    static double Ease(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    IEnumerable<Step> MoveTo(Func<float2?> target, double seconds = 0.7)
    {
        float2 from = default, to = default;
        yield return new Do(() =>
        {
            _cursorShown = true;
            from = _mouse;
            if (target() is { } where)
                to = where;
            else
            {
                to = _mouse;
                Console.Error.WriteLine("nothing to move the mouse to");
            }
        });
        yield return new Over(seconds, through =>
        {
            _mouse = from + (to - from) * (float)Ease(through);
            _app.Ui.MouseMove(_mouse);
        });
    }

    IEnumerable<Step> MoveTo(Func<Element?> target, double seconds = 0.7) =>
        MoveTo(() => target() is { } e ? e.ScreenPosition + e.Size / 2 : null, seconds);

    IEnumerable<Step> Click()
    {
        yield return new Do(() =>
        {
            _mouseDown = true;
            Event("click", string.Create(CultureInfo.InvariantCulture, $"\"x\":{_mouse.X:F1},\"y\":{_mouse.Y:F1},\"right\":false"));
            _app.Ui.MouseButton(MouseButton.Left, true);
        });
        yield return new Hold(0.12);
        yield return new Do(() =>
        {
            _app.Ui.MouseButton(MouseButton.Left, false);
            _mouseDown = false;
        });
        yield return new Hold(0.2);
    }

    IEnumerable<Step> ClickOn(Func<Element?> target, double seconds = 0.7) => MoveTo(target, seconds).Concat(Click());

    IEnumerable<Step> ClickOn(Func<float2?> target, double seconds = 0.7) => MoveTo(target, seconds).Concat(Click());

    /// <summary>
    /// Presses where the mouse is and drags it by <paramref name="by"/> over <paramref name="seconds"/>.
    /// </summary>
    IEnumerable<Step> Drag(float2 by, double seconds)
    {
        var from = float2.Zero;
        yield return new Do(() =>
        {
            from = _mouse;
            _mouseDown = true;
            _app.Ui.MouseButton(MouseButton.Left, true);
        });
        yield return new Over(seconds, through =>
        {
            _mouse = from + by * (float)Ease(through);
            _app.Ui.MouseMove(_mouse);
        });
        yield return new Do(() =>
        {
            _app.Ui.MouseButton(MouseButton.Left, false);
            _mouseDown = false;
        });
    }

    Step HideMouse() =>
        new Do(() =>
        {
            _cursorShown = false;
            _app.Ui.MouseMove(new float2(-100, -100));
        });

    // --------------------------------------------------------------------------------------------- the keyboard

    /// <summary>
    /// Presses a key as the keyboard would: the UI first, then the app's own shortcuts if the UI didn't use it,
    /// held for <paramref name="hold"/> seconds. What was pressed is shown on the video as <paramref name="label"/>.
    /// </summary>
    IEnumerable<Step> Press(GlfwKey key, GlfwModifier mods = 0, double hold = 0.12, string? label = null, bool show = true)
    {
        var handled = false;
        var uiMods = (Modifiers)((int)mods & 0xF);
        yield return new Do(() =>
        {
            if (show)
                Event("key", $"\"label\":{Json(label ?? (key == GlfwKey.Escape ? "Esc" : key.ToString()))}");
            _app.Ui.ModifiersHeld(uiMods);
            handled = _app.Ui.KeyDown(GlfwUiInput.ToUiKey(key), uiMods);
            if (!handled)
                _app.Key(key, GlfwKeyAction.Press, mods);
        });
        yield return new Hold(hold);
        yield return new Do(() =>
        {
            if (!handled)
                _app.Key(key, GlfwKeyAction.Release, mods);
            _app.Ui.ModifiersHeld(Modifiers.None);
        });
    }

    IEnumerable<Step> TypeSlowly(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            yield return new Do(() => _app.Ui.TextInput(rune));
            yield return new Hold(0.09);
        }
    }

    /// <summary>
    /// Nothing focused, so the next key goes to the app's shortcuts rather than a button the mouse last pressed.
    /// </summary>
    Step Unfocus() => new Do(() => _app.Ui.Focus(null));

    // ------------------------------------------------------------------------------------------------- the tour

    IEnumerable<Step> Tour()
    {
        // The picture at its size. The app's own title bar draws over the window's frame, so the frame's in the
        // picture, while the size asked of the window has it added on: it's asked for that much less.
        for (var i = 0; i < 5 && _app.Window.FramebufferSize != TourSize; i++)
        {
            _app.Window.Size = TourSize - (_app.Window.FramebufferSize - _app.Window.Size) - (_app.Window.Size - TourSize);
            yield return new Wait(10);
        }
        if (_app.Window.FramebufferSize != TourSize)
            Console.Error.WriteLine($"the window is {_app.Window.FramebufferSize}, not {TourSize}");
        yield return new Wait(5);
        PatchRig();
        ProgramShow();

        // The first song analysed and a light show generated for it, before the camera rolls; then back to the list.
        foreach (var step in OpenSong())
            yield return step;
        var dj = _app.DjEdit;
        dj.Timelines.Flush();
        dj.ShowTrackList();
        ToConsole();
        Tab("Cues");
        Look([]);
        _app.Screen.Console.Cues.List.SelectedIndex = -1;
        yield return Settle(30);
        HideMouseNow();
        Step Rest(double seconds) => new Hold(seconds);
        var console = _app.Screen.Console;
        var viz = _app.Screen.Visualizer;

        yield return new Do(StartRecordingVideo);

        // ---- Opening card, over the dark stage.
        yield return Card("Cuedeck", "A DMX lighting console for Windows, with a live 3D stage and lights that follow the DJ");
        yield return Rest(4);
        yield return NoCard();
        yield return Rest(0.6);

        // ---- Cues.
        yield return Caption("Cues", "Record looks as cues with fade times, delays and follows. Space is GO, like a real console");
        yield return Highlight(() => Around([ButtonCalled("GO"), console.Cues.List]), "Cue list");
        yield return Rest(1.5);
        yield return Unfocus();
        foreach (var step in Press(GlfwKey.Space))
            yield return step;
        yield return Rest(4);
        yield return NoHighlights();

        // ---- The 3D Visualizer.
        yield return Caption("The 3D Visualizer", "The patched rig lit live by the output: beams, haze, gobos and shadows. Drag to orbit");
        foreach (var step in MoveTo(() => viz.ScreenPosition + viz.Size * new float2(0.45f, 0.55f), 0.8))
            yield return step;
        foreach (var step in Drag(new float2(110, -15), 2.2))
            yield return step;
        yield return Rest(0.4);
        foreach (var step in Drag(new float2(-80, 10), 1.8))
            yield return step;
        yield return HideMouse();
        yield return Rest(1.5);

        // ---- The next cue.
        yield return Caption("Tracking cues", "The next GO fades to a cooler look. Cues can track, storing only what they change");
        yield return Unfocus();
        foreach (var step in Press(GlfwKey.Space))
            yield return step;
        yield return Rest(4);

        // ---- Chases.
        yield return Caption("Chases", "Steps cycled at a tempo, crossfaded, or stepped on the beat");
        foreach (var step in ClickOn(() => ButtonCalled("Chases")))
            yield return step;
        yield return new Do(() => console.Chases.List.SelectedIndex = 0);
        yield return Rest(0.5);
        foreach (var step in ClickOn(() => Find<Checkbox>(c => c.Text == "Running"), 0.6))
            yield return step;
        yield return HideMouse();
        yield return Highlight(() => console.Chases.List, "Colour Wipe running");
        yield return Rest(4);
        yield return NoHighlights();
        yield return new Do(() => Show.Effects.Chases[0].Running = false);

        // ---- Effects.
        yield return Caption("Effects", "Waves on any kind of channel, fanned across a group: here, the movers' pan");
        foreach (var step in ClickOn(() => ButtonCalled("Effects")))
            yield return step;
        yield return new Do(() => console.Effects.List.SelectedIndex = 0);
        yield return Rest(0.5);
        foreach (var step in ClickOn(() => Find<Checkbox>(c => c.Text == "Running"), 0.6))
            yield return step;
        yield return HideMouse();
        yield return Rest(4.5);

        // ---- The tempo.
        yield return Caption("Tempo clock", "Tap it in with T, or follow MIDI clock or the DJ software's beats");
        yield return Highlight(() => Around([ButtonCalled("Tap"), ButtonCalled("×")]), "Tap");
        yield return Unfocus();
        for (var i = 0; i < 4; i++)
        {
            foreach (var step in Press(GlfwKey.T, hold: 0.08))
                yield return step;
            yield return Rest(0.4);
        }
        yield return Rest(1.5);
        yield return NoHighlights();
        yield return new Do(() => Show.Effects.Effects[0].Running = false);

        // ---- The patch and the fixture library.
        yield return Caption("Patch", "Fixtures from built-in and GDTF profiles, each with its own controls, on up to eight universes");
        foreach (var step in ClickOn(() => ButtonCalled("Patch")))
            yield return step;
        yield return Rest(0.5);
        foreach (var step in ClickOn(() => Find<Button>(b => b.Tooltip == "Expand"), 0.6))
            yield return step;
        yield return HideMouse();
        yield return Highlight(() => FindAny(e => e is FixtureRow), "Mover L", 2);
        yield return Rest(3);
        yield return NoHighlights();
        yield return Caption("Fixture library", "Search thousands of fixture profiles, and add one to the patch");
        foreach (var step in ClickOn(() => ButtonCalled("Library…")))
            yield return step;
        yield return Rest(0.6);
        foreach (var step in ClickOn(() => Find<TextBox>(t => t.Placeholder.StartsWith("Search", StringComparison.Ordinal)), 0.5))
            yield return step;
        yield return HideMouse();
        foreach (var step in TypeSlowly("moving head"))
            yield return step;
        yield return Rest(2.5);
        foreach (var step in Press(GlfwKey.Escape))
            yield return step;
        yield return new Do(() => _app.Ui.Elements.OfType<LibraryDialog>().FirstOrDefault()?.Close());
        yield return Rest(0.5);

        // ---- Faders and triggers.
        yield return Caption("Faders and triggers", "Playback faders for cues, chases, groups and effects, learned from a MIDI controller, with LED feedback");
        foreach (var step in ClickOn(() => ButtonCalled("Faders")))
            yield return step;
        yield return HideMouse();
        yield return Highlight(() => Around(_app.Ui.Elements.SelectMany(All).Where(e => e is FaderStrip)), "Faders");
        yield return Rest(2.5);
        yield return NoHighlights();
        foreach (var step in ClickOn(() => MenuTitle("Edit")))
            yield return step;
        foreach (var step in ClickOn(() => ButtonCalled("Triggers…"), 0.5))
            yield return step;
        yield return HideMouse();
        yield return Rest(3);
        foreach (var step in Press(GlfwKey.Escape))
            yield return step;
        yield return new Do(() => _app.Screen.Dialogs.Triggers.Close());
        yield return Rest(0.5);

        // ---- DJ Edit Mode.
        yield return Caption("DJ Edit Mode", "Lighting timelines for songs, from VirtualDJ's or Cuepoint's library");
        foreach (var step in ClickOn(() => MenuTitle("Mode")))
            yield return step;
        foreach (var step in ClickOn(() => ButtonCalled("DJ Edit Mode"), 0.5))
            yield return step;
        yield return new Do(() => dj.ShowFolder(DemoSongs.Key(_music)));
        yield return Rest(1.5);
        foreach (var step in ClickOn(() => dj.List.ScreenPosition + new float2(90, dj.List.RowHeight / 2)))
            yield return step;
        yield return HideMouse();
        yield return new Do(() =>
        {
            if (dj.Browser.Rows.Count > 0)
                dj.OpenTrack(dj.Browser.Rows[0]);
        });
        var tv = dj.Timeline;
        bool SongReady() => !tv.IsLoading && !tv.Timelines.IsAnalysing && tv.Timelines.Structure is not null;
        yield return new Do(PauseVideo);
        yield return new Until(SongReady, 60 * 60, "the song to open");
        yield return new Wait(10);
        yield return new Do(ResumeVideo);
        yield return Caption("Generate a light show", "From the song's sections, tempo and energy: dimmers, colours, strobes and moves, on every fixture");
        yield return Highlight(() => tv.GenerateButton, "Generate");
        yield return Rest(1);
        foreach (var step in ClickOn(() => tv.GenerateButton))
            yield return step;
        yield return HideMouse();
        yield return Rest(2);
        yield return NoHighlights();
        yield return Caption("Play it", "The song plays on the rig, the timelines under the playhead. Edit every node, cut, copy and paste");
        yield return new Do(() => tv.Seek(ChorusStart() - 4 * 240 / (tv.Timelines.Structure?.Bpm ?? 124)));
        yield return Unfocus();
        foreach (var step in Press(GlfwKey.Space))
            yield return step;
        yield return new Do(() => Music(Path.Combine(_music, DemoSongs.All[0].File), tv.PlaybackPosition));
        yield return Rest(11);
        yield return Unfocus();
        foreach (var step in Press(GlfwKey.Space))
            yield return step;
        yield return new Do(NoMusic);
        yield return Rest(0.6);

        // ---- DJ Perform Mode, VirtualDJ told to it as it would over OS2L.
        yield return Caption("DJ Perform Mode", "Follows VirtualDJ or Cuepoint over OS2L, playing each song's timeline on the rig as it's mixed");
        foreach (var step in ClickOn(() => MenuTitle("Mode")))
            yield return step;
        foreach (var step in ClickOn(() => ButtonCalled("DJ Perform Mode"), 0.5))
            yield return step;
        yield return HideMouse();
        var perf = _app.Performer;
        using var client = new TcpClient();
        yield return new Wait(5);
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
        var song = Path.Combine(_music, DemoSongs.All[0].File);
        var from = ChorusStart();
        void Vdj(string trigger, JsonNode? value) =>
            perf.Os2l?.HandleMessage(new JsonObject { ["evt"] = "subscribed", ["trigger"] = trigger, ["value"] = value });
        yield return new Do(() =>
        {
            Vdj("get_decks", 2);
            Vdj("deck 1 get_filepath", song);
            Vdj("deck 1 level", 1.0);
            Vdj("deck 1 get_time elapsed absolute", from * 1000);
            Vdj("deck 1 play", "on");
            Music(song, from);
        });
        yield return Rest(3);
        yield return Caption("Live flashes", "Hold B, S, W or C for a blackout, strobe, white or colour over everything playing");
        yield return Highlight(() => Around(_app.Ui.Elements.SelectMany(All).Where(e => e is Button b && b.Text.EndsWith(")", StringComparison.Ordinal) && b.Text.Length < 16)), "Live flashes");
        yield return Unfocus();
        foreach (var (key, hold) in new[] { (GlfwKey.S, 1.4), (GlfwKey.W, 0.8), (GlfwKey.C, 0.8), (GlfwKey.B, 0.8) })
        {
            foreach (var step in Press(key, hold: hold))
                yield return step;
            yield return Rest(0.7);
        }
        yield return NoHighlights();
        yield return Rest(1);
        yield return new Do(() =>
        {
            Vdj("deck 1 play", "off");
            NoMusic();
        });
        client.Close();

        // ---- Options and shortcuts.
        yield return Caption("Your way", "Dark, Light or High contrast, a screen size to suit, and ? for every shortcut");
        foreach (var step in ClickOn(() => WithTooltip("Options")))
            yield return step;
        yield return HideMouse();
        yield return Rest(2.5);
        foreach (var step in Press(GlfwKey.Escape))
            yield return step;
        yield return Rest(0.4);
        yield return Unfocus();
        foreach (var step in Press(GlfwKey.Slash, GlfwModifier.Shift, label: "?"))
            yield return step;
        yield return Rest(3.5);
        foreach (var step in Press(GlfwKey.Escape))
            yield return step;
        yield return NoCaption();
        yield return Rest(0.3);

        // ---- Closing card, back on the lit stage.
        yield return new Do(() =>
        {
            ToConsole();
            Tab("Cues");
            viz.UiVisible = false;
            Look(Warm);
        });
        yield return Rest(1);
        yield return Card("Cuedeck", "Opens sLight's shows · C# on .NET 10, drawn with Vulkan");
        yield return Rest(5);
        yield return NoCard();
        yield return Rest(0.3);
    }

    void HideMouseNow()
    {
        _cursorShown = false;
        _app.Ui.MouseMove(new float2(-100, -100));
    }

    // ---------------------------------------------------------------------------------------- the video writer

    /// <summary>
    /// Pipes frames to ffmpeg at a steady frame rate on a thread of its own, repeating a frame where the app drew
    /// slower than that and dropping some where it drew faster, by the time each was drawn.
    /// </summary>
    sealed class VideoWriter
    {
        readonly Process _ffmpeg;
        readonly Stream _input;
        readonly BlockingCollection<(byte[] Rgba, double Time)> _frames = new(boundedCapacity: 8);
        readonly Thread _thread;
        readonly int _fps;
        byte[]? _last;

        public int Captured { get; private set; }
        public int Written { get; private set; }

        public VideoWriter(string ffmpeg, string path, int width, int height, int fps, string log)
        {
            _fps = fps;
            var start = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            foreach (var arg in new[]
            {
                "-y", "-f", "rawvideo", "-pix_fmt", "rgba", "-s", $"{width}x{height}", "-r", $"{fps}", "-i", "-",
                // x264 takes only even sizes.
                "-vf", "crop=trunc(iw/2)*2:trunc(ih/2)*2", "-c:v", "libx264", "-preset", "veryfast", "-crf", "12", "-pix_fmt", "yuv420p", path,
            })
                start.ArgumentList.Add(arg);
            _ffmpeg = Process.Start(start)!;
            var logWriter = new StreamWriter(log);
            _ffmpeg.ErrorDataReceived += (_, e) =>
            {
                lock (logWriter)
                    logWriter.WriteLine(e.Data);
            };
            _ffmpeg.Exited += (_, _) =>
            {
                lock (logWriter)
                    logWriter.Dispose();
            };
            _ffmpeg.EnableRaisingEvents = true;
            _ffmpeg.BeginErrorReadLine();
            _input = _ffmpeg.StandardInput.BaseStream;
            _thread = new Thread(Write) { IsBackground = true, Name = "Tour video" };
            _thread.Start();
        }

        public void Add(byte[] rgba, double time)
        {
            Captured++;
            _frames.Add((rgba, time));
        }

        void Write()
        {
            foreach (var (rgba, time) in _frames.GetConsumingEnumerable())
            {
                WriteUntil(time);
                _last = rgba;
            }
        }

        // Writes the last frame for every output frame up to time.
        void WriteUntil(double time)
        {
            var due = (int)Math.Round(time * _fps);
            if (_last is null)
                return;
            while (Written < due)
            {
                _input.Write(_last);
                Written++;
            }
        }

        public void Finish(double end)
        {
            _frames.CompleteAdding();
            _thread.Join();
            WriteUntil(end);
            _input.Flush();
            _input.Dispose();
            _ffmpeg.WaitForExit();
        }
    }
}
