using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Dmx;

namespace Cuedeck.Show;

/// <summary>
/// One cue: a stored look, with fade-in and fade-out times, a delay before its fade starts, and an optional follow
/// into the next cue.
/// <para>
/// A block cue (<see cref="Tracking"/> off) stores a full look, its lit channels; running it wipes the rig first, so
/// channels it doesn't store go to 0. A tracking cue stores only the channels it changes from the look the cues
/// before it leave standing (moves to 0 included); everything else tracks through. So editing a cue ripples down the
/// list until the next block. <see cref="CueList"/> folds them together for playback.
/// </para>
/// </summary>
sealed class Cue
{
    public string Label { get; set; } = "";
    public double FadeUp { get; set; } = 3;
    public double FadeDown { get; set; } = 3;
    public Look Levels { get; set; } = new();
    public bool Tracking { get; set; }

    /// <summary>
    /// Seconds from GO until its fade starts.
    /// </summary>
    public double Delay { get; set; }

    /// <summary>
    /// When its fade finishes, wait <see cref="FollowWait"/> seconds, then GO the next cue.
    /// </summary>
    public bool Follow { get; set; }

    public double FollowWait { get; set; }

    /// <summary>
    /// The palettes it uses instead of holding those values: fixture uid, then "colour", "position" or "beam", then
    /// the palette's id. The channels a reference covers aren't in <see cref="Levels"/>.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> Refs { get; set; } = [];

    public Cue() { }

    public Cue(string label, double fadeUp = 3, double fadeDown = 3)
    {
        Label = label;
        FadeUp = fadeUp;
        FadeDown = fadeDown;
    }

    /// <summary>
    /// Stores the live levels as a full look (the channels that aren't 0), for a block cue.
    /// </summary>
    public void Capture(DmxOutput dmx) => Levels = Look.Capture(dmx);

    /// <summary>
    /// Stores the look given (per universe, channel to value), leaving out zeros, without touching the live levels.
    /// </summary>
    public void SetLevels(IEnumerable<IReadOnlyDictionary<int, int>> perUniverse) =>
        Levels = new Look(perUniverse.Select(d => d.Where(kv => Math.Clamp(kv.Value, 0, 255) != 0).ToDictionary(kv => kv.Key, kv => Math.Clamp(kv.Value, 0, 255))));

    /// <summary>
    /// Stores only the live channels that differ from <paramref name="previous"/>, the look the cues before leave
    /// standing; a channel taken to 0 from a lit one is stored as a 0. For a tracking cue.
    /// </summary>
    public void CaptureTracked(DmxOutput dmx, Look previous)
    {
        var look = new Look();
        for (var i = 0; i < dmx.UniverseCount; i++)
        {
            var u = dmx.Universes[i];
            var d = new Dictionary<int, int>();
            for (var c = 0; c < DmxUniverse.Size; c++)
                if (u.Data[c] != previous.Level(i, c))
                    d[c] = u.Data[c];
            look.Universes.Add(d);
        }
        Levels = look;
    }

    /// <summary>
    /// How many channels it stores (its "moves"), across every universe.
    /// </summary>
    public int MoveCount => Levels.Count;

    public JsonObject ToJson()
    {
        var refs = new JsonObject();
        foreach (var (uid, per) in Refs)
        {
            var o = new JsonObject();
            foreach (var (k, id) in per)
                o[k] = id;
            refs[uid] = o;
        }
        return new JsonObject
        {
            ["label"] = Label,
            ["fade_up"] = FadeUp,
            ["fade_down"] = FadeDown,
            ["tracking"] = Tracking,
            ["delay"] = Delay,
            ["follow"] = Follow,
            ["follow_wait"] = FollowWait,
            ["refs"] = refs,
            ["levels"] = Levels.ToJson(),
        };
    }

    public static Cue FromJson(JsonNode? d)
    {
        var c = new Cue(d.Str("label", ""), d.Num("fade_up", 3), d.Num("fade_down", 3))
        {
            // Older show files predate tracking, delay and follow: their cues load as GO-only blocks, playing as they
            // did.
            Tracking = d.Bool("tracking", false),
            Delay = Math.Max(d.Num("delay", 0), 0),
            Follow = d.Bool("follow", false),
            FollowWait = Math.Max(d.Num("follow_wait", 0), 0),
            Levels = Look.FromJson(d.Get("levels")),
        };
        if (d.Get("refs") is JsonObject refs)
            foreach (var (uid, per) in refs)
                if (per is JsonObject o)
                    c.Refs[uid] = o.ToDictionary(kv => kv.Key, kv => Json.AsString(kv.Value));
        return c;
    }

    public Cue Clone() => FromJson(ToJson());
}
