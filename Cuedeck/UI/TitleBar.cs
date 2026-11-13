using Brutal.Numerics;

namespace Cuedeck.UI;

enum TitleIcon
{
    /// <summary>
    /// A picture, the button's <see cref="IconButton.Picture"/>: the options button's cog.
    /// </summary>
    Picture,
    Minimize,
    Maximize,
    Restore,
    Close,
}

/// <summary>
/// A flat button with an icon, for the title bar: the options' cog and the window's own buttons.
/// </summary>
sealed class IconButton(TitleIcon icon) : Element
{
    public TitleIcon Icon { get; set; } = icon;

    /// <summary>
    /// The picture for <see cref="TitleIcon.Picture"/>, white for tinting with <see cref="IconColor"/>.
    /// </summary>
    public Image? Picture { get; init; }
    public float2 FixedSize { get; set; } = new(46, 34);
    public float4 IconColor { get; set; } = Theme.Text;
    public float4 HoverBackground { get; set; } = Colors.Hex(0xFFFFFF, 0.1f);
    public float4 PressedBackground { get; set; } = Colors.Hex(0xFFFFFF, 0.05f);

    public override bool IsInteractive => true;
    public override Cursor Cursor => Cursor.Arrow;

    public override float2 Measure() => Size = FixedSize;

    public override void Draw(Batch batch)
    {
        if (IsHovered || IsPressed)
            batch.AddRect(ScreenPosition, Size, IsPressed && IsHovered ? PressedBackground : HoverBackground);

        // A 10 pixel icon, on whole pixels so its lines stay crisp.
        var x = MathF.Round(ScreenPosition.X + Size.X / 2 - 5);
        var y = MathF.Round(ScreenPosition.Y + Size.Y / 2 - 5);
        switch (Icon)
        {
            case TitleIcon.Picture when Picture is { } picture:
                batch.AddImage(picture, ScreenPosition + (Size - new float2(picture.Size.X, picture.Size.Y)) / 2, IconColor);
                break;
            case TitleIcon.Minimize:
                batch.AddRect(new float2(x, y + 5), new float2(10, 1), IconColor);
                break;
            case TitleIcon.Maximize:
                batch.AddBorder(new float2(x, y), new float2(10, 10), 1, IconColor);
                break;
            case TitleIcon.Restore:
                batch.AddBorder(new float2(x, y + 2), new float2(8, 8), 1, IconColor);
                batch.AddRect(new float2(x + 2, y), new float2(8, 1), IconColor);
                batch.AddRect(new float2(x + 9, y), new float2(1, 8), IconColor);
                break;
            case TitleIcon.Close:
                for (var i = 0; i < 10; i++)
                {
                    batch.AddRect(new float2(x + i, y + i), new float2(1, 1), IconColor);
                    batch.AddRect(new float2(x + 9 - i, y + i), new float2(1, 1), IconColor);
                }
                break;
        }
    }
}

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

    public TitleBar(Font body, Font bold, Images images)
    {
        Background = Theme.Panel;

        _left = Add(new Panel { Direction = Direction.Horizontal, Spacing = 10 });
        _left.Add(new Picture(images.Logo));
        _left.Add(new Label(bold, "Cuedeck") { Color = Theme.Text });
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

/// <summary>
/// Empty space of a set width (or height, in a column), to push things apart in a row.
/// </summary>
sealed class Spacer(float length) : Element
{
    public override float2 Measure() => Size = new float2(length, length);

    public override void Draw(Batch batch) { }
}
