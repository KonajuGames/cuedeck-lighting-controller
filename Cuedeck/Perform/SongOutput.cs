using System.Numerics;
using Cuedeck.Common;
using Cuedeck.Fixtures;
using Cuedeck.Show;
using Cuedeck.Timelines;
using StrobeSpeed = Cuedeck.Timelines.StrobeSpeed;

namespace Cuedeck.Perform;

/// <summary>
/// A patched fixture as song playback sees it: its uid (what its timelines are saved under), where it's patched, and
/// whether it's left out of show generation (then the master's timelines don't drive it).
/// </summary>
sealed record PerformFixture(string Uid, int Universe, int Start, FixtureProfile Profile, int Mode = 0, bool ExcludeGenerate = false)
{
    public static PerformFixture Of(PatchedFixture f) => new(f.Uid, f.Universe, f.Start, f.Profile, f.Mode, f.ExcludeGenerate);
}

/// <summary>
/// A song's lighting timelines played into DMX values, and several songs (DJ Perform Mode's decks) mixed: sLight's
/// <c>DjPerformer.song_output()</c> and <c>mix()</c>, ported exactly.
/// </summary>
static class SongOutput
{
    /// <summary>
    /// Channel roles merged by averaging across decks; every other role is HTP.
    /// </summary>
    public static readonly string[] AverageRoles = [Roles.Pan, Roles.PanFine, Roles.Tilt, Roles.TiltFine];

    /// <summary>
    /// How far through a strobe range each speed goes (slow, medium, fast).
    /// </summary>
    static readonly double[] StrobeSpeedFraction = [0.2, 0.55, 1.0];

    public static List<Dictionary<int, int>> Empty(int n) => [.. Enumerable.Range(0, n).Select(_ => new Dictionary<int, int>())];

    /// <summary>
    /// One song's lighting at <paramref name="t"/> seconds, as <paramref name="n"/> channel-to-value maps (only the
    /// channels it drives), from its timelines by key ("master", a fixture's uid, "&lt;uid&gt;/h&lt;n&gt;"):
    /// <list type="bullet">
    /// <item>dimmer: a head's line where it has values (its first node to its last), else its fixture's, else the
    /// master's, overriding rather than multiplying;</item>
    /// <item>colour: a head's colour row, else its fixture's, else the master's, sets that head's red, green and
    /// blue (where a span covers <paramref name="t"/>);</item>
    /// <item>position: the fixture's position row, else the master's, names a position in
    /// <paramref name="positions"/>, and its pan and tilt go to that position's point for it;</item>
    /// <item>movement: the fixture's movement row, else the master's, moves its pan and tilt around that position (or
    /// around the console's pan and tilt); "none" holds it still;</item>
    /// <item>strobe: where its strobe row or the master's is on, its strobe channels go to a strobing value; off,
    /// they're left as the console has them;</item>
    /// <item>colour wheel: on a fixture without RGB, its colour turns its wheel to the nearest slot.</item>
    /// </list>
    /// On a fixture with a dimmer channel that channel takes the level (the brightest head's where heads have lines of
    /// their own, each head's colour scaled down to its own); on one without, the colour channels (the timeline's
    /// colour, or the console's in <paramref name="bases"/>) are scaled by it, its virtual dimmer. Only what the
    /// timelines cover is driven. A fixture left out of show generation ignores the master; one left out by mute or
    /// solo isn't driven at all; the master muted, nothing is.
    /// </summary>
    public static List<Dictionary<int, int>> Of(
        IReadOnlyDictionary<string, TimelineTrack> tracks,
        double t,
        IReadOnlyList<PerformFixture> fixtures,
        IReadOnlyList<byte[]> bases,
        int n,
        PositionLibrary? positions = null
    )
    {
        var output = Empty(n);
        var flags = tracks.ToDictionary(kv => kv.Key, kv => (kv.Value.Mute, kv.Value.Solo));
        var mix = TimelineTrack.MixStateOf(flags);
        if (!TimelineTrack.Plays(flags, mix, "master"))
            return output;
        var master = tracks.GetValueOrDefault("master");
        List<Vector2> allLine = master?.DimmerNodes ?? [];
        var allLm = TimelineTrack.LevelAt(allLine, t);
        var allColour = TimelineTrack.SegmentColourAt(master?.ColourSegments ?? [], t);
        var allStrobe = SpeedAt(master?.StrobeSegments, t);
        var allPlace = PositionIdAt(master?.PositionSegments, t);
        var allMove = TimelineTrack.SpanAt(master?.MovementSegments ?? [], t);
        foreach (var fx in fixtures)
        {
            var u = fx.Universe;
            if (u < 0 || u >= n)
                continue;
            // The master's, unless it's left out of the show: then none.
            var ownOnly = fx.ExcludeGenerate;
            var masterLine = ownOnly ? [] : allLine;
            var lm = ownOnly ? 1.0 : allLm;
            var masterColour = ownOnly ? null : allColour;
            var masterStrobe = ownOnly ? -1 : allStrobe;
            var masterPlace = ownOnly ? "" : allPlace;
            var masterMove = ownOnly ? null : allMove;
            var uid = fx.Uid;
            if (!TimelineTrack.Plays(flags, mix, uid))
                continue;
            var o = output[u];
            var start = fx.Start;
            var own = tracks.GetValueOrDefault(uid);
            List<Vector2> ownLine = own?.DimmerNodes ?? [];
            var fixtureColour = TimelineTrack.SegmentColourAt(own?.ColourSegments ?? [], t) ?? masterColour;
            // Its own line where that has values, else the master's.
            var ownDefined = TimelineTrack.LineDefinedAt(ownLine, t);
            var fixtureDimmed = ownDefined || masterLine.Count > 0;
            var lf = ownDefined ? TimelineTrack.LevelAt(ownLine, t) : lm;
            var bse = u < bases.Count ? bases[u] : [];
            var chans = fx.Profile.Channels(fx.Mode);
            var dim = -1;
            for (var k = 0; k < chans.Count; k++)
                if (chans[k].Role == Roles.Dimmer)
                {
                    dim = k;
                    break;
                }
            // What a head without a line of its own gets: the fixture's level, or with no lines at all the console's
            // dimmer.
            var inherited = fixtureDimmed ? lf : dim >= 0 ? Byte(bse, start + dim) / 255.0 : 1.0;
            var place = PositionIdAt(own?.PositionSegments, t);
            if (place == "")
                place = masterPlace;
            var placed = place != "" && positions?.Find(place) is not null;
            var move = TimelineTrack.SpanAt(own?.MovementSegments ?? [], t) ?? masterMove;
            var moving = move is not null && move.Effect != "none";
            if (placed || moving)
            {
                // Where it points: the named position, else the console's pan and tilt; then the movement around it.
                var pt = placed ? positions!.Point(place, uid) : new Vector2((float)ReadAxis(bse, chans, start, Roles.Pan), (float)ReadAxis(bse, chans, start, Roles.Tilt));
                if (moving)
                {
                    var (dp, dt) = move!.Offset(t - move.T0);
                    pt += new Vector2((float)dp, (float)dt);
                }
                Patch.WriteAxis(o, chans, start, Roles.Pan, pt.X);
                Patch.WriteAxis(o, chans, start, Roles.Tilt, pt.Y);
            }
            // The faster of the master's strobe and its own.
            var strobe = Math.Max(masterStrobe, SpeedAt(own?.StrobeSegments, t));
            if (strobe >= 0)
                for (var k = 0; k < chans.Count; k++)
                    if (chans[k].Role == Roles.Strobe)
                        o[start + k] = StrobeValue(chans[k], (StrobeSpeed)strobe);
            var groups = fx.Profile.HeadGroups(fx.Mode);
            if (groups.Count == 0 && fixtureColour is { } wheelColour)
                // No RGB: the colour wheel's nearest slot.
                for (var k = 0; k < chans.Count; k++)
                    if (chans[k].Role == Roles.ColorWheel && !chans[k].Fine)
                    {
                        var v = FixtureProfile.NearestWheelValue(chans[k], wheelColour);
                        if (v >= 0)
                            o[start + k] = v;
                    }
            // Per head: its level (its own line where that has values, else inherited) and colour.
            var heads = new List<(bool Own, double Level, Rgb? Colour, bool On)>();
            var anyHead = false;
            for (var h = 0; h < groups.Count; h++)
            {
                var headKey = $"{uid}/h{h}";
                var head = tracks.GetValueOrDefault(headKey);
                List<Vector2> headLine = head?.DimmerNodes ?? [];
                var ownHead = TimelineTrack.LineDefinedAt(headLine, t);
                var on = groups.Count < 2 || TimelineTrack.Plays(flags, mix, headKey);
                anyHead = anyHead || ownHead && on;
                var headColour = TimelineTrack.SegmentColourAt(head?.ColourSegments ?? [], t);
                heads.Add((ownHead, ownHead ? TimelineTrack.LevelAt(headLine, t) : inherited, headColour ?? fixtureColour, on));
            }
            // A dimmer channel serves every head: the brightest head's level, each head's colour then scaled down to
            // its own.
            var dimLevel = inherited;
            if (dim >= 0 && anyHead)
            {
                dimLevel = 0;
                foreach (var hd in heads)
                    if (hd.On)
                        dimLevel = Math.Max(dimLevel, hd.Level);
            }
            if (dim >= 0 && (fixtureDimmed || anyHead))
                o[start + dim] = Gd.RoundToInt(255.0 * Math.Clamp(dimLevel, 0, 1));
            var inHead = new HashSet<int>();
            for (var h = 0; h < groups.Count; h++)
            {
                var g = groups[h];
                var hd = heads[h];
                int[] members = [g.R, g.G, g.B, .. g.Extra];
                if (!hd.On)
                {
                    // Left out by mute or solo: its channels are the console's.
                    inHead.UnionWith(members);
                    continue;
                }
                double level;
                bool dimmed;
                if (dim >= 0)
                {
                    // Heads with lines of their own: scaled to theirs under the shared dimmer; otherwise the dimmer
                    // channel does it all.
                    level = anyHead ? (dimLevel > 0 ? hd.Level / dimLevel : 0) : 1;
                    dimmed = anyHead;
                }
                else
                {
                    level = hd.Level;
                    dimmed = hd.Own || fixtureDimmed;
                }
                foreach (var k in members)
                {
                    if (k < 0)
                        continue;
                    inHead.Add(k);
                    var component = k == g.R ? 0 : k == g.G ? 1 : k == g.B ? 2 : -1;
                    var coloured = hd.Colour is not null && component >= 0;
                    if (!coloured && !dimmed)
                        continue;
                    var v = coloured ? Component(hd.Colour!.Value, component) * 255.0 : Byte(bse, start + k);
                    o[start + k] = Gd.RoundToInt(v * Math.Clamp(level, 0, 1));
                }
            }
            // Colour channels outside any head (a lone white, say): the fixture's dimming, as its virtual dimmer.
            if (dim < 0 && fixtureDimmed)
                foreach (var k in fx.Profile.VirtualDimmerChannels(fx.Mode))
                    if (!inHead.Contains(k))
                        o[start + k] = Gd.RoundToInt(Byte(bse, start + k) * Math.Clamp(lf, 0, 1));
        }
        return output;
    }

    static double Component(Rgb c, int i) =>
        i switch
        {
            0 => c.R,
            1 => c.G,
            _ => c.B,
        };

    /// <summary>
    /// The strobe speed at <paramref name="t"/>, or -1 where there's no strobe span.
    /// </summary>
    static int SpeedAt(IEnumerable<StrobeSpan>? spans, double t) => spans is not null && TimelineTrack.SpanAt(spans, t) is { } sg ? (int)sg.Speed : -1;

    /// <summary>
    /// The position named at <paramref name="t"/>, or "".
    /// </summary>
    static string PositionIdAt(IEnumerable<PositionSpan>? spans, double t) => spans is not null && TimelineTrack.SpanAt(spans, t) is { } sg ? sg.Position : "";

    /// <summary>
    /// A pan or tilt axis's value in <paramref name="bse"/> (the console's), 0 to 1 over its range:
    /// <see cref="Patch.WriteAxis"/> backwards. 0.5 if the fixture has no such axis.
    /// </summary>
    static double ReadAxis(byte[] bse, IReadOnlyList<FixtureChannel> chans, int start, string role)
    {
        for (var k = 0; k < chans.Count; k++)
        {
            if (chans[k].Role != role)
                continue;
            var fine = k + 1 < chans.Count && (chans[k + 1].Fine || chans[k + 1].Role == role + "_FINE");
            if (fine)
                return (Byte(bse, start + k) * 256 + Byte(bse, start + k + 1)) / 65535.0;
            var lo = chans[k].Min;
            var hi = chans[k].Max;
            return Math.Clamp((double)(Byte(bse, start + k) - lo) / Math.Max(hi - lo, 1.0), 0, 1);
        }
        return 0.5;
    }

    /// <summary>
    /// The value that makes a strobe channel strobe at <paramref name="speed"/>: that far through a range labelled
    /// strobe or pulse (slow to fast), or through 20 to 240 (strobing on a plain 0 to 255 channel, as the visualizer
    /// reads one) without such a range.
    /// </summary>
    public static int StrobeValue(FixtureChannel ch, StrobeSpeed speed = StrobeSpeed.Fast)
    {
        int lo = 20,
            hi = 240;
        foreach (var r in ch.Ranges)
        {
            var label = r.Label.ToLowerInvariant();
            if (label.Contains("strob") || label.Contains("puls"))
            {
                (lo, hi) = (r.Lo, r.Hi);
                break;
            }
        }
        var f = StrobeSpeedFraction[Math.Clamp((int)speed, 0, 2)];
        return Gd.RoundToInt(Gd.Lerp(lo, hi, f));
    }

    static int Byte(byte[] bse, int ch) => ch >= 0 && ch < bse.Length ? bse[ch] : 0;

    /// <summary>
    /// The channels merged by averaging (movement), per universe: the fixtures' pan and tilt (and fine) channels.
    /// </summary>
    public static List<HashSet<int>> AverageChannels(IReadOnlyList<PerformFixture> fixtures, int n)
    {
        var output = Enumerable.Range(0, n).Select(_ => new HashSet<int>()).ToList();
        foreach (var fx in fixtures)
        {
            if (fx.Universe < 0 || fx.Universe >= n)
                continue;
            var chans = fx.Profile.Channels(fx.Mode);
            for (var k = 0; k < chans.Count; k++)
                if (AverageRoles.Contains(chans[k].Role))
                    output[fx.Universe].Add(fx.Start + k);
        }
        return output;
    }

    /// <summary>
    /// Merges several songs' outputs: a channel in <paramref name="average"/> (per universe) takes the mean of the
    /// songs driving it; every other channel the highest.
    /// </summary>
    public static List<Dictionary<int, int>> Mix(IReadOnlyList<IReadOnlyList<Dictionary<int, int>>> outputs, IReadOnlyList<HashSet<int>> average, int n)
    {
        var output = new List<Dictionary<int, int>>(n);
        for (var i = 0; i < n; i++)
        {
            var merged = new Dictionary<int, int>();
            var counts = new Dictionary<int, int>();
            var avg = i < average.Count ? average[i] : [];
            foreach (var o in outputs)
            {
                if (i >= o.Count)
                    continue;
                foreach (var (ch, v) in o[i])
                {
                    if (avg.Contains(ch))
                    {
                        merged[ch] = merged.GetValueOrDefault(ch) + v;
                        counts[ch] = counts.GetValueOrDefault(ch) + 1;
                    }
                    else
                        merged[ch] = Math.Max(merged.GetValueOrDefault(ch), v);
                }
            }
            foreach (var (ch, count) in counts)
                merged[ch] = Gd.RoundToInt((double)merged[ch] / count);
            output.Add(merged);
        }
        return output;
    }
}
