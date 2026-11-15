using Brutal.Numerics;

namespace Cuedeck.UI;

/// <summary>
/// Pages behind a strip of tabs: clicking a tab shows its page. Fills the room it's given, the strip across the top
/// and the page below it; a page that's a <see cref="Region"/> fills the rest.
/// </summary>
sealed class TabView : Region
{
    public const float StripHeight = 30;

    readonly Font _font;
    readonly Row _strip;
    readonly List<(Button Tab, Element Page)> _tabs = [];
    readonly Panel _pageFrame;
    int _selected = -1;

    public TabView(Font font)
    {
        _font = font;
        _strip = Add(new Row(2));
        _pageFrame = Add(new Panel { Background = Theme.Panel, BorderColor = Theme.PanelBorder, BorderWidth = 1 });
    }

    /// <summary>
    /// Another tab was chosen; its index.
    /// </summary>
    public event Action<int>? Changed;

    public int Count => _tabs.Count;

    public int Selected
    {
        get => _selected;
        set
        {
            value = _tabs.Count == 0 ? -1 : Math.Clamp(value, 0, _tabs.Count - 1);
            if (value == _selected)
                return;
            _selected = value;
            for (var i = 0; i < _tabs.Count; i++)
            {
                _tabs[i].Tab.Lit = i == _selected;
                _tabs[i].Page.Visible = i == _selected;
            }
            Changed?.Invoke(_selected);
        }
    }

    public Element? SelectedPage => _selected >= 0 ? _tabs[_selected].Page : null;

    /// <summary>
    /// Adds a page behind a tab called <paramref name="title"/>; the first one added shows.
    /// </summary>
    public Button AddTab(string title, Element page)
    {
        var tab = _strip.Add(
            new Button(_font, title)
            {
                Padding = 7,
                Background = Theme.PanelRaised,
                HoverBackground = Colors.Hex(0x31323C),
                LitBackground = Theme.Panel,
                TextColor = Theme.Text,
            }
        );
        var index = _tabs.Count;
        tab.Clicked += _ => Selected = index;
        page.Visible = false;
        _pageFrame.Add(page);
        _tabs.Add((tab, page));
        if (_selected < 0)
            Selected = 0;
        else
            tab.TextColor = Theme.Muted;
        return tab;
    }

    /// <summary>
    /// Removes every tab and page.
    /// </summary>
    public void Clear()
    {
        _tabs.Clear();
        _strip.Children.Clear();
        _pageFrame.Children.Clear();
        _selected = -1;
    }

    public void SetTitle(int index, string title) => _tabs[index].Tab.Text = title;

    /// <summary>
    /// Puts <paramref name="page"/> behind tab <paramref name="index"/> in place of its page.
    /// </summary>
    public void SetPage(int index, Element page)
    {
        var old = _tabs[index].Page;
        var at = _pageFrame.Children.IndexOf(old);
        _pageFrame.Children[at] = page;
        page.Visible = index == _selected;
        _tabs[index] = (_tabs[index].Tab, page);
    }

    /// <summary>
    /// The tab whose page is <paramref name="page"/>, or -1.
    /// </summary>
    public int IndexOf(Element page) => _tabs.FindIndex(t => t.Page == page);

    protected override void MeasureContent()
    {
        for (var i = 0; i < _tabs.Count; i++)
            _tabs[i].Tab.TextColor = i == _selected ? Theme.Text : Theme.Muted;
        _strip.Measure();
        var pageSize = new float2(Available.X, MathF.Max(0, Available.Y - StripHeight));
        _pageFrame.MinSize = pageSize;
        foreach (var (_, page) in _tabs)
            if (page is Region region)
                region.Available = pageSize - new float2(2, 2);
        _pageFrame.Measure();
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        _strip.Arrange(position + new float2(0, StripHeight - _strip.Size.Y));
        _pageFrame.Arrange(position + new float2(0, StripHeight));
        if (SelectedPage is { } page)
            page.Arrange(_pageFrame.ScreenPosition + new float2(1, 1));
    }

    public override void Draw(Batch batch)
    {
        _pageFrame.Draw(batch);
        // The strip after the frame, so the tab that's showing covers the frame's top edge and joins its page.
        _strip.Draw(batch);
    }
}
