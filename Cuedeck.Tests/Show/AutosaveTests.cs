using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Show;

namespace Cuedeck.Tests.Show;

/// <summary>
/// sLight's autosave_test.gd: snapshots and sessions.
/// </summary>
public sealed class AutosaveTests : IDisposable
{
    readonly TempFolder _dir = new();

    public void Dispose() => _dir.Dispose();

    Autosave NewAutosave() => new() { File = _dir.File("autosave.slight"), SessionFile = _dir.File("session.json") };

    [Fact]
    public void Snapshots_and_sessions()
    {
        var a = NewAutosave();
        Assert.Null(a.Begin()); // a first run: nothing to recover
        var show = Json.Parse("""{"format":"sLight show","cues":{"cues":[1,2]}}""")!.AsObject();
        a.MarkSaved(show);
        Assert.False(a.Tick(show, "D:/x.slight"));
        Assert.False(File.Exists(a.File)); // unchanged since it was saved: no snapshot
        show["cues"] = Json.Parse("""{"cues":[1,2,3]}""");
        Assert.True(a.Tick(show, "D:/x.slight"));
        Assert.True(File.Exists(a.File));
        Assert.False(a.Tick(show, "D:/x.slight")); // not again until it changes again
        show["cues"] = Json.Parse("""{"cues":[4]}""");
        Assert.True(a.Tick(show, "D:/x.slight"));
        Assert.False(File.Exists(a.File + ".tmp")); // no temporary file left behind

        var snap = a.Read()!;
        Assert.Equal("D:/x.slight", snap.Str("show_path"));
        Assert.True(Json.SameTree(Json.Parse("[4]"), snap.Get("show").Get("cues").Get("cues")));
        Assert.True(Math.Abs(snap.Int("saved_at") - DateTimeOffset.Now.ToUnixTimeSeconds()) < 5);
        Assert.Matches("^[0-9]{2}:[0-9]{2}$", a.When(snap)); // taken today: just the time
        Assert.StartsWith("1970-01-0", a.When(new JsonObject { ["saved_at"] = 0 })); // another day: the date too

        // The app's killed here: the next start finds the session still marked.
        var b = NewAutosave();
        var found = b.Begin();
        Assert.True(Json.SameTree(Json.Parse("[4]"), found.Get("show").Get("cues").Get("cues")));
        b.End();
        Assert.False(File.Exists(b.File)); // a clean quit removes it
        Assert.Null(NewAutosave().Begin()); // after a clean quit: nothing

        // Crashed with nothing autosaved: nothing either.
        var d = NewAutosave();
        Assert.Null(d.Begin());
        File.WriteAllText(d.File, "{ not json");
        Assert.Null(d.Read()); // an unreadable snapshot is ignored
        d.End();
    }
}
