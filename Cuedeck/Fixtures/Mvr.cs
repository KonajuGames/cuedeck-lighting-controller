using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Security;
using System.Text;
using System.Xml.Linq;
using Cuedeck.Show;

namespace Cuedeck.Fixtures;

/// <summary>
/// A fixture read from an MVR: its profile (from its GDTF), the mode named, its name, where it's patched (0-based)
/// and where it hangs (metres, Y up; only its heading is read).
/// </summary>
sealed record MvrFixture(FixtureProfile Profile, int Mode, string Name, int Universe, int Start, Vector3 Position, Vector3 Rotation);

/// <summary>
/// What an MVR held: its fixtures, where its trusses are, notes on what couldn't be read, each profile's 3D models
/// (by profile id), or why it couldn't be read at all.
/// </summary>
sealed record MvrImport(
    List<MvrFixture> Fixtures,
    List<Vector3> Trusses,
    List<string> Warnings,
    Dictionary<string, IReadOnlyDictionary<string, byte[]>> ModelBytes,
    string? Error = null
)
{
    public static MvrImport Fail(string error) => new([], [], [], [], error);
}

/// <summary>
/// My Virtual Rig (.mvr) import and export: an MVR is a ZIP holding <c>GeneralSceneDescription.xml</c> and the
/// <c>.gdtf</c> files it names. Import is best-effort: each fixture's matrix, DMX address and GDTF (imported as a
/// profile), and the trusses' places. Export writes a scene description of the patch with a GDTF made for each
/// profile. MVR is Z up and in millimetres; Cuedeck Y up and in metres. sLight's <c>MvrIO</c>.
/// </summary>
static class Mvr
{
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // ------------------------------------------------------------------ import --

    public static MvrImport Import(string path)
    {
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(path);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return MvrImport.Fail("Can't open .mvr (not a ZIP?).");
        }
        using (zip)
        {
            var entries = new Dictionary<string, ZipArchiveEntry>();
            foreach (var e in zip.Entries)
                entries.TryAdd(e.FullName.ToLowerInvariant(), e);
            if (!entries.TryGetValue("generalscenedescription.xml", out var gsdEntry))
                return MvrImport.Fail("No GeneralSceneDescription.xml in the archive.");
            XElement? scene;
            try
            {
                using var s = gsdEntry.Open();
                scene = XDocument.Load(s).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Scene");
            }
            catch (System.Xml.XmlException)
            {
                scene = null;
            }
            if (scene is null)
                return MvrImport.Fail("GeneralSceneDescription has no <Scene>.");

            var result = new MvrImport([], [], [], []);
            var gdtfs = new Dictionary<string, ImportResult>();
            foreach (var fx in scene.Descendants().Where(e => e.Name.LocalName == "Fixture"))
            {
                var spec = Text(fx, "GDTFSpec").Trim();
                var modeName = Text(fx, "GDTFMode").Trim();
                var name = (string?)fx.Attribute("name") ?? Text(fx, "Name");
                var (pos, rot) = ParseMatrix(Text(fx, "Matrix"));
                var (universe, start) = Address(fx);

                FixtureProfile? profile = null;
                var entry = spec == "" ? null : entries.GetValueOrDefault(("gdtf/" + spec).ToLowerInvariant()) ?? entries.GetValueOrDefault(spec.ToLowerInvariant());
                if (entry is not null)
                {
                    if (!gdtfs.TryGetValue(spec, out var read))
                    {
                        using var s = entry.Open();
                        using var memory = new MemoryStream();
                        s.CopyTo(memory);
                        memory.Position = 0;
                        gdtfs[spec] = read = FixtureImport.FromGdtf(memory);
                        if (read.Profile is { } p)
                        {
                            if (read.ModelBytes.Count > 0)
                                result.ModelBytes[p.Id] = read.ModelBytes;
                            foreach (var w in read.Warnings)
                                result.Warnings.Add($"{spec}: {w}");
                        }
                        else
                            result.Warnings.Add($"Couldn't read GDTF '{spec}' ({read.Error})");
                    }
                    profile = read.Profile;
                }
                if (profile is null)
                {
                    result.Warnings.Add($"Fixture '{name}' skipped — no usable GDTF.");
                    continue;
                }
                var mode = 0;
                for (var i = 0; i < profile.ModeCount; i++)
                    if (profile.ModeNames[i] == modeName)
                        mode = i;
                result.Fixtures.Add(new MvrFixture(profile, mode, name, universe, start, pos, rot));
            }
            foreach (var tr in scene.Descendants().Where(e => e.Name.LocalName == "Truss"))
                result.Trusses.Add(ParseMatrix(Text(tr, "Matrix")).Position);
            return result;
        }
    }

    static string Text(XElement node, string child) => node.Elements().FirstOrDefault(e => e.Name.LocalName == child)?.Value ?? "";

    /// <summary>
    /// A fixture's first address (1-based, across universes of 512) as a 0-based universe and channel.
    /// </summary>
    static (int Universe, int Start) Address(XElement fx)
    {
        var address = fx.Elements().FirstOrDefault(e => e.Name.LocalName == "Addresses")?.Elements().FirstOrDefault(e => e.Name.LocalName == "Address");
        var raw = address is not null && int.TryParse(address.Value.Trim(), NumberStyles.Integer, Invariant, out var n) ? n : 1;
        raw = Math.Max(raw, 1);
        return ((raw - 1) / 512, (raw - 1) % 512);
    }

    /// <summary>
    /// MVR's "{right}{up}{forward}{position}" (millimetres, Z up) as a position (metres, Y up) and a heading.
    /// </summary>
    public static (Vector3 Position, Vector3 Rotation) ParseMatrix(string s)
    {
        var nums = new List<double>();
        var cur = new StringBuilder();
        foreach (var ch in s + " ")
        {
            if ("-0123456789.eE+".Contains(ch))
                cur.Append(ch);
            else if (cur.Length > 0)
            {
                if (double.TryParse(cur.ToString(), NumberStyles.Float, Invariant, out var v))
                    nums.Add(v);
                cur.Clear();
            }
        }
        if (nums.Count < 12)
            return (Vector3.Zero, Vector3.Zero);
        var p = new Vector3((float)nums[9], (float)nums[10], (float)nums[11]) / 1000;
        var forward = new Vector3((float)nums[6], (float)nums[7], (float)nums[8]);
        var heading = forward.Length() > 0.01f ? Math.Atan2(-forward.X, forward.Y) * 180 / Math.PI : 0;
        return (new Vector3(p.X, p.Z, -p.Y), new Vector3(0, (float)heading, 0));
    }

    /// <summary>
    /// Puts an import into the show: universes enough for it, each new profile kept (with its models), its fixtures
    /// patched where it says, and a 3 m truss at each truss. Returns what to say.
    /// </summary>
    public static string Apply(MvrImport import, LightingShow show, ProfileStore store, string fileName)
    {
        if (import.Error is { } error)
            return "MVR import failed: " + error;
        if (import.Fixtures.Count == 0)
            return "MVR had no usable fixtures.";
        var needed = Math.Clamp(import.Fixtures.Max(f => f.Universe) + 1, 1, Dmx.DmxOutput.MaxUniverses);
        while (show.Patch.Universes.Count < needed && show.Patch.AddUniverse() is not null) { }
        foreach (var profile in import.Fixtures.Select(f => f.Profile).Distinct())
            if (!store.IdInUse(profile.Id))
                store.AddImported(profile, import.ModelBytes.GetValueOrDefault(profile.Id) ?? new Dictionary<string, byte[]>());
        var count = 0;
        foreach (var f in import.Fixtures)
        {
            if (f.Universe < 0 || f.Universe >= show.Patch.Universes.Count)
                continue;
            show.Patch.Add(f.Universe, f.Profile, f.Start, f.Mode, f.Name, f.Position, f.Rotation);
            count++;
        }
        foreach (var t in import.Trusses)
            show.Visualizer.Trusses.Add(new Visualizer.Truss { Position = t, Length = 3 });
        show.Patch.RaiseChanged();
        foreach (var w in import.Warnings)
            System.Diagnostics.Trace.TraceWarning("MVR import: " + w);
        var notes = import.Warnings.Count > 0 ? $"  ({import.Warnings.Count} notes — see the log)" : "";
        return $"Imported {count} fixtures, {import.Trusses.Count} trusses from {fileName}{notes}";
    }

    // ------------------------------------------------------------------ export --

    /// <summary>
    /// Writes the show's patch to <paramref name="path"/> as an MVR. Returns what to say.
    /// </summary>
    public static string Export(string path, LightingShow show)
    {
        try
        {
            using var file = File.Create(path);
            using var zip = new ZipArchive(file, ZipArchiveMode.Create);
            var seen = new HashSet<string>();
            var fixtures = new StringBuilder();
            var id = 1;
            for (var u = 0; u < show.Patch.Universes.Count; u++)
                foreach (var f in show.Patch.Universes[u].Fixtures)
                {
                    var spec = f.Profile.Id + ".gdtf";
                    if (seen.Add(f.Profile.Id))
                    {
                        using var s = zip.CreateEntry("gdtf/" + spec).Open();
                        s.Write(Gdtf(f.Profile));
                    }
                    var modeName = f.Profile.ModeNames.Count > 0 ? f.Profile.ModeNames[Math.Clamp(f.Mode, 0, f.Profile.ModeCount - 1)] : "Default";
                    fixtures.Append(FixtureNode(f.Name, spec, modeName, u * 512 + f.Start + 1, f.Position, f.Rotation, id++));
                }
            var gsd = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <GeneralSceneDescription verMajor="1" verMinor="6" provider="Cuedeck">
                 <Scene>
                  <Layers>
                   <Layer name="Rig" uuid="{Uuid()}">
                    <ChildList>
                {fixtures}    </ChildList>
                   </Layer>
                  </Layers>
                 </Scene>
                </GeneralSceneDescription>

                """;
            using (var s = zip.CreateEntry("GeneralSceneDescription.xml").Open())
                s.Write(Encoding.UTF8.GetBytes(gsd));
            return $"Exported {id - 1} fixtures to {Path.GetFileName(path)}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Export failed: can't write {path}";
        }
    }

    static string FixtureNode(string name, string spec, string modeName, int address, Vector3 pos, Vector3 rot, int id)
    {
        // Y up (m) to Z up (mm).
        var p = new Vector3(pos.X, -pos.Z, pos.Y) * 1000;
        var h = rot.Y * Math.PI / 180;
        var right = new Vector3((float)Math.Cos(h), (float)Math.Sin(h), 0);
        var forward = new Vector3((float)-Math.Sin(h), (float)Math.Cos(h), 0);
        var matrix = string.Create(Invariant, $"{{{V(right)}}}{{0,0,1}}{{{V(forward)}}}{{{p.X:F3},{p.Y:F3},{p.Z:F3}}}");
        return $"""
                 <Fixture name="{Esc(name)}" uuid="{Uuid()}">
                  <Matrix>{matrix}</Matrix>
                  <GDTFSpec>{Esc(spec)}</GDTFSpec>
                  <GDTFMode>{Esc(modeName)}</GDTFMode>
                  <Addresses><Address break="1">{address}</Address></Addresses>
                  <FixtureID>{id}</FixtureID>
                  <CastShadow>false</CastShadow>
                 </Fixture>

            """;
    }

    static string V(Vector3 v) => string.Create(Invariant, $"{v.X:F4},{v.Y:F4},{v.Z:F4}");

    static string Esc(string s) => SecurityElement.Escape(s) ?? "";

    static string Uuid() => Guid.NewGuid().ToString();

    /// <summary>
    /// GDTF attributes for Cuedeck's channel roles.
    /// </summary>
    static readonly Dictionary<string, string> RoleAttribute = new()
    {
        [Roles.Dimmer] = "Dimmer",
        [Roles.Red] = "ColorAdd_R",
        [Roles.Green] = "ColorAdd_G",
        [Roles.Blue] = "ColorAdd_B",
        [Roles.White] = "ColorAdd_W",
        [Roles.Amber] = "ColorAdd_A",
        [Roles.Uv] = "ColorAdd_UV",
        [Roles.Pan] = "Pan",
        [Roles.PanFine] = "Pan",
        [Roles.Tilt] = "Tilt",
        [Roles.TiltFine] = "Tilt",
        [Roles.Strobe] = "Shutter1",
        [Roles.Zoom] = "Zoom",
        [Roles.Gobo] = "Gobo1",
        [Roles.GoboRotation] = "Gobo1Pos",
        [Roles.ColorWheel] = "Color1",
        [Roles.Generic] = "NoFeature",
    };

    /// <summary>
    /// A GDTF made for <paramref name="profile"/>: a ZIP holding its description.xml (its modes and channels; a fine
    /// channel folded into its coarse one's offset).
    /// </summary>
    public static byte[] Gdtf(FixtureProfile profile)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        using (var s = zip.CreateEntry("description.xml").Open())
            s.Write(GdtfXml(profile));
        return memory.ToArray();
    }

    static byte[] GdtfXml(FixtureProfile profile)
    {
        var used = new List<string>();
        var modes = new StringBuilder();
        for (var mi = 0; mi < profile.ModeCount; mi++)
        {
            var chans = profile.Channels(mi);
            var channels = new StringBuilder();
            for (var ci = 0; ci < chans.Count; ci++)
            {
                var ch = chans[ci];
                if (ch.Fine)
                    continue; // folded into the coarse channel's offset
                var attr = RoleAttribute.GetValueOrDefault(ch.Role, "NoFeature");
                if (!used.Contains(attr))
                    used.Add(attr);
                var offset = ci + 1 < chans.Count && chans[ci + 1].Fine ? $"{ci + 1},{ci + 2}" : $"{ci + 1}";
                channels.Append(
                    $"""
                          <DMXChannel DMXBreak="1" Offset="{offset}" Geometry="Body" Highlight="None">
                           <LogicalChannel Attribute="{attr}">
                            <ChannelFunction Attribute="{attr}" Name="{attr} 1" DMXFrom="0/1" Default="{ch.Default}/1"/>
                           </LogicalChannel>
                          </DMXChannel>

                    """
                );
            }
            modes.Append(
                $"""
                    <DMXMode Name="{Esc(profile.ModeNames[mi])}" Geometry="Body">
                     <DMXChannels>
                {channels}     </DMXChannels>
                    </DMXMode>

                """
            );
        }
        var attributes = string.Concat(used.Select(a => $"   <Attribute Name=\"{a}\" Pretty=\"{a}\"/>\n"));
        var shortName = profile.Id.Length > 10 ? profile.Id[..10] : profile.Id;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <GDTF DataVersion="1.1">
             <FixtureType Name="{Esc(profile.Model)}" ShortName="{Esc(shortName)}" LongName="{Esc(profile.Model)}" Manufacturer="{Esc(profile.Manufacturer != "" ? profile.Manufacturer : "Cuedeck")}" FixtureTypeID="{Uuid()}">
              <AttributeDefinitions>
               <ActivationGroups/>
               <FeatureGroups/>
               <Attributes>
            {attributes}   </Attributes>
              </AttributeDefinitions>
              <Wheels/>
              <PhysicalDescriptions/>
              <Models/>
              <Geometries>
               <Geometry Name="Body" Model="" Position="{"{"}1,0,0,0{"}{"}0,1,0,0{"}{"}0,0,1,0{"}{"}0,0,0,1{"}"}"/>
              </Geometries>
              <DMXModes>
            {modes}  </DMXModes>
              <Revisions/>
             </FixtureType>
            </GDTF>

            """;
        return Encoding.UTF8.GetBytes(xml);
    }
}
