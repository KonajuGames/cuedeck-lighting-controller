using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Cuedeck.Common;

namespace Cuedeck.Timelines;

/// <summary>
/// A timeline track as a song's timelines file keeps it (sLight's <c>MusicTimeline</c> wrote and read these), under
/// its key in "tracks":
/// <code>
/// { "dimmer": [[t, level], ...],
///   "colour": [[t0, t1, "rrggbb", "rrggbb"], ...],
///   "strobe": [[t0, t1, speed], ...],                  (0 slow, 1 medium, 2 fast)
///   "position": [[t0, t1, position id], ...],
///   "movement": [[t0, t1, effect, period, size(, phase)], ...],
///   "mute": true, "solo": true, "collapsed": true }    (each only when it's so)
/// </code>
/// A row with nothing in it is left out, and a track with nothing at all.
/// </summary>
static class TimelineFormat
{
    static JsonArray Row(params object[] values) =>
        new([
            .. values.Select(v =>
                v switch
                {
                    double d => (JsonNode)d,
                    int i => i,
                    string s => s,
                    _ => throw new ArgumentException("not a timeline value"),
                }
            ),
        ]);

    public static JsonArray NodesTo(IEnumerable<Vector2> nodes) => new([.. nodes.Select(p => (JsonNode)Row(Gd.Snapped(p.X, 0.001), Gd.Snapped(p.Y, 0.0001)))]);

    public static List<Vector2> NodesFrom(JsonNode? arr)
    {
        var output = new List<Vector2>();
        foreach (var p in arr as JsonArray ?? [])
            if (p is JsonArray { Count: >= 2 } a)
                output.Add(new Vector2((float)Json.AsDouble(a[0]), (float)Json.AsDouble(a[1])));
        return output;
    }

    public static JsonArray ColoursTo(IEnumerable<ColourSpan> segments) =>
        new([.. segments.Select(sg => (JsonNode)Row(Gd.Snapped(sg.T0, 0.001), Gd.Snapped(sg.T1, 0.001), sg.C0.ToHtml(), sg.C1.ToHtml()))]);

    public static List<ColourSpan> ColoursFrom(JsonNode? arr)
    {
        var output = new List<ColourSpan>();
        foreach (var e in arr as JsonArray ?? [])
        {
            if (e is JsonArray { Count: >= 4 } a)
            {
                output.Add(
                    new ColourSpan(Json.AsDouble(a[0]), Json.AsDouble(a[1]), Rgb.FromString(Text(a[2]), Rgb.White), Rgb.FromString(Text(a[3]), Rgb.White))
                );
            }
        }
        return output;
    }

    public static JsonArray StrobesTo(IEnumerable<StrobeSpan> spans) =>
        new([.. spans.Select(sg => (JsonNode)Row(Gd.Snapped(sg.T0, 0.001), Gd.Snapped(sg.T1, 0.001), (int)sg.Speed))]);

    public static List<StrobeSpan> StrobesFrom(JsonNode? arr)
    {
        var output = new List<StrobeSpan>();
        foreach (var e in arr as JsonArray ?? [])
        {
            if (e is not JsonArray { Count: >= 2 } a || Json.AsDouble(a[1]) <= Json.AsDouble(a[0]))
                continue;
            var speed = Math.Clamp(a.Count >= 3 ? Json.AsInt(a[2]) : (int)StrobeSpeed.Medium, (int)StrobeSpeed.Slow, (int)StrobeSpeed.Fast);
            output.Add(new StrobeSpan(Json.AsDouble(a[0]), Json.AsDouble(a[1]), (StrobeSpeed)speed));
        }
        return output;
    }

    public static JsonArray PositionsTo(IEnumerable<PositionSpan> spans) =>
        new([.. spans.Select(sg => (JsonNode)Row(Gd.Snapped(sg.T0, 0.001), Gd.Snapped(sg.T1, 0.001), sg.Position))]);

    public static List<PositionSpan> PositionsFrom(JsonNode? arr)
    {
        var output = new List<PositionSpan>();
        foreach (var e in arr as JsonArray ?? [])
            if (e is JsonArray { Count: >= 3 } a && Json.AsDouble(a[1]) > Json.AsDouble(a[0]))
                output.Add(new PositionSpan(Json.AsDouble(a[0]), Json.AsDouble(a[1]), Text(a[2])));
        return output;
    }

    /// <summary>
    /// Movement spans as saved; the phase only where it isn't 0.
    /// </summary>
    public static JsonArray MovementsTo(IEnumerable<MovementSpan> spans) =>
        new([
            .. spans.Select(sg =>
            {
                var e = Row(Gd.Snapped(sg.T0, 0.001), Gd.Snapped(sg.T1, 0.001), sg.Effect, Gd.Snapped(sg.Period, 0.0001), Gd.Snapped(sg.Size, 0.0001));
                if (sg.Phase != 0)
                    e.Add(Gd.Snapped(sg.Phase, 0.0001));
                return (JsonNode)e;
            }),
        ]);

    public static List<MovementSpan> MovementsFrom(JsonNode? arr)
    {
        var output = new List<MovementSpan>();
        foreach (var e in arr as JsonArray ?? [])
        {
            if (e is not JsonArray { Count: >= 3 } a || Json.AsDouble(a[1]) <= Json.AsDouble(a[0]) || !MovementSpan.Effects.Contains(Text(a[2])))
                continue;
            output.Add(
                new MovementSpan(
                    Json.AsDouble(a[0]),
                    Json.AsDouble(a[1]),
                    Text(a[2]),
                    a.Count > 3 ? Json.AsDouble(a[3]) : MovementSpan.PERIOD,
                    a.Count > 4 ? Json.AsDouble(a[4]) : MovementSpan.SIZE,
                    a.Count > 5 ? Json.AsDouble(a[5]) : 0
                )
            );
        }
        return output;
    }

    /// <summary>
    /// GDScript's <c>str()</c>: a string as itself, a number as its digits.
    /// </summary>
    static string Text(JsonNode? v) =>
        v is JsonValue j && j.TryGetValue<string>(out var s) ? s
        : v is null ? "<null>"
        : Json.AsDouble(v).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A track into its entry in a song's "tracks" (keeping anything else the entry has), or null if there's nothing
    /// left in it.
    /// </summary>
    public static JsonObject? WriteTrack(TimelineTrack track, JsonObject? entry)
    {
        entry = (JsonObject?)entry?.DeepClone() ?? [];
        void Put(string key, bool any, Func<JsonNode> value)
        {
            if (any)
                entry[key] = value();
            else
                entry.Remove(key);
        }
        Put("dimmer", track.DimmerNodes.Count > 0, () => NodesTo(track.DimmerNodes));
        Put("colour", track.ColourSegments.Count > 0, () => ColoursTo(track.ColourSegments));
        Put("movement", track.MovementSegments.Count > 0, () => MovementsTo(track.MovementSegments));
        Put("position", track.PositionSegments.Count > 0, () => PositionsTo(track.PositionSegments));
        Put("strobe", track.StrobeSegments.Count > 0, () => StrobesTo(track.StrobeSegments));
        Put("mute", track.Mute, () => true);
        Put("solo", track.Solo, () => true);
        Put("collapsed", track.Collapsed, () => true);
        return entry.Count > 0 ? entry : null;
    }

    /// <summary>
    /// A track's entry onto it (empty rows for none).
    /// </summary>
    public static void ReadTrack(TimelineTrack track, JsonNode? entry)
    {
        track.DimmerNodes = NodesFrom(entry.Get("dimmer"));
        track.ColourSegments = ColoursFrom(entry.Get("colour"));
        track.StrobeSegments = StrobesFrom(entry.Get("strobe"));
        track.PositionSegments = PositionsFrom(entry.Get("position"));
        track.MovementSegments = MovementsFrom(entry.Get("movement"));
        track.Collapsed = entry.Bool("collapsed", false);
        track.Mute = entry.Bool("mute", false);
        track.Solo = entry.Bool("solo", false);
    }
}
