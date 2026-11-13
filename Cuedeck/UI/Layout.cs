using Brutal.Numerics;

namespace Cuedeck.UI;

/// <summary>
/// Children side by side, each centred on the row's middle (a <see cref="Panel"/> lines them up along the top), so
/// labels sit level with the boxes and buttons beside them.
/// </summary>
class Row : Panel
{
    public Row(float spacing = 6)
    {
        Direction = Direction.Horizontal;
        Spacing = spacing;
    }

    public override void Arrange(float2 position)
    {
        base.Arrange(position);
        var inner = Size.Y - 2 * Inset;
        foreach (var child in Children)
            if (child.Visible)
                child.Arrange(new float2(child.ScreenPosition.X, MathF.Round(ScreenPosition.Y + Inset + (inner - child.Size.Y) / 2)));
    }
}

/// <summary>
/// Children in rows that wrap: as many side by side as fit in <see cref="Width"/>, then onto the next line, as
/// sLight's flow containers did with a toolbar's buttons.
/// </summary>
sealed class Flow : Panel
{
    public float Width { get; set; } = 400;
    public float LineSpacing { get; set; } = 6;

    public Flow(float spacing = 6)
    {
        Direction = Direction.Horizontal;
        Spacing = spacing;
    }

    IEnumerable<List<Element>> Lines()
    {
        var line = new List<Element>();
        var x = 0f;
        foreach (var child in Children)
        {
            if (!child.Visible)
                continue;
            var w = child.Size.X;
            if (line.Count > 0 && x + Spacing + w > Width - 2 * Inset)
            {
                yield return line;
                line = [];
                x = 0;
            }
            x += (line.Count > 0 ? Spacing : 0) + w;
            line.Add(child);
        }
        if (line.Count > 0)
            yield return line;
    }

    public override float2 Measure()
    {
        foreach (var child in Children)
            if (child.Visible)
                child.Measure();
        var height = 0f;
        var first = true;
        foreach (var line in Lines())
        {
            height += (first ? 0 : LineSpacing) + line.Max(c => c.Size.Y);
            first = false;
        }
        return Size = float2.Max(new float2(Width, height + 2 * Inset), MinSize);
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        var y = position.Y + Inset;
        foreach (var line in Lines())
        {
            var lineHeight = line.Max(c => c.Size.Y);
            var x = position.X + Inset;
            foreach (var child in line)
            {
                child.Arrange(new float2(x, MathF.Round(y + (lineHeight - child.Size.Y) / 2)));
                x += child.Size.X + Spacing;
            }
            y += lineHeight + LineSpacing;
        }
    }
}

/// <summary>
/// Something that fills the room it's given, rather than sizing to its contents: a tab's page, a split of the
/// screen. Whoever holds it sets <see cref="Available"/> before measuring it.
/// </summary>
abstract class Region : Panel
{
    public float2 Available { get; set; } = new(400, 300);

    public override float2 Measure()
    {
        MeasureContent();
        return Size = Available;
    }

    /// <summary>
    /// Measures the children for <see cref="Available"/>.
    /// </summary>
    protected abstract void MeasureContent();
}

/// <summary>
/// A page that fills its room with one element scrolling inside it: most tabs' pages.
/// </summary>
sealed class ScrollingPage : Region
{
    readonly ScrollPanel _scroll;

    public ScrollingPage(Element content, float padding = 8)
    {
        _scroll = Add(new ScrollPanel { Padding = padding });
        _scroll.Add(content);
        Content = content;
    }

    public Element Content { get; }

    protected override void MeasureContent()
    {
        _scroll.MaxWidth = Available.X;
        _scroll.MaxHeight = Available.Y;
        _scroll.MinSize = Available;
        if (Content is Flow flow)
            flow.Width = Available.X - 2 * _scroll.Padding - 12;
        if (Content is IWidthAware aware)
            aware.Width = Available.X - 2 * _scroll.Padding - 12;
        _scroll.Measure();
    }
}

/// <summary>
/// Something that lays itself out to the width it's given, as a page's content.
/// </summary>
interface IWidthAware
{
    float Width { get; set; }
}

/// <summary>
/// A message for a page with nothing on it yet.
/// </summary>
sealed class Placeholder : Region
{
    readonly Label _label;

    public Placeholder(Font font, string text)
    {
        _label = Add(new Label(font, text) { Color = Theme.Faint, MaxWidth = 420, Wrap = true });
    }

    protected override void MeasureContent() => _label.Measure();

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        _label.Arrange(position + float2.Round((Available - _label.Size) / 2));
    }
}

/// <summary>
/// A thin line across, between sections.
/// </summary>
sealed class Divider(float length, Direction direction = Direction.Horizontal) : Element
{
    public float Length { get; set; } = length;

    public override float2 Measure() => Size = direction == Direction.Horizontal ? new float2(Length, 9) : new float2(9, Length);

    public override void Draw(Batch batch)
    {
        if (direction == Direction.Horizontal)
            batch.AddRect(ScreenPosition + new float2(0, 4), new float2(Size.X, 1), Theme.PanelBorder);
        else
            batch.AddRect(ScreenPosition + new float2(4, 0), new float2(1, Size.Y), Theme.PanelBorder);
    }
}

/// <summary>
/// An element given a set width, so the controls after it line up in a column.
/// </summary>
sealed class FixedWidth(Element inner, float width) : Element
{
    public Element Inner { get; } = inner;

    public override float2 Measure()
    {
        var size = Inner.Measure();
        return Size = new float2(width, size.Y);
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        Inner.Arrange(position);
    }

    public override void Draw(Batch batch) => Inner.Draw(batch);

    public override Element? HitTest(float2 point) => Inner.HitTest(point);
}

/// <summary>
/// Settings in two columns: each a name, then its control, the controls lined up.
/// </summary>
sealed class FieldGrid(Font font, float nameWidth = 104) : Panel
{
    /// <summary>
    /// Adds a row called <paramref name="name"/> holding <paramref name="control"/>, and returns the row, for more
    /// controls after it.
    /// </summary>
    public Row Field(string name, Element control)
    {
        var row = Add(new Row(8));
        row.Add(new FixedWidth(new Label(font, name) { Color = Theme.Muted, MaxWidth = nameWidth }, nameWidth));
        row.Add(control);
        return row;
    }

    public FieldGrid Init(float spacing = 6)
    {
        Spacing = spacing;
        return this;
    }
}
