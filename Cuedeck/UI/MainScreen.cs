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
/// The whole window: the title bar, the console's top bar, the console filling the middle, and a status line along
/// the bottom. Everything is sized to the window each frame, so set <see cref="ScreenSize"/> and call
/// <see cref="Update"/> before laying out.
/// </summary>
/// <summary>
/// What fills the screen below the top bar: the console, or DJ Edit Mode.
/// </summary>
enum AppView
{
    Console,
    DjEdit,
}

sealed class MainScreen : Panel
{
    const float STATUS_HEIGHT = 26;

    readonly Label _status;

    public float2 ScreenSize { get; set; } = new(1280, 720);

    public TitleBar TitleBar { get; }
    public TopBar TopBar { get; }
    public ConsoleView Console { get; }

    /// <summary>
    /// DJ Edit Mode, if the app has it (tests of the console leave it out).
    /// </summary>
    public DjEdit.DjEditView? DjEdit { get; }

    public AppView View { get; private set; } = AppView.Console;

    public MainScreen(Fonts fonts, Images images, ContextMenu menu, Dialogs dialogs, LightingShow show, ProfileStore profiles, DjEdit.DjEditView? djEdit = null)
    {
        Background = Theme.Background;
        TitleBar = Add(new TitleBar(fonts.Body, fonts.Bold, images));
        TopBar = Add(new TopBar(fonts, menu, show));
        Console = Add(new ConsoleView(fonts, menu, dialogs, show, profiles));
        if (djEdit is not null)
        {
            DjEdit = Add(djEdit);
            djEdit.Visible = false;
            TopBar.HasDjEdit = true;
            dialogs.Options.DjSoftwareChanged += _ => djEdit.ReloadLibrary();
        }
        TopBar.ViewChosen += SetView;
        _status = Add(new Label(fonts.Body, "") { Color = Theme.Muted });
        TopBar.Status += Log;
        TopBar.OpenTriggers += dialogs.Triggers.Show;
        TitleBar.OptionsClicked += dialogs.Options.Show;
        dialogs.Options.DjSoftwareChanged += d => Log($"Music library: {AppSettings.NameOf(d)}.");
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
            DjEdit!.Timeline.Hidden();
        View = view;
        Console.Visible = view == AppView.Console;
        if (DjEdit is not null)
        {
            DjEdit.Visible = view == AppView.DjEdit;
            if (DjEdit.Visible)
                DjEdit.EnsureLoaded();
        }
        TopBar.View = view;
    }

    public void Update()
    {
        TitleBar.Width = ScreenSize.X;
        TitleBar.Update();
        TopBar.Width = ScreenSize.X - 2 * TopBar.Padding;
        TopBar.Update();
        if (Console.Visible)
            Console.Update();
        DjEdit?.Update();
        _status.MaxWidth = ScreenSize.X - 20;
    }

    public override float2 Measure()
    {
        TitleBar.Measure();
        TopBar.Measure();
        _status.Measure();
        var room = new float2(ScreenSize.X, MathF.Max(100, ScreenSize.Y - TitleBar.HEIGHT - TopBar.Size.Y - STATUS_HEIGHT));
        Console.Available = room;
        if (Console.Visible)
            Console.Measure();
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
        TopBar.Arrange(position + new float2(0, TitleBar.HEIGHT));
        if (Console.Visible)
            Console.Arrange(position + new float2(0, TitleBar.HEIGHT + TopBar.Size.Y));
        DjEdit?.Arrange(position + new float2(8, TitleBar.HEIGHT + TopBar.Size.Y + 6));
        _status.Arrange(position + new float2(10, ScreenSize.Y - STATUS_HEIGHT + MathF.Round((STATUS_HEIGHT - _status.Size.Y) / 2)));
    }

    public override void Draw(Batch batch)
    {
        DrawFrame(batch);
        batch.AddRect(ScreenPosition + new float2(0, ScreenSize.Y - STATUS_HEIGHT), new float2(ScreenSize.X, STATUS_HEIGHT), Theme.TitleBar);
        DrawChildren(batch);
    }
}
