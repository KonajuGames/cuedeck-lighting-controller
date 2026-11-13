using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Fixtures;

namespace Cuedeck.Show;

enum PaletteKind
{
    Colour,
    Position,
    Beam,
}

/// <summary>
/// A named colour, position or beam look: channel values for each fixture it covers, by the fixture's uid and the
/// channel's offset from its start, so it follows a fixture re-patched to another address.
/// </summary>
sealed class Palette
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public PaletteKind Kind { get; set; } = PaletteKind.Colour;

    /// <summary>
    /// Fixture uid, then channel offset, then value.
    /// </summary>
    public Dictionary<string, Dictionary<int, int>> Values { get; } = [];

    public JsonObject ToJson()
    {
        var values = new JsonObject();
        foreach (var (uid, per) in Values)
            values[uid] = Look.ChannelsToJson(per);
        return new JsonObject { ["id"] = Id, ["name"] = Name, ["kind"] = PaletteLibrary.KindKeys[(int)Kind], ["values"] = values };
    }

    public static Palette FromJson(JsonNode? d)
    {
        var p = new Palette
        {
            Id = d.Str("id", ""),
            Name = d.Str("name", "Palette"),
            Kind = (PaletteKind)Math.Max(Array.IndexOf(PaletteLibrary.KindKeys, d.Str("kind", "colour")), 0),
        };
        if (d.Get("values") is JsonObject values)
            foreach (var (uid, per) in values)
                p.Values[uid] = Look.ChannelsFromJson(per);
        return p;
    }
}

/// <summary>
/// The show's palettes: named colours, positions and beam looks that cues refer to instead of holding the values, so
/// changing a palette changes every cue that uses it.
/// <para>
/// Cues link to palettes as they're recorded: a fixture whose channels of a kind exactly match a palette's for it gets
/// a reference to the palette instead of the values. Playback looks the palette up as it is now.
/// </para>
/// </summary>
sealed class PaletteLibrary
{
    public static readonly string[] Kinds = ["Colour", "Position", "Beam"];

    /// <summary>
    /// The kinds by the names they're saved under.
    /// </summary>
    public static readonly string[] KindKeys = ["colour", "position", "beam"];

    /// <summary>
    /// The channel roles each kind holds; a fine channel goes with its coarse one.
    /// </summary>
    public static readonly string[][] KindRoles =
    [
        [Roles.Red, Roles.Green, Roles.Blue, Roles.White, Roles.Amber, Roles.Uv, Roles.ColorWheel],
        [Roles.Pan, Roles.PanFine, Roles.Tilt, Roles.TiltFine],
        [Roles.Zoom, Roles.Gobo, Roles.GoboRotation],
    ];

    int _nextId = 1;

    public List<Palette> Palettes { get; } = [];

    /// <summary>
    /// A palette was added, changed, renamed or removed.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// A fixture's channel offsets of <paramref name="kind"/>, fine channels included.
    /// </summary>
    public static List<int> Offsets(PatchedFixture fixture, PaletteKind kind)
    {
        var output = new List<int>();
        var chans = fixture.Channels;
        for (var k = 0; k < chans.Count; k++)
            if (KindRoles[(int)kind].Contains(chans[k].Role))
                output.Add(k);
        return output;
    }

    public Palette? Find(string id) => Palettes.FirstOrDefault(p => p.Id == id);

    public IEnumerable<Palette> OfKind(PaletteKind kind) => Palettes.Where(p => p.Kind == kind);

    /// <summary>
    /// A new palette from <paramref name="fixtures"/>' channels of <paramref name="kind"/> in
    /// <paramref name="bases"/> (each universe's levels). Fixtures without any such channel aren't in it; null if
    /// none of them has any.
    /// </summary>
    public Palette? Record(PaletteKind kind, string name, IEnumerable<PatchedFixture> fixtures, IReadOnlyList<byte[]> bases)
    {
        var p = new Palette { Kind = kind, Name = name };
        Capture(p, fixtures, bases);
        if (p.Values.Count == 0)
            return null;
        p.Id = $"p{_nextId++}";
        Palettes.Add(p);
        Changed?.Invoke();
        return p;
    }

    /// <summary>
    /// Records <paramref name="p"/> again from <paramref name="bases"/>: every fixture it holds that's among
    /// <paramref name="fixtures"/>, and any of them it doesn't hold yet.
    /// </summary>
    public void Update(Palette p, IEnumerable<PatchedFixture> fixtures, IReadOnlyList<byte[]> bases)
    {
        Capture(p, fixtures, bases);
        Changed?.Invoke();
    }

    static void Capture(Palette p, IEnumerable<PatchedFixture> fixtures, IReadOnlyList<byte[]> bases)
    {
        foreach (var fx in fixtures)
        {
            var offsets = Offsets(fx, p.Kind);
            if (offsets.Count == 0)
                continue;
            var b = fx.Universe < bases.Count ? bases[fx.Universe] : [];
            var per = new Dictionary<int, int>();
            foreach (var o in offsets)
            {
                var c = fx.Start + o;
                per[o] = c < b.Length ? b[c] : 0;
            }
            p.Values[fx.Uid] = per;
        }
    }

    public void Rename(Palette p, string name)
    {
        p.Name = name;
        Changed?.Invoke();
    }

    public void Remove(Palette p)
    {
        Palettes.Remove(p);
        Changed?.Invoke();
    }

    /// <summary>
    /// The palette of <paramref name="kind"/> that <paramref name="fixture"/>'s channels of that kind in
    /// <paramref name="b"/> (its universe's levels) match exactly, or "" if none does.
    /// </summary>
    public string MatchFixture(PaletteKind kind, PatchedFixture fixture, byte[] b)
    {
        var offsets = Offsets(fixture, kind);
        if (offsets.Count == 0)
            return "";
        foreach (var p in Palettes)
        {
            if (p.Kind != kind || !p.Values.TryGetValue(fixture.Uid, out var per) || per.Count != offsets.Count)
                continue;
            var same = offsets.All(o =>
            {
                var c = fixture.Start + o;
                return per.TryGetValue(o, out var v) && v == (c < b.Length ? b[c] : 0);
            });
            if (same)
                return p.Id;
        }
        return "";
    }

    /// <summary>
    /// Palette <paramref name="id"/>'s values for <paramref name="fixture"/>, by channel in its universe; none if the
    /// palette's gone or doesn't hold the fixture.
    /// </summary>
    public Dictionary<int, int> ChannelsFor(string id, PatchedFixture fixture)
    {
        var output = new Dictionary<int, int>();
        if (Find(id) is not { } p || !p.Values.TryGetValue(fixture.Uid, out var per))
            return output;
        foreach (var (o, v) in per)
            output[fixture.Start + o] = v;
        return output;
    }

    /// <summary>
    /// <paramref name="bases"/> with <paramref name="p"/> applied to the fixtures it holds, for loading into the
    /// fixture controls.
    /// </summary>
    public List<byte[]> Applied(Palette p, IEnumerable<PatchedFixture> fixtures, IReadOnlyList<byte[]> bases)
    {
        var output = bases.Select(b => (byte[])b.Clone()).ToList();
        foreach (var fx in fixtures)
        {
            if (fx.Universe >= output.Count)
                continue;
            foreach (var (c, v) in ChannelsFor(p.Id, fx))
                if (c < output[fx.Universe].Length)
                    output[fx.Universe][c] = (byte)v;
        }
        return output;
    }

    /// <summary>
    /// A colour to show <paramref name="p"/> as: a colour palette's first fixture's red, green and blue (brightened to
    /// full), or its colour wheel slot's; null for other kinds, or with nothing to go on.
    /// </summary>
    public static Rgb? Swatch(Palette p, IEnumerable<PatchedFixture> fixtures)
    {
        if (p.Kind != PaletteKind.Colour)
            return null;
        foreach (var fx in fixtures)
        {
            if (!p.Values.TryGetValue(fx.Uid, out var per))
                continue;
            var chans = fx.Channels;
            int r = -1, g = -1, b = -1, w = 0;
            foreach (var (k, v) in per)
            {
                if (k >= chans.Count)
                    continue;
                switch (chans[k].Role)
                {
                    case Roles.Red:
                        r = v;
                        break;
                    case Roles.Green:
                        g = v;
                        break;
                    case Roles.Blue:
                        b = v;
                        break;
                    case Roles.White:
                        w = Math.Max(w, v);
                        break;
                    case Roles.ColorWheel when !chans[k].Fine:
                        if (DmxRender.SlotColour(chans[k], v) is { } wc)
                            return wc;
                        break;
                }
            }
            if (r >= 0 && g >= 0 && b >= 0)
            {
                var c = new Rgb(Math.Max(r, w) / 255f, Math.Max(g, w) / 255f, Math.Max(b, w) / 255f);
                var top = Math.Max(c.R, Math.Max(c.G, c.B));
                return top > 0 ? new Rgb(c.R / top, c.G / top, c.B / top) : Rgb.Black;
            }
        }
        return null;
    }

    public JsonObject ToJson() => new() { ["palettes"] = new JsonArray([.. Palettes.Select(p => (JsonNode)p.ToJson())]), ["next_id"] = _nextId };

    public void FromJson(JsonNode? d)
    {
        Palettes.Clear();
        foreach (var e in d.Arr("palettes"))
        {
            if (e is not JsonObject)
                continue;
            var p = Palette.FromJson(e);
            if (p.Id != "")
                Palettes.Add(p);
        }
        _nextId = d.Int("next_id", 1);
        // Never reuse an id the show has.
        foreach (var p in Palettes)
            if (p.Id.StartsWith('p') && Json.IsValidInt(p.Id[1..]))
                _nextId = Math.Max(_nextId, int.Parse(p.Id[1..]) + 1);
        Changed?.Invoke();
    }
}
