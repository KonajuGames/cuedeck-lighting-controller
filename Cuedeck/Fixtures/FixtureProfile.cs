using System.Numerics;
using System.Text.Json.Nodes;
using Cuedeck.Common;

namespace Cuedeck.Fixtures;

/// <summary>
/// A named slot in a channel's values: a gobo, a colour wheel's colour, a shutter's "strobe slow-fast".
/// </summary>
/// <param name="Color">A swatch colour for it (HTML hex or a name), or "" to take one from the label.</param>
/// <param name="Image">A base64 PNG of the gobo's artwork, from a GDTF import, or "".</param>
sealed record RangeSlot(int Lo, int Hi, string Label, string Color = "", string Image = "")
{
    /// <summary>
    /// The value in the middle of the slot, which selects it.
    /// </summary>
    public int Middle => (Lo + Hi) / 2;

    public JsonObject ToJson() => new() { ["lo"] = Lo, ["hi"] = Hi, ["label"] = Label, ["color"] = Color, ["image"] = Image };
}

/// <summary>
/// One DMX channel of a fixture's mode: what it does (its <see cref="Role"/>), its home value and limits, and any named
/// slots in its values.
/// </summary>
sealed class FixtureChannel
{
    public string Name { get; set; } = "Ch";
    public string Role { get; set; } = Roles.Generic;
    public int Min { get; set; }
    public int Max { get; set; } = 255;

    /// <summary>
    /// The value the fixture snaps to when it's patched or homed.
    /// </summary>
    public int Default { get; set; }

    /// <summary>
    /// The low byte of a 16-bit pair, whose high byte is the channel before it.
    /// </summary>
    public bool Fine { get; set; }

    public List<RangeSlot> Ranges { get; set; } = [];

    /// <summary>
    /// The slot <paramref name="value"/> falls into, or null.
    /// </summary>
    public RangeSlot? SlotAt(int value) => Ranges.FirstOrDefault(r => value >= r.Lo && value <= r.Hi);

    /// <summary>
    /// Roles whose home value is the middle of the range when the definition gives none: a moving head parks centre
    /// stage.
    /// </summary>
    static readonly string[] CentreDefaultRoles = [Roles.Pan, Roles.Tilt];

    /// <summary>
    /// A channel read from sLight's JSON, every field filled in and in range, as <c>_normalize_channel()</c> made them.
    /// </summary>
    public static FixtureChannel FromJson(JsonNode? c)
    {
        var lo = Math.Clamp(c.Int("min", 0), 0, 255);
        var hi = Math.Clamp(c.Int("max", 255), 0, 255);
        if (lo > hi)
            (lo, hi) = (hi, lo);
        var role = c.Str("role", Roles.Generic);
        var defaultValue = 0;
        if (c.Get("default") is { } given)
            defaultValue = Json.AsInt(given);
        else if (CentreDefaultRoles.Contains(role))
            defaultValue = Gd.RoundToInt((lo + hi) / 2.0);

        var channel = new FixtureChannel
        {
            Name = c.Str("name", "Ch"),
            Role = role,
            Min = lo,
            Max = hi,
            Default = Math.Clamp(defaultValue, lo, hi),
            Fine = c.Bool("fine", false),
        };
        foreach (var r in c.Arr("ranges"))
        {
            var rlo = Math.Clamp(r.Int("lo", 0), 0, 255);
            var rhi = Math.Clamp(r.Int("hi", 0), 0, 255);
            if (rlo > rhi)
                (rlo, rhi) = (rhi, rlo);
            var label = r.Str("label", "");
            if (label == "")
                label = $"{rlo}-{rhi}";
            channel.Ranges.Add(new RangeSlot(rlo, rhi, label, r.Str("color", ""), r.Str("image", "")));
        }
        return channel;
    }

    public JsonObject ToJson() =>
        new()
        {
            ["name"] = Name,
            ["role"] = Role,
            ["min"] = Min,
            ["max"] = Max,
            ["default"] = Default,
            ["fine"] = Fine,
            ["ranges"] = new JsonArray([.. Ranges.Select(r => (JsonNode)r.ToJson())]),
        };

    public FixtureChannel Clone() => FromJson(ToJson());
}

/// <summary>
/// One of a fixture's DMX personalities: its channels, in order.
/// </summary>
sealed class FixtureMode
{
    public string Name { get; set; } = "Mode";
    public List<FixtureChannel> Channels { get; set; } = [];

    public static FixtureMode FromJson(JsonNode? m) =>
        new() { Name = m.Str("name", "Mode"), Channels = [.. m.Arr("channels").Select(FixtureChannel.FromJson)] };

    public JsonObject ToJson() => new() { ["name"] = Name, ["channels"] = new JsonArray([.. Channels.Select(c => (JsonNode)c.ToJson())]) };
}

/// <summary>
/// A fixture's physical side, for the 3D view: what kind of light it is, its beam, how far it pans and tilts, and
/// where its heads are on a multi-head fixture.
/// </summary>
sealed class FixturePhysical
{
    /// <summary>
    /// moving_head, wash, par, beam, strip, blinder or generic; "" to let the visualizer guess from the roles.
    /// </summary>
    public string Category { get; set; } = "";
    public double BeamDeg { get; set; } = 14;
    public double PanRange { get; set; } = 540;
    public double TiltRange { get; set; } = 270;

    /// <summary>
    /// Per-head placement: x, y, z in metres, then rotation x, y, z in degrees relative to the first head (Godot's XYZ
    /// Euler order). In the order of the mode's RGB triplets; empty for a single head.
    /// </summary>
    public List<double[]> Heads { get; set; } = [];

    public static FixturePhysical FromJson(JsonNode? p)
    {
        var physical = new FixturePhysical
        {
            Category = p.Str("category", ""),
            BeamDeg = Math.Clamp(p.Num("beam_deg", 14), 1, 120),
            PanRange = Math.Clamp(p.Num("pan_range", 540), 0, 1080),
            TiltRange = Math.Clamp(p.Num("tilt_range", 270), 0, 540),
        };
        foreach (var e in p.Arr("heads"))
        {
            if (e is not JsonArray a)
                continue;
            if (a.Count == 3)
                physical.Heads.Add([Json.AsDouble(a[0]), Json.AsDouble(a[1]), Json.AsDouble(a[2]), 0, 0, 0]);
            else if (a.Count >= 6)
                physical.Heads.Add([.. a.Take(6).Select(v => Json.AsDouble(v))]);
        }
        return physical;
    }

    public JsonObject ToJson() =>
        new()
        {
            ["category"] = Category,
            ["beam_deg"] = BeamDeg,
            ["pan_range"] = PanRange,
            ["tilt_range"] = TiltRange,
            ["heads"] = new JsonArray([.. Heads.Select(h => (JsonNode)new JsonArray([.. h.Select(v => (JsonNode)v)]))]),
        };

    public FixturePhysical Clone() => FromJson(ToJson());
}

/// <summary>
/// One head of a fixture: an RGB triplet (channel offsets in the mode), the white, amber or UV channels that go with
/// it, and where it sits.
/// </summary>
/// <param name="Offset">Metres from the fixture's origin.</param>
/// <param name="Rotation">Radians, relative to the first head.</param>
sealed record HeadGroup(int R, int G, int B, IReadOnlyList<int> Extra, Vector3 Offset, Vector3 Rotation);

/// <summary>
/// A fixture type: one or more DMX modes, each an ordered list of channels tagged with a role, plus its physical
/// side for the 3D view. The GUI reads all of this to draw a fixture's controls once it's patched. Plain data that
/// saves as sLight's JSON, so custom profiles and the patch go in show files.
/// </summary>
sealed class FixtureProfile
{
    public string Id { get; set; } = "";
    public string Manufacturer { get; set; } = "";

    /// <summary>
    /// The fixture's model: the name the app shows for the profile.
    /// </summary>
    public string Model { get; set; } = "";

    /// <summary>
    /// Always at least one once the profile carries any channels; a single-mode fixture has just one.
    /// </summary>
    public List<FixtureMode> Modes { get; set; } = [];
    public FixturePhysical Physical { get; set; } = new();

    /// <summary>
    /// GDTF geometry (its tree of parts and models), carried as it was read; empty for a profile not imported from a
    /// geometry-rich GDTF.
    /// </summary>
    public JsonObject Geometry { get; set; } = [];

    /// <summary>
    /// The categories a patched fixture can be put in, for show generation. Chosen per fixture (saved with the patch,
    /// not the profile), starting at <see cref="DefaultShowCategory"/>.
    /// </summary>
    public static readonly string[] ShowCategories =
    [
        "Primary Wash",
        "Secondary Wash",
        "Primary Mover",
        "Secondary Mover",
        "Primary Multi-cell",
        "Secondary Multi-cell",
    ];

    public FixtureProfile() { }

    /// <summary>
    /// A profile with one "Default" mode of <paramref name="channels"/> (given as sLight's channel JSON), or the
    /// <paramref name="modes"/> given.
    /// </summary>
    public FixtureProfile(string id, string model, JsonArray? channels = null, JsonArray? modes = null, JsonObject? physical = null, string manufacturer = "")
    {
        Id = id;
        Model = model;
        Manufacturer = manufacturer;
        Physical = FixturePhysical.FromJson(physical);
        if (modes is { Count: > 0 })
            Modes = [.. modes.Select(FixtureMode.FromJson)];
        else if (channels is { Count: > 0 })
            Modes = [new FixtureMode { Name = "Default", Channels = [.. channels.Select(FixtureChannel.FromJson)] }];
    }

    /// <summary>
    /// Manufacturer and model ("Cameo Auro Beam 150"), or the model alone without a manufacturer.
    /// </summary>
    public string FullName => $"{Manufacturer} {Model}".Trim();

    public int ModeCount => Modes.Count;

    public IReadOnlyList<string> ModeNames => [.. Modes.Select(m => m.Name)];

    /// <summary>
    /// The channels of mode <paramref name="mode"/>, clamped to the modes there are; none if there are none.
    /// </summary>
    public IReadOnlyList<FixtureChannel> Channels(int mode) => Modes.Count == 0 ? [] : Modes[Math.Clamp(mode, 0, Modes.Count - 1)].Channels;

    public int ChannelCount(int mode = 0) => Channels(mode).Count;

    /// <summary>
    /// The channel offsets a virtual dimmer scales in this mode: every colour or white channel when the mode has no
    /// DIMMER of its own; none when it has one (or no colour).
    /// </summary>
    public IReadOnlyList<int> VirtualDimmerChannels(int mode)
    {
        var chans = Channels(mode);
        var output = new List<int>();
        for (var k = 0; k < chans.Count; k++)
        {
            if (chans[k].Role == Roles.Dimmer)
                return [];
            if (Roles.VirtualDimmer.Contains(chans[k].Role))
                output.Add(k);
        }
        return output;
    }

    /// <summary>
    /// The mode's channels grouped into heads: one per complete RGB triplet, in channel order, with the white, amber
    /// and UV channels between or after a triplet folded into it. Empty if the fixture has no RGB. Offsets and
    /// rotations come from <see cref="FixturePhysical.Heads"/>, or a 15 cm horizontal spread when it has none.
    /// </summary>
    public IReadOnlyList<HeadGroup> HeadGroups(int mode)
    {
        var chans = Channels(mode);
        var heads = new List<(int R, int G, int B, List<int> Extra)>();
        (int R, int G, int B, List<int> Extra)? cur = null;
        for (var i = 0; i < chans.Count; i++)
        {
            switch (chans[i].Role)
            {
                case Roles.Red:
                    if (cur is { } done && done.G != -1 && done.B != -1)
                    {
                        heads.Add(done);
                        cur = null;
                    }
                    cur = cur is { } c ? c with { R = i } : (i, -1, -1, new List<int>());
                    break;
                case Roles.Green:
                    if (cur is null)
                        cur = (-1, i, -1, new List<int>());
                    else if (cur.Value.G == -1)
                        cur = cur.Value with { G = i };
                    break;
                case Roles.Blue:
                    if (cur is null)
                        cur = (-1, -1, i, new List<int>());
                    else if (cur.Value.B == -1)
                        cur = cur.Value with { B = i };
                    break;
                case Roles.White or Roles.Amber or Roles.Uv:
                    cur?.Extra.Add(i);
                    break;
            }
        }
        if (cur is { } last)
            heads.Add(last);

        var full = heads.Where(h => h.R != -1 && h.G != -1 && h.B != -1).ToList();
        var offsets = Physical.Heads;
        var n = full.Count;
        var output = new List<HeadGroup>(n);
        for (var k = 0; k < n; k++)
        {
            var offset = Vector3.Zero;
            var rotation = Vector3.Zero;
            if (k < offsets.Count)
            {
                var h = offsets[k];
                offset = new Vector3((float)h[0], (float)h[1], (float)h[2]);
                if (h.Length >= 6)
                    rotation = new Vector3((float)(h[3] * Math.PI / 180), (float)(h[4] * Math.PI / 180), (float)(h[5] * Math.PI / 180));
            }
            else if (n > 1)
            {
                offset = new Vector3((float)((k - (n - 1) * 0.5) * 0.15), 0, 0);
            }
            output.Add(new HeadGroup(full[k].R, full[k].G, full[k].B, full[k].Extra, offset, rotation));
        }
        return output;
    }

    /// <summary>
    /// The colour a range slot gives (a colour wheel's): its own, else one its label names ("Deep Red"; "Open / white"
    /// white; "UV" or "Congo" a violet), or null for a slot that isn't a colour (an effect, a rotation).
    /// </summary>
    public static Rgb? SlotColour(RangeSlot slot)
    {
        if (slot.Color != "")
        {
            var c = Rgb.FromString(slot.Color, Rgb.Transparent);
            return c != Rgb.Transparent ? c : null;
        }
        var low = slot.Label.ToLowerInvariant();
        if (low.Contains("open") || low.Contains("white") || low.Contains("none"))
            return Rgb.White;
        if (low.Contains("uv") || low.Contains("congo"))
            return new Rgb(0.35f, 0.12f, 0.72f);
        foreach (var word in low.Replace('/', ' ').Replace('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var c = Rgb.FromString(word, Rgb.Transparent);
            if (c != Rgb.Transparent)
                return c;
        }
        return null;
    }

    /// <summary>
    /// A colour wheel channel's colour slots, in the wheel's order, each with the value in its middle.
    /// </summary>
    public static IReadOnlyList<(Rgb Colour, int Value)> WheelSlots(FixtureChannel channel)
    {
        var output = new List<(Rgb, int)>();
        foreach (var r in channel.Ranges)
            if (SlotColour(r) is { } c)
                output.Add((c, r.Middle));
        return output;
    }

    /// <summary>
    /// The value that turns colour wheel <paramref name="channel"/> to its slot nearest <paramref name="colour"/>, or
    /// -1 if it has no colour slots.
    /// </summary>
    public static int NearestWheelValue(FixtureChannel channel, Rgb colour)
    {
        var best = -1;
        var bestDistance = double.PositiveInfinity;
        foreach (var (c, value) in WheelSlots(channel))
        {
            var d = ColourDistance(colour, c);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = value;
            }
        }
        return best;
    }

    /// <summary>
    /// How far apart two colours look: their RGB distance.
    /// </summary>
    public static double ColourDistance(Rgb a, Rgb b)
    {
        double dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    /// <summary>
    /// Where this mode's colour comes from a colour wheel alone (no RGB heads), the colours that wheel can make: its
    /// first colour wheel channel with two or more colour slots. Otherwise none.
    /// </summary>
    public IReadOnlyList<Rgb> WheelColours(int mode)
    {
        if (HeadGroups(mode).Count > 0)
            return [];
        foreach (var ch in Channels(mode))
        {
            if (ch.Role != Roles.ColorWheel || ch.Fine)
                continue;
            var output = new List<Rgb>();
            foreach (var (c, _) in WheelSlots(ch))
                if (!output.Contains(c))
                    output.Add(c);
            if (output.Count >= 2)
                return output;
        }
        return [];
    }

    /// <summary>
    /// The category a fixture patched in this mode starts in: a mover if it has pan or tilt, else a multi-cell if it
    /// has more than one head, else a wash.
    /// </summary>
    public string DefaultShowCategory(int mode)
    {
        if (Channels(mode).Any(c => c.Role is Roles.Pan or Roles.Tilt))
            return "Primary Mover";
        return HeadGroups(mode).Count > 1 ? "Primary Multi-cell" : "Primary Wash";
    }

    public JsonObject ToJson() =>
        new()
        {
            ["id"] = Id,
            ["manufacturer"] = Manufacturer,
            ["model"] = Model,
            ["physical"] = Physical.ToJson(),
            ["geometry"] = Geometry.DeepClone(),
            ["modes"] = new JsonArray([.. Modes.Select(m => (JsonNode)m.ToJson())]),
        };

    public static FixtureProfile FromJson(JsonNode? d)
    {
        var p = new FixtureProfile
        {
            Id = d.Str("id", ""),
            Manufacturer = d.Str("manufacturer", ""),
            // Saved before manufacturer and model: the one name, as the model.
            Model = d.Has("model") ? d.Str("model") : d.Str("profile_name", "Custom"),
            Physical = FixturePhysical.FromJson(d.Get("physical")),
            Geometry = d.Get("geometry") is JsonObject g ? (JsonObject)g.DeepClone() : [],
        };
        if (d.Get("modes") is JsonArray { Count: > 0 } modes)
            p.Modes = [.. modes.Select(FixtureMode.FromJson)];
        else
            // The oldest format: a bare channel list becomes one "Default" mode.
            p.Modes = [new FixtureMode { Name = "Default", Channels = [.. d.Arr("channels").Select(FixtureChannel.FromJson)] }];
        return p;
    }

    /// <summary>
    /// A copy that shares nothing with this one, as each patched fixture holds its own.
    /// </summary>
    public FixtureProfile Clone() => FromJson(ToJson());

    /// <summary>
    /// A handful of common fixture shapes to start from. Real fixtures vary in channel order: always check the
    /// fixture's own DMX chart before trusting one of these for a physical light.
    /// </summary>
    public static IReadOnlyList<FixtureProfile> BuiltIn() => BuiltInProfiles.All();
}
