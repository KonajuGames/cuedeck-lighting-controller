using System.Text.Json.Nodes;
using Cuedeck.Common;

namespace Cuedeck.Show;

/// <summary>
/// A show as a file: sLight's JSON show format ("sLight show", version 1), so shows go back and forth between the two
/// apps. Holds the patch, live levels, cues, chases, effects, sound reactors, groups, positions, palettes, faders and
/// live flash settings; the parts Cuedeck doesn't handle yet (triggers, the visualizer's stage) are carried through
/// unchanged.
/// </summary>
static class ShowFile
{
    public const string Extension = "slight";
    public const string Format = "sLight show";
    public const int Version = 1;

    /// <summary>
    /// The top-level keys this reads; any others are carried.
    /// </summary>
    static readonly HashSet<string> Known =
    [
        "format", "version", "universes", "live", "run_mode", "cues", "chases", "effects", "sound", "groups", "positions",
        "palettes", "live_flash", "faders", "sacn_cid", "timelines",
    ];

    /// <summary>
    /// The show as a show file's JSON.
    /// </summary>
    public static JsonObject ToJson(LightingShow show)
    {
        var universes = new JsonArray();
        var live = new JsonArray();
        for (var i = 0; i < show.Patch.Universes.Count; i++)
        {
            universes.Add(show.Patch.PatchToJson(i));
            live.Add(show.Patch.LiveToJson(i));
        }
        var d = new JsonObject
        {
            ["format"] = Format,
            ["version"] = Version,
            ["universes"] = universes,
            ["live"] = live,
            ["run_mode"] = (int)show.RunMode,
            ["cues"] = show.Cues.ToJson(),
            ["chases"] = new JsonObject { ["chases"] = new JsonArray([.. show.Effects.Chases.Select(c => (JsonNode)c.ToJson())]) },
            ["effects"] = new JsonObject { ["effects"] = new JsonArray([.. show.Effects.Effects.Select(e => (JsonNode)e.ToJson())]) },
            ["sound"] = new JsonObject
            {
                ["reactors"] = new JsonArray([.. show.Effects.Reactors.Select(r => (JsonNode)r.ToJson())]),
                ["device"] = show.Sound.Device,
                ["gain"] = show.Sound.Gain,
                ["beat_sensitivity"] = show.Sound.BeatSensitivity,
                ["response"] = show.Sound.Response,
            },
            ["triggers"] = Carried(show, "triggers"),
            ["groups"] = new JsonObject
            {
                ["groups"] = new JsonArray(
                    [.. show.Groups.Select(g => (JsonNode)new JsonObject { ["name"] = g.Name, ["members"] = new JsonArray([.. g.Members.Select(m => (JsonNode)m)]) })]
                ),
            },
            ["positions"] = show.Positions.ToJson(),
            ["palettes"] = show.Palettes.ToJson(),
            ["live_flash"] = show.Flash.ToJson(),
            ["faders"] = show.Faders.ToJson(),
            ["viz"] = Carried(show, "viz"),
            ["sacn_cid"] = Convert.ToBase64String(show.Dmx.SacnCid),
        };
        if (show.TimelinesFolder is { } folder)
            d["timelines"] = folder;
        foreach (var (key, value) in show.Carried)
            if (!d.ContainsKey(key))
                d[key] = value?.DeepClone();
        return d;
    }

    static JsonNode Carried(LightingShow show, string key) => show.Carried.GetValueOrDefault(key)?.DeepClone() ?? new JsonObject();

    /// <summary>
    /// The show without what changes just by running it (the live levels, the faders' positions, the run mode, the
    /// visualizer's camera), as compact JSON: what unsaved changes are measured by.
    /// </summary>
    public static string Authored(LightingShow show)
    {
        var d = ToJson(show);
        d.Remove("live");
        d.Remove("run_mode");
        foreach (var f in d.Get("faders").Arr("faders"))
            (f as JsonObject)?.Remove("level");
        (d["viz"] as JsonObject)?.Remove("cam");
        return Json.Stringify(d);
    }

    /// <summary>
    /// Whether <paramref name="parsed"/> is a show file this can load: a show (an object with universes), or the
    /// oldest single-universe patch (a bare list of fixtures). Returns why not, or null.
    /// </summary>
    public static string? Problem(JsonNode? parsed, string fileName)
    {
        if (!(parsed is JsonObject o && o.ContainsKey("universes")) && parsed is not JsonArray)
            return $"{fileName} isn't a show file.";
        if (parsed is JsonObject && parsed.Int("version", 1) > Version)
            return $"{fileName} was saved by a newer version.";
        return null;
    }

    /// <summary>
    /// Replaces <paramref name="show"/> with a parsed show file (or the oldest format's bare fixture list).
    /// </summary>
    public static void Apply(LightingShow show, JsonNode parsed, bool usbAvailable = false)
    {
        var universeList = parsed is JsonObject ? parsed.Arr("universes") : new JsonArray(new JsonObject { ["fixtures"] = parsed.DeepClone() });

        show.Dmx.StopFade();
        show.Effects.StopAll();

        if (parsed.Get("sacn_cid") is { } cidNode)
        {
            try
            {
                var cid = Convert.FromBase64String(Json.AsString(cidNode));
                if (cid.Length == 16)
                    show.Dmx.SacnCid = cid;
            }
            catch (FormatException) { }
        }

        var n = Math.Clamp(universeList.Count, 1, Dmx.DmxOutput.MaxUniverses);
        show.Dmx.SetUniverseCount(n);
        show.Patch.SyncToUniverses();
        for (var i = 0; i < n; i++)
            show.Patch.ApplyPatch(i, i < universeList.Count ? universeList[i] : new JsonObject(), usbAvailable);

        var doc = parsed as JsonObject ?? [];
        // The fixtures' live levels (older files have none: the patch's defaults stand).
        var live = doc.Arr("live");
        for (var i = 0; i < Math.Min(live.Count, n); i++)
            if (live[i] is JsonObject)
                show.Patch.ApplyLive(i, live[i]);

        show.Palettes.FromJson(doc.Obj("palettes")); // before the cues that use them
        show.Cues.FromJson(doc.Get("cues"));

        show.Effects.Chases.Clear();
        foreach (var e in doc.Get("chases").Arr("chases"))
            if (e is JsonObject)
                show.Effects.Chases.Add(Chase.FromJson(e));

        show.Groups.Clear();
        if (doc.Get("groups") is JsonObject groups)
        {
            foreach (var g in groups.Arr("groups"))
            {
                if (g is not JsonObject)
                    continue;
                var group = new FixtureGroup { Name = g.Str("name", "Group") };
                foreach (var m in g.Arr("members"))
                {
                    if (m is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String)
                    {
                        group.Members.Add(v.GetValue<string>());
                    }
                    else if (m is JsonArray { Count: >= 2 } legacy)
                    {
                        // Older shows: [universe, patch index], which is that fixture's uid now the patch is in.
                        var u = Json.AsInt(legacy[0]);
                        var index = Json.AsInt(legacy[1]);
                        if (u >= 0 && u < show.Patch.Universes.Count && index >= 0 && index < show.Patch.Universes[u].Fixtures.Count)
                            group.Members.Add(show.Patch.Universes[u].Fixtures[index].Uid);
                    }
                }
                show.Groups.Add(group);
            }
        }

        show.Positions.FromJson(doc.Obj("positions"));

        show.Effects.Effects.Clear();
        foreach (var e in doc.Get("effects").Arr("effects"))
            if (e is JsonObject)
                show.Effects.Effects.Add(WaveEffect.FromJson(e));

        // A show with no reactors saved at all (a new show, or one from before the Sound tab) gets the starter set;
        // one saved with none keeps none.
        var sound = doc.Get("sound");
        show.Effects.Reactors.Clear();
        if (!sound.Has("reactors"))
            show.Effects.Reactors.AddRange(SoundReactor.Defaults());
        foreach (var e in sound.Arr("reactors"))
            if (e is JsonObject)
                show.Effects.Reactors.Add(SoundReactor.FromJson(e));
        show.Sound.Device = sound.Str("device", "");
        show.Sound.Gain = Math.Clamp(sound.Num("gain", 1), 0.25, 4);
        show.Sound.BeatSensitivity = Math.Clamp(sound.Num("beat_sensitivity", 1.4), 1.05, 2.5);
        show.Sound.Response = Math.Clamp(sound.Num("response", 0.5), 0, 1);

        show.Faders.FromJson(doc.Obj("faders")); // after the cues, chases and groups
        show.Flash.FromJson(doc.Obj("live_flash"));
        show.RunMode = (RunMode)doc.Int("run_mode", 0);

        show.TimelinesFolder = doc.Get("timelines") is JsonValue t && t.GetValueKind() == System.Text.Json.JsonValueKind.String && t.GetValue<string>() != "" ? t.GetValue<string>() : null;
        show.Carried.Clear();
        foreach (var (key, value) in doc)
            if (!Known.Contains(key))
                show.Carried[key] = value?.DeepClone();

        show.Retarget();
        show.Patch.RaiseChanged();
    }

    /// <summary>
    /// Writes the show to <paramref name="path"/>. Returns what went wrong, or null.
    /// </summary>
    public static string? Save(LightingShow show, string path)
    {
        try
        {
            File.WriteAllText(path, Json.StringifyIndented(ToJson(show)));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Couldn't write {Path.GetFileName(path)} ({e.Message}).";
        }
    }

    /// <summary>
    /// Reads the show at <paramref name="path"/> into <paramref name="show"/>. Returns what went wrong, or null (the
    /// show is left alone if anything did).
    /// </summary>
    public static string? Load(LightingShow show, string path, bool usbAvailable = false)
    {
        if (!File.Exists(path))
            return $"No show file at {path}.";
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Couldn't read {Path.GetFileName(path)} ({e.Message}).";
        }
        var parsed = Json.Parse(text);
        if (Problem(parsed, Path.GetFileName(path)) is { } problem)
            return problem;
        Apply(show, parsed!, usbAvailable);
        return null;
    }

    /// <summary>
    /// The folder a show's song timelines go in by default: "&lt;show&gt;.timelines" beside it.
    /// </summary>
    public static string TimelinesFolderFor(string showPath) =>
        Path.Combine(Path.GetDirectoryName(showPath) ?? "", Path.GetFileNameWithoutExtension(showPath) + ".timelines");
}
