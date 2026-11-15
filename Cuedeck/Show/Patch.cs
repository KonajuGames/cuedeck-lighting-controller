using System.Numerics;
using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Dmx;
using Cuedeck.Fixtures;

namespace Cuedeck.Show;

/// <summary>
/// A fixture patched into a universe: its profile (its own copy), mode and start address, and how the show treats it.
/// </summary>
sealed class PatchedFixture
{
    /// <summary>
    /// Its number for this session only: what its virtual dimmer is kept by.
    /// </summary>
    public int Id { get; init; }

    /// <summary>
    /// Its permanent identity, saved with the show: 16 hex digits. What groups, palettes, positions and song
    /// timelines know it by.
    /// </summary>
    public string Uid { get; set; } = NewUid();

    /// <summary>
    /// The universe slot it's patched in.
    /// </summary>
    public int Universe { get; set; }

    public string Name { get; set; } = "";
    public FixtureProfile Profile { get; set; } = new();

    /// <summary>
    /// The first channel, 0-based.
    /// </summary>
    public int Start { get; set; }

    public int Mode { get; set; }

    /// <summary>
    /// One of <see cref="FixtureProfile.ShowCategories"/>, for show generation.
    /// </summary>
    public string Category { get; set; } = "";

    /// <summary>
    /// 1 to 4: which of its category's groups it's in, for show generation.
    /// </summary>
    public int CategoryGroup { get; set; } = 1;

    /// <summary>
    /// Left out of generated shows.
    /// </summary>
    public bool ExcludeGenerate { get; set; }

    /// <summary>
    /// Where it hangs in the 3D view, in metres.
    /// </summary>
    public Vector3 Position { get; set; }

    /// <summary>
    /// How it's turned in the 3D view, in degrees.
    /// </summary>
    public Vector3 Rotation { get; set; } = new(90, 0, 0);

    public IReadOnlyList<FixtureChannel> Channels => Profile.Channels(Mode);

    /// <summary>
    /// 16 random hex digits.
    /// </summary>
    public static string NewUid() => $"{Random.Shared.Next():x8}{Random.Shared.Next():x8}";
}

/// <summary>
/// One universe's side of the patch: where it's sent and the fixtures in it.
/// </summary>
sealed class UniversePatch
{
    public string Ip { get; set; } = "127.0.0.1";
    public int Port { get; set; } = ArtNetPacket.Port;
    public int ArtNetUniverse { get; set; }
    public OutputMode Output { get; set; } = OutputMode.ArtNet;
    public string UsbSerial { get; set; } = "";
    public int UsbMode { get; set; }
    public int SacnPriority { get; set; } = 100;
    public string SacnIp { get; set; } = "";
    public List<PatchedFixture> Fixtures { get; } = [];
}

/// <summary>
/// Every universe's patch, kept in step with <see cref="DmxOutput"/>'s universes: what's patched where, each fixture's
/// home values and virtual dimmer, and where each universe is sent.
/// </summary>
sealed class Patch
{
    readonly DmxOutput _dmx;
    readonly List<UniversePatch> _universes = [];
    int _nextId;

    public Patch(DmxOutput dmx)
    {
        _dmx = dmx;
        SyncToUniverses();
    }

    public IReadOnlyList<UniversePatch> Universes => _universes;

    /// <summary>
    /// The patch changed: a fixture added, removed or re-patched, or another show loaded.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Every patched fixture, universe by universe, in patch order.
    /// </summary>
    public IEnumerable<PatchedFixture> All => _universes.SelectMany(u => u.Fixtures);

    public PatchedFixture? FindByUid(string uid) => All.FirstOrDefault(f => f.Uid == uid);

    /// <summary>
    /// Adds or drops universe patches to match <see cref="DmxOutput.Universes"/>.
    /// </summary>
    public void SyncToUniverses()
    {
        while (_universes.Count < _dmx.UniverseCount)
        {
            // A new universe is sent where its patch says straight away: to this computer, by default.
            _universes.Add(new UniversePatch { ArtNetUniverse = _dmx.Universes[_universes.Count].Number });
            ApplyConnection(_universes.Count - 1);
        }
        while (_universes.Count > _dmx.UniverseCount)
            _universes.RemoveAt(_universes.Count - 1);
    }

    /// <summary>
    /// Adds a universe slot (null at the most there can be).
    /// </summary>
    public UniversePatch? AddUniverse()
    {
        if (_dmx.AddUniverse() is null)
            return null;
        SyncToUniverses();
        Changed?.Invoke();
        return _universes[^1];
    }

    /// <summary>
    /// Removes universe slot <paramref name="index"/> and its fixtures, but never the last one.
    /// </summary>
    public void RemoveUniverse(int index)
    {
        if (index < 0 || index >= _universes.Count || _universes.Count <= 1)
            return;
        _dmx.RemoveUniverse(index);
        _universes.RemoveAt(index);
        for (var u = index; u < _universes.Count; u++)
            foreach (var f in _universes[u].Fixtures)
                f.Universe = u;
        Changed?.Invoke();
    }

    /// <summary>
    /// Sends universe <paramref name="index"/> where its patch says.
    /// </summary>
    public void ApplyConnection(int index)
    {
        var p = _universes[index];
        var u = _dmx.Universes[index];
        if (u.Mode == OutputMode.Usb && (p.Output != OutputMode.Usb || p.UsbSerial != u.UsbSerial))
            _dmx.ReleaseUsb?.Invoke(u.UsbSerial);
        u.Number = p.ArtNetUniverse;
        u.SacnPriority = p.SacnPriority;
        u.SacnUnicastIp = p.SacnIp;
        u.UsbSerial = p.UsbSerial;
        u.UsbMode = p.UsbMode;
        u.SetTarget(p.Ip, p.Port);
        u.SetOutputMode(p.Output);
        if (p.Output == OutputMode.Usb && p.UsbSerial != "")
            _dmx.RouteUsb?.Invoke(p.UsbSerial, p.UsbMode);
    }

    /// <summary>
    /// "Model #n", one more than the highest such number in the patch.
    /// </summary>
    public string DefaultName(FixtureProfile profile)
    {
        var model = profile.Model.Trim();
        if (model == "")
            model = "Fixture";
        var prefix = model + " #";
        var highest = 0;
        foreach (var f in All)
            if (f.Name.StartsWith(prefix, StringComparison.Ordinal) && Json.IsValidInt(f.Name[prefix.Length..]))
                highest = Math.Max(highest, int.Parse(f.Name[prefix.Length..]));
        return $"{prefix}{highest + 1}";
    }

    /// <summary>
    /// Where the <paramref name="n"/>th fixture of a show goes in the 3D view when it has no place of its own: rows of
    /// six over the stage.
    /// </summary>
    public static Vector3 AutoPlace(int n)
    {
        var col = n % 6;
        var row = n / 6;
        return new Vector3((float)((col - 2.5) * 1.7), 4.6f, (float)(-1.5 - row * 1.7));
    }

    /// <summary>
    /// Patches <paramref name="profile"/> in <paramref name="universe"/> at <paramref name="start"/> (0-based), homed:
    /// its channels at their defaults and its virtual dimmer, if it needs one, full.
    /// </summary>
    public PatchedFixture Add(int universe, FixtureProfile profile, int start, int mode = 0, string name = "", Vector3? position = null, Vector3? rotation = null)
    {
        mode = Math.Clamp(mode, 0, Math.Max(profile.ModeCount - 1, 0));
        var fixture = new PatchedFixture
        {
            Id = _nextId++,
            Universe = universe,
            Name = name.Trim() != "" ? name : DefaultName(profile),
            Profile = profile,
            Start = start,
            Mode = mode,
            Category = profile.DefaultShowCategory(mode),
            Position = position ?? AutoPlace(All.Count()),
            Rotation = rotation ?? new Vector3(90, 0, 0),
        };
        _universes[universe].Fixtures.Add(fixture);
        RegisterVirtualDimmer(fixture);
        Home(fixture);
        Changed?.Invoke();
        return fixture;
    }

    /// <summary>
    /// Unpatches <paramref name="fixture"/>.
    /// </summary>
    public void Remove(PatchedFixture fixture)
    {
        SetHighlight(fixture, false);
        if (!_universes[fixture.Universe].Fixtures.Remove(fixture))
            return;
        _dmx.Universes[fixture.Universe].RemoveVirtualDimmer(fixture.Id);
        Changed?.Invoke();
    }

    /// <summary>
    /// Gives an edited profile to every fixture patched with one of its id. Each holds its own copy: a fixture whose
    /// mode keeps its channel count takes the whole profile (its live levels stay as they are); the others need
    /// re-patching, so take only its physical side for now. Returns how many of each.
    /// </summary>
    public (int Updated, int Repatch) UpdateProfile(FixtureProfile profile)
    {
        int updated = 0, repatch = 0;
        foreach (var fixture in All)
        {
            var old = fixture.Profile;
            if (old.Id != profile.Id)
                continue;
            if (fixture.Mode < profile.ModeCount && profile.ChannelCount(fixture.Mode) == old.ChannelCount(fixture.Mode))
            {
                fixture.Profile = profile.Clone();
                RegisterVirtualDimmer(fixture);
                updated++;
            }
            else
            {
                old.Physical = profile.Physical.Clone();
                repatch++;
            }
        }
        if (updated > 0)
            Changed?.Invoke();
        return (updated, repatch);
    }

    void RegisterVirtualDimmer(PatchedFixture fixture) =>
        _dmx.Universes[fixture.Universe].SetVirtualDimmer(fixture.Id, [.. fixture.Profile.VirtualDimmerChannels(fixture.Mode).Select(k => fixture.Start + k)]);

    /// <summary>
    /// Snaps <paramref name="fixture"/>'s channels to their defaults, and its virtual dimmer to full, as its Home button
    /// does.
    /// </summary>
    public void Home(PatchedFixture fixture)
    {
        var universe = _dmx.Universes[fixture.Universe];
        var chans = fixture.Channels;
        for (var k = 0; k < chans.Count; k++)
            universe.SetChannel(fixture.Start + k, chans[k].Default);
        universe.SetVirtualDimmerLevel(fixture.Id, 1);
    }

    /// <summary>
    /// What a fixture's Flash sets, by channel offset: dimmer, red, green, blue and white to their maximum; amber and
    /// UV to their minimum where there's a white; a colour wheel to its open or white slot. Empty for a fixture with
    /// none of those.
    /// </summary>
    public static Dictionary<int, int> FlashTargets(IReadOnlyList<FixtureChannel> chans)
    {
        var output = new Dictionary<int, int>();
        var hasWhite = chans.Any(c => c.Role == Roles.White);
        for (var k = 0; k < chans.Count; k++)
        {
            var ch = chans[k];
            switch (ch.Role)
            {
                case Roles.Dimmer or Roles.Red or Roles.Green or Roles.Blue or Roles.White:
                    output[k] = ch.Max;
                    break;
                case Roles.Amber or Roles.Uv:
                    if (hasWhite)
                        output[k] = ch.Min;
                    break;
                case Roles.ColorWheel:
                    var slot = ch.Ranges.FirstOrDefault(r => r.Label.ToLowerInvariant() is var l && (l.Contains("white") || l.Contains("open")));
                    if (slot is not null)
                        output[k] = (int)Math.Floor((slot.Lo + slot.Hi) / 2.0);
                    break;
            }
        }
        return output;
    }

    /// <summary>
    /// Lights <paramref name="fixture"/> full white (see <see cref="FlashTargets"/>) and returns what to put back:
    /// each channel it changed and its level before.
    /// </summary>
    public Dictionary<int, int> FlashOn(PatchedFixture fixture)
    {
        var universe = _dmx.Universes[fixture.Universe];
        var saved = new Dictionary<int, int>();
        foreach (var (k, v) in FlashTargets(fixture.Channels))
        {
            saved[fixture.Start + k] = universe.GetChannel(fixture.Start + k);
            universe.SetChannel(fixture.Start + k, v);
        }
        return saved;
    }

    /// <summary>
    /// Puts back what <see cref="FlashOn"/> changed, exactly.
    /// </summary>
    public void FlashOff(PatchedFixture fixture, IReadOnlyDictionary<int, int> saved)
    {
        var universe = _dmx.Universes[fixture.Universe];
        foreach (var (c, v) in saved)
            universe.SetChannel(c, v);
    }

    // ------------------------------------------------------------ highlight --

    /// <summary>
    /// Fixtures lit full white to find them (the Positions editor's selection): what each channel had, and its
    /// virtual dimmer's level.
    /// </summary>
    readonly Dictionary<PatchedFixture, (Dictionary<int, int> Channels, double VirtualDimmer)> _highlighted = new(ReferenceEqualityComparer.Instance);

    public bool IsHighlighted(PatchedFixture fixture) => _highlighted.ContainsKey(fixture);

    /// <summary>
    /// Lights <paramref name="fixture"/> full white to find it, as Flash does, with its shutter open and its virtual
    /// dimmer full; or (<paramref name="on"/> false) puts back exactly what it had, every channel, so an
    /// <see cref="Aim"/> is undone too. A show saved meanwhile keeps its real levels (<see cref="LiveToJson"/>).
    /// </summary>
    public void SetHighlight(PatchedFixture fixture, bool on)
    {
        if (on == _highlighted.ContainsKey(fixture))
            return;
        var u = _dmx.Universes[fixture.Universe];
        if (!on)
        {
            var (channels, vdim) = _highlighted[fixture];
            _highlighted.Remove(fixture);
            foreach (var (c, v) in channels)
                u.SetChannel(c, v);
            if (u.HasVirtualDimmer(fixture.Id))
                u.SetVirtualDimmerLevel(fixture.Id, vdim);
            return;
        }
        var chans = fixture.Channels;
        var targets = FlashTargets(chans);
        for (var k = 0; k < chans.Count; k++)
            if (chans[k].Role == Roles.Strobe)
                targets[k] = OpenShutterValue(chans[k]);
        var saved = new Dictionary<int, int>();
        for (var k = 0; k < chans.Count; k++)
            saved[fixture.Start + k] = u.GetChannel(fixture.Start + k);
        _highlighted[fixture] = (saved, u.VirtualDimmerLevel(fixture.Id));
        foreach (var (k, v) in targets)
            u.SetChannel(fixture.Start + k, v);
        if (u.HasVirtualDimmer(fixture.Id))
            u.SetVirtualDimmerLevel(fixture.Id, 1);
    }

    /// <summary>
    /// Points highlighted <paramref name="fixture"/>'s pan and tilt at <paramref name="point"/> (0 to 1 over each
    /// axis, tilt bottom-up: a named position's point).
    /// </summary>
    public void Aim(PatchedFixture fixture, System.Numerics.Vector2 point)
    {
        if (!_highlighted.ContainsKey(fixture))
            return;
        var values = new Dictionary<int, int>();
        WriteAxis(values, fixture.Channels, fixture.Start, Roles.Pan, point.X);
        WriteAxis(values, fixture.Channels, fixture.Start, Roles.Tilt, point.Y);
        var u = _dmx.Universes[fixture.Universe];
        foreach (var (c, v) in values)
            u.SetChannel(c, v);
    }

    /// <summary>
    /// A shutter channel's open value: the middle of its range labelled open, else of the first that isn't closed,
    /// blackout, strobe or pulse; else its default; else (a default of 0 being closed) 255.
    /// </summary>
    public static int OpenShutterValue(FixtureChannel ch)
    {
        var fallback = -1;
        foreach (var r in ch.Ranges)
        {
            var label = r.Label.ToLowerInvariant();
            if (label.Contains("clos") || label.Contains("black") || label.Contains("strob") || label.Contains("puls"))
                continue;
            var mid = (r.Lo + r.Hi) / 2;
            if (label.Contains("open"))
                return mid;
            if (fallback == -1)
                fallback = mid;
        }
        return fallback != -1 ? fallback : ch.Default != 0 ? ch.Default : 255;
    }

    /// <summary>
    /// Writes a pan or tilt axis at <paramref name="norm"/> (0 to 1) into <paramref name="output"/> by channel: 16-bit
    /// where a fine channel follows, else over the channel's range.
    /// </summary>
    public static void WriteAxis(Dictionary<int, int> output, IReadOnlyList<FixtureChannel> chans, int start, string role, double norm)
    {
        for (var k = 0; k < chans.Count; k++)
        {
            if (chans[k].Role != role)
                continue;
            norm = Math.Clamp(norm, 0, 1);
            var fine = k + 1 < chans.Count && (chans[k + 1].Fine || chans[k + 1].Role == role + "_FINE");
            if (fine)
            {
                var v = Gd.RoundToInt(norm * 65535.0);
                output[start + k] = (v >> 8) & 0xFF;
                output[start + k + 1] = v & 0xFF;
            }
            else
                output[start + k] = Gd.RoundToInt(Gd.Lerp(chans[k].Min, chans[k].Max, norm));
            return;
        }
    }

    /// <summary>
    /// A fixture's virtual dimmer level, 0 to 1, or null if it has none.
    /// </summary>
    public double? VirtualDimmer(PatchedFixture fixture)
    {
        var u = _dmx.Universes[fixture.Universe];
        return u.HasVirtualDimmer(fixture.Id) ? u.VirtualDimmerLevel(fixture.Id) : null;
    }

    /// <summary>
    /// Sets a fixture's virtual dimmer, as its slider does: to the nearest of 256 steps.
    /// </summary>
    public void SetVirtualDimmer(PatchedFixture fixture, double level) =>
        _dmx.Universes[fixture.Universe].SetVirtualDimmerLevel(fixture.Id, Math.Clamp(Gd.RoundToInt(level * 255), 0, 255) / 255.0);

    /// <summary>
    /// The channels of <paramref name="role"/> (not fine channels, even carrying their coarse channel's role) of the
    /// fixtures in group <paramref name="group"/> if it's a group, else in universe slot <paramref name="universe"/>
    /// (-1 for every one): what an effect or reactor drives.
    /// </summary>
    public List<ChannelTarget> Targets(string role, int universe, IReadOnlySet<string>? group)
    {
        var output = new List<ChannelTarget>();
        for (var ui = 0; ui < _universes.Count; ui++)
        {
            if (group is null && universe != -1 && universe != ui)
                continue;
            foreach (var fixture in _universes[ui].Fixtures)
            {
                if (group is not null && !group.Contains(fixture.Uid))
                    continue;
                var chans = fixture.Channels;
                for (var k = 0; k < chans.Count; k++)
                    if (chans[k].Role == role && !chans[k].Fine)
                        output.Add(new ChannelTarget(ui, fixture.Start + k));
            }
        }
        return output;
    }

    /// <summary>
    /// Per universe, every patched fixture's intensity channels: its DIMMER (not a fine one), or without one its
    /// colour and white channels. Only the fixtures in <paramref name="only"/> when it's given.
    /// </summary>
    public List<IReadOnlySet<int>> IntensityChannels(int n, IReadOnlySet<string>? only = null)
    {
        var output = new List<HashSet<int>>(n);
        for (var i = 0; i < n; i++)
            output.Add([]);
        foreach (var fixture in All)
        {
            if (fixture.Universe < 0 || fixture.Universe >= n || (only is not null && !only.Contains(fixture.Uid)))
                continue;
            var chans = fixture.Channels;
            var dims = new List<int>();
            var colours = new List<int>();
            for (var k = 0; k < chans.Count; k++)
            {
                if (chans[k].Fine)
                    continue;
                if (chans[k].Role == Roles.Dimmer)
                    dims.Add(k);
                else if (Roles.Colour.Contains(chans[k].Role))
                    colours.Add(k);
            }
            foreach (var k in dims.Count > 0 ? dims : colours)
                output[fixture.Universe].Add(fixture.Start + k);
        }
        return [.. output];
    }

    // ------------------------------------------------------------ show file --

    /// <summary>
    /// Universe <paramref name="index"/>'s connection and fixtures, as a show file holds them.
    /// </summary>
    public JsonObject PatchToJson(int index)
    {
        var p = _universes[index];
        return new JsonObject
        {
            ["ip"] = p.Ip,
            ["port"] = p.Port,
            ["artnet_universe"] = p.ArtNetUniverse,
            ["output"] = p.Output switch
            {
                OutputMode.Sacn => "sacn",
                OutputMode.Usb => "usb",
                _ => "artnet",
            },
            ["usb_serial"] = p.UsbSerial,
            ["usb_mode"] = p.UsbMode,
            ["sacn_priority"] = p.SacnPriority,
            ["sacn_ip"] = p.SacnIp,
            ["fixtures"] = new JsonArray(
                [
                    .. p.Fixtures.Select(f =>
                        (JsonNode)
                            new JsonObject
                            {
                                ["uid"] = f.Uid,
                                ["name"] = f.Name,
                                ["start"] = f.Start,
                                ["mode"] = f.Mode,
                                ["category"] = f.Category,
                                ["category_group"] = f.CategoryGroup,
                                ["exclude_generate"] = f.ExcludeGenerate,
                                ["profile"] = f.Profile.ToJson(),
                                ["pos"] = Vector(f.Position),
                                ["rot"] = Vector(f.Rotation),
                            }
                    ),
                ]
            ),
        };
    }

    static JsonArray Vector(Vector3 v) => [v.X, v.Y, v.Z];

    static Vector3 Vector(JsonNode? node, Vector3 fallback) =>
        node is JsonArray { Count: 3 } a ? new Vector3((float)Json.AsDouble(a[0]), (float)Json.AsDouble(a[1]), (float)Json.AsDouble(a[2])) : fallback;

    /// <summary>
    /// Replaces universe <paramref name="index"/>'s connection and fixtures with a show file's. Every fixture is homed;
    /// the show's live levels go on afterwards (<see cref="ApplyLive"/>). <paramref name="usbAvailable"/> says
    /// whether a USB universe can be sent to USB; otherwise it falls back to Art-Net.
    /// </summary>
    public void ApplyPatch(int index, JsonNode? d, bool usbAvailable = false)
    {
        var p = _universes[index];
        if (d.Has("ip"))
            p.Ip = d.Str("ip");
        if (d.Has("port"))
            p.Port = d.Int("port");
        if (d.Has("artnet_universe"))
            p.ArtNetUniverse = d.Int("artnet_universe");
        p.SacnPriority = Math.Clamp(d.Int("sacn_priority", 100), 0, 200);
        p.SacnIp = d.Str("sacn_ip", "");
        p.UsbMode = Math.Max(d.Int("usb_mode", 0), 0);
        p.UsbSerial = d.Str("usb_serial", "");
        p.Output = d.Str("output", "artnet") switch
        {
            "usb" when usbAvailable => OutputMode.Usb,
            "sacn" => OutputMode.Sacn,
            _ => OutputMode.ArtNet,
        };
        ApplyConnection(index);

        var universe = _dmx.Universes[index];
        foreach (var f in p.Fixtures)
            universe.RemoveVirtualDimmer(f.Id);
        p.Fixtures.Clear();
        var i = 0;
        foreach (var entry in d.Arr("fixtures"))
        {
            var profile = FixtureProfile.FromJson(entry.Get("profile"));
            var mode = Math.Clamp(entry.Int("mode", 0), 0, Math.Max(profile.ModeCount - 1, 0));
            // Older shows have none (or an unknown one): the mode's default.
            var category = entry.Str("category", "");
            if (!FixtureProfile.ShowCategories.Contains(category))
                category = profile.DefaultShowCategory(mode);
            var uid = entry.Str("uid", "");
            var fixture = new PatchedFixture
            {
                Id = _nextId++,
                Uid = uid != "" ? uid : PatchedFixture.NewUid(),
                Universe = index,
                Name = entry.Has("name") ? entry.Str("name") : profile.Model,
                Profile = profile,
                Start = entry.Int("start", 0),
                Mode = mode,
                Category = category,
                CategoryGroup = Math.Clamp(entry.Int("category_group", 1), 1, 4),
                ExcludeGenerate = entry.Bool("exclude_generate", false),
                Position = Vector(entry.Get("pos"), AutoPlace(i)),
                Rotation = Vector(entry.Get("rot"), new Vector3(90, 0, 0)),
            };
            p.Fixtures.Add(fixture);
            RegisterVirtualDimmer(fixture);
            Home(fixture);
            i++;
        }
    }

    /// <summary>
    /// Universe <paramref name="index"/>'s live state: its levels (the channels that aren't 0) and each fixture's
    /// virtual dimmer level, in patch order (null where it has none).
    /// </summary>
    public JsonObject LiveToJson(int index)
    {
        var universe = _dmx.Universes[index];
        var channels = new Dictionary<int, int>();
        for (var c = 0; c < DmxUniverse.Size; c++)
            if (universe.Data[c] != 0)
                channels[c] = universe.Data[c];
        // Highlighted fixtures: the levels they really have, not the white.
        foreach (var (f, (saved, _)) in _highlighted)
        {
            if (f.Universe != index)
                continue;
            foreach (var (c, v) in saved)
            {
                if (v != 0)
                    channels[c] = v;
                else
                    channels.Remove(c);
            }
        }
        double? Vdim(PatchedFixture f) => VirtualDimmer(f) is { } v ? _highlighted.TryGetValue(f, out var h) ? h.VirtualDimmer : v : null;
        return new JsonObject
        {
            ["channels"] = Look.ChannelsToJson(channels),
            ["virtual_dimmers"] = new JsonArray([.. _universes[index].Fixtures.Select(f => Vdim(f) is { } v ? JsonValue.Create(v) : null)]),
        };
    }

    /// <summary>
    /// Puts back <see cref="LiveToJson"/>'s state once the patch is in place: the levels, then the virtual dimmers.
    /// </summary>
    public void ApplyLive(int index, JsonNode? d)
    {
        var buffer = new byte[DmxUniverse.Size];
        foreach (var (c, v) in Look.ChannelsFromJson(d.Get("channels")))
            if (c is >= 0 and < DmxUniverse.Size)
                buffer[c] = (byte)v;
        LoadLook(index, buffer);
        var vdims = d.Arr("virtual_dimmers");
        var fixtures = _universes[index].Fixtures;
        for (var i = 0; i < Math.Min(vdims.Count, fixtures.Count); i++)
            if (vdims[i] is not null && VirtualDimmer(fixtures[i]) is not null)
                SetVirtualDimmer(fixtures[i], Json.AsDouble(vdims[i]));
    }

    /// <summary>
    /// Puts a full look (512 bytes; a shorter one is padded with 0) into universe <paramref name="index"/>'s levels, as
    /// Load to Patch does: its fixtures' virtual dimmers open, as the look holds each colour as it's stored.
    /// </summary>
    public void LoadLook(int index, ReadOnlySpan<byte> look)
    {
        var buffer = new byte[DmxUniverse.Size];
        look[..Math.Min(look.Length, DmxUniverse.Size)].CopyTo(buffer);
        _dmx.Universes[index].Data = buffer;
        foreach (var f in _universes[index].Fixtures)
            _dmx.Universes[index].SetVirtualDimmerLevel(f.Id, 1);
    }

    /// <summary>
    /// Tells everything watching that the patch has changed, after a show's loaded.
    /// </summary>
    public void RaiseChanged() => Changed?.Invoke();
}
