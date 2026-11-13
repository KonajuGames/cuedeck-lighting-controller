using System.Numerics;
using Cuedeck.Common;
using Cuedeck.Show;

namespace Cuedeck.Tests.Show;

/// <summary>
/// sLight's position_test.gd: the position library, and positions saved with the show. (The position editor, its
/// icon and its highlighting come with DJ Edit Mode.)
/// </summary>
public class PositionTests
{
    [Fact]
    public void The_library()
    {
        var lib = new PositionLibrary();
        var changes = 0;
        lib.Changed += () => changes++;
        var a = lib.Add();
        var b = lib.Add();
        var c = lib.Add();
        Assert.Equal(("Position 1", "Position 2"), (a.Name, b.Name));
        Assert.Equal(3, lib.Positions.Count);
        Assert.Equal(3, new[] { a.Colour, b.Colour, c.Colour }.Distinct().Count()); // each its own colour
        Assert.True(Math.Abs(a.Colour.Hue - b.Colour.Hue) > 0.1 && Math.Abs(b.Colour.Hue - c.Colour.Hue) > 0.1); // well apart
        Assert.NotEqual(a.Id, b.Id);

        lib.Rename(a.Id, "  Drop  ");
        Assert.Equal("Drop", lib.Find(a.Id)!.Name); // trimmed
        lib.Rename(a.Id, "   ");
        Assert.Equal("Drop", lib.Find(a.Id)!.Name); // a blank name is ignored
        Assert.Equal(PositionLibrary.DefaultPoint, lib.Point(a.Id, "fx1"));
        lib.SetPoint(a.Id, "fx1", new Vector2(0.25f, 1.4f));
        Assert.Equal(new Vector2(0.25f, 1.0f), lib.Point(a.Id, "fx1")); // kept inside 0..1
        Assert.True(changes >= 5);

        var lib2 = new PositionLibrary();
        lib2.FromJson(Json.Parse(Json.Stringify(lib.ToJson())));
        Assert.Equal(3, lib2.Positions.Count);
        Assert.Equal("Drop", lib2.Find(a.Id)!.Name);
        Assert.Equal(new Vector2(0.25f, 1.0f), lib2.Point(a.Id, "fx1"));
        Assert.Equal(b.Colour.ToHtml(), lib2.Find(b.Id)!.Colour.ToHtml());

        var d = lib2.Add();
        Assert.Equal("Position 4", d.Name); // a loaded library carries on numbering
        Assert.NotEqual(c.Colour, d.Colour);
        lib2.Clear();
        Assert.Empty(lib2.Positions);
    }

    [Fact]
    public void Duplicating_and_clearing_points()
    {
        var lib = new PositionLibrary();
        var a = lib.Add();
        lib.Add();
        lib.SetPoint(a.Id, "fx", new Vector2(0.1f, 0.2f));
        var copy = lib.Duplicate(a.Id)!;
        Assert.Equal("Position 1 copy", copy.Name);
        Assert.Equal(copy, lib.Positions[1]); // right after it
        Assert.Equal(new Vector2(0.1f, 0.2f), lib.Point(copy.Id, "fx"));
        lib.ClearPoint(copy.Id, "fx");
        Assert.Equal(PositionLibrary.DefaultPoint, lib.Point(copy.Id, "fx"));
        Assert.Equal(new Vector2(0.1f, 0.2f), lib.Point(a.Id, "fx")); // the original keeps its own
        lib.Remove(a.Id);
        Assert.Null(lib.Find(a.Id));
    }

    [Fact]
    public void Positions_are_saved_with_the_show()
    {
        using var dir = new TempFolder();
        using var show = new LightingShow();
        var session = new ShowSession(show) { LastShowStore = dir.File("last.json") };
        var mover = show.Patch.Add(0, Profiles.Simple("mh", "Mover", ("Pan", "PAN"), ("Tilt", "TILT"), ("Dimmer", "DIMMER")), 0, name: "Spot");
        var p = show.Positions.Add();
        show.Positions.Rename(p.Id, "Stage");
        show.Positions.SetPoint(p.Id, mover.Uid, new Vector2(0.3f, 0.6f));
        var path = dir.File("positions.slight");
        session.Save(path);
        show.Positions.Clear();
        session.Load(path);
        Assert.Equal("Stage", show.Positions.Positions.Single().Name);
        Assert.True(Vector2.Distance(new Vector2(0.3f, 0.6f), show.Positions.Point(p.Id, mover.Uid)) < 0.0001f);
        session.New();
        Assert.Empty(show.Positions.Positions);
    }
}
