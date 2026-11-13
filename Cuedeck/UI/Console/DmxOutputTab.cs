using System.Globalization;
using Brutal.Numerics;
using Cuedeck.Dmx;
using Cuedeck.Show;

namespace Cuedeck.UI.Console;

/// <summary>
/// A universe's 512 channels as they go out, a cell each in rows of 16: the channel's number small at the top, its
/// value large below, the cell lit by the value, and tinted with its fixture's model colour where one's patched.
/// Drawn as one element, as 512 of them would be a lot to lay out every frame.
/// </summary>
sealed class ChannelGrid(Fonts fonts, LightingShow show, int universe) : Element
{
    public const int COLUMNS = 16;
    const float CELL_W = 46;
    const float CELL_H = 42;
    const float GAP = 4;

    /// <summary>
    /// How much of a fixture's model colour shows through a channel's grey.
    /// </summary>
    const float TINT = 0.5f;

    public Dictionary<int, float4> Tints { get; set; } = [];

    public override float2 Measure() =>
        Size = new float2(COLUMNS * (CELL_W + GAP) - GAP, DmxUniverse.Size / COLUMNS * (CELL_H + GAP) - GAP);

    /// <summary>
    /// A cell's background: grey by its value, with a fixture's colour mixed in, keeping a hint of it at full rather
    /// than washing out to white.
    /// </summary>
    float4 CellColour(int value, float4? tint)
    {
        var t = value / 255f;
        var grey = Colors.FromSrgb(t, t, t);
        if (tint is not { } c)
            return grey;
        var towards = Theme.Mix(c, Colors.White, t * 0.8f);
        return Theme.Mix(grey, towards, TINT);
    }

    public override void Draw(Batch batch)
    {
        if (show.Dmx.GetUniverse(universe) is not { } u)
            return;
        var output = u.Output;
        for (var ch = 0; ch < DmxUniverse.Size; ch++)
        {
            var at = ScreenPosition + new float2(ch % COLUMNS * (CELL_W + GAP), ch / COLUMNS * (CELL_H + GAP));
            var value = output[ch];
            batch.AddRect(at, new float2(CELL_W, CELL_H), CellColour(value, Tints.TryGetValue(ch, out var tint) ? tint : null));
            // Dark text on a bright cell, light on a dark one.
            var ink = value > 150 ? Colors.Hex(0x000000) : Colors.White;
            var number = (ch + 1).ToString(CultureInfo.InvariantCulture);
            var numberSize = fonts.Small.Measure(number);
            batch.AddText(fonts.Small, number, float2.Round(at + new float2((CELL_W - numberSize.X) / 2, 1)), ink * new float4(1, 1, 1, 0.7f));
            var text = value.ToString(CultureInfo.InvariantCulture);
            var size = fonts.Heading.Measure(text);
            batch.AddText(fonts.Heading, text, float2.Round(at + new float2((CELL_W - size.X) / 2, CELL_H - size.Y - 1)), ink);
        }
    }
}

/// <summary>
/// The DMX Output tab: every universe's output as it goes out (after effects, flashes and the grand master; what the
/// 3D view shows), a grid per universe. Read-only.
/// </summary>
sealed class DmxOutputTab : Region
{
    readonly Fonts _fonts;
    readonly LightingShow _show;
    readonly TabView _universes;
    readonly List<ChannelGrid> _grids = [];
    string _patchSignature = "";

    public DmxOutputTab(Fonts fonts, LightingShow show)
    {
        _fonts = fonts;
        _show = show;
        _universes = Add(new TabView(fonts.Body));
    }

    public void Update()
    {
        var count = _show.Dmx.UniverseCount;
        if (_grids.Count != count)
        {
            var selected = _universes.Selected;
            _universes.Clear();
            _grids.Clear();
            for (var u = 0; u < count; u++)
            {
                var grid = new ChannelGrid(_fonts, _show, u);
                _grids.Add(grid);
                _universes.AddTab($"Universe {u + 1}", new ScrollingPage(grid));
            }
            _universes.Selected = Math.Clamp(selected, 0, count - 1);
            _patchSignature = "";
        }
        // Tinted again when the patch changes.
        var signature = string.Join(";", _show.Patch.All.Select(f => $"{f.Universe}/{f.Start}/{f.Mode}/{f.Profile.FullName}"));
        if (signature != _patchSignature)
        {
            _patchSignature = signature;
            var tints = ModelColours.Channels(_show.Patch);
            for (var u = 0; u < _grids.Count; u++)
                _grids[u].Tints = u < tints.Count ? tints[u] : [];
        }
    }

    protected override void MeasureContent()
    {
        _universes.Available = Available - new float2(8, 8);
        _universes.Measure();
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        _universes.Arrange(position + new float2(4, 4));
    }
}
