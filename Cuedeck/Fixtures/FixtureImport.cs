using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using Cuedeck.Common;
using Cuedeck.UI;

namespace Cuedeck.Fixtures;

/// <summary>
/// What an import gives back: the profile and notes on what was approximated, or why it failed.
/// </summary>
/// <param name="ModelBytes">A GDTF's 3D model files (glTF binaries) by model name, for the 3D view.</param>
sealed record ImportResult(FixtureProfile? Profile, IReadOnlyList<string> Warnings, IReadOnlyDictionary<string, byte[]> ModelBytes, string? Error)
{
    public static ImportResult Fail(string error) => new(null, [], new Dictionary<string, byte[]>(), error);
}

/// <summary>
/// Reads fixture definitions made elsewhere into a <see cref="FixtureProfile"/>, as sLight's <c>FixtureImport</c>:
/// <list type="bullet">
/// <item>GDTF: a ".gdtf" ZIP holding "description.xml" (DIN SPEC 15800, as GDTF-Share gives them);</item>
/// <item>the Open Fixture Library: one fixture's ".json" (its "Download as JSON", or a file from its GitHub).</item>
/// </list>
/// Best effort: channels that don't map cleanly become GENERIC, and the warnings say what was approximated.
/// </summary>
static class FixtureImport
{
    const int MAX_FOOTPRINT = 512;

    /// <summary>
    /// Reads a .gdtf, an Open Fixture Library .json, or a profile saved as JSON by sLight or Cuedeck.
    /// </summary>
    public static ImportResult FromPath(string path)
    {
        if (!File.Exists(path))
            return ImportResult.Fail("File not found.");
        var low = path.ToLowerInvariant();
        if (low.EndsWith(".gdtf", StringComparison.Ordinal))
            return FromGdtfPath(path);
        if (low.EndsWith(".json", StringComparison.Ordinal))
        {
            JsonNode? parsed;
            try
            {
                parsed = Json.Parse(File.ReadAllText(path));
            }
            catch (IOException e)
            {
                return ImportResult.Fail(e.Message);
            }
            if (parsed is not JsonObject d)
                return ImportResult.Fail("Not a JSON object.");
            if (d.Has("availableChannels"))
                return FromOfl(d);
            if (d.Has("modes") || d.Has("channels"))
                return new ImportResult(FixtureProfile.FromJson(d), [], new Dictionary<string, byte[]>(), null);
            return ImportResult.Fail("Unrecognised JSON (expected an Open Fixture Library fixture).");
        }
        return ImportResult.Fail("Unsupported file type — need .gdtf or .json.");
    }

    // ================================================================ SHARED ==

    /// <summary>
    /// The role a GDTF attribute or a channel's name suggests ("ColorAdd_R", "Dimmer", "Pan"), else GENERIC.
    /// </summary>
    public static string RoleFor(string raw)
    {
        var s = raw.ToLowerInvariant();
        if (s.Contains("dimmer") || s.Contains("intensity") || s == "master" || s.Contains("master dim"))
            return Roles.Dimmer;
        if (s.Contains("coloradd_r") || s.Contains("colorrgb_red") || s == "red" || s == "r")
            return Roles.Red;
        if (s.Contains("coloradd_g") || s.Contains("colorrgb_green") || s == "green" || s == "g")
            return Roles.Green;
        if (s.Contains("coloradd_b") || s.Contains("colorrgb_blue") || s == "blue" || s == "b")
            return Roles.Blue;
        if (s.Contains("coloradd_w") || s == "white" || s == "w" || s.Contains("warm white") || s.Contains("cool white"))
            return Roles.White;
        if (s.Contains("coloradd_a") || s == "amber" || s == "a")
            return Roles.Amber;
        if (s.Contains("coloradd_uv") || s == "uv" || s.Contains("ultraviolet"))
            return Roles.Uv;
        if (s.Contains("pan") && !s.Contains("span"))
            return Roles.Pan;
        if (s.Contains("tilt"))
            return Roles.Tilt;
        if (s.Contains("gobopos") || s.Contains("gobo1pos") || (s.Contains("gobo") && (s.Contains("rot") || s.Contains("index") || s.Contains("spin"))))
            return Roles.GoboRotation;
        if (s.Contains("gobo"))
            return Roles.Gobo;
        if (s.Contains("zoom"))
            return Roles.Zoom;
        if (s.Contains("color") && (s.Contains("wheel") || s.Contains("macro") || s.Contains('1')))
            return Roles.ColorWheel;
        if (s.Contains("cto") || s.Contains("ctb") || s.Contains("ctc") || s.Contains("colortemp"))
            return Roles.ColorWheel;
        if (s.Contains("strob") || s.Contains("shutter"))
            return Roles.Strobe;
        return Roles.Generic;
    }

    static string FineRole(string coarse) =>
        coarse switch
        {
            Roles.Pan => Roles.PanFine,
            Roles.Tilt => Roles.TiltFine,
            _ => Roles.Generic,
        };

    /// <summary>
    /// A file-safe id from a fixture's name: lower case letters and digits, with spaces, dashes, underscores and
    /// slashes as underscores.
    /// </summary>
    public static string SafeIdHint(string s)
    {
        var output = new System.Text.StringBuilder();
        foreach (var ch in s.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
                output.Append(ch);
            else if (ch is ' ' or '-' or '_' or '/')
                output.Append('_');
        }
        var id = output.ToString().Trim('_');
        return id != "" ? id : "imported";
    }

    static double SrgbGamma(double c)
    {
        c = Math.Clamp(c, 0, 1);
        return c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
    }

    /// <summary>
    /// A CIE "x,y,Y" colour (GDTF's ColorCIE) as "#rrggbb", or "" if it can't be used.
    /// </summary>
    public static string CieToHex(string cie)
    {
        var parts = cie.Trim().Split(',');
        if (parts.Length < 3)
            return "";
        var x = Gd.ToFloat(parts[0]);
        var y = Gd.ToFloat(parts[1]);
        var bigY = Gd.ToFloat(parts[2]) / 100;
        if (y <= 0.0001)
            return "";
        bigY = Math.Clamp(bigY, 0.05, 1); // keep swatches visible
        var xf = x / y * bigY;
        var zf = (1 - x - y) / y * bigY;
        var r = 3.2406 * xf - 1.5372 * bigY - 0.4986 * zf;
        var g = -0.9689 * xf + 1.8758 * bigY + 0.0415 * zf;
        var b = 0.0557 * xf - 0.2040 * bigY + 1.0570 * zf;
        return "#" + new Rgb((float)SrgbGamma(r), (float)SrgbGamma(g), (float)SrgbGamma(b)).ToHtml();
    }

    static JsonObject Channel(string name, string role, int? defaultValue, bool fine, JsonArray? ranges = null)
    {
        var ch = new JsonObject
        {
            ["name"] = name,
            ["role"] = role,
            ["fine"] = fine,
            ["ranges"] = ranges ?? [],
        };
        if (defaultValue is { } d)
            ch["default"] = d;
        return ch;
    }

    static JsonObject Generic(string name) => Channel(name, Roles.Generic, 0, false);

    // ================================================================== GDTF ==

    static ImportResult FromGdtfPath(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            return FromGdtf(file);
        }
        catch (IOException)
        {
            return ImportResult.Fail("Can't open .gdtf (not a ZIP archive?).");
        }
    }

    /// <summary>
    /// Reads a .gdtf from a stream (one inside an MVR, say).
    /// </summary>
    public static ImportResult FromGdtf(Stream stream)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return ImportResult.Fail("Can't open .gdtf (not a ZIP archive?).");
        }
        using (zip)
        {
            var byName = new Dictionary<string, ZipArchiveEntry>();
            foreach (var e in zip.Entries)
                byName.TryAdd(e.FullName.ToLowerInvariant(), e);
            if (!byName.ContainsKey("description.xml"))
                return ImportResult.Fail("No description.xml inside the .gdtf archive.");

            // Embedded media, by name in any case: gobo images are in "wheels/<MediaFileName>.png".
            byte[] Reader(string want)
            {
                if (!byName.TryGetValue(want.ToLowerInvariant(), out var entry))
                    return [];
                using var stream = entry.Open();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return memory.ToArray();
            }

            return FromGdtfXml(Reader("description.xml"), Reader);
        }
    }

    /// <summary>
    /// Reads a GDTF's description.xml. <paramref name="media"/> reads a file from the same archive (empty if it isn't
    /// there), for gobo images and 3D models.
    /// </summary>
    public static ImportResult FromGdtfXml(byte[] xml, Func<string, byte[]>? media = null)
    {
        XElement? root;
        try
        {
            using var stream = new MemoryStream(xml);
            root = XDocument.Load(stream).Root;
        }
        catch (XmlException)
        {
            root = null;
        }
        var ft = root?.Name.LocalName == "GDTF" ? Kid(root, "FixtureType") : null;
        if (ft is null)
            return ImportResult.Fail("description.xml has no <FixtureType>.");

        var warnings = new List<string>();
        var maker = Attr(ft, "Manufacturer");
        var model = Attr(ft, "LongName", Attr(ft, "Name", "GDTF Fixture"));
        var fixtureName = maker != "" ? $"{maker} {model}".Trim() : model;

        var wheels = Wheels(ft, media, warnings);
        var modeNodes = Kids(Kid(ft, "DMXModes"), "DMXMode").ToList();
        if (modeNodes.Count == 0)
            return ImportResult.Fail("No <DMXMode> found.");

        // GeometryReference nodes turn one "module" of channels into many cells or heads: they're expanded so the
        // flat channel list has every head.
        var refs = References(ft);
        var modes = new JsonArray();
        foreach (var mn in modeNodes)
            modes.Add(Mode(mn, wheels, warnings, refs));

        var phys = Physical(ft, modes);
        var (geometry, modelBytes) = Geometry(ft, media);
        var heads = HeadOffsets(modeNodes, geometry, refs);
        if (heads.Count > 0)
            phys["heads"] = new JsonArray([.. heads.Select(h => (JsonNode)new JsonArray([.. h.Select(v => (JsonNode)v)]))]);

        var profile = new FixtureProfile(SafeIdHint(fixtureName), model, null, modes, phys, maker) { Geometry = geometry };
        return new ImportResult(profile, warnings, modelBytes, null);
    }

    // ------------------------------------------------------------ GDTF heads --

    /// <summary>
    /// GDTF keeps a 4x4 as column-major "{c0}{c1}{c2}{c3}" in a right-handed Z-up frame; Godot (and sLight's saved
    /// heads) are right-handed Y-up. A GDTF vector (x, y, z) is (x, z, -y) there, so this is the same rigid transform
    /// in those axes.
    /// </summary>
    internal static Xform Transform(IReadOnlyList<double> m)
    {
        if (m.Count != 16)
            return Xform.Identity;
        static Vec3 Conv(Vec3 v) => new(v.X, v.Z, -v.Y);
        var cx = new Vec3(m[0], m[1], m[2]);
        var cy = new Vec3(m[4], m[5], m[6]);
        var cz = new Vec3(m[8], m[9], m[10]);
        var b = new Basis(Conv(cx), Conv(cz), -Conv(cy));
        b = b.Determinant > 0.0001 ? b.Orthonormalized() : Basis.Identity;
        return new Xform(b, Conv(new Vec3(m[12], m[13], m[14])));
    }

    static List<double> Mat(JsonNode? node) => [.. node.Arr("mat").Select(v => Json.AsDouble(v))];

    /// <summary>
    /// Every named geometry's transform, all the way down the tree from <paramref name="node"/>.
    /// </summary>
    static void TransformMap(JsonNode? node, Xform parent, Dictionary<string, Xform> output)
    {
        if (node is null)
            return;
        var world = parent * Transform(Mat(node));
        var name = node.Str("name");
        if (name != "")
            output[name] = world;
        foreach (var c in node.Arr("children"))
            TransformMap(c, world, output);
    }

    sealed record Reference(string Name, string Geometry, int DmxOffset, Xform Xform);

    static List<Reference> References(XElement ft) =>
        [
            .. ft.Descendants("GeometryReference")
                .Select(gr =>
                {
                    var brk = Kid(gr, "Break");
                    return new Reference(
                        Attr(gr, "Name"),
                        Attr(gr, "Geometry"),
                        brk is not null ? Gd.ToInt(Attr(brk, "DMXOffset", "0")) : 0,
                        Transform(Matrix(Attr(gr, "Position")))
                    );
                }),
        ];

    /// <summary>
    /// Euler angles in degrees of <paramref name="b"/> relative to <paramref name="reference"/>: the extra rotation
    /// that turns the first head's orientation into this head's.
    /// </summary>
    static (double X, double Y, double Z) RelativeEuler(Basis b, Basis reference)
    {
        var e = (reference.Inverse() * b).Orthonormalized().Euler();
        return (e.X * 180 / Math.PI, e.Y * 180 / Math.PI, e.Z * 180 / Math.PI);
    }

    /// <summary>
    /// Each head's place from the fixture's origin, as [x, y, z, rot x, rot y, rot z] in metres and degrees (rotation
    /// relative to the first head), from the geometries the red channels drive in whichever mode names most of them.
    /// </summary>
    static List<double[]> HeadOffsets(IReadOnlyList<XElement> modeNodes, JsonObject geometry, IReadOnlyList<Reference> refs)
    {
        if (geometry.Get("tree") is not JsonObject tree)
            return ReferenceHeadOffsets(refs);
        var map = new Dictionary<string, Xform>();
        TransformMap(tree, Xform.Identity, map);
        var origin = map.GetValueOrDefault(tree.Str("name"), Xform.Identity);

        List<string> best = [];
        foreach (var mn in modeNodes)
        {
            var geos = new List<string>();
            foreach (var dc in Kids(Kid(mn, "DMXChannels"), "DMXChannel"))
            foreach (var lc in Kids(dc, "LogicalChannel"))
            {
                if (Attr(lc, "Attribute") is "ColorAdd_R" or "ColorRGB_Red")
                {
                    var g = Attr(dc, "Geometry");
                    if (g != "" && !geos.Contains(g))
                        geos.Add(g);
                }
            }
            if (geos.Count > best.Count)
                best = geos;
        }
        if (best.Count < 2)
            return ReferenceHeadOffsets(refs);

        var inverse = origin.AffineInverse();
        var head0 = inverse * map.GetValueOrDefault(best[0], origin);
        var output = new List<double[]>();
        foreach (var g in best)
        {
            var t = inverse * map.GetValueOrDefault(g, origin);
            var rot = RelativeEuler(t.Basis, head0.Basis);
            output.Add([t.Origin.X, t.Origin.Y, t.Origin.Z, rot.X, rot.Y, rot.Z]);
        }
        return output;
    }

    /// <summary>
    /// Each head's place from the GeometryReference matrices (a pixel bar or spider made of references rather than
    /// separate RGB geometries), centred on their middle.
    /// </summary>
    static List<double[]> ReferenceHeadOffsets(IReadOnlyList<Reference> refs)
    {
        if (refs.Count < 2)
            return [];
        var mid = Vec3.Zero;
        foreach (var r in refs)
            mid += r.Xform.Origin;
        mid /= refs.Count;
        var basis = refs[0].Xform.Basis;
        var output = new List<double[]>();
        foreach (var r in refs)
        {
            var p = r.Xform.Origin - mid;
            var rot = RelativeEuler(r.Xform.Basis, basis);
            output.Add([p.X, p.Y, p.Z, rot.X, rot.Y, rot.Z]);
        }
        return output;
    }

    // --------------------------------------------------------- GDTF geometry --

    /// <summary>
    /// The geometry tree, which geometries pan and tilt, and the glTF model files.
    /// </summary>
    static (JsonObject Geometry, Dictionary<string, byte[]> Models) Geometry(XElement ft, Func<string, byte[]>? media)
    {
        var models = new Dictionary<string, byte[]>();
        var geos = Kid(ft, "Geometries");
        var root = geos?.Elements().FirstOrDefault(c => c.Name.LocalName is "Geometry" or "Axis");
        if (root is null)
            return ([], models);

        string pan = "", tilt = "";
        foreach (var dc in ft.Descendants("DMXChannel"))
        {
            var g = Attr(dc, "Geometry");
            if (g == "")
                continue;
            foreach (var lc in Kids(dc, "LogicalChannel"))
            {
                switch (Attr(lc, "Attribute"))
                {
                    case "Pan":
                        pan = g;
                        break;
                    case "Tilt":
                        tilt = g;
                        break;
                }
            }
        }

        if (Kid(ft, "Models") is { } modelsElement && media is not null)
        {
            foreach (var m in Kids(modelsElement, "Model"))
            {
                var name = Attr(m, "Name");
                var file = Attr(m, "File", name);
                foreach (var candidate in new[] { $"models/gltf/{file}.glb", $"models/gltf/{file}.gltf", $"models/{file}.glb", $"{file}.glb" })
                {
                    var bytes = media(candidate);
                    if (bytes.Length > 0)
                    {
                        models[name] = bytes;
                        break;
                    }
                }
            }
        }

        var geometry = new JsonObject
        {
            ["tree"] = GeometryNode(root),
            ["pan_geo"] = pan,
            ["tilt_geo"] = tilt,
            ["models"] = new JsonObject(), // filled in once the files are saved
            ["models_dir"] = "",
        };
        return (geometry, models);
    }

    static readonly string[] GeometryKinds = ["Geometry", "Axis", "Beam", "FilterBeam", "Support", "Structure"];

    static JsonObject GeometryNode(XElement el)
    {
        var kind = el.Name.LocalName switch
        {
            "Axis" => "axis",
            "Beam" or "FilterBeam" => "beam",
            _ => "geometry",
        };
        return new JsonObject
        {
            ["name"] = Attr(el, "Name"),
            ["kind"] = kind,
            ["mat"] = new JsonArray([.. Matrix(Attr(el, "Position")).Select(v => (JsonNode)v)]),
            ["model"] = Attr(el, "Model"),
            ["beam_deg"] = kind == "beam" ? Math.Clamp(Gd.ToFloat(Attr(el, "BeamAngle", "0")), 0, 120) : 0.0,
            ["children"] = new JsonArray([.. el.Elements().Where(c => GeometryKinds.Contains(c.Name.LocalName)).Select(c => (JsonNode)GeometryNode(c))]),
        };
    }

    /// <summary>
    /// GDTF's "{a,b,c,d}{...}{...}{...}" as 16 numbers (column-major); the identity if it isn't one.
    /// </summary>
    static List<double> Matrix(string s)
    {
        var output = new List<double>();
        var num = new System.Text.StringBuilder();
        foreach (var ch in s)
        {
            if ("-0123456789.eE+".Contains(ch))
                num.Append(ch);
            else if (num.Length > 0)
            {
                output.Add(Gd.ToFloat(num.ToString()));
                num.Clear();
            }
        }
        if (num.Length > 0)
            output.Add(Gd.ToFloat(num.ToString()));
        return output.Count == 16 ? output : [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
    }

    static JsonObject Physical(XElement ft, JsonArray modes)
    {
        var phys = new JsonObject
        {
            ["category"] = GuessCategory(modes),
            ["beam_deg"] = 14.0,
            ["pan_range"] = 540.0,
            ["tilt_range"] = 270.0,
        };
        if (ft.Descendants("Beam").FirstOrDefault() is { } beam && beam.Attribute("BeamAngle") is { } angle)
            phys["beam_deg"] = Math.Clamp(Gd.ToFloat(angle.Value), 1, 120);

        bool panFound = false, tiltFound = false;
        foreach (var cf in ft.Descendants("ChannelFunction"))
        {
            if (cf.Attribute("PhysicalFrom") is not { } from || cf.Attribute("PhysicalTo") is not { } to)
                continue;
            var span = Math.Abs(Gd.ToFloat(to.Value) - Gd.ToFloat(from.Value));
            switch (Attr(cf, "Attribute"))
            {
                case "Pan":
                    phys["pan_range"] = panFound ? Math.Max(phys.Num("pan_range"), span) : span;
                    panFound = true;
                    break;
                case "Tilt":
                    phys["tilt_range"] = tiltFound ? Math.Max(phys.Num("tilt_range"), span) : span;
                    tiltFound = true;
                    break;
            }
        }
        return phys;
    }

    /// <summary>
    /// A category from the roles in its modes: a moving head if it pans and tilts, a blinder for a strobe with no
    /// colour or dimmer, a par for colour or a dimmer, else generic.
    /// </summary>
    static string GuessCategory(JsonArray modes)
    {
        var roles = modes.SelectMany(m => m.Arr("channels")).Select(c => c.Str("role")).ToHashSet();
        if (roles.Contains(Roles.Pan) && roles.Contains(Roles.Tilt))
            return "moving_head";
        if (roles.Contains(Roles.Strobe) && !roles.Contains(Roles.Red) && !roles.Contains(Roles.Dimmer))
            return "blinder";
        if (roles.Contains(Roles.Red) && roles.Contains(Roles.Green) && roles.Contains(Roles.Blue))
            return "par";
        return roles.Contains(Roles.Dimmer) ? "par" : "generic";
    }

    sealed record Slot(string Name, string Color, string Image);

    static Dictionary<string, List<Slot>> Wheels(XElement ft, Func<string, byte[]>? media, List<string> warnings)
    {
        var output = new Dictionary<string, List<Slot>>();
        foreach (var w in Kids(Kid(ft, "Wheels"), "Wheel"))
        {
            var slots = new List<Slot>();
            foreach (var s in Kids(w, "Slot"))
            {
                var file = Attr(s, "MediaFileName").Trim();
                var image = "";
                if (file != "" && media is not null)
                {
                    image = MediaBase64(file, media);
                    if (image == "")
                        warnings.Add($"Gobo image '{file}' not found or unreadable");
                }
                slots.Add(new Slot(Attr(s, "Name"), CieToHex(Attr(s, "Color")), image));
            }
            output[Attr(w, "Name")] = slots;
        }
        return output;
    }

    /// <summary>
    /// A gobo's picture from the archive, shrunk to 64 pixels at most and base64-encoded, so it travels inside the
    /// profile's JSON; "" if there's none to read.
    /// </summary>
    static string MediaBase64(string file, Func<string, byte[]> media)
    {
        foreach (var candidate in new[] { $"wheels/{file}.png", $"{file}.png", $"wheels/{file}" })
        {
            var bytes = media(candidate);
            if (bytes.Length == 0)
                continue;
            Png.Image image;
            try
            {
                image = Png.Decode(bytes);
            }
            catch (InvalidDataException)
            {
                continue;
            }
            var big = Math.Max(image.Width, image.Height);
            if (big > 64)
            {
                var scale = 64.0 / big;
                image = Png.Resize(image, Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
            }
            return Convert.ToBase64String(Png.Encode(image));
        }
        return "";
    }

    /// <summary>
    /// GDTF's DMX values are "value/bytes" ("10/1", "32768/2"): the value.
    /// </summary>
    static int DmxInt(string v) => Gd.ToInt(v.Split('/')[0]);

    /// <summary>
    /// The channel's home value, or -1 if the GDTF gives none (the profile then picks one by role).
    /// </summary>
    static int GdtfDefault(XElement dc, IReadOnlyList<XElement> funcs)
    {
        XElement? chosen = null;
        var initial = Attr(dc, "InitialFunction");
        if (initial != "")
        {
            var want = initial.Split('.')[^1];
            chosen = funcs.FirstOrDefault(cf => Attr(cf, "Name") == want);
        }
        chosen ??= funcs.FirstOrDefault();
        if (chosen?.Attribute("Default") is not { } d)
            return -1;
        return DmxInt(d.Value);
    }

    /// <summary>
    /// The wheel slot a channel function picks: its WheelSlotIndex, else a slot of the same name, else the one in
    /// the same place.
    /// </summary>
    static Slot? MatchWheelSlot(List<Slot> slots, XElement cf, string label, int fi)
    {
        var wsi = Gd.ToInt(Attr(cf, "WheelSlotIndex", "0"));
        if (wsi >= 1 && wsi <= slots.Count)
            return slots[wsi - 1];
        var low = label.ToLowerInvariant();
        foreach (var s in slots)
            if (s.Name.ToLowerInvariant() == low)
                return s;
        return fi >= 0 && fi < slots.Count ? slots[fi] : null;
    }

    static JsonObject Mode(XElement mn, Dictionary<string, List<Slot>> wheels, List<string> warnings, IReadOnlyList<Reference> refs)
    {
        var modeName = Attr(mn, "Name", "Mode");

        // Geometries a GeometryReference instances hold "module" channels (one pixel or head), repeated at each
        // reference's DMX offset.
        var moduleNames = refs.Select(r => r.Geometry).Where(g => g != "").ToHashSet();
        var slots = new Dictionary<int, JsonObject>(); // by 1-based offset
        var moduleSlots = new Dictionary<int, JsonObject>();
        var footprint = 0;

        foreach (var dc in Kids(Kid(mn, "DMXChannels"), "DMXChannel"))
        {
            var offsetText = Attr(dc, "Offset").Trim();
            if (offsetText == "" || offsetText.Equals("none", StringComparison.OrdinalIgnoreCase))
                continue; // a virtual channel, with no DMX footprint
            var offs = offsetText.Split(',').Select(t => Gd.ToInt(t.Trim())).Where(n => n >= 1 && n <= MAX_FOOTPRINT).ToList();
            if (offs.Count == 0)
                continue;
            var isModule = moduleNames.Contains(Attr(dc, "Geometry"));

            var logical = Kids(dc, "LogicalChannel").ToList();
            var funcs = logical.SelectMany(lc => Kids(lc, "ChannelFunction")).ToList();
            var attr = logical.Select(lc => Attr(lc, "Attribute")).FirstOrDefault(a => a != "") ?? "";
            if (attr == "" && funcs.Count > 0)
                attr = Attr(funcs[0], "Attribute");

            var role = RoleFor(attr);
            var name = attr != "" ? attr : $"Ch {offs[0]}";
            var value = GdtfDefault(dc, funcs);
            var hasDefault = value >= 0;
            var coarseDefault = value > 255 ? value >> 8 : Math.Max(value, 0);
            var fineDefault = value > 255 ? value & 0xFF : 0;

            var ranges = new JsonArray();
            if (funcs.Count > 1 || role == Roles.ColorWheel || role == Roles.Gobo)
            {
                var lcWheel = "";
                foreach (var lc in logical)
                    if (lc.Attribute("Wheel") is { } wa)
                        lcWheel = wa.Value;
                for (var fi = 0; fi < funcs.Count; fi++)
                {
                    var cf = funcs[fi];
                    var lo = DmxInt(Attr(cf, "DMXFrom", "0/1"));
                    var hi = 255;
                    if (fi + 1 < funcs.Count)
                        hi = Math.Max(lo, DmxInt(Attr(funcs[fi + 1], "DMXFrom", "255/1")) - 1);
                    var label = Attr(cf, "Name", $"{lo}-{hi}");
                    string color = "", image = "";
                    var wheelName = Attr(cf, "Wheel", lcWheel);
                    if (wheelName != "" && wheels.TryGetValue(wheelName, out var ws) && MatchWheelSlot(ws, cf, label, fi) is { } slot)
                        (color, image) = (slot.Color, slot.Image);
                    ranges.Add(
                        new JsonObject
                        {
                            ["lo"] = lo,
                            ["hi"] = hi,
                            ["label"] = label,
                            ["color"] = color,
                            ["image"] = image,
                        }
                    );
                }
            }

            // No default when the GDTF gives none, so the profile picks one by role (pan and tilt park centred).
            var coarse = Channel(name, role, hasDefault ? Math.Clamp(coarseDefault, 0, 255) : null, false, ranges);
            slots[offs[0]] = coarse;
            if (isModule)
                moduleSlots[offs[0]] = coarse;
            footprint = Math.Max(footprint, offs[0]);
            for (var k = 1; k < offs.Count; k++)
            {
                var fine = Channel($"{name} fine", FineRole(role), hasDefault ? Math.Clamp(k == 1 ? fineDefault : 0, 0, 255) : null, true);
                slots[offs[k]] = fine;
                if (isModule)
                    moduleSlots[offs[k]] = fine;
                footprint = Math.Max(footprint, offs[k]);
            }
        }

        // The module's channels again at every GeometryReference's DMX offset.
        if (moduleSlots.Count > 0 && refs.Count >= 2)
        {
            var baseOffset = refs.Min(r => r.DmxOffset);
            foreach (var r in refs)
            {
                var shift = r.DmxOffset - baseOffset;
                if (shift <= 0)
                    continue;
                foreach (var (offset, slot) in moduleSlots)
                {
                    var target = offset + shift;
                    if (target >= 1 && target <= MAX_FOOTPRINT && !slots.ContainsKey(target))
                    {
                        slots[target] = (JsonObject)slot.DeepClone();
                        footprint = Math.Max(footprint, target);
                    }
                }
            }
        }

        var channels = new JsonArray();
        for (var off = 1; off <= footprint; off++)
        {
            if (slots.TryGetValue(off, out var ch))
                channels.Add(ch);
            else
            {
                channels.Add(Generic($"Ch {off}"));
                warnings.Add($"{modeName}: DMX channel {off} not defined, left as GENERIC");
            }
        }
        return new JsonObject { ["name"] = modeName, ["channels"] = channels };
    }

    // ================================================= Open Fixture Library ==

    static int OflNum(JsonNode? v)
    {
        if (v is JsonValue jv && jv.TryGetValue<string>(out var s))
        {
            if (s.EndsWith('%'))
                return Math.Clamp(Gd.RoundToInt(Gd.ToFloat(s[..^1]) * 2.55), 0, 255);
            return Math.Clamp(Gd.ToInt(s), 0, 255);
        }
        return Math.Clamp((int)Json.AsDouble(v), 0, 255);
    }

    static string WheelKind(JsonArray slots)
    {
        foreach (var sl in slots)
        {
            var t = sl.Str("type").ToLowerInvariant();
            if (t.StartsWith("gobo", StringComparison.Ordinal))
                return Roles.Gobo;
            if (t == "color")
                return Roles.ColorWheel;
        }
        return "";
    }

    static JsonArray Capabilities(JsonNode? cdef)
    {
        var caps = cdef.Arr("capabilities");
        if (caps.Count == 0 && cdef.Get("capability") is { } single)
            return [single.DeepClone()];
        return caps;
    }

    static JsonArray WheelSlotsOf(JsonObject wheels, string name) => wheels.Get(name).Arr("slots");

    static string OflRole(string name, JsonNode? cdef, JsonObject wheels)
    {
        foreach (var cap in Capabilities(cdef))
        {
            switch (cap.Str("type"))
            {
                case "Intensity":
                    return Roles.Dimmer;
                case "Pan" or "PanContinuous":
                    return Roles.Pan;
                case "Tilt" or "TiltContinuous":
                    return Roles.Tilt;
                case "ShutterStrobe":
                    return Roles.Strobe;
                case "Zoom":
                    return Roles.Zoom;
                case "ColorIntensity":
                    return RoleFor(cap.Str("color", name));
                case "WheelSlotRotation" or "WheelRotation":
                {
                    var wheel = cap.Str("wheel", name);
                    if (wheels.Has(wheel) && WheelKind(WheelSlotsOf(wheels, wheel)) == Roles.Gobo)
                        return Roles.GoboRotation;
                    break;
                }
                case "WheelSlot" or "WheelShake":
                {
                    var wheel = cap.Str("wheel", name);
                    if (wheels.Has(wheel) && WheelKind(WheelSlotsOf(wheels, wheel)) is { Length: > 0 } kind)
                        return kind;
                    break;
                }
            }
        }
        if (wheels.Has(name) && WheelKind(WheelSlotsOf(wheels, name)) is { Length: > 0 } k)
            return k;
        return RoleFor(name);
    }

    static string CapLabel(JsonNode? cap, JsonArray wheelSlots)
    {
        if (cap.Has("comment"))
            return cap.Str("comment");
        var t = cap.Str("type");
        if (cap.Has("slotNumber"))
        {
            var n = (int)cap.Num("slotNumber");
            if (n >= 1 && n <= wheelSlots.Count && wheelSlots[n - 1].Str("name") is { Length: > 0 } name)
                return name;
            return $"Slot {n}";
        }
        if (cap.Has("effectName"))
            return cap.Str("effectName");
        if (cap.Has("color"))
            return cap.Str("color");
        if (t == "NoFunction")
            return "—";
        return t != "" ? t : "Range";
    }

    static string CapColour(JsonNode? cap, JsonArray wheelSlots)
    {
        if (cap.Arr("colors") is { Count: > 0 } colors)
            return Json.AsString(colors[0]);
        if (cap.Has("color") && Rgb.FromString(cap.Str("color"), Rgb.Transparent) != Rgb.Transparent)
            return cap.Str("color");
        if (cap.Has("slotNumber"))
        {
            var n = (int)cap.Num("slotNumber");
            if (n >= 1 && n <= wheelSlots.Count && wheelSlots[n - 1].Arr("colors") is { Count: > 0 } sc)
                return Json.AsString(sc[0]);
        }
        return "";
    }

    static JsonObject OflChannel(string name, JsonNode? cdef, JsonObject wheels)
    {
        var caps = Capabilities(cdef);
        var role = OflRole(name, cdef, wheels);

        // The wheel this channel turns, for its slots' names and colours.
        var wheelSlots = new JsonArray();
        if (wheels.Has(name))
            wheelSlots = WheelSlotsOf(wheels, name);
        else if (caps.FirstOrDefault(c => c.Has("wheel") && wheels.Has(c.Str("wheel"))) is { } withWheel)
            wheelSlots = WheelSlotsOf(wheels, withWheel.Str("wheel"));

        var ranges = new JsonArray();
        if (caps.Count > 1)
        {
            foreach (var cap in caps)
            {
                if (cap.Get("dmxRange") is not JsonArray { Count: 2 } dr)
                    continue;
                ranges.Add(
                    new JsonObject
                    {
                        ["lo"] = (int)Json.AsDouble(dr[0]),
                        ["hi"] = (int)Json.AsDouble(dr[1]),
                        ["label"] = CapLabel(cap, wheelSlots),
                        ["color"] = CapColour(cap, wheelSlots),
                    }
                );
            }
        }
        // No default given: the profile picks one by role (pan and tilt park centred rather than at 0).
        return Channel(name, role, cdef.Has("defaultValue") ? OflNum(cdef.Get("defaultValue")) : null, false, ranges);
    }

    sealed record Pixel(string Key, Vec3 Pos);

    /// <summary>
    /// An OFL matrix's pixels in order, each with its place in grid units (x right, y up, z towards the viewer),
    /// centred on the fixture.
    /// </summary>
    static List<Pixel> MatrixPixels(JsonNode? matrix)
    {
        var raw = new List<(string Key, int X, int Y, int Z)>();
        if (matrix.Get("pixelKeys") is JsonArray layers)
        {
            for (var z = 0; z < layers.Count; z++)
            {
                if (layers[z] is not JsonArray rows)
                    continue;
                for (var y = 0; y < rows.Count; y++)
                {
                    if (rows[y] is not JsonArray cols)
                        continue;
                    for (var x = 0; x < cols.Count; x++)
                        if (cols[x] is { } k)
                            raw.Add((Json.AsString(k), x + 1, rows.Count - y, z + 1));
                }
            }
        }
        else if (matrix.Get("pixelCount") is JsonArray { Count: 3 } pc)
        {
            int nx = (int)Json.AsDouble(pc[0]), ny = (int)Json.AsDouble(pc[1]), nz = (int)Json.AsDouble(pc[2]);
            for (var z = 0; z < nz; z++)
            for (var y = 0; y < ny; y++)
            for (var x = 0; x < nx; x++)
                raw.Add(($"({x + 1}, {y + 1}, {z + 1})", x + 1, y + 1, z + 1));
        }
        if (raw.Count == 0)
            return [];
        double cx = raw.Average(r => r.X), cy = raw.Average(r => r.Y), cz = raw.Average(r => r.Z);
        return [.. raw.Select(r => new Pixel(r.Key, new Vec3(r.X - cx, r.Y - cy, r.Z - cz)))];
    }

    /// <summary>
    /// A mode's <c>{insert: "matrixChannels"}</c> as its flat list of channels, noting each pixel's place.
    /// </summary>
    static List<JsonObject> ExpandMatrix(JsonNode entry, List<Pixel> pixels, JsonObject templates, JsonObject wheels, List<double[]> headPos, List<string> warnings)
    {
        var order = entry.Str("channelOrder", "perPixel");
        var names = entry.Arr("templateChannels").Select(t => Json.AsString(t)).ToList();
        var keys = entry.Get("repeatFor") is JsonArray rf ? rf.Select(k => Json.AsString(k)).ToList() : pixels.Select(p => p.Key).ToList();
        var posOf = new Dictionary<string, Vec3>();
        foreach (var p in pixels)
            posOf[p.Key] = p.Pos;

        var output = new List<JsonObject>();
        void Emit(string key, string template)
        {
            var name = template.Replace("$pixelKey", key, StringComparison.Ordinal);
            var def = templates.Get(template) ?? templates.Get(name);
            output.Add(def is JsonObject ? OflChannel(name, def, wheels) : Generic(name));
        }

        if (order == "perChannel")
        {
            warnings.Add("Matrix channelOrder 'perChannel' — per-head colour pickers may not align");
            foreach (var t in names)
            foreach (var k in keys)
                Emit(k, t);
        }
        else
        {
            foreach (var k in keys)
            {
                foreach (var t in names)
                    Emit(k, t);
                var pv = posOf.GetValueOrDefault(k, Vec3.Zero);
                headPos.Add([pv.X, pv.Y, pv.Z]);
            }
        }
        return output;
    }

    /// <summary>
    /// Reads an Open Fixture Library fixture's JSON.
    /// </summary>
    public static ImportResult FromOfl(JsonNode d)
    {
        var warnings = new List<string>();
        var name = d.Str("name", "Imported Fixture");
        var available = d.Obj("availableChannels") ?? [];
        var wheels = d.Obj("wheels") ?? [];
        var templates = d.Obj("templateChannels") ?? [];
        var pixels = MatrixPixels(d.Get("matrix"));
        var headPos = new List<double[]>();

        // A fine channel's alias → its coarse channel's name.
        var fineOf = new Dictionary<string, string>();
        foreach (var (cn, cdef) in available)
            foreach (var fa in cdef.Arr("fineChannelAliases"))
                fineOf[Json.AsString(fa)] = cn;

        var modes = new JsonArray();
        foreach (var m in d.Arr("modes"))
        {
            var mch = new JsonArray();
            foreach (var entry in m.Arr("channels"))
            {
                if (entry is null)
                    mch.Add(Generic("Unused"));
                else if (entry is JsonValue v && v.TryGetValue<string>(out var s))
                {
                    if (fineOf.TryGetValue(s, out var coarse))
                        mch.Add(Channel(s, FineRole(OflRole(coarse, available.Get(coarse), wheels)), 0, true));
                    else if (available.Has(s))
                        mch.Add(OflChannel(s, available.Get(s), wheels));
                    else
                    {
                        mch.Add(Generic(s));
                        warnings.Add($"Channel '{s}' is not in availableChannels");
                    }
                }
                else if (entry is JsonObject && entry.Str("insert") == "matrixChannels")
                {
                    var hp = new List<double[]>();
                    foreach (var ch in ExpandMatrix(entry, pixels, templates, wheels, hp, warnings))
                        mch.Add(ch);
                    if (headPos.Count == 0)
                        headPos = hp;
                }
                else
                {
                    mch.Add(Generic("Matrix"));
                    warnings.Add("Matrix / template channels are imported as GENERIC");
                }
            }
            modes.Add(new JsonObject { ["name"] = m.Str("name", "Mode"), ["channels"] = mch });
        }
        if (modes.Count == 0)
            return ImportResult.Fail("The OFL fixture has no modes.");

        var phys = OflPhysical(d, modes);
        if (headPos.Count >= 2)
        {
            // Grid steps to metres: from the fixture's real width if it's given.
            var pitch = 0.15;
            if (d.Get("physical").Get("dimensions") is JsonArray { Count: >= 1 } dims && pixels.Count >= 2)
            {
                var span = pixels.Max(p => Math.Abs(p.Pos.X));
                if (span > 0)
                    pitch = Json.AsDouble(dims[0]) / 1000 / (span * 2 + 1);
            }
            phys["heads"] = new JsonArray([.. headPos.Select(h => (JsonNode)new JsonArray([.. h.Select(x => (JsonNode)(x * pitch))]))]);
        }

        var profile = new FixtureProfile(SafeIdHint(name), name, null, modes, phys);
        return new ImportResult(profile, warnings, new Dictionary<string, byte[]>(), null);
    }

    static JsonObject OflPhysical(JsonNode d, JsonArray modes)
    {
        var phys = new JsonObject
        {
            ["category"] = "",
            ["beam_deg"] = 14.0,
            ["pan_range"] = 540.0,
            ["tilt_range"] = 270.0,
        };
        var op = d.Get("physical");
        if (op.Get("lens").Get("degreesMinMax") is JsonArray { Count: 2 } dm)
            phys["beam_deg"] = Math.Clamp(Json.AsDouble(dm[0]), 1, 120);
        var focus = op.Get("focus");
        if (focus.Get("panMax") is JsonValue pm && pm.GetValueKind() == System.Text.Json.JsonValueKind.Number)
            phys["pan_range"] = Math.Clamp(Json.AsDouble(pm), 0, 1080);
        if (focus.Get("tiltMax") is JsonValue tm && tm.GetValueKind() == System.Text.Json.JsonValueKind.Number)
            phys["tilt_range"] = Math.Clamp(Json.AsDouble(tm), 0, 540);

        foreach (var c in d.Arr("categories"))
        {
            var s = Json.AsString(c).ToLowerInvariant();
            if (s.Contains("moving head") || s.Contains("scanner"))
                phys["category"] = "moving_head";
            else if (s.Contains("blinder") || s.Contains("strobe"))
                phys["category"] = "blinder";
            else if (s.Contains("pixel bar") || s.Contains("matrix"))
                phys["category"] = "strip";
        }
        if (phys.Str("category") == "")
            phys["category"] = GuessCategory(modes);
        return phys;
    }

    // ================================================================= XML ==

    static IEnumerable<XElement> Kids(XElement? node, string name) => node?.Elements(name) ?? [];

    static XElement? Kid(XElement? node, string name) => node?.Element(name);

    static string Attr(XElement e, string name, string fallback = "") => e.Attribute(name)?.Value ?? fallback;
}
