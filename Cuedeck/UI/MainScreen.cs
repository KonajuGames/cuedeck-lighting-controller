using Brutal.Numerics;
using Cuedeck.Fixtures;
using Cuedeck.Show;
using Cuedeck.UI.Console;

namespace Cuedeck.UI;

/// <summary>
/// The fonts the screen is drawn in.
/// </summary>
sealed record Fonts(Font Small, Font Body, Font Bold, Font Heading, Font Big);

/// <summary>
/// What fills the screen below the top bar: the console, DJ Edit Mode or DJ Perform Mode.
/// </summary>
enum AppView
{
    Console,
    DjEdit,
    DjPerform,
}

/// <summary>
/// The whole window: the title bar, the menu bar, the console's top bar, the console (or DJ Edit or DJ Perform Mode)
/// filling the middle with the 3D Visualizer docked on its right, and a status line along the bottom. Everything is sized to the window each frame, so set <see cref="ScreenSize"/> and call
/// <see cref="Update"/> before laying out.
/// </summary>
sealed class MainScreen : Panel
{
    const float STATUS_HEIGHT = 26;

    readonly Label _status;

    public float2 ScreenSize { get; set; } = new(1280, 720);

    public TitleBar TitleBar { get; }

    /// <summary>
    /// The File, Edit, Mode and Help menus, under the title bar.
    /// </summary>
    public MenuBar MenuBar { get; }

    /// <summary>
    /// What the menus do, for the app's shell to handle the file commands.
    /// </summary>
    public AppMenus Menus { get; }

    public TopBar TopBar { get; }
    public ConsoleView Console { get; }

    /// <summary>
    /// The 3D Visualizer, in <see cref="VizDock"/> unless it's popped out into a window of its own.
    /// </summary>
    public Visualizer.VisualizerView Visualizer { get; }

    /// <summary>
    /// Where the 3D Visualizer is docked, down the right of the window, beside whichever view shows.
    /// </summary>
    public Visualizer.VizDock VizDock { get; }

    /// <summary>
    /// DJ Edit Mode, if the app has it (tests of the console leave it out).
    /// </summary>
    public DjEdit.DjEditView? DjEdit { get; }

    /// <summary>
    /// DJ Perform Mode, if the app has it.
    /// </summary>
    public DjPerform.DjPerformView? DjPerform { get; }

    public AppView View { get; private set; } = AppView.Console;

    /// <summary>
    /// The dialogs the screen opens.
    /// </summary>
    public Dialogs Dialogs { get; }

    /// <summary>
    /// The keyboard shortcuts overlay, opened from the Help menu, if the app has added one over the screen.
    /// </summary>
    public ShortcutsDialog? Shortcuts { get; set; }

    /// <summary>
    /// Shows the keyboard shortcuts.
    /// </summary>
    public void ShowShortcuts() => Shortcuts?.Show();

    public MainScreen(Fonts fonts, Images images, ContextMenu menu, Dialogs dialogs, LightingShow show, ProfileStore profiles, DjEdit.DjEditView? djEdit = null, DjPerform.DjPerformView? djPerform = null)
    {
        Background = Theme.Background;
        Dialogs = dialogs;
        TitleBar = Add(
            new TitleBar(fonts.Body, fonts.Bold, images.Logo, images.Cog, ("Cue", Theme.Accent), ("deck", Theme.Text))
            {
                Version = $"v{AppInfo.ShortVersion}",
                VersionFont = fonts.Small,
            }
        );
        MenuBar = Add(new MenuBar(fonts.Body, menu));
        TopBar = Add(new TopBar(fonts, menu, show));
        Console = Add(new ConsoleView(fonts, images.Fixture, menu, dialogs, show, profiles));
        Visualizer = new Visualizer.VisualizerView(fonts, menu, dialogs.Prompt, show);
        VizDock = Add(new Visualizer.VizDock(Visualizer));
        Visualizer.Status += m =>
        {
            if (m != "")
                Log(m);
        };
        if (djEdit is not null)
        {
            DjEdit = Add(djEdit);
            djEdit.Visible = false;
            dialogs.Options.DjSoftwareChanged += _ => djEdit.ReloadLibrary();
        }
        if (djPerform is not null)
        {
            DjPerform = Add(djPerform);
            djPerform.Visible = false;
            djPerform.DjSoftwareName = () => dialogs.Options.Settings.DjSoftwareName;
        }
        Menus = new AppMenus(this, MenuBar, show);
        _status = Add(new Label(fonts.Body, "") { Color = Theme.Muted });
        TopBar.Status += Log;
        TitleBar.OptionsClicked += dialogs.Options.Show;
        dialogs.Options.DjSoftwareChanged += d => Log($"Music library: {AppSettings.NameOf(d)}.");
        dialogs.Options.ScreenSizeChosen += scale =>
        {
            var options = dialogs.Options;
            var name = ScreenScale.Name(options.Drawn(scale), options.WindowsScale);
            Log(options.Drawn(scale) == options.RunningScale ? $"The screen will stay at {name}." : $"Restart Cuedeck to draw the screen at {name}.");
        };
        Console.Patch.Status += Log;
        Console.Status += Log;
    }

    /// <summary>
    /// Says <paramref name="message"/> on the status line.
    /// </summary>
    public void Log(string message) => _status.Text = message;

    public string StatusText => _status.Text;

    /// <summary>
    /// Shows the console or DJ Edit Mode. Leaving DJ Edit Mode pauses its song and saves its timelines; showing it
    /// reads the DJ software's library the first time.
    /// </summary>
    public void SetView(AppView view)
    {
        if (view == AppView.DjEdit && DjEdit is null)
            view = AppView.Console;
        if (View == AppView.DjEdit && view != AppView.DjEdit)
            DjEdit!.Hidden();
        View = view;
        Console.Visible = view == AppView.Console;
        if (DjEdit is not null)
        {
            DjEdit.Visible = view == AppView.DjEdit;
            if (DjEdit.Visible)
                DjEdit.EnsureLoaded();
        }
        // DJ Perform Mode's listener and timeline playback run only while it shows.
        if (DjPerform is not null)
        {
            DjPerform.Visible = view == AppView.DjPerform;
            var performer = DjPerform.Performer;
            if (view == AppView.DjPerform && !performer.IsActive)
            {
                _ = DjEdit?.Browser.Library; // read now, not mid-set when a song needs its beat grid
                var err = performer.Start();
                Log("DJ Perform Mode: " + (err == "" ? $"listening for {DjPerform.DjSoftwareName()} (OS2L)." : performer.Status));
            }
            else if (view != AppView.DjPerform && performer.IsActive)
            {
                performer.Stop();
                Log("DJ Perform Mode stopped.");
            }
        }
    }

    // Below the title bar and the menu bar.
    float BarsHeight => TitleBar.HEIGHT + MenuBar.Size.Y;

    public void Update()
    {
        TitleBar.Width = ScreenSize.X;
        TitleBar.Update();
        MenuBar.Width = ScreenSize.X;
        TopBar.Width = ScreenSize.X - 2 * TopBar.Padding;
        TopBar.Update();
        if (Console.Visible)
            Console.Update();
        DjEdit?.Update();
        if (DjPerform is { Visible: true })
            DjPerform.Update();
        VizDock.Update();
        _status.MaxWidth = ScreenSize.X - 20;
    }

    public override float2 Measure()
    {
        TitleBar.Measure();
        MenuBar.Measure();
        TopBar.Measure();
        _status.Measure();
        var height = MathF.Max(100, ScreenSize.Y - BarsHeight - TopBar.Size.Y - STATUS_HEIGHT);
        var dock = VizDock.WidthIn(ScreenSize.X);
        VizDock.Available = new float2(dock, height);
        VizDock.Measure();
        var room = new float2(ScreenSize.X - dock, height);
        Console.Available = room;
        if (Console.Visible)
            Console.Measure();
        if (DjPerform is { Visible: true } perform)
        {
            perform.Available = room - new float2(16, 8);
            perform.Measure();
        }
        if (DjEdit is { Visible: true } dj)
        {
            dj.Available = room - new float2(16, 8);
            dj.Measure();
        }
        return Size = ScreenSize;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        TitleBar.Arrange(position);
        MenuBar.Arrange(position + new float2(0, TitleBar.HEIGHT));
        TopBar.Arrange(position + new float2(0, BarsHeight));
        if (Console.Visible)
            Console.Arrange(position + new float2(0, BarsHeight + TopBar.Size.Y));
        DjEdit?.Arrange(position + new float2(8, BarsHeight + TopBar.Size.Y + 6));
        DjPerform?.Arrange(position + new float2(8, BarsHeight + TopBar.Size.Y + 6));
        VizDock.Arrange(position + new float2(ScreenSize.X - VizDock.Size.X, BarsHeight + TopBar.Size.Y));
        _status.Arrange(position + new float2(10, ScreenSize.Y - STATUS_HEIGHT + MathF.Round((STATUS_HEIGHT - _status.Size.Y) / 2)));
    }

    public override void Draw(Batch batch)
    {
        DrawFrame(batch);
        batch.AddRect(ScreenPosition + new float2(0, ScreenSize.Y - STATUS_HEIGHT), new float2(ScreenSize.X, STATUS_HEIGHT), Theme.TitleBar);
        DrawChildren(batch);
    }
}
