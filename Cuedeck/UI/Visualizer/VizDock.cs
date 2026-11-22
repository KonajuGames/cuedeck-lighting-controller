using Brutal.Numerics;

namespace Cuedeck.UI.Visualizer;

/// <summary>
/// The 3D Visualizer docked down the right of the window, beside whichever view shows (the console, DJ Edit Mode or
/// DJ Perform Mode), with a strip between them: drag the strip to make the visualizer wider or narrower, or press its
/// button to fold the visualizer away to the right and bring it back. While the visualizer's in a window of its own
/// the dock is empty and takes no room.
/// </summary>
sealed class VizDock : Region
{
    /// <summary>
    /// How wide the strip between the visualizer and the view is: all the dock takes while folded away.
    /// </summary>
    public const float STRIP = 18;

    const float GAP = 8;

    /// <summary>
    /// The narrowest the docked visualizer goes, and the least room it leaves the view beside it.
    /// </summary>
    public const float MIN_WIDTH = 320;
    public const float MIN_VIEW = 640;

    /// <summary>
    /// How much of the window the docked visualizer takes to begin with.
    /// </summary>
    public const float DEFAULT_SHARE = 0.36f;

    readonly VisualizerView _view;
    readonly Grip _grip;
    readonly FoldButton _fold;
    float _windowWidth = 1280;

    public VizDock(VisualizerView view)
    {
        _view = view;
        _grip = Add(new Grip(this));
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
    /// The strip, dragged to resize the visualizer.
    /// </summary>
    public Element Strip => _grip;

    /// <summary>
    /// How much of the window's width the docked visualizer takes, from 0 to 1 (kept within
    /// <see cref="MIN_WIDTH"/> and <see cref="MIN_VIEW"/> as the window's sized).
    /// </summary>
    public float Share { get; set; } = DEFAULT_SHARE;

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
    /// How much of a window <paramref name="width"/> wide the dock takes: its <see cref="Share"/>, leaving the view
    /// room enough; just the strip while folded away; nothing while the visualizer's in a window of its own.
    /// </summary>
    public float WidthIn(float width)
    {
        _windowWidth = width;
        return !Holds ? 0
            : Collapsed ? STRIP
            : Fit(width * Share, width);
    }

    static float Fit(float wanted, float window) => MathF.Round(Math.Clamp(wanted, MIN_WIDTH, MathF.Max(MIN_WIDTH, window - MIN_VIEW)));

    /// <summary>
    /// The strip's dragged to <paramref name="x"/> on screen: the visualizer as wide as the room to its right.
    /// </summary>
    void DragTo(float x)
    {
        var right = ScreenPosition.X + Size.X;
        Share = Fit(right - x, _windowWidth) / MathF.Max(1, _windowWidth);
    }

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
        _grip.Measure();
        if (Holds && !Collapsed)
        {
            _view.Available = new float2(MathF.Max(1, Available.X - STRIP - GAP), MathF.Max(1, Available.Y - 2 * GAP));
            _view.Measure();
        }
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        _grip.Arrange(position);
        _fold.Arrange(position + new float2(MathF.Round((STRIP - _fold.Size.X) / 2), MathF.Round((Size.Y - _fold.Size.Y) / 2)));
        if (Holds && !Collapsed)
            _view.Arrange(position + new float2(STRIP, GAP));
    }

    public override void Draw(Batch batch)
    {
        if (Holds)
            DrawChildren(batch);
    }

    /// <summary>
    /// The strip itself: a line down its middle, lit while the pointer's on it, that drags left and right to resize
    /// the visualizer (while it isn't folded away).
    /// </summary>
    sealed class Grip(VizDock dock) : Element
    {
        float _grabbedAt;

        public override bool IsInteractive => !dock.Collapsed;
        public override Cursor Cursor => Cursor.ResizeHorizontal;

        public override float2 Measure() => Size = new float2(STRIP, dock.Available.Y);

        public override void PointerDown(float2 point, Modifiers modifiers) => _grabbedAt = point.X - ScreenPosition.X;

        public override void PointerDrag(float2 point) => dock.DragTo(point.X - _grabbedAt);

        public override void Draw(Batch batch)
        {
            var lit = !dock.Collapsed && (IsHovered || IsPressed);
            var x = MathF.Round(ScreenPosition.X + STRIP / 2) - (lit ? 1 : 0);
            batch.AddRect(new float2(x, ScreenPosition.Y + GAP), new float2(lit ? 3 : 1, Size.Y - 2 * GAP), lit ? Theme.Accent : Theme.PanelBorder);
        }
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
