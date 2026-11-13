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
sealed class MainScreen : Panel
{
    const float STATUS_HEIGHT = 26;

    readonly Label _status;

    public float2 ScreenSize { get; set; } = new(1280, 720);

    public TitleBar TitleBar { get; }
    public TopBar TopBar { get; }
    public ConsoleView Console { get; }

    public MainScreen(Fonts fonts, Images images, ContextMenu menu, Dialogs dialogs, LightingShow show, ProfileStore profiles)
    {
        Background = Theme.Background;
        TitleBar = Add(new TitleBar(fonts.Body, fonts.Bold, images));
        TopBar = Add(new TopBar(fonts, menu, show));
        Console = Add(new ConsoleView(fonts, menu, dialogs, show, profiles));
        _status = Add(new Label(fonts.Body, "") { Color = Theme.Muted });
        TopBar.Status += Log;
        TopBar.OpenTriggers += dialogs.Triggers.Show;
        Console.Patch.Status += Log;
        Console.Status += Log;
    }

    /// <summary>
    /// Says <paramref name="message"/> on the status line.
    /// </summary>
    public void Log(string message) => _status.Text = message;

    public string StatusText => _status.Text;

    public void Update()
    {
        TitleBar.Width = ScreenSize.X;
        TitleBar.Update();
        TopBar.Width = ScreenSize.X - 2 * TopBar.Padding;
        TopBar.Update();
        Console.Update();
        _status.MaxWidth = ScreenSize.X - 20;
    }

    public override float2 Measure()
    {
        TitleBar.Measure();
        TopBar.Measure();
        _status.Measure();
        Console.Available = new float2(ScreenSize.X, MathF.Max(100, ScreenSize.Y - TitleBar.HEIGHT - TopBar.Size.Y - STATUS_HEIGHT));
        Console.Measure();
        return Size = ScreenSize;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        TitleBar.Arrange(position);
        TopBar.Arrange(position + new float2(0, TitleBar.HEIGHT));
        Console.Arrange(position + new float2(0, TitleBar.HEIGHT + TopBar.Size.Y));
        _status.Arrange(position + new float2(10, ScreenSize.Y - STATUS_HEIGHT + MathF.Round((STATUS_HEIGHT - _status.Size.Y) / 2)));
    }

    public override void Draw(Batch batch)
    {
        DrawFrame(batch);
        batch.AddRect(ScreenPosition + new float2(0, ScreenSize.Y - STATUS_HEIGHT), new float2(ScreenSize.X, STATUS_HEIGHT), Theme.TitleBar);
        DrawChildren(batch);
    }
}
