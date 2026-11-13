using Cuedeck.Show;

namespace Cuedeck.UI.Console;

/// <summary>
/// The Groups tab: named sets of patched fixtures, for effects, reactors and group masters to work on, and a
/// checklist of every patched fixture to say which are in the selected group.
/// </summary>
sealed class GroupsTab : ListPage<FixtureGroup>
{
    readonly LightingShow _show;
    readonly Fonts _fonts;
    readonly TextBox _name;
    readonly Panel _members;
    readonly Label _count;
    List<PatchedFixture> _listedFixtures = [];
    FixtureGroup? _membersOf;

    protected override IReadOnlyList<FixtureGroup> Source => _show.Groups;

    public GroupsTab(Fonts fonts, LightingShow show)
        : base(fonts, "Fixture Groups", "No groups yet: New Group, then tick its fixtures.")
    {
        _show = show;
        _fonts = fonts;
        List.Columns.Add(new ListColumn<FixtureGroup>("Name", 180, g => g.Name) { Resizable = false });
        List.Columns.Add(new ListColumn<FixtureGroup>("Fixtures", 100, g => $"{g.Members.Count} fixture{(g.Members.Count == 1 ? "" : "s")}") { Color = _ => Theme.Muted, Resizable = false });

        var add = Buttons.Add(new Button(fonts.Body, "New Group"));
        add.Clicked += _ =>
        {
            var g = new FixtureGroup { Name = $"Group {show.Groups.Count + 1}" };
            show.Groups.Add(g);
            Select(g);
        };
        var delete = Buttons.Add(new Button(fonts.Body, "Delete Group"));
        delete.Clicked += _ =>
        {
            if (Selected is not { } g)
                return;
            var i = show.Groups.IndexOf(g);
            show.Groups.Remove(g);
            show.Retarget();
            Select(show.Groups.Count > 0 ? show.Groups[Math.Min(i, show.Groups.Count - 1)] : null);
        };

        var fields = Editor.Add(new FieldGrid(fonts.Body).Init());
        _name = new TextBox(fonts.Body) { Width = 200 };
        fields.Field("Name", _name);
        _name.TextChanged += _ => Rename();

        _count = Editor.Add(new Label(fonts.Body) { Color = Theme.Muted });
        _members = Editor.Add(new Panel { Spacing = 4 });
    }

    /// <summary>
    /// Renames the selected group to what's typed, and the effects and reactors on it with it.
    /// </summary>
    void Rename()
    {
        if (Selected is not { } g)
            return;
        var name = _name.Text.Trim();
        if (name == "" || name == g.Name)
            return;
        var old = g.Name;
        g.Name = name;
        foreach (var e in _show.Effects.Effects.Where(e => e.Group == old))
            e.Group = name;
        foreach (var r in _show.Effects.Reactors.Where(r => r.Group == old))
            r.Group = name;
        foreach (var f in _show.Faders.Faders.Where(f => f.Kind == FaderKind.Group && f.Target.Trim().Equals(old, StringComparison.OrdinalIgnoreCase)))
            f.Target = name;
    }

    protected override void Load(FixtureGroup g) => _name.Text = g.Name;

    protected override void Sync(FixtureGroup g)
    {
        // Members whose fixture has gone leave the group.
        var patched = _show.Patch.All.ToList();
        var uids = patched.Select(f => f.Uid).ToHashSet();
        g.Members.RemoveAll(m => !uids.Contains(m));

        if (_membersOf != g || !_listedFixtures.SequenceEqual(patched))
        {
            _membersOf = g;
            _listedFixtures = patched;
            _members.Children.Clear();
            foreach (var fixture in patched)
            {
                var f = fixture;
                var box = _members.Add(
                    new Checkbox(_fonts.Body, $"U{f.Universe + 1} · {f.Name} (ch {f.Start + 1}-{f.Start + f.Channels.Count})") { Checked = g.Members.Contains(f.Uid) }
                );
                box.Changed += b =>
                {
                    if (b.Checked && !g.Members.Contains(f.Uid))
                        g.Members.Add(f.Uid);
                    else if (!b.Checked)
                        g.Members.Remove(f.Uid);
                    _show.Retarget();
                };
            }
        }
        foreach (var box in _members.Children.OfType<Checkbox>().Zip(_listedFixtures))
            box.First.Checked = g.Members.Contains(box.Second.Uid);
        _count.Text = patched.Count == 0 ? "Patch some fixtures first." : $"{g.Members.Count} of {patched.Count} fixtures in '{g.Name}':";
    }
}
