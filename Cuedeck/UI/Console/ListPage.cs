using Brutal.Numerics;

namespace Cuedeck.UI.Console;

/// <summary>
/// A tab's page that's a list of things (chases, effects, groups) with buttons over it and the selected one's
/// settings under it. Subclasses build the columns, buttons and editor; this keeps the list in step with what it
/// lists, shows the editor only while something's selected, and loads the editor when the selection moves.
/// </summary>
abstract class ListPage<T> : Panel, IWidthAware
    where T : class
{
    readonly ScrollPanel _scroll;
    List<T> _listed = [];
    T? _shown;

    protected ListPage(Fonts fonts, string title, string emptyText)
    {
        Spacing = 8;
        Add(new Label(fonts.Heading, title) { Color = Theme.Text });
        Buttons = Add(new Flow(6));
        List = new ListView<T>(fonts.Body) { EmptyText = emptyText };
        _scroll = Add(new ScrollPanel { Background = Theme.Well, MaxHeight = 170 });
        _scroll.Add(List);
        List.ScrollOwner = _scroll;
        Editor = Add(new Panel { Spacing = 8, Visible = false });
    }

    /// <summary>
    /// The buttons over the list.
    /// </summary>
    protected Flow Buttons { get; }

    public ListView<T> List { get; }

    /// <summary>
    /// The selected thing's settings, shown while something's selected.
    /// </summary>
    protected Panel Editor { get; }

    /// <summary>
    /// What's listed, as it is now.
    /// </summary>
    protected abstract IReadOnlyList<T> Source { get; }

    public T? Selected => List.SelectedItem;

    public float Width { get; set; } = 360;

    /// <summary>
    /// Something to say on the status line.
    /// </summary>
    public event Action<string>? Status;

    protected void Say(string message) => Status?.Invoke(message);

    /// <summary>
    /// Selects <paramref name="item"/>, once it's listed.
    /// </summary>
    protected void Select(T? item)
    {
        Refresh();
        List.SelectedIndex = item is null ? -1 : _listed.IndexOf(item);
    }

    void Refresh()
    {
        if (_listed.SequenceEqual(Source))
            return;
        _listed = [.. Source];
        List.Items = _listed;
    }

    /// <summary>
    /// Loads <paramref name="item"/>'s settings into the editor, as the selection moves to it.
    /// </summary>
    protected abstract void Load(T item);

    /// <summary>
    /// Brings the editor up to date with what may have changed by itself (running, steps recorded), each frame.
    /// </summary>
    protected virtual void Sync(T item) { }

    public virtual void Update()
    {
        Refresh();
        var selected = Selected;
        Editor.Visible = selected is not null;
        if (selected != _shown)
        {
            _shown = selected;
            if (selected is not null)
                Load(selected);
        }
        if (selected is not null)
            Sync(selected);
    }

    public override float2 Measure()
    {
        Buttons.Width = Width;
        _scroll.MaxWidth = Width;
        _scroll.MinSize = new float2(Width, 120);
        var last = List.Columns[^1];
        last.Width = MathF.Max(60, Width - List.Columns.Take(List.Columns.Count - 1).Sum(c => c.Width) - 12);
        foreach (var flow in Editor.Children.OfType<Flow>())
            flow.Width = Width;
        return base.Measure();
    }
}
