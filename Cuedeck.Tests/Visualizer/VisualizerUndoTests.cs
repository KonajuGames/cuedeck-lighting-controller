using System.Numerics;
using Brutal.Numerics;
using Cuedeck.Show;
using Cuedeck.Visualizer;

namespace Cuedeck.Tests.Visualizer;

/// <summary>
/// Undoing moves in the 3D Visualizer (sLight's tests/visualizer_undo_test.gd): dragging a fixture, typing its
/// position or turn, Auto-arrange, trusses. Its Ctrl+Z / Ctrl+Y and Enter-in-a-field checks come with the screen.
/// </summary>
public sealed class VisualizerUndoTests : IDisposable
{
    readonly LightingShow _show = new();
    readonly VizScene _viz;
    readonly PatchedFixture _a;
    readonly PatchedFixture _b;

    public VisualizerUndoTests()
    {
        var profile = Profiles.Simple("par", "Par", ("Dimmer", "DIMMER"));
        _a = _show.Patch.Add(0, profile, 0, name: "A", position: new Vector3(1, 4, -2), rotation: new Vector3(90, 0, 0));
        _b = _show.Patch.Add(0, profile, 1, name: "B", position: new Vector3(-1, 4, -2), rotation: new Vector3(90, 0, 0));
        _viz = new VizScene(_show.Patch);
    }

    public void Dispose() => _show.Dispose();

    static void Near(Vector3 expected, Vector3 actual) => Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"expected {expected}, got {actual}");

    /// <summary>
    /// The properties panel's boxes changed: the selected item's state with <paramref name="change"/> made.
    /// </summary>
    void Type(Func<ItemState, ItemState> change) => _viz.EditSelected(change(_viz.StateOf(_viz.Selected!)!));

    [Fact]
    public void Undoing_moves()
    {
        Assert.False(_viz.CanUndo || _viz.CanRedo); // nothing to undo at first

        // Typing a position, then a tilt.
        _viz.Select(_a);
        Type(s => s with { Position = s.Position with { X = 3 } });
        Near(new Vector3(3, 4, -2), _a.Position);
        Assert.True(_viz.CanUndo);
        Type(s => s with { Tilt = 45 });
        Near(new Vector3(45, 0, 0), _a.Rotation);
        _viz.Undo();
        Near(new Vector3(90, 0, 0), _a.Rotation); // the tilt back, the move kept
        Near(new Vector3(3, 4, -2), _a.Position);
        Assert.Equal(90, _viz.StateOf(_a)!.Tilt); // what the boxes show
        _viz.Undo();
        Near(new Vector3(1, 4, -2), _a.Position); // back where it was
        Assert.False(_viz.CanUndo);
        Assert.True(_viz.CanRedo);
        _viz.Redo();
        Near(new Vector3(3, 4, -2), _a.Position);
        _viz.Redo();
        Near(new Vector3(45, 0, 0), _a.Rotation);
        Assert.False(_viz.CanRedo);

        // Dragging: the whole drag one step, on the fixture's own height.
        _viz.Undo();
        _viz.Undo();
        var size = new float2(900, 600);
        _viz.BeginMove(_a);
        for (var k = 0; k < 5; k++)
            _viz.DragTo(new float2(450 + 40 * (k + 1), 300 + 10 * k), size);
        _viz.EndDrag();
        var dragged = _a.Position;
        Assert.True(Vector3.Distance(dragged, new Vector3(1, 4, -2)) > 0.01f && Math.Abs(dragged.Y - 4) < 1e-5, $"dragged to {dragged}");
        Assert.False(_viz.CanRedo); // a new move clears redo
        _viz.Undo();
        Near(new Vector3(1, 4, -2), _a.Position); // one undo takes back the whole drag
        _viz.Redo();
        Near(dragged, _a.Position);
        var steps = _viz.UndoCount;
        _viz.BeginMove(_a);
        _viz.EndDrag();
        Assert.Equal(steps, _viz.UndoCount); // a click that doesn't move it: no step

        // Auto-arrange: every fixture, one step.
        var beforeB = _b.Position;
        _viz.AutoArrange();
        Assert.True(Vector3.Distance(_a.Position, dragged) > 0.01f || Vector3.Distance(_b.Position, beforeB) > 0.01f);
        _viz.Undo();
        Near(dragged, _a.Position);
        Near(beforeB, _b.Position);

        // Trusses.
        var truss = _viz.AddTruss();
        Assert.Same(truss, _viz.Selected);
        var tpos = truss.Position;
        Type(s => s with { Size = 6 });
        Type(s => s with { Position = s.Position with { Z = tpos.Z - 2 } });
        _viz.Undo();
        Assert.Equal(tpos.Z, truss.Position.Z, 5); // its move undone, its length kept
        Assert.Equal(6, truss.Length);
        _viz.Undo();
        Assert.Equal(4, truss.Length); // ...then its length
        _viz.DeleteSelected();
        Assert.DoesNotContain(truss, _viz.Trusses);
        Assert.True(_viz.Redo()); // a deleted truss: skipped, no error

        // A fixture unpatched since: skipped.
        _viz.Select(_b);
        Type(s => s with { Position = s.Position with { X = 7 } });
        _show.Patch.Remove(_b);
        Assert.Null(_viz.Selected); // its selection dropped
        Assert.True(_viz.Undo());
        Near(dragged, _a.Position);

        // Another show: nothing to undo.
        _viz.Select(_a);
        Type(s => s with { Position = s.Position with { X = 5 } });
        _viz.FromJson(new System.Text.Json.Nodes.JsonObject());
        Assert.False(_viz.CanUndo || _viz.CanRedo);
    }
}
