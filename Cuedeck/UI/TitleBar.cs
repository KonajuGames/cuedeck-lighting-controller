using Brutal.Numerics;

namespace Cuedeck.UI;

/// <summary>
/// The bar across the top of the window, in place of Windows' own: the logo and the app's name (with the show's), the
/// options button and the window's buttons on the right. Panels for the console's top bar go in <see cref="Centre"/>.
/// Its empty space is where the window can be dragged from (see <see cref="IsCaption"/>). Call <see cref="Update"/>
/// once a frame.
/// </summary>
sealed class TitleBar : Panel
{
    public const float HEIGHT = 34;
    const float GAP = 16;

    readonly Panel _left;
    readonly Panel _right;
    readonly Label _title;
    readonly Label _clock;
    readonly IconButton _maximize;

    public float Width { get; set; } = 1280;

    /// <summary>
    /// Whether the window is maximised, which the middle button shows and toggles.
    /// </summary>
    public Func<bool> IsMaximized { get; set; } = () => false;

    /// <summary>
    /// What follows the app's name: the show's name, with a star while it has unsaved changes.
    /// </summary>
    public string Subtitle { get; set; } = "";

    /// <summary>
    /// The controls in the middle of the bar, centred between the sides, or moved left to keep clear of the right.
    /// </summary>
    public Panel Centre { get; }

    public event Action? OptionsClicked;
    public event Action? MinimizeClicked;
    public event Action? MaximizeClicked;
    public event Action? CloseClicked;

    readonly Picture _logo;
    readonly Panel _name;
    readonly Font _body;
    readonly Font _bold;

    public TitleBar(Font body, Font bold, Images images)
    {
        (_body, _bold) = (body, bold);
        Background = Theme.Panel;

        _left = Add(new Panel { Direction = Direction.Horizontal, Spacing = 10 });
        _logo = _left.Add(new Picture(images.Logo));
        // The wordmark: "Cue" in the accent, "deck" in the text's colour, as one word.
        _name = _left.Add(new Panel { Direction = Direction.Horizontal, Spacing = 0 });
        _name.Add(new Label(bold, "Cue") { Color = Theme.Accent });
        _name.Add(new Label(bold, "deck") { Color = Theme.Text });
        _title = _left.Add(new Label(body) { Color = Theme.Muted });

        Centre = Add(new Panel { Direction = Direction.Horizontal, Spacing = 8 });

        _right = Add(new Panel { Direction = Direction.Horizontal, Spacing = 0 });
        _clock = _right.Add(new Label(body) { Color = Theme.Text });
        _right.Add(new Spacer(12));
        var options = _right.Add(new IconButton(TitleIcon.Picture) { Picture = images.Cog, Tooltip = "Options" });
        options.Clicked += _ => OptionsClicked?.Invoke();
        _right.Add(new Spacer(8));
        var minimize = _right.Add(new IconButton(TitleIcon.Minimize) { Tooltip = "Minimise" });
        minimize.Clicked += _ => MinimizeClicked?.Invoke();
        _maximize = _right.Add(new IconButton(TitleIcon.Maximize));
        _maximize.Clicked += _ => MaximizeClicked?.Invoke();
        var close = _right.Add(
            new IconButton(TitleIcon.Close) { HoverBackground = Colors.Hex(0xE81123), PressedBackground = Colors.Hex(0xF1707A), Tooltip = "Close Cuedeck" }
        );
        close.Clicked += _ => CloseClicked?.Invoke();
        Update();
    }

    /// <summary>
    /// Whether the window can be dragged from <paramref name="point"/>: inside the bar, but not on a button.
    /// </summary>
    public bool IsCaption(float2 point) => Contains(point) && HitTest(point) is null;

    public void Update()
    {
        _title.Text = Subtitle;
        _title.Visible = Subtitle.Length > 0;
        _clock.Text = DateTime.Now.ToString("HH:mm:ss");
        _maximize.Icon = IsMaximized() ? TitleIcon.Restore : TitleIcon.Maximize;
        _maximize.Tooltip = IsMaximized() ? "Restore" : "Maximise";
    }

    public override float2 Measure()
    {
        MeasureChildren();
        return Size = new float2(Width, HEIGHT);
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        float Middle(Element child) => MathF.Round(position.Y + (HEIGHT - child.Size.Y) / 2);
        _left.Arrange(new float2(position.X + 10, Middle(_left)));
        var centreMin = position.X + 10 + _left.Size.X + GAP;
        var centreMax = position.X + Width - _right.Size.X - GAP - Centre.Size.X;
        var centreX = Math.Clamp(position.X + (Width - Centre.Size.X) / 2, centreMin, MathF.Max(centreMin, centreMax));
        Centre.Arrange(new float2(MathF.Round(centreX), Middle(Centre)));
        _right.Arrange(new float2(position.X + Width - _right.Size.X, Middle(_right)));
        AlignMiddle(_left);
        AlignMiddle(Centre);
        AlignMiddle(_right);
        // The name and the show's capitals centred on the logo: centring their lines would drop them, as a line keeps
        // room below for descenders.
        var middle = _logo.ScreenPosition.Y + _logo.Size.Y / 2;
        foreach (var (text, font) in new (Element, Font)[] { (_name, _bold), (_title, _body) })
            text.Arrange(new float2(text.ScreenPosition.X, MathF.Round(middle - font.Ascender + font.CapHeight / 2)));
    }

    /// <summary>
    /// Centres a row's children on its middle, since a row lines them up along the top.
    /// </summary>
    static void AlignMiddle(Panel row)
    {
        foreach (var child in row.Children)
            child.Arrange(new float2(child.ScreenPosition.X, MathF.Round(row.ScreenPosition.Y + (row.Size.Y - child.Size.Y) / 2)));
    }

    public override void Draw(Batch batch)
    {
        DrawFrame(batch);
        batch.AddRect(ScreenPosition + new float2(0, HEIGHT - 1), new float2(Width, 1), Theme.PanelBorder);
        DrawChildren(batch);
    }
}
