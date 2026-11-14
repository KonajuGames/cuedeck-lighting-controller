using System.Numerics;
using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Dmx;
using Cuedeck.Fixtures;
using Cuedeck.Show;

namespace Cuedeck.Tests.Show;

/// <summary>
/// sLight's show_file_test.gd and unsaved_changes_test.gd, through the show and its session rather than the shell:
/// saving and loading everything, older files, refused files, groups by uid, New Show and unsaved changes. (The parts
/// about the patch tab's rows, keyboard shortcuts, the visualizer and music timelines come with those ports.)
/// </summary>
public sealed class ShowFileTests : IDisposable
{
    readonly TempFolder _dir = new();
    readonly LightingShow _show = new();
    readonly ShowSession _session;

    public ShowFileTests()
    {
        _session = new ShowSession(_show) { LastShowStore = _dir.File("last.json") };
        _session.Autosave.File = _dir.File("autosave.slight");
        _session.Autosave.SessionFile = _dir.File("session.json");
    }

    public void Dispose()
    {
        _show.Dispose();
        _dir.Dispose();
    }

    static FixtureProfile Bar() =>
        Profiles.Simple("bar2", "2-head bar", ("Red", "RED"), ("Green", "GREEN"), ("Blue", "BLUE"), ("Red", "RED"), ("Green", "GREEN"), ("Blue", "BLUE"));

    static FixtureProfile RgbPar() => Profiles.Simple("rgb", "RGB par", ("Red", "RED"), ("Green", "GREEN"), ("Blue", "BLUE"));

    [Fact]
    public void Save_change_everything_and_load_it_back()
    {
        var patch = _show.Patch;
        var par = patch.Add(0, RgbPar(), 0, name: "Par L", position: new Vector3(-2, 4, -1), rotation: new Vector3(90, 15, 0));
        var bar = patch.Add(0, Bar(), 10, name: "Bar", position: new Vector3(1.5f, 3.5f, -2), rotation: new Vector3(80, 0, 30));
        _show.LoadLook([[200, 40, 10]]);
        patch.SetVirtualDimmer(par, 0.4);
        _show.Cues.Record(-1);
        Assert.Equal("Primary Wash", par.Category);
        Assert.Equal("Primary Multi-cell", bar.Category);
        Assert.Equal("Primary Mover", Profiles.Simple("mh", "Moving head", ("Pan", "PAN"), ("Dimmer", "DIMMER")).DefaultShowCategory(0));
        par.Category = "Secondary Wash";
        Assert.Equal((1, 1), (par.CategoryGroup, bar.CategoryGroup));
        bar.CategoryGroup = 3;
        Assert.False(bar.ExcludeGenerate);
        bar.ExcludeGenerate = true;
        _show.Groups.Add(new FixtureGroup { Name = "Front", Members = { par.Uid, bar.Uid } });
        _show.Effects.Effects.Add(new WaveEffect { Name = "Test wave", Group = "Front" });

        var path = _dir.File("show.slight");
        Assert.Null(_session.Save(path));
        Assert.Equal(path, _session.Path);
        Assert.Equal(path, _session.Remembered);
        var doc = Json.Parse(File.ReadAllText(path))!;
        Assert.Equal("sLight show", doc.Str("format"));
        Assert.Equal(1, doc.Int("version"));
        var saved = doc.Arr("universes")[0].Arr("fixtures");
        Assert.Equal(2, saved.Count);
        Assert.Equal("RGB par", saved[0].Get("profile").Str("model")); // each fixture with its whole profile

        // Change everything.
        patch.ApplyPatch(0, new JsonObject { ["fixtures"] = new JsonArray() });
        _show.Cues.FromJson(new JsonObject());
        _show.Groups.Clear();
        _show.Effects.Effects.Clear();
        Assert.Empty(patch.All);

        Assert.Null(_session.Load(path));
        Assert.Equal(path, _session.Path);
        var pf = patch.Universes[0].Fixtures;
        Assert.Equal(["Par L", "Bar"], pf.Select(f => f.Name));
        Assert.Equal(10, pf[1].Start);
        Assert.Equal(new Vector3(-2, 4, -1), pf[0].Position);
        Assert.Equal(new Vector3(90, 15, 0), pf[0].Rotation);
        Assert.Equal(new Vector3(1.5f, 3.5f, -2), pf[1].Position);
        Assert.Equal(new Vector3(80, 0, 30), pf[1].Rotation);
        Assert.Equal(("Secondary Wash", "Primary Multi-cell"), (pf[0].Category, pf[1].Category));
        Assert.Equal((1, 3), (pf[0].CategoryGroup, pf[1].CategoryGroup));
        Assert.Equal((false, true), (pf[0].ExcludeGenerate, pf[1].ExcludeGenerate));
        var u = _show.Dmx.Universes[0];
        Assert.Equal((200, 40, 10), (u.GetChannel(0), u.GetChannel(1), u.GetChannel(2))); // the actual colour
        Assert.True(Gd.ApproxEqual(u.VirtualDimmerLevel(pf[0].Id), 0.4));
        Assert.Single(_show.Cues.Cues);
        Assert.Equal(200, _show.Cues.Cues[0].Levels.Level(0, 0));
        Assert.Equal("Front", _show.Groups.Single().Name);
        Assert.Equal([par.Uid, bar.Uid], _show.Groups[0].Members);
        Assert.Equal("Test wave", _show.Effects.Effects.Single().Name);
        Assert.Equal("Front", _show.Effects.Effects[0].Group);
    }

    [Fact]
    public void Bad_files_leave_the_show_alone()
    {
        _show.Patch.Add(0, RgbPar(), 0);
        var path = _dir.File("show.slight");
        _session.Save(path);
        var bad = _dir.File("bad.json");
        File.WriteAllText(bad, """{"hello": 1}""");
        Assert.NotNull(_session.Load(bad));
        Assert.Single(_show.Patch.All);
        Assert.Equal(path, _session.Path);
        File.WriteAllText(bad, """{"version": 99, "universes": []}""");
        Assert.NotNull(_session.Load(bad)); // from a newer version
        Assert.NotNull(_session.Load(_dir.File("no such show.slight")));
        Assert.Equal(path, _session.Remembered); // a refused file isn't remembered
    }

    [Fact]
    public void An_older_show_file_loads()
    {
        _show.Patch.Add(0, RgbPar(), 0).Category = "Secondary Wash";
        _show.Patch.Add(0, Bar(), 10).CategoryGroup = 3;
        _show.Dmx.Universes[0].SetChannel(0, 99);
        var doc = ShowFile.ToJson(_show);
        // No live levels, categories, format or version, and the old Auto Show run mode.
        doc.Remove("live");
        foreach (var f in doc.Arr("universes")[0].Arr("fixtures").Cast<JsonObject>())
        {
            f.Remove("category");
            f.Remove("category_group");
            f.Remove("exclude_generate");
        }
        doc.Remove("format");
        doc.Remove("version");
        doc["run_mode"] = 2;
        doc["auto_show"] = new JsonObject { ["song"] = "C:/old.mp3" };
        var path = _dir.File("old.slight");
        File.WriteAllText(path, Json.Stringify(doc));
        Assert.Null(_session.Load(path));
        var pf = _show.Patch.Universes[0].Fixtures;
        Assert.Equal(2, pf.Count);
        Assert.Equal(0, _show.Dmx.Universes[0].GetChannel(0)); // at the patch's defaults
        Assert.Equal(("Primary Wash", "Primary Multi-cell"), (pf[0].Category, pf[1].Category));
        Assert.Equal(1, pf[1].CategoryGroup);
        Assert.False(pf[1].ExcludeGenerate);
        Assert.Equal(RunMode.Cues, _show.RunMode); // the old Auto Show mode opens in Cue Mode
    }

    [Fact]
    public void The_oldest_format_a_bare_fixture_list_loads_as_one_universe()
    {
        var path = _dir.File("ancient.json");
        var fixtures = new JsonArray(new JsonObject { ["name"] = "Old", ["start"] = 4, ["profile"] = RgbPar().ToJson() });
        File.WriteAllText(path, Json.Stringify(fixtures));
        Assert.Null(_session.Load(path));
        Assert.Equal("Old", _show.Patch.All.Single().Name);
        Assert.Equal(4, _show.Patch.All.Single().Start);
    }

    [Fact]
    public void What_isnt_ported_yet_is_carried_through_unchanged()
    {
        var path = _dir.File("show.slight");
        _session.Save(path);
        var doc = (JsonObject)Json.Parse(File.ReadAllText(path))!;
        doc["triggers"] = Json.Parse("""{"triggers":[{"action":3,"midi_number":40}],"osc_port":9000}""");
        doc["viz"] = Json.Parse("""{"room":"Club","haze":0.5,"cam":{"yaw":0.4}}""");
        doc["something_new"] = 7;
        File.WriteAllText(path, Json.Stringify(doc));
        Assert.Null(_session.Load(path));
        var again = ShowFile.ToJson(_show);
        // The visualizer's scene is read now (phase 6): its room and haze kept, a camera it can't read left as it was.
        Assert.Equal(("Club", 0.5), (again.Obj("viz").Str("room"), again.Obj("viz").Num("haze")));
        Assert.Equal(6, again.Obj("viz").Arr("cam").Count);
        // Triggers are read now (phase 3), and saved whole.
        var trigger = again.Get("triggers").Arr("triggers")[0];
        Assert.Equal((3, 40, "action"), (trigger.Int("action"), trigger.Int("midi_number"), trigger.Str("fb_watch_key")));
        Assert.Equal(7, again.Int("something_new"));
    }

    [Fact]
    public void Groups_are_kept_by_fixture_uid()
    {
        var a = _show.Patch.Add(0, RgbPar(), 0, name: "A");
        _show.Patch.Add(0, RgbPar(), 3, name: "B");
        var c = _show.Patch.Add(0, RgbPar(), 6, name: "C");
        _show.Groups.Add(new FixtureGroup { Name = "Ends", Members = { a.Uid, c.Uid } });
        Assert.Equal([new ChannelTarget(0, 0), new ChannelTarget(0, 6)], _show.Targets("RED", -1, "Ends"));

        var path = _dir.File("groups.slight");
        _session.Save(path);
        var doc = (JsonObject)Json.Parse(File.ReadAllText(path))!;
        Assert.Equal([a.Uid, c.Uid], doc.Get("groups").Arr("groups")[0].Arr("members").Select(m => Json.AsString(m)));

        // The patch reordered in the file: the group still means A and C.
        var fixtures = doc.Arr("universes")[0].Arr("fixtures");
        var reversed = new JsonArray([.. fixtures.Reverse().Select(f => f!.DeepClone())]);
        ((JsonObject)doc.Arr("universes")[0]!)["fixtures"] = reversed;
        File.WriteAllText(path, Json.Stringify(doc));
        _session.Load(path);
        var names = _show.Groups[0].Members.Select(m => _show.Patch.FindByUid(m)!.Name);
        Assert.Equal(["A", "C"], names);
        Assert.Equal([0, 6], _show.Targets("RED", -1, "Ends").Select(t => t.Channel).Order());

        // An older show: members as [universe, patch index], out-of-range ones dropped.
        doc["groups"] = Json.Parse("""{"groups":[{"name":"Old","members":[[0,1],[0,7]]}]}""");
        File.WriteAllText(path, Json.Stringify(doc));
        _session.Load(path);
        Assert.Equal([_show.Patch.Universes[0].Fixtures[1].Uid], _show.Groups[0].Members);

        // Removing a universe takes its fixtures out of groups.
        _show.Patch.AddUniverse();
        var d = _show.Patch.Add(1, RgbPar(), 0, name: "D");
        var keep = _show.Groups[0].Members[0];
        _show.Groups[0].Members.Add(d.Uid);
        _show.RemoveUniverse(1);
        Assert.Equal([keep], _show.Groups[0].Members);
    }

    [Fact]
    public void New_show()
    {
        var u0 = _show.Patch.Universes[0];
        u0.Ip = "10.1.2.3";
        _show.Patch.ApplyConnection(0);
        var par = _show.Patch.Add(0, RgbPar(), 0, name: "Par");
        _show.LoadLook([[200, 100, 50]]);
        _show.Patch.AddUniverse();
        _show.Cues.Record(-1);
        _show.Groups.Add(new FixtureGroup { Name = "G", Members = { par.Uid } });
        _show.Effects.Effects.Add(new WaveEffect());
        var path = _dir.File("new.slight");
        _session.Save(path);

        _session.New();
        Assert.Single(_show.Patch.Universes);
        Assert.Empty(_show.Patch.All);
        Assert.Equal((0, 0), (_show.Dmx.Universes[0].GetChannel(0), _show.Dmx.Universes[0].GetChannel(1)));
        Assert.Empty(_show.Cues.Cues);
        Assert.Empty(_show.Groups);
        Assert.Empty(_show.Effects.Effects);
        Assert.Equal(["Bass Pulse", "Mid Glow", "Treble Sparkle", "Beat Strobe"], _show.Effects.Reactors.Select(r => r.Name));
        Assert.Equal("10.1.2.3", _show.Patch.Universes[0].Ip); // universe 1's output kept
        Assert.Equal("10.1.2.3", _show.Dmx.Universes[0].TargetIp);
        Assert.Equal("", _session.Path);
        Assert.Equal("Untitled", _session.Name);
        Assert.Equal(path, _session.Remembered); // still the one reopened at start
        Assert.False(_session.HasUnsavedChanges);

        // A show saved with no reactors keeps none.
        ShowFile.Apply(_show, Json.Parse("""{"universes":[{}],"sound":{"reactors":[]}}""")!);
        Assert.Empty(_show.Effects.Reactors);
    }

    [Fact]
    public void Unsaved_changes()
    {
        Assert.False(_session.HasUnsavedChanges);
        Assert.Equal("Untitled — Cuedeck", _session.Title);
        var path = _dir.File("show.slight");
        _session.Save(path);
        Assert.Equal("show — Cuedeck", _session.Title);

        // Playing the show isn't an edit.
        _show.Dmx.Universes[0].SetChannel(0, 200);
        _show.RunMode = RunMode.SoundReactive;
        Assert.False(_session.HasUnsavedChanges);
        _show.Cues.Cues.Add(new Cue("One"));
        _session.Save(path);
        _show.Cues.Go();
        Assert.False(_session.HasUnsavedChanges); // a GO moves the cue list on, but changes nothing in the show

        // The output type is.
        _show.Patch.Universes[0].Output = OutputMode.Sacn;
        _show.Patch.ApplyConnection(0);
        Assert.True(_session.HasUnsavedChanges);
        Assert.Equal("*show — Cuedeck", _session.Title);

        _show.Cues.Record(-1);
        _session.Save(path);
        Assert.False(_session.HasUnsavedChanges);
        using var other = new LightingShow();
        var otherSession = new ShowSession(other) { LastShowStore = _dir.File("last2.json") };
        Assert.Null(otherSession.Load(path));
        Assert.Equal(2, other.Cues.Cues.Count);
        Assert.Equal(OutputMode.Sacn, other.Dmx.Universes[0].Mode);
        Assert.False(otherSession.HasUnsavedChanges);
    }

    [Fact]
    public void Autosave_and_recovery()
    {
        _show.Patch.Add(0, RgbPar(), 0);
        var path = _dir.File("show.slight");
        _session.Save(path);
        Assert.False(_session.AutosaveNow()); // unchanged since saved
        _show.Cues.Record(-1);
        Assert.True(_session.AutosaveNow());

        // The app's killed: the next start finds the snapshot.
        using var next = new LightingShow();
        var nextSession = new ShowSession(next) { LastShowStore = _dir.File("last.json") };
        nextSession.Autosave.File = _session.Autosave.File;
        nextSession.Autosave.SessionFile = _session.Autosave.SessionFile;
        _session.Autosave.Begin(); // this run marked running
        var snapshot = nextSession.Autosave.Begin();
        Assert.NotNull(snapshot);
        Assert.Null(nextSession.Load(nextSession.Remembered));
        var offer = nextSession.RecoveryOffer(snapshot);
        Assert.NotNull(offer); // it has a cue the saved show hasn't
        Assert.Null(nextSession.Restore(offer!));
        Assert.Single(next.Cues.Cues);
        Assert.Equal(path, nextSession.Path);
        Assert.True(nextSession.HasUnsavedChanges);
        Assert.Null(nextSession.RecoveryOffer(nextSession.Autosave.Read())); // now nothing more to offer
    }
}
