using Brutal.Numerics;

namespace Cuedeck.UI.Visualizer;

/// <summary>
/// The 3D Visualizer docked down the right of the window, beside whichever view shows (the console, DJ Edit Mode or
/// DJ Perform Mode), with a strip between them whose button folds it away to the right and brings it back. While
/// the visualizer's in a window of its own the dock is empty and takes no room.
/// </summary>
sealed class VizDock : Region
{
    /// <summary>
    /// How wide the strip between the visualizer and the view is: all the dock takes while folded away.
    /// </summary>
    public const float STRIP = 18;

    const float GAP = 8;

    readonly VisualizerView _view;
    readonly FoldButton _fold;

    public VizDock(VisualizerView view)
    {
        _view = view;
        _fold = Add(new FoldButton());
        _fold.Clicked += _ => Collapsed = !Collapsed;
        Add(view);
        view.IsShown = () => Visible && !Collapsed && Holds;
        Collapsed = false;
    }

    /// <summary>
    /// Whether the visualizer's folded away to the right, leaving just the strip.
    /// </summary>
    public bool Collapsed
    {
        get => _collapsed;
        set
        {
            _collapsed = value;
            _view.Visible = !value;
            _fold.Collapsed = value;
            _fold.Tooltip = value ? "Show the 3D Visualizer" : "Fold the 3D Visualizer away to the right";
        }
    }

    bool _collapsed;

    /// <summary>
    /// Whether the visualizer's here, rather than in a window of its own.
    /// </summary>
    public bool Holds => Children.Contains(_view);

    /// <summary>
    /// The visualizer, here or in a window of its own.
    /// </summary>
    public VisualizerView View => _view;

    /// <summary>
    /// The fold button on the strip.
    /// </summary>
    public Element Fold => _fold;

    /// <summary>
    /// Lets the visualizer go to a window of its own.
    /// </summary>
    public void Release() => Children.Remove(_view);

    /// <summary>
    /// Takes the visualizer back from its window.
    /// </summary>
    public void Take()
    {
        if (!Holds)
            Add(_view);
        _view.Visible = !Collapsed;
    }

    /// <summary>
    /// How much of a window <paramref name="width"/> wide the dock takes: a little over a third, leaving the view
    /// room enough; just the strip while folded away; nothing while the visualizer's in a window of its own.
    /// </summary>
    public float WidthIn(float width) =>
        !Holds ? 0
        : Collapsed ? STRIP
        : MathF.Round(Math.Clamp(width * 0.36f, 320, MathF.Max(320, width - 640)));

    /// <summary>
    /// Each frame, while it shows: the visualizer's rigs and controls kept up.
    /// </summary>
    public void Update()
    {
        if (Holds && !Collapsed)
            _view.Update();
    }

    protected override void MeasureContent()
    {
        _fold.Measure();
        if (Holds && !Collapsed)
        {
            _view.Available = new float2(MathF.Max(1, Available.X - STRIP - GAP), MathF.Max(1, Available.Y - 2 * GAP));
            _view.Measure();
        }
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        _fold.Arrange(position + new float2(MathF.Round((STRIP - _fold.Size.X) / 2), MathF.Round((Size.Y - _fold.Size.Y) / 2)));
        if (Holds && !Collapsed)
            _view.Arrange(position + new float2(STRIP, GAP));
    }

    public override void Draw(Batch batch)
    {
        if (!Holds)
            return;
        // The strip's line down its middle, either side of the button.
        var x = MathF.Round(ScreenPosition.X + STRIP / 2);
        batch.AddRect(new float2(x, ScreenPosition.Y + GAP), new float2(1, Size.Y - 2 * GAP), Theme.PanelBorder);
        DrawChildren(batch);
    }

    /// <summary>
    /// The button on the strip: an arrow pointing the way the visualizer will go.
    /// </summary>
    sealed class FoldButton : Element
    {
        public bool Collapsed { get; set; }

        public override bool IsInteractive => true;

        public override float2 Measure() => Size = new float2(STRIP - 2, 56);

        public override void Draw(Batch batch)
        {
            var back = IsPressed && IsHovered ? Theme.ControlPressed : IsHovered ? Theme.ControlHover : Theme.Control;
            batch.AddRect(ScreenPosition, Size, back);
            batch.AddBorder(ScreenPosition, Size, 1, Theme.PanelBorder);
            // Right to fold it away, left to bring it back.
            var c = ScreenPosition + Size / 2;
            var d = Collapsed ? -1f : 1f;
            batch.AddTriangle(c + new float2(-3 * d, -5), c + new float2(-3 * d, 5), c + new float2(3 * d, 0), IsHovered ? Theme.Text : Theme.Muted);
        }
    }
}
