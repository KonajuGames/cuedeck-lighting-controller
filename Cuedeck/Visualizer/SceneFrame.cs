using System.Numerics;
using Brutal.Numerics;
using Cuedeck.Common;
using Cuedeck.Show;
using Cuedeck.UI;

namespace Cuedeck.Visualizer;

/// <summary>
/// One shape to draw: which, where (a row-vector world matrix), its colour (linear, as the renderer works) and how
/// much it glows regardless of the light on it (0 to 1).
/// </summary>
readonly record struct DrawItem(MeshKind Mesh, Matrix4x4 World, float4 Colour, float Glow = 0);

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
            f.Items.Add(
                new DrawItem(
                    MeshKind.Box,
                    Matrix4x4.CreateScale(p.Bounds.Size) * Matrix4x4.CreateTranslation(p.Bounds.Centre) * VizScene.PropWorld(p),
                    ReferenceEquals(p, selected) ? Lerp(PropColour, Selected, 0.4f) : PropColour,
                    ReferenceEquals(p, selected) ? 0.4f : 0
                )
            );
        foreach (var fx in patch.All)
        {
            if (!rigs.TryGetValue(fx, out var rig))
                continue;
            var colour = bodyColours.TryGetValue(fx.Profile.FullName, out var c) ? c : DefaultBody;
            AddBody(f.Items, rig, FixtureRig.Placement(fx.Position, fx.Rotation), colour);
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

    static float4 Linear(Rgb c) => Colors.FromSrgb(c.R, c.G, c.B);

    static float4 Lerp(float4 a, float4 b, float t) => a + (b - a) * t;
}
