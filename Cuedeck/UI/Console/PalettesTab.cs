using Brutal.Numerics;
using Cuedeck.Common;
using Cuedeck.Show;

namespace Cuedeck.UI.Console;

/// <summary>
/// The dialogs the console's tabs ask things in.
/// </summary>
sealed record Dialogs(ConfirmDialog Confirm, NamePrompt Prompt);

/// <summary>
/// The Palettes tab: the show's colour, position and beam palettes as a grid of buttons each, like a desk's palette
/// windows. Record makes one from the live levels of the fixtures picked (all, or a group); clicking one applies it to
/// them, and a cue recorded then uses it. Right-click one to update it from the live levels (every cue using it
/// changes), rename or delete it.
/// </summary>
sealed class PalettesTab : Panel, IWidthAware
{
    readonly Fonts _fonts;
    readonly ContextMenu _menu;
    readonly Dialogs _dialogs;
    readonly LightingShow _show;
    readonly Dropdown<string> _fixtures;
    readonly Flow[] _grids = new Flow[3];
    readonly Label _hint;
    string _shown = "";

    public event Action<string>? Status;

    public PalettesTab(Fonts fonts, ContextMenu menu, Dialogs dialogs, LightingShow show)
    {
        _fonts = fonts;
        _menu = menu;
        _dialogs = dialogs;
        _show = show;
        Spacing = 10;
        Add(new Label(fonts.Heading, "Palettes") { Color = Theme.Text });
        var pick = Add(new Row(8));
        pick.Add(new Label(fonts.Body, "Fixtures:") { Color = Theme.Muted });
        _fixtures = pick.Add(
            new Dropdown<string>(fonts.Body)
            {
                Menu = menu,
                Width = 200,
                Items = () => ["", .. show.Groups.Select(g => g.Name)],
                Text = g => g == "" ? "All fixtures" : g,
                Selected = "",
                Tooltip = "Which fixtures Record captures and a palette is applied to",
            }
        );
        for (var kind = 0; kind < PaletteLibrary.Kinds.Length; kind++)
        {
            var k = (PaletteKind)kind;
            var header = Add(new Row(10));
            header.Add(new Label(fonts.Bold, PaletteLibrary.Kinds[kind]) { Color = Theme.Text });
            var record = header.Add(
                new Button(fonts.Body, $"Record {PaletteLibrary.Kinds[kind]}")
                {
                    Tooltip = $"A new {PaletteLibrary.Kinds[kind].ToLowerInvariant()} palette from the fixtures' live {string.Join(", ", PaletteLibrary.KindRoles[kind].Select(r => r.ToLowerInvariant().Replace('_', ' ')))} channels",
                }
            );
            record.Clicked += _ => Record(k);
            _grids[kind] = Add(new Flow(6));
        }
        _hint = Add(
            new Label(fonts.Small, "Set the fixtures the way you want, then Record. Click a palette to apply it; record a cue and the cue uses the palette, so updating the palette changes the cue. Right-click a palette to update, rename or delete it.")
            {
                Color = Theme.Faint,
                Wrap = true,
            }
        );
    }

    public float Width { get; set; } = 600;

    /// <summary>
    /// The fixtures Fixtures picks: all of them, or a group's.
    /// </summary>
    public List<PatchedFixture> Picked()
    {
        var all = _show.Patch.All.ToList();
        return _fixtures.Selected == "" || _show.GroupMembers(_fixtures.Selected) is not { } members ? all : [.. all.Where(f => members.Contains(f.Uid))];
    }

    List<byte[]> Bases => [.. _show.Dmx.Universes.Select(u => u.Data)];

    public Palette? Record(PaletteKind kind)
    {
        var n = _show.Palettes.OfKind(kind).Count() + 1;
        var p = _show.Palettes.Record(kind, $"{PaletteLibrary.Kinds[(int)kind]} {n}", Picked(), Bases);
        Status?.Invoke(
            p is null
                ? $"None of those fixtures has {PaletteLibrary.Kinds[(int)kind].ToLowerInvariant()} channels."
                : $"Recorded {p.Name} for {p.Values.Count} fixture{(p.Values.Count == 1 ? "" : "s")}."
        );
        return p;
    }

    public void Apply(Palette p)
    {
        var fixtures = Picked();
        _show.LoadLook(_show.Palettes.Applied(p, fixtures, Bases));
        var n = fixtures.Count(f => p.Values.ContainsKey(f.Uid));
        Status?.Invoke(n > 0 ? $"{p.Name} applied to {n} fixture{(n == 1 ? "" : "s")}." : $"{p.Name} holds none of those fixtures.");
    }

    public void UpdateFromLive(Palette p)
    {
        _show.Palettes.Update(p, Picked(), Bases);
        var users = _show.Cues.PaletteUsers(p.Id);
        Status?.Invoke($"Updated {p.Name} from the live levels — {users} cue{(users == 1 ? " uses" : "s use")} it.");
    }

    /// <summary>
    /// Deletes <paramref name="p"/>; the cues using it keep its values.
    /// </summary>
    public void Delete(Palette p)
    {
        var n = _show.Cues.BakePalette(p.Id);
        _show.Palettes.Remove(p);
        Status?.Invoke($"Deleted {p.Name}{(n > 0 ? $" — its values are now in the {n} cue{(n == 1 ? "" : "s")} that used it" : "")}.");
    }

    void ShowMenu(Palette p, float2 at) =>
        _menu.Show(
            at,
            [
                new MenuItem("Update from Live", () => UpdateFromLive(p)),
                new MenuItem("Rename…", () => _dialogs.Prompt.Show("Rename Palette", p.Name, name => name.Trim() == "" ? "A palette needs a name." : null, name => _show.Palettes.Rename(p, name.Trim()))),
                new MenuItem(
                    "Delete…",
                    () =>
                    {
                        var users = _show.Cues.PaletteUsers(p.Id);
                        _dialogs.Confirm.Show(
                            $"Delete {p.Name}?",
                            users > 0 ? $"{users} cue{(users == 1 ? " uses" : "s use")} it; they'll keep its values." : "No cues use it.",
                            "Delete",
                            () => Delete(p)
                        );
                    }
                ),
            ]
        );

    /// <summary>
    /// A palette's button: in its colour, for a colour palette.
    /// </summary>
    Button ButtonFor(Palette p, List<PatchedFixture> fixtures)
    {
        var b = new Button(_fonts.Body, p.Name)
        {
            MinSize = new float2(96, 34),
            Tooltip = $"{p.Name} — click to apply, right-click to update, rename or delete. {p.Values.Count} fixture{(p.Values.Count == 1 ? "" : "s")}.",
        };
        if (PaletteLibrary.Swatch(p, fixtures) is { } c)
        {
            b.Background = Colors.FromSrgb(c.R * 0.85f, c.G * 0.85f, c.B * 0.85f);
            b.HoverBackground = Colors.FromSrgb(c.R, c.G, c.B);
            b.PressedBackground = b.HoverBackground;
            var luminance = 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;
            b.TextColor = luminance > 0.55f ? Colors.Hex(0x000000) : Colors.White;
        }
        b.Clicked += _ => Apply(p);
        b.SecondaryClicked += button => ShowMenu(p, button.ScreenPosition + new float2(0, button.Size.Y));
        return b;
    }

    public void Update()
    {
        // Rebuilt when the palettes, or the fixtures their swatches come from, change.
        var fixtures = _show.Patch.All.ToList();
        var signature = Json.Stringify(_show.Palettes.ToJson()) + string.Join(",", fixtures.Select(f => f.Uid));
        if (signature == _shown)
            return;
        _shown = signature;
        for (var kind = 0; kind < _grids.Length; kind++)
        {
            var grid = _grids[kind];
            grid.Children.Clear();
            var list = _show.Palettes.OfKind((PaletteKind)kind).ToList();
            if (list.Count == 0)
                grid.Add(new Label(_fonts.Body, "none yet") { Color = Theme.Faint });
            foreach (var p in list)
                grid.Add(ButtonFor(p, fixtures));
        }
    }

    public override float2 Measure()
    {
        foreach (var grid in _grids)
            grid.Width = Width;
        _hint.MaxWidth = Width;
        return base.Measure();
    }
}
