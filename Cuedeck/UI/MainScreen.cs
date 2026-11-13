using Brutal.Numerics;

namespace Cuedeck.UI;

/// <summary>
/// The fonts the screen is drawn in.
/// </summary>
sealed record Fonts(Font Small, Font Body, Font Bold, Font Heading, Font Big);

/// <summary>
/// The whole window: the title bar across the top and, filling the rest, the console. Everything is sized to the
/// window each frame, so set <see cref="ScreenSize"/> and call <see cref="Update"/> before laying out.
/// </summary>
sealed class MainScreen : Panel
{
    readonly Label _placeholder;

    public float2 ScreenSize { get; set; } = new(1280, 720);

    public TitleBar TitleBar { get; }

    public MainScreen(Fonts fonts, Images images)
    {
        Background = Theme.Background;
        TitleBar = Add(new TitleBar(fonts.Body, fonts.Bold, images));
        _placeholder = Add(new Label(fonts.Heading, "No show loaded") { Color = Theme.Faint });
    }

    public void Update()
    {
        TitleBar.Width = ScreenSize.X;
        TitleBar.Update();
    }

    public override float2 Measure()
    {
        MeasureChildren();
        return Size = ScreenSize;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        TitleBar.Arrange(position);
        var body = new float2(ScreenSize.X, ScreenSize.Y - TitleBar.HEIGHT);
        _placeholder.Arrange(position + new float2(0, TitleBar.HEIGHT) + float2.Round((body - _placeholder.Size) / 2));
    }

    public override void Draw(Batch batch)
    {
        DrawFrame(batch);
        DrawChildren(batch);
    }
}
