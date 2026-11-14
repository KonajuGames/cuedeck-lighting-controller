using System.Numerics;
using Brutal.Numerics;
using Cuedeck.Common;
using Cuedeck.Show;
using Cuedeck.UI;

namespace Cuedeck.Visualizer;

/// <summary>
/// One shape to draw: which, where (a row-vector world matrix), its colour (linear, as the renderer works) and how
/// much it glows regardless of the light on it (0 to 1).
/// A model's mesh (<paramref name="Custom"/>) is drawn in place of the shape when it's given.
readonly record struct DrawItem(MeshKind Mesh, Matrix4x4 World, float4 Colour, float Glow = 0, MeshData? Custom = null);

/// <summary>
/// One head's light, as Godot's spotlight was set up for it: where it is and which way it shines, its colour times
/// its energy (linear), the cosine of its cone's half angle, its range, and how it falls off with distance (Godot's
/// <c>spot_attenuation</c>, a power of the distance) and towards the cone's edge (<c>spot_angle_attenuation</c>), and
/// the gobo it projects (base64 artwork, "" for none).
/// A gobo turns with the light about its direction: <paramref name="Right"/> is the light's right, turned with it.
/// </summary>
readonly record struct SpotLight(
    Vector3 Position,
    Vector3 Direction,
    Vector3 Colour,
    float CosCone,
    Vector3 Right = default,
    string Gobo = "",
    float Range = SpotLight.RANGE,
    float Decay = 1.2f,
    float EdgeSoftness = 0.6f
)
{
    /// <summary>
    /// sLight's <c>spot_range</c>, metres.
    /// </summary>
    public const float RANGE = 28;

    /// <summary>
    /// Its light's energy at full (sLight's <c>_base_energy</c>).
    /// </summary>
    public const float ENERGY = 12;
}

/// <summary>
/// Everything the 3D pass needs for one frame, worked out without the GPU: the shapes, the camera's matrices, the
/// backdrop and the light that isn't the fixtures' (the ambient and the work light). Colours are linear.
/// </summary>
sealed class SceneFrame
{
    /// <summary>
    /// The ambient light: Godot's colour times its energy.
    /// </summary>
    public static readonly float4 Ambient = Colors.FromSrgb(0.5f, 0.5f, 0.55f) * 0.35f;

    public static readonly float4 WorkLightColour = Colors.FromSrgb(0.9f, 0.92f, 1.0f) * 0.5f;

    /// <summary>
    /// Where the work light shines (sLight's turned -55° about X, then -30° about Y).
    /// </summary>
    public static readonly Vector3 WorkLightDirection = Vector3.Normalize(
        Vector3.TransformNormal(-Vector3.UnitZ, FixtureRig.Euler(new Vector3(-55 * MathF.PI / 180, -30 * MathF.PI / 180, 0)))
    );

    static readonly float4 Selected = Colors.FromSrgb(0.2f, 1.0f, 1.0f);
    static readonly float4 TrussColour = Colors.FromSrgb(0.1f, 0.1f, 0.11f);
    static readonly float4 PropColour = Colors.FromSrgb(0.35f, 0.35f, 0.38f);
    static readonly float4 DefaultBody = Colors.FromSrgb(0.14f, 0.14f, 0.14f);

    public List<DrawItem> Items { get; } = [];

    /// <summary>
    /// The fixtures' heads that are lit, each a spotlight.
    /// </summary>
    public List<SpotLight> Lights { get; } = [];

    /// <summary>
    /// The beams, drawn added over everything: open cones from the lit heads that throw one, their colour (linear,
    /// its brightness already in it) fading out along their length.
    /// </summary>
    public List<DrawItem> Beams { get; } = [];

    /// <summary>
    /// A beam's length (sLight's <c>_beam_len</c>), metres.
    /// </summary>
    public const float BEAM_LENGTH = 13;
    public Matrix4x4 View { get; private set; }

    /// <summary>
    /// Depth 0 to 1 and Y up, as System.Numerics makes it (the renderer flips it for Vulkan).
    /// </summary>
    public Matrix4x4 Projection { get; private set; }

    public Vector3 CameraPosition { get; private set; }
    public float4 Background { get; private set; }

    /// <summary>
    /// The work light's colour times its energy; black when it's off.
    /// </summary>
    public float4 WorkLight { get; private set; }

    /// <summary>
    /// How thick the haze is: its density per metre (sLight's fog density).
    /// </summary>
    public float HazeDensity { get; private set; }

    /// <summary>
    /// The frame for <paramref name="scene"/> seen at <paramref name="aspect"/> (width over height), its fixtures
    /// drawn from their <paramref name="rigs"/> in their model's colour (<paramref name="bodyColours"/>, by full name).
    /// </summary>
    public static SceneFrame Build(VizScene scene, Patch patch, IReadOnlyDictionary<PatchedFixture, FixtureRig> rigs, IReadOnlyDictionary<string, float4> bodyColours, float aspect)
    {
        var f = new SceneFrame
        {
            View = scene.Camera.View,
            Projection = scene.Camera.Projection(aspect),
            CameraPosition = scene.Camera.Position,
            Background = Linear(scene.Room.Sky),
            WorkLight = scene.WorkLight ? WorkLightColour : new float4(0, 0, 0, 0),
            HazeDensity = (float)(scene.Haze * VizScene.HAZE_MAX),
        };
        var room = scene.Room;
        f.Items.Add(new DrawItem(MeshKind.Plane, Matrix4x4.CreateScale(room.Floor.X, 1, room.Floor.Y), Linear(VizScene.FloorColour)));
        f.Items.Add(
            new DrawItem(
                MeshKind.Plane,
                Matrix4x4.CreateScale(room.Floor.X, 1, 8) * Matrix4x4.CreateRotationX(MathF.PI / 2) * Matrix4x4.CreateTranslation(0, 4, -room.Floor.Y * 0.5f),
                Linear(room.Wall)
            )
        );
        var selected = scene.Selected;
        foreach (var t in scene.Trusses)
            f.Items.Add(
                new DrawItem(
                    MeshKind.Box,
                    Matrix4x4.CreateScale((float)t.Length, 0.16f, 0.16f) * VizScene.TrussWorld(t),
                    ReferenceEquals(t, selected) ? Lerp(TrussColour, Selected, 0.4f) : TrussColour,
                    ReferenceEquals(t, selected) ? 0.4f : 0
                )
            );
        foreach (var p in scene.Props)
        {
            var sel = ReferenceEquals(p, selected);
            if (ModelCache.Get(p.Path) is { Parts.Count: > 0 } model)
            {
                // Its model, in its own colours; picked by the box round it.
                p.Bounds = ((model.Bounds.Min + model.Bounds.Max) / 2, model.Bounds.Max - model.Bounds.Min);
                foreach (var part in model.Parts)
                {
                    var c = Linear(part.Colour);
                    f.Items.Add(new DrawItem(MeshKind.Box, part.Transform * VizScene.PropWorld(p), sel ? Lerp(c, Selected, 0.4f) : c, sel ? 0.4f : 0, part.Mesh));
                }
            }
            else
                f.Items.Add(
                    new DrawItem(
                        MeshKind.Box,
                        Matrix4x4.CreateScale(p.Bounds.Size) * Matrix4x4.CreateTranslation(p.Bounds.Centre) * VizScene.PropWorld(p),
                        sel ? Lerp(PropColour, Selected, 0.4f) : PropColour,
                        sel ? 0.4f : 0
                    )
                );
        }
        foreach (var fx in patch.All)
        {
            if (!rigs.TryGetValue(fx, out var rig))
                continue;
            var colour = bodyColours.TryGetValue(fx.Profile.FullName, out var c) ? c : DefaultBody;
            var placement = FixtureRig.Placement(fx.Position, fx.Rotation);
            AddBody(f.Items, rig, placement, colour);
            AddLights(f.Lights, rig, placement);
            AddHeads(f, rig, placement);
            if (ReferenceEquals(fx, selected))
                f.Items.Add(new DrawItem(MeshKind.Ring, Matrix4x4.CreateTranslation(0, -0.02f, 0) * FixtureRig.Placement(fx.Position, fx.Rotation), Selected, 1));
        }
        return f;
    }

    /// <summary>
    /// A fixture's schematic body, by its kind, at <paramref name="world"/> (sLight's sizes).
    /// </summary>
    static void AddBody(List<DrawItem> items, FixtureRig rig, Matrix4x4 world, float4 colour)
    {
        void Box(Vector3 size, Vector3 at, Matrix4x4 frame) =>
            items.Add(new DrawItem(MeshKind.Box, Matrix4x4.CreateScale(size) * Matrix4x4.CreateTranslation(at) * frame, colour));
        switch (rig.Kind)
        {
            case "gdtf":
                // Its parts' models, in its model's colour (sLight tinted them).
                rig.EachNode(
                    world,
                    (node, at) =>
                    {
                        if (node.Model is { } m)
                            foreach (var part in m.Parts)
                                items.Add(new DrawItem(MeshKind.Box, part.Transform * at, colour, 0, part.Mesh));
                    }
                );
                break;
            case "moving_head" or "scanner":
                items.Add(new DrawItem(MeshKind.Base, world, colour));
                var yoke = rig.YokeWorld(world);
                Box(new Vector3(0.04f, 0.26f, 0.14f), new Vector3(-0.16f, 0.13f, 0), yoke);
                Box(new Vector3(0.04f, 0.26f, 0.14f), new Vector3(0.16f, 0.13f, 0), yoke);
                Box(new Vector3(0.24f, 0.20f, 0.24f), Vector3.Zero, rig.HeadBoxWorld(world));
                break;
            case "blinder":
                Box(new Vector3(0.55f, 0.38f, 0.12f), Vector3.Zero, world);
                break;
            case "bar":
                var span = rig.Heads.Max(h => Math.Abs(h.Offset.X) * 2);
                Box(new Vector3(Math.Max(1, span + 0.2f), 0.09f, 0.09f), Vector3.Zero, world);
                break;
            default:
                items.Add(
                    new DrawItem(
                        MeshKind.Cylinder,
                        Matrix4x4.CreateScale(0.22f, 0.24f, 0.22f) * Matrix4x4.CreateRotationX(MathF.PI / 2) * Matrix4x4.CreateTranslation(0, 0, -0.02f) * world,
                        colour
                    )
                );
                break;
        }
    }

    /// <summary>
    /// A spotlight for each of a fixture's heads that's lit (over 0.2 %, as sLight hid its lights below that).
    /// </summary>
    static void AddLights(List<SpotLight> lights, FixtureRig rig, Matrix4x4 placement)
    {
        var cos = (float)Math.Cos(rig.HalfAngle * Math.PI / 180);
        for (var i = 0; i < rig.Heads.Count; i++)
        {
            var h = rig.Heads[i];
            if (h.Level <= 0.002)
                continue;
            var world = rig.HeadWorld(i, placement);
            var c = Linear(h.Colour) * (float)(SpotLight.ENERGY * h.Level);
            // The first head carries the gobo, turned about the light's direction.
            var gobo = i == 0 ? rig.Gobo : "";
            var right = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, Matrix4x4.CreateRotationZ(i == 0 ? (float)rig.GoboRoll : 0) * world));
            lights.Add(
                new SpotLight(
                    Vector3.Transform(Vector3.Zero, world),
                    Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, world)),
                    new Vector3(c.X, c.Y, c.Z),
                    cos,
                    right,
                    gobo
                )
            );
        }
    }

    static readonly float4 DarkFace = Colors.FromSrgb(0.02f, 0.02f, 0.02f);

    /// <summary>
    /// What a fixture's heads show themselves: a lit head's beam (sLight's cone: as wide as its angle, 13 m long,
    /// 0.16 of its level bright up to 0.3), or the lit face of a blinder's or bar's head (its colour glowing seven times
    /// its level).
    /// </summary>
    static void AddHeads(SceneFrame f, FixtureRig rig, Matrix4x4 placement)
    {
        var wide = (float)Math.Tan(rig.HalfAngle * Math.PI / 180) * BEAM_LENGTH;
        for (var i = 0; i < rig.Heads.Count; i++)
        {
            var h = rig.Heads[i];
            var world = rig.HeadWorld(i, placement);
            if (rig.HasCone && h.Level > 0.004)
            {
                var c = Linear(h.Colour);
                var bright = (float)Math.Clamp(0.16 * h.Level, 0, 0.30);
                f.Beams.Add(
                    new DrawItem(
                        MeshKind.Cone,
                        Matrix4x4.CreateScale(wide, BEAM_LENGTH, wide)
                            * Matrix4x4.CreateRotationX(-MathF.PI / 2)
                            * Matrix4x4.CreateTranslation(0, 0, -BEAM_LENGTH / 2)
                            * world,
                        new float4(c.X, c.Y, c.Z, bright)
                    )
                );
            }
            if (rig.HasFace)
            {
                var size = rig.Heads.Count > 1 ? new Vector3(0.22f, 0.14f, 0.02f) : new Vector3(0.5f, 0.32f, 0.02f);
                var lit = h.Level > 0.002;
                f.Items.Add(new DrawItem(MeshKind.Box, Matrix4x4.CreateScale(size) * world, lit ? Linear(h.Colour) : DarkFace, (float)(h.Level * 7)));
            }
        }
    }

    static float4 Linear(Rgb c) => Colors.FromSrgb(c.R, c.G, c.B);

    static float4 Lerp(float4 a, float4 b, float t) => a + (b - a) * t;
}
