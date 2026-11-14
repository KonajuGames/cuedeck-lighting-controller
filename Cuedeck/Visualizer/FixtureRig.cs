using System.Numerics;
using Cuedeck.Common;
using Cuedeck.Fixtures;

namespace Cuedeck.Visualizer;

/// <summary>
/// One light source of a fixture in the 3D view: where it sits on the fixture (from the profile's heads), and the
/// colour and level it's at.
/// </summary>
sealed class RigHead(Vector3 offset, Vector3 rotation)
{
    public Vector3 Offset { get; } = offset;

    /// <summary>
    /// Radians, relative to the first head (applied Y, then X, then Z, as Godot's nodes are).
    /// </summary>
    public Vector3 Rotation { get; } = rotation;

    public Rgb Colour { get; set; } = Rgb.White;

    /// <summary>
    /// 0 to 1, a strobe's off half included.
    /// </summary>
    public double Level { get; set; }
}

/// <summary>
/// A patched fixture as the 3D view builds it, without drawing it: what kind of body it gets (by its profile's
/// category, else guessed from its channels), whether it hangs in a yoke that follows its pan and tilt, its heads (one
/// light and beam each) and where each points. Each frame <see cref="Update"/> takes its state from the DMX
/// (<see cref="DmxRender"/>): the pan and tilt eased towards it, the beam's angle, the strobe, each head's colour and
/// level. Transforms are row-vector matrices (System.Numerics'), local to the fixture's own placement; a light shines
/// along its frame's -Z. sLight's <c>FixtureView</c>, but for the GDTF models (slice 6e).
/// </summary>
sealed class FixtureRig
{
    /// <summary>
    /// The bodies that hang in a yoke.
    /// </summary>
    static readonly string[] YokeKinds = ["moving_head", "scanner"];

    /// <summary>
    /// Bodies that keep their shape with pan and tilt (anything else that has them is drawn in a yoke).
    /// </summary>
    static readonly string[] KeepKinds = ["moving_head", "scanner", "strip", "bar", "pixel_bar"];

    readonly List<RigHead> _heads = [];

    public FixtureRig(FixtureProfile profile, int mode)
    {
        Profile = profile;
        Mode = mode;
        Category = ResolveCategory(profile, mode);
        var kind = Category;
        if (PansOrTilts(profile, mode) && !KeepKinds.Contains(kind))
            kind = "moving_head";
        Kind = kind switch
        {
            "moving_head" or "scanner" => kind,
            "blinder" or "strobe" => "blinder",
            "strip" or "bar" or "pixel_bar" => "bar",
            _ => "can",
        };
        (HeadBase, HasCone, HasFace, var angle) = Kind switch
        {
            "moving_head" or "scanner" => (new Vector3(0, 0, -0.13f), true, false, Category == "wash" ? 32.0 : 0),
            "blinder" => (new Vector3(0, 0, -0.07f), false, true, 65.0),
            "bar" => (new Vector3(0, 0, -0.06f), false, true, 55.0),
            _ => (new Vector3(0, 0, -0.13f), Category != "wash", false, Category == "wash" ? 32.0 : 0),
        };
        HalfAngle = angle > 0 ? angle * 0.5 : Math.Clamp(profile.Physical.BeamDeg * 0.5, 2, 60);
        var groups = profile.HeadGroups(mode);
        if (groups.Count == 0)
            _heads.Add(new RigHead(Vector3.Zero, Vector3.Zero));
        else
            foreach (var g in groups)
                _heads.Add(new RigHead(g.Offset, g.Rotation));
    }

    public FixtureProfile Profile { get; }
    public int Mode { get; }

    /// <summary>
    /// The profile's category, or what its channels suggest: moving_head, blinder, wash, par or generic.
    /// </summary>
    public string Category { get; }

    /// <summary>
    /// The schematic body: "moving_head" or "scanner" (a base, a yoke and a head), "blinder" (a panel), "bar" (a long
    /// box) or "can" (a cylinder).
    /// </summary>
    public string Kind { get; }

    /// <summary>
    /// Whether it hangs in a yoke that pans, holding a head that tilts.
    /// </summary>
    public bool Yoked => YokeKinds.Contains(Kind);

    /// <summary>
    /// Where the heads start on their carrier (the face of the head, or the body).
    /// </summary>
    public Vector3 HeadBase { get; }

    /// <summary>
    /// Whether its heads throw a visible beam (a cone).
    /// </summary>
    public bool HasCone { get; }

    /// <summary>
    /// Whether its heads are lit faces (blinders, bars) rather than lenses.
    /// </summary>
    public bool HasFace { get; }

    public IReadOnlyList<RigHead> Heads => _heads;

    /// <summary>
    /// Radians either side of centre, eased towards the DMX's.
    /// </summary>
    public double Pan { get; private set; }

    public double Tilt { get; private set; }

    /// <summary>
    /// Half the beam's angle, degrees.
    /// </summary>
    public double HalfAngle { get; private set; }

    double _strobePhase;

    /// <summary>
    /// The gobo in its first head's light: its base64 artwork, or "" for none.
    /// </summary>
    public string Gobo { get; private set; } = "";

    /// <summary>
    /// How far the gobo's turned, radians.
    /// </summary>
    public double GoboRoll { get; private set; }

    static string ResolveCategory(FixtureProfile profile, int mode)
    {
        if (profile.Physical.Category != "")
            return profile.Physical.Category;
        var roles = profile.Channels(mode).Select(c => c.Role).ToHashSet();
        if (roles.Contains(Roles.Pan) && roles.Contains(Roles.Tilt))
            return "moving_head";
        if (roles.Contains(Roles.Strobe) && !roles.Contains(Roles.Red))
            return "blinder";
        if (roles.Contains(Roles.Red) || roles.Contains(Roles.Dimmer))
            return profile.Physical.BeamDeg > 28 ? "wash" : "par";
        return "generic";
    }

    static bool PansOrTilts(FixtureProfile profile, int mode) => profile.Channels(mode).Any(c => c.Role is Roles.Pan or Roles.Tilt);

    /// <summary>
    /// Takes the fixture's state from its DMX, <paramref name="delta"/> seconds after the last.
    /// </summary>
    public void Update(FixtureState st, double delta)
    {
        var k = Math.Clamp(delta * 8, 0, 1);
        if (Yoked)
        {
            Pan = Gd.Lerp(Pan, st.Pan * Math.PI / 180, k);
            Tilt = Gd.Lerp(Tilt, st.Tilt * Math.PI / 180, k);
        }
        var mult = 1.0;
        if (st.StrobeHz > 0.01)
        {
            _strobePhase += delta * st.StrobeHz;
            mult = (_strobePhase % 1) < 0.5 ? 1 : 0;
        }
        else
            _strobePhase = 0;
        HalfAngle = Math.Clamp(st.Zoom * 0.5, 2, 65);
        Gobo = st.Gobo;
        if (Gobo != "" && st.GoboRotation != 0)
            GoboRoll += st.GoboRotation * Math.PI / 180 * delta;
        for (var i = 0; i < _heads.Count; i++)
        {
            var hs = i < st.Heads.Count ? st.Heads[i] : st.Heads[^1];
            _heads[i].Level = hs.Level * mult;
            _heads[i].Colour = hs.Color;
        }
    }

    // ---------------------------------------------------------- transforms --

    /// <summary>
    /// Where a fixture's placed, as sLight stores it: metres, and degrees tilted down, turned (heading) and rolled.
    /// </summary>
    public static Matrix4x4 Placement(Vector3 position, Vector3 rotationDegrees)
    {
        const float D = MathF.PI / 180;
        return Euler(new Vector3(-rotationDegrees.X * D, rotationDegrees.Y * D, rotationDegrees.Z * D)) * Matrix4x4.CreateTranslation(position);
    }

    /// <summary>
    /// A Godot node's rotation (radians, applied Z, then X, then Y to a point: its YXZ order).
    /// </summary>
    public static Matrix4x4 Euler(Vector3 r) => Matrix4x4.CreateRotationZ(r.Z) * Matrix4x4.CreateRotationX(r.X) * Matrix4x4.CreateRotationY(r.Y);

    /// <summary>
    /// The yoke's frame (it pans about its Y), in the room, for a fixture placed at <paramref name="fixture"/>.
    /// </summary>
    public Matrix4x4 YokeWorld(Matrix4x4 fixture) => Matrix4x4.CreateRotationY((float)Pan) * Matrix4x4.CreateTranslation(0, 0.16f, 0) * fixture;

    /// <summary>
    /// The frame the heads are built on: the tilting head's face, or the body itself.
    /// </summary>
    public Matrix4x4 CarrierWorld(Matrix4x4 fixture)
    {
        if (!Yoked)
            return fixture;
        var head = Matrix4x4.CreateRotationX((float)Tilt) * Matrix4x4.CreateTranslation(0, 0.22f, 0) * YokeWorld(fixture);
        return Matrix4x4.CreateRotationX(MathF.PI / 2) * head; // centred, it aims along the pan axis, out of the yoke
    }

    /// <summary>
    /// The tilting head's frame (a yoked fixture's box), in the room.
    /// </summary>
    public Matrix4x4 HeadBoxWorld(Matrix4x4 fixture) => Matrix4x4.CreateRotationX((float)Tilt) * Matrix4x4.CreateTranslation(0, 0.22f, 0) * YokeWorld(fixture);

    /// <summary>
    /// Head <paramref name="i"/>'s frame in the room: it shines along -Z.
    /// </summary>
    public Matrix4x4 HeadWorld(int i, Matrix4x4 fixture)
    {
        var h = _heads[i];
        return Euler(h.Rotation) * Matrix4x4.CreateTranslation(HeadBase + h.Offset) * CarrierWorld(fixture);
    }

    /// <summary>
    /// Which way head <paramref name="i"/> shines, in the room.
    /// </summary>
    public Vector3 HeadAim(int i, Matrix4x4 fixture) => Vector3.TransformNormal(-Vector3.UnitZ, HeadWorld(i, fixture));

    /// <summary>
    /// The axis the yoke pans about, in the room.
    /// </summary>
    public Vector3 PanAxis(Matrix4x4 fixture) => Vector3.TransformNormal(Vector3.UnitY, YokeWorld(fixture));
}
