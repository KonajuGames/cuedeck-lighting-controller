using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Cuedeck;
using Cuedeck.Dmx;
using Cuedeck.Fixtures;
using Cuedeck.Rendering;
using Cuedeck.Show;
using Cuedeck.UI;
using Cuedeck.UI.Console;
using Cuedeck.UI.DjEdit;
using Cuedeck.UI.DjPerform;
using Cuedeck.UI.Visualizer;
using Cuedeck.UI.Timeline;
using Cuedeck.Audio;
using Cuedeck.Music;
using Cuedeck.Perform;
using Cuedeck.Timelines;

WindowsShell.AttachParentConsole();

// --version says which build this is, from the project's version and the commit it was built from, and exits.
if (args.Contains("--version"))
{
    Console.WriteLine($"Cuedeck {AppInfo.Version}");
    return 0;
}

// The screenshot tool (--capture <folder> [name ...]) and the tour (--tour <folder>) run on a scratch app folder,
// leaving the user's alone.
var capture = ScreenshotTool.Parse(args);
if (capture is not null)
    ScreenshotTool.UseScratch();
// The tour notes when a song plays and puts it in afterwards, so it plays to no sound card, and nothing's heard.
if (capture is { Tour: true })
    Environment.SetEnvironmentVariable("SDL_AUDIO_DRIVER", "dummy");

// Cuedeck's first start on this computer: sLight's files, if it finds them, are offered for import.
var firstStart = capture is null && !Directory.Exists(AppPaths.Folder);

// What goes wrong is written down beside the settings, to look into after a show; even on another thread, where it
// still ends the app.
ErrorLog.FilePath = Path.Combine(AppPaths.Folder, "errors.log");
AppDomain.CurrentDomain.UnhandledException += (_, args) =>
{
    if (args.ExceptionObject is Exception e)
        ErrorLog.Write("working in the background", e);
};

try
{
    if (!Glfw.Init())
    {
        var errorCode = Glfw.GetError(out var errorMessage);
        throw new InvalidOperationException($"Failed to initialize GLFW: {errorCode}, {errorMessage}");
    }

    // GLFW can't find Brutal's Vulkan loader by itself, so touching Vulkan first loads it.
    _ = Vulkan.GetInstanceProcAddr;

    try
    {
        Run(args, capture, firstStart);
    }
    finally
    {
        Glfw.Terminate();
    }
}
catch (Exception e)
{
    // A windowed app has nowhere to print an unhandled exception, so it would otherwise just vanish.
    ErrorLog.Write("running", e);
    WindowsShell.ShowFatalError(e);
    return 1;
}

return 0;

static void Run(string[] args, ScreenshotTool.Request? capture, bool firstStart)
{
    // The look, before anything's made, as elements take their colours as they're made.
    Theme.Use(new AppSettings(AppSettings.DefaultPath).Theme);
    Glfw.WindowHint(GlfwWindowHint.ClientApi, Glfw.Constants.GLFW_NO_API);

    // Made hidden, and shown once the first frame is drawn, so Windows doesn't show it empty while the app gets going.
    Glfw.WindowHint(GlfwWindowHint.Visible, 0);
    Glfw.WindowHint(GlfwWindowHint.Maximized, capture is null ? 1 : 0);
    // The tour drives the window itself: the mouse passes through it to whatever's beneath, and it doesn't take the
    // focus from what's being worked in, so nothing typed or clicked meanwhile reaches it.
    if (capture is { Tour: true })
    {
        Glfw.WindowHint(GlfwWindowHint.MousePassthrough, 1);
        Glfw.WindowHint(GlfwWindowHint.FocusOnShow, 0);
        Glfw.WindowHint(GlfwWindowHint.Focused, 0);
    }

    // Declared first so it's disposed last, after the GPU objects that draw to it.
    var windowSize = capture is null ? new int2(1366, 820) : capture.Tour ? ScreenshotTool.TourSize : ScreenshotTool.Size;
    using var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "Cuedeck", Size = windowSize });
    // How big everything's drawn: as chosen, or as Windows has it for the screen; the screenshots are as designed.
    var windowsScale = Glfw.PrimaryMonitor.ContentScale.X;
    // "--scale 1.5" on the command line tries another for one run, leaving the setting as it is.
    var scaleAt = Array.IndexOf(args, "--scale");
    float? tried = scaleAt >= 0 && scaleAt + 1 < args.Length && float.TryParse(args[scaleAt + 1], System.Globalization.CultureInfo.InvariantCulture, out var given) ? given : null;
    // Never so big that the screen hasn't room for the smallest window, which would draw things over each other: on a
    // 1920 by 1080 screen, 169% at most.
    var screenPixels = Glfw.PrimaryMonitor.VideoMode;
    var largestScale = ScreenScale.Largest(screenPixels.Width, screenPixels.Height, SmallestWindow);
    var screenScale = capture is null ? ScreenScale.Use(tried ?? new AppSettings(AppSettings.DefaultPath).ScreenScale, windowsScale, largestScale) : 1;
    window.SetSizeLimits(
        new int2((int)Math.Min(SmallestWindow.X * screenScale, screenPixels.Width), (int)Math.Min(SmallestWindow.Y * screenScale, screenPixels.Height)),
        new int2(-1, -1)
    );

#if DEBUG
    const bool ENABLE_VALIDATION = true;
#else
    const bool ENABLE_VALIDATION = false;
#endif

    // Text's rasterised at the size it's drawn, so it's sharp.
    var atlas = new FontAtlas { Scale = screenScale };
    var regular = FindFont(bold: false);
    var bold = FindFont(bold: true);
    var fonts = new Fonts(
        Small: atlas.Add(regular, 12),
        Body: atlas.Add(regular, 14),
        Bold: atlas.Add(bold, 14),
        Heading: atlas.Add(bold, 18),
        Big: atlas.Add(bold, 26)
    );
    var images = Images.Load(atlas);

    // The logo on the taskbar and in Alt+Tab too.
    Images.SetWindowIcon(window);

    using var gpu = new GpuContext(window, ENABLE_VALIDATION);
    using var renderer = new Renderer(gpu, window, atlas);
    using var scenePass = new ScenePass(gpu);
    renderer.PrePass = scenePass;

    // The show, and the show as a document: its file, unsaved changes, autosave.
    using var show = new LightingShow(OperatingSystem.IsWindows() ? new Cuedeck.Audio.WasapiInput() : new Cuedeck.Audio.NoCapture());
    // USB DMX through FTDI's D2XX driver, if it's installed; a universe set to USB is streamed to its interface.
    using var usb = new UsbDmx(OperatingSystem.IsWindows() ? Ftd2xx.Load() : null, OperatingSystem.IsWindows() ? Libusb.Load() : null);
    show.Dmx.RouteUsb = usb.Route;
    show.Dmx.SendUsb = usb.Send;
    show.Dmx.ReleaseUsb = usb.Unroute;
    var session = new ShowSession(show) { UsbAvailable = usb.Available };
    var profiles = new ProfileStore(Path.Combine(AppPaths.Folder, "fixture_profiles"));

    var ui = new Root { TooltipFont = fonts.Body, Scale = screenScale };
    var menu = new ContextMenu(fonts.Body);
    var confirm = new ConfirmDialog(ui, fonts.Body, fonts.Bold, fonts.Heading);
    var prompt = new NamePrompt(ui, fonts.Body, fonts.Bold, fonts.Heading);
    var picker = new ColourPicker(ui, fonts.Body);
    var bundled = new FixtureLibrary(FixtureLibrary.DefaultFolder);
    var library = new LibraryDialog(ui, fonts, menu, bundled, new OnlineLibrary(bundled, Path.Combine(AppPaths.Folder, "fixture_online")));
    var editor = new ProfileEditor(ui, fonts, menu);
    var triggers = new TriggersDialog(ui, fonts, menu, show);
    var settings = new AppSettings(AppSettings.DefaultPath);
    var options = new OptionsDialog(ui, fonts, menu, settings);
    (options.WindowsScale, options.LargestScale, options.RunningScale) = (windowsScale, largestScale, screenScale);
    // DJ Edit Mode: the song timelines follow the show (the session points them at its folder), over the patch and
    // its named positions.
    var timelines = new SongTimelines
    {
        Fixtures = () => [.. show.Patch.All],
        PositionIds = SongTimelines.PositionsFrom(show.Positions),
    };
    session.Timelines = timelines;
    using var songPlayer = new SdlSongPlayer();
    var timelineView = new TimelineView(fonts, menu, picker, timelines, songPlayer)
    {
        Positions = () => [.. show.Positions.Positions.Select(p => new PositionChoice(p.Id, p.Name, Colors.FromSrgb(p.Colour.R, p.Colour.G, p.Colour.B)))],
        PlateColour = uid => show.Patch.FindByUid(uid) is { } f && ModelColours.Of(show.Patch).TryGetValue(f.Profile.FullName, out var c) ? c : null,
    };
    var positionEditor = new PositionEditor(fonts, menu, show.Positions, show.Patch);
    var djEdit = new DjEditView(fonts, images, menu, confirm, timelines, timelineView, new MusicBrowser(timelines, () => DjLibrary.Load(settings.DjSoftware)), positionEditor)
    {
        Root = ui,
    };
    show.Patch.Changed += timelines.RefreshFixtures;
    show.Patch.Changed += positionEditor.RefreshFixtures;
    // DJ Perform Mode's engine (started from its view): VirtualDJ's decks played into the rig, a song without
    // timelines given temporary ones as DJ Edit Mode would generate them.
    var performer = new DjPerformer(show.Dmx)
    {
        FixturesProvider = () => [.. show.Patch.All.Select(PerformFixture.Of)],
        StoreProvider = () => timelines.Store,
        Positions = show.Positions,
        Timeline = timelines,
        SongLookup = path => djEdit.Browser.Library.FindSong(path),
        Tempo = show.Tempo,
    };
    if (capture is not null)
    {
        // Screenshots: VirtualDJ's stood in for on this machine only, and nothing's advertised.
        (performer.Port, performer.BindAddress, performer.Advertise) = (0, "127.0.0.1", false);
    }
    timelines.TimelineStoreChanged += () =>
    {
        if (performer.IsActive)
            performer.ReloadAll();
    };
    // DJ Edit Mode's open song goes live while it plays, or while it's paused with its timeline showing.
    using var editPlayback = new EditPlayback(show.Dmx, show.Patch, show.Positions, timelines)
    {
        CanPlay = () => timelineView.CanPlay,
        IsPlaying = () => timelineView.IsPlaying,
        PlaybackPosition = () => timelineView.PlaybackPosition,
        Performer = performer,
    };
    ui.PointerHooks.Add(timelineView);
    // Drawn in this order: the screen, then the dialogs over it, then the menu over everything.
    var djPerform = new DjPerformView(fonts, performer, show.Flash, picker);
    var screen = ui.Add(new MainScreen(fonts, images, menu, new Dialogs(confirm, prompt, picker, library, editor, triggers, options), show, profiles, djEdit, djPerform));
    // The 3D Visualizer: drawn by the renderer's 3D pass; the middle button slides its camera.
    scenePass.Source = screen.Visualizer;
    scenePass.Scale = screenScale;
    screen.Visualizer.Root = ui;
    screen.Visualizer.ModelsFolder = profiles.ModelsFolder;
    // Screenshots and recordings: what's playing as the sound; the render folder shown when one's saved.
    if (OperatingSystem.IsWindows())
        screen.Visualizer.StartAudio = LoopbackAudio.Start;
    screen.Visualizer.OpenFolder = folder => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    ui.PointerHooks.Add(screen.Visualizer);
    editPlayback.IsShown = () => screen.View == AppView.DjEdit && djEdit.IsTimelineShown;
    ui.Add(editor);
    ui.Add(triggers);
    ui.Add(options);
    var shortcuts = ui.Add(new ShortcutsDialog(ui, fonts));
    screen.Shortcuts = shortcuts;
    ui.Add(library);
    ui.Add(confirm);
    ui.Add(prompt);
    ui.Add(picker);
    ui.Add(menu);
    using var input = new GlfwUiInput(window, ui);
    // The 3D Visualizer can go to a window of its own, and be put back; it's where it was last time.
    using var popOut = new VizPopOut(gpu, atlas, fonts, screen.VizDock, scenePass, ui, menu, prompt, screen.Log, VizWindowState.DefaultPath);
    popOut.Restore();
    // Each file dialog starts where it was last used.
    var recent = new RecentFolders(RecentFolders.DefaultPath);
    var shell = new AppShell(session, screen, confirm, () => OperatingSystem.IsWindows() ? WindowChrome.Handle : 0) { Recent = recent };
    screen.Visualizer.AskForModel = () =>
        recent.Ask(
            "gltf_model",
            folder => WindowsShell.AskForFile(OperatingSystem.IsWindows() ? WindowChrome.Handle : 0, "Load glTF Model (set piece / stage element)", ("glTF model", "*.glb;*.gltf"), folder)
        );
    // MVR import and export, from the visualizer's toolbar (they change the patch and keep profiles).
    screen.Visualizer.ImportMvr = () =>
        recent.Ask("mvr", folder => WindowsShell.AskForFile(OperatingSystem.IsWindows() ? WindowChrome.Handle : 0, "Import MVR (My Virtual Rig)", ("My Virtual Rig", "*.mvr"), folder)) is { } mvr
            ? Mvr.Apply(Mvr.Import(mvr), show, profiles, Path.GetFileName(mvr))
            : "";
    screen.Visualizer.ExportMvr = () =>
        recent.Ask("mvr", folder => WindowsShell.AskWhereToSave(OperatingSystem.IsWindows() ? WindowChrome.Handle : 0, "Export MVR (My Virtual Rig)", [("My Virtual Rig", "*.mvr")], "rig.mvr", folder)?.Path) is { } where
            ? Mvr.Export(where, show)
            : "";
    screen.Console.Patch.Usb = usb;
    screen.Console.Patch.AskForFixtureFile = () =>
        recent.Ask(
            "fixture_import",
            folder =>
                WindowsShell.AskForFile(
                    OperatingSystem.IsWindows() ? WindowChrome.Handle : 0,
                    "Import Fixture Definition (GDTF / Open Fixture Library)",
                    ("GDTF or Open Fixture Library fixture", "*.gdtf;*.json"),
                    folder
                )
        );

    // The window's own buttons, and dragging it by the title bar, which Windows does once told where it is. Closing
    // asks about unsaved changes first.
    screen.TitleBar.IsMaximized = () => window.IsMaximised;
    screen.TitleBar.MinimizeClicked += window.Iconify;
    screen.TitleBar.MaximizeClicked += () =>
    {
        if (window.IsMaximised)
            window.Restore();
        else
            window.Maximize();
    };
    screen.TitleBar.CloseClicked += shell.RequestClose;
    if (OperatingSystem.IsWindows())
        WindowChrome.Install(window, point => screen.TitleBar.IsCaption(new float2(point.X, point.Y) / screenScale));

    // The console's keys, when nothing focused (a text box, say) has taken them; the tour presses keys through here
    // too.
    input.UnhandledKey += OnKey;

    void OnKey(GlfwKey key, GlfwKeyAction action, GlfwModifier mods)
    {
        // A letter typed into a text box isn't a shortcut, though the box leaves it unused.
        var typing = ui.Focused is TextBox;
        // ? opens and closes the shortcuts; while they're up, no other key does anything behind them.
        if (action == GlfwKeyAction.Press && !typing && key == GlfwKey.Slash && (mods & GlfwModifier.Shift) != 0 && (mods & (GlfwModifier.Control | GlfwModifier.Alt)) == 0)
        {
            shortcuts.Toggle();
            return;
        }
        if (shortcuts.Visible && action == GlfwKeyAction.Press)
            return;
        // DJ Perform Mode's B / S / W / C hold the live flashes (a key coming up lets go in any view).
        if (action != GlfwKeyAction.Repeat && !(typing && action == GlfwKeyAction.Press) && djPerform.FlashKey(GlfwUiInput.ToUiKey(key), action == GlfwKeyAction.Press, (Modifiers)((int)mods & 0xF)))
            return;
        if (action != GlfwKeyAction.Press)
            return;
        var ctrl = (mods & GlfwModifier.Control) != 0;
        var shift = (mods & GlfwModifier.Shift) != 0;
        // DJ Edit Mode's timeline has first say (Space plays its song there, rather than going the next cue).
        var uiKey = GlfwUiInput.ToUiKey(key);
        var uiMods = (Modifiers)((int)mods & 0xF);
        // The 3D Visualizer's Ctrl+Z / Ctrl+Y, while the mouse is over it, in any view.
        if (uiKey != Key.Other && screen.Visualizer.KeyDown(uiKey, uiMods, ui.Mouse))
            return;
        if (screen.View == AppView.DjEdit && djEdit.IsTimelineShown && uiKey != Key.Other && timelineView.KeyDown(uiKey, uiMods))
            return;
        // The Positions editor's Ctrl+C / Ctrl+V.
        if (screen.View == AppView.DjEdit && djEdit.IsPositionShown && uiKey != Key.Other && positionEditor.KeyDown(uiKey, uiMods))
            return;
        switch (key)
        {
            case GlfwKey.S when ctrl && shift:
                shell.SaveAs();
                break;
            case GlfwKey.S when ctrl:
                shell.Save();
                break;
            case GlfwKey.N when ctrl && shift:
                shell.AddUniverse();
                break;
            case GlfwKey.N when ctrl:
                shell.New();
                break;
            case GlfwKey.R when ctrl && shift:
                shell.ConfirmRemoveUniverse();
                break;
            case GlfwKey.O when ctrl && !shift:
                shell.Load();
                break;
            case GlfwKey.T when !ctrl && !shift && !typing:
                show.Tempo.Tap();
                break;
            // Space GOes the next cue, like a real console: in Cue Mode, when nothing else took it.
            case GlfwKey.Space when !ctrl && !shift && show.RunMode == RunMode.Cues:
                show.Cues.Go();
                break;
        }
    }

    // A show file given on the command line (opened with the app) is opened instead of the last one.
    shell.Start(capture is null ? args.FirstOrDefault(File.Exists) : null);
    // sLight's own files: importable from Options, and offered at the first start.
    if (capture is null)
        shell.SlightFolders = SlightImport.Folders();
    shell.SlightImported += () =>
    {
        profiles.Reload();
        settings.Reload();
        recent.Reload();
        djEdit.ReloadLibrary();
    };
    if (firstStart)
        shell.OfferSlightImport();
    var screenshots = capture is { } c ? new ScreenshotTool(new(window, renderer, ui, screen, show, performer, djEdit, session, OnKey), c) : null;

    // Every MIDI input, opened as it's plugged in; what they send goes to the show each frame. Their outputs are left
    // for trigger feedback to open (some drivers let only one program have a port).
    Cuedeck.Midi.IMidiPorts midiPorts = OperatingSystem.IsWindows() ? new Cuedeck.Midi.WinMidi() : new Cuedeck.Midi.NoMidiPorts();
    using var midi = new Cuedeck.Midi.MidiDevices(midiPorts, openOutputs: false);
    show.Triggers.MidiPorts = midiPorts;
    show.Triggers.MidiInputs = () => [.. midi.Devices.Select(d => d.Name)];
    show.Triggers.RescanMidi = midi.Scan;

    // The show runs at sLight's refresh rate, 30 frames a second, however fast the screen draws.
    const double TICK = 1.0 / 30;
    var lastTime = Glfw.Time;
    var tickTime = 0.0;
    var lastAutosave = Glfw.Time;
    const double TITLE_CHECK = 0.25;
    var lastTitleCheck = double.NegativeInfinity;
    var shownTitle = "";
    var windowShown = false;
    while (!shell.MayClose && screenshots?.Done != true)
    {
        // Minimised, there's nothing to draw, but the show still runs, waking often enough to keep its frames
        // steady. Whether it's minimised is asked after handling events, as one of them may be the minimise itself:
        // drawing into a minimised window leaves it black for good once it's restored.
        if (window.IsIconified)
            Glfw.WaitEventsTimeout(TICK / 2);
        else
            Glfw.PollEvents();

        // Alt+F4 and the like: asked about unsaved changes, like the close button.
        if (window.ShouldClose)
        {
            window.ShouldClose = false;
            shell.RequestClose();
        }

        var now = Glfw.Time;
        midi.Update(now);
        while (midi.TryRead(out _, out var message))
            show.HandleMidi(message);
        // A long stall (a file dialog open, a breakpoint) isn't made up for frame by frame.
        tickTime += Math.Min(now - lastTime, 1.0);
        lastTime = now;
        performer.Update();
        while (tickTime >= TICK)
        {
            show.Tick(TICK, show.Sending);
            tickTime -= TICK;
        }
        if (now - lastAutosave >= Autosave.Interval)
        {
            session.AutosaveNow();
            lastAutosave = now;
        }

        popOut.Frame();

        var size = window.FramebufferSize;
        if (window.IsIconified || size.X == 0 || size.Y == 0)
            continue;

        screen.ScreenSize = menu.ScreenSize = confirm.ScreenSize = prompt.ScreenSize = picker.ScreenSize = library.ScreenSize = editor.ScreenSize = triggers.ScreenSize = options.ScreenSize = shortcuts.ScreenSize = new float2(size.X, size.Y) / screenScale;
        // Whether there are unsaved changes means writing out the whole show, so it's asked a few times a second, not
        // every frame, and the window's told only when the title changes.
        if (now - lastTitleCheck >= TITLE_CHECK)
        {
            lastTitleCheck = now;
            if (session.Title is var title && title != shownTitle)
            {
                shownTitle = title;
                screen.TitleBar.Subtitle = title.Replace(" — Cuedeck", "");
                window.Title = title;
            }
        }
        screen.Update();
        ui.Layout(screen.ScreenSize);
        input.UpdateCursor();
        renderer.DrawFrame(ui);
        screenshots?.Frame();
        if (!windowShown)
            window.Visible = windowShown = true;
    }

    popOut.SaveState();
    performer.Stop();
    timelines.Flush(); // the open song's timelines
    timelines.WaitForSaves(); // and any still being written in the background
    shell.End();
    if (OperatingSystem.IsWindows())
        WindowChrome.Remove();
}

static string FindFont(bool bold) =>
    FontAtlas.FindSystemFont(bold)
    ?? FontAtlas.FindSystemFont()
    ?? throw new FileNotFoundException("No usable system font found; install DejaVu Sans or Liberation Sans");

partial class Program
{
    /// <summary>
    /// The smallest the window can be, in the UI's units, with everything still laid out without overlapping.
    /// </summary>
    internal static readonly float2 SmallestWindow = new(1024, 640);
}
