using System.Numerics;
using System.Text.Json.Nodes;
using Brutal.Numerics;
using Cuedeck.Common;

namespace Cuedeck.Visualizer;

/// <summary>
/// A camera position kept by name (the View dropdown's): what it looks at, from which side, how high and how far, its
/// field of view, and whether it's flat (orthographic). Saved with the show as sLight wrote it.
/// </summary>
sealed record CameraView(string Name, double TargetX, double TargetY, double TargetZ, double Yaw, double Pitch, double Distance, double Fov = 55, bool Ortho = false)
{
    public JsonObject ToJson() =>
        new()
        {
            ["name"] = Name,
            ["target"] = new JsonArray(TargetX, TargetY, TargetZ),
            ["yaw"] = Yaw,
            ["pitch"] = Pitch,
            ["dist"] = Distance,
            ["fov"] = Fov,
            ["ortho"] = Ortho,
        };

    public static CameraView FromJson(JsonNode? v)
    {
        var t = v.Arr("target");
        double At(int i) => i < t.Count ? Json.AsDouble(t[i]) : 0;
        return new CameraView(v.Str("name"), At(0), At(1), At(2), v.Num("yaw"), v.Num("pitch"), v.Num("dist", 15), v.Num("fov", 55), v.Bool("ortho"));
    }
}

/// <summary>
/// The 3D view's camera: it orbits a point in the room (dragging turns it round and up and down, the wheel moves it
/// in and out, a shift-drag or middle-drag slides the point), looking at it as Godot's <c>look_at</c> does. Metres,
/// Y up, the stage's back wall towards -Z. Its matrices take row vectors (System.Numerics' way) and give depth 0 to
/// 1 with Y up. sLight's visualizer camera.
/// </summary>
sealed class OrbitCamera
{
    public const float NEAR = 0.05f;
    public const float FAR = 250;

    public Vector3 Target { get; set; } = new(0, 1.5f, -3);
    public double Yaw { get; set; } = 0.4;
    public double Pitch { get; set; } = 0.35;
    public double Distance { get; set; } = 15;

    /// <summary>
    /// The vertical field of view, degrees (perspective only).
    /// </summary>
    public double Fov { get; set; } = 55;

    public bool Ortho { get; set; }

    /// <summary>
    /// Keeps it in range: not quite straight up or down, 2 to 120 metres away.
    /// </summary>
    public void Clamp()
    {
        Pitch = Math.Clamp(Pitch, -1.45, 1.45);
        Distance = Math.Clamp(Distance, 2, 120);
    }

    public Vector3 Position
    {
        get
        {
            var dir = new Vector3((float)(Math.Cos(Pitch) * Math.Sin(Yaw)), (float)Math.Sin(Pitch), (float)(Math.Cos(Pitch) * Math.Cos(Yaw)));
            return Target + dir * (float)Distance;
        }
    }

    /// <summary>
    /// Which way is up on screen: the room's up, but forward when it's looking nearly straight down or up.
    /// </summary>
    Vector3 Up => Math.Abs(Pitch) < 1.4 ? Vector3.UnitY : -Vector3.UnitZ;

    /// <summary>
    /// Its right, up and backward axes in the room.
    /// </summary>
    public (Vector3 Right, Vector3 Up, Vector3 Back) Axes
    {
        get
        {
            var back = Vector3.Normalize(Position - Target);
            var right = Vector3.Normalize(Vector3.Cross(Up, back));
            return (right, Vector3.Cross(back, right), back);
        }
    }

    /// <summary>
    /// The height of what an orthographic view shows, metres.
    /// </summary>
    public float OrthoSize => (float)(Distance * 1.1);

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Target, Up);

    public Matrix4x4 Projection(float aspect) =>
        Ortho
            ? Matrix4x4.CreateOrthographic(OrthoSize * aspect, OrthoSize, NEAR, FAR)
            : Matrix4x4.CreatePerspectiveFieldOfView((float)(Fov * Math.PI / 180), aspect, NEAR, FAR);

    /// <summary>
    /// The ray from the camera through <paramref name="point"/> of a view <paramref name="size"/> pixels big (Godot's
    /// <c>project_ray_origin</c> and <c>project_ray_normal</c>).
    /// </summary>
    public (Vector3 Origin, Vector3 Direction) Ray(float2 point, float2 size)
    {
        var aspect = size.X / Math.Max(size.Y, 1);
        var nx = 2 * point.X / Math.Max(size.X, 1) - 1;
        var ny = 1 - 2 * point.Y / Math.Max(size.Y, 1);
        var (right, up, back) = Axes;
        if (Ortho)
        {
            var half = OrthoSize / 2;
            return (Position + right * (nx * half * aspect) + up * (ny * half) - back * NEAR, -back);
        }
        var t = (float)Math.Tan(Fov * Math.PI / 360);
        return (Position, Vector3.Normalize(right * (nx * t * aspect) + up * (ny * t) - back));
    }

    /// <summary>
    /// Turns it round (and up and down) by a drag of <paramref name="delta"/> pixels.
    /// </summary>
    public void Orbit(float2 delta)
    {
        Yaw -= delta.X * 0.01;
        Pitch += delta.Y * 0.01;
        Clamp();
    }

    /// <summary>
    /// Slides what it looks at by a drag of <paramref name="delta"/> pixels, faster the further away it is.
    /// </summary>
    public void Pan(float2 delta)
    {
        var (right, up, _) = Axes;
        Target -= (right * delta.X - up * delta.Y) * (float)(Distance * 0.0016);
        Clamp();
    }

    /// <summary>
    /// Moves it in (<paramref name="factor"/> under 1) or out.
    /// </summary>
    public void Zoom(double factor)
    {
        Distance *= factor;
        Clamp();
    }

    public CameraView ToView(string name) => new(name, Target.X, Target.Y, Target.Z, Yaw, Pitch, Distance, Fov, Ortho);

    public void Apply(CameraView v)
    {
        Target = new Vector3((float)v.TargetX, (float)v.TargetY, (float)v.TargetZ);
        (Yaw, Pitch, Distance, Fov, Ortho) = (v.Yaw, v.Pitch, v.Distance, v.Fov, v.Ortho);
        Clamp();
    }
}
