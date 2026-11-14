using System.Numerics;

namespace Cuedeck.Visualizer;

/// <summary>
/// The shapes the 3D view is built from, each made once and placed, turned and scaled per use.
/// </summary>
enum MeshKind
{
    /// <summary>
    /// A 1 m cube about its centre.
    /// </summary>
    Box,

    /// <summary>
    /// A cylinder along Y, 1 m across its ends and 1 m tall, about its centre.
    /// </summary>
    Cylinder,

    /// <summary>
    /// A moving head's base: a cylinder along Y, 0.26 m across the top and 0.30 m the bottom, 0.10 m tall (sLight's,
    /// at its own size).
    /// </summary>
    Base,

    /// <summary>
    /// The selection ring round a fixture: a torus in the XZ plane, 0.26 m to 0.32 m out.
    /// </summary>
    Ring,

    /// <summary>
    /// A 1 m square in the XZ plane, facing up, about its centre.
    /// </summary>
    Plane,
}

/// <summary>
/// A shape's triangles: positions and normals, and the indices of each triangle's corners (anticlockwise seen from
/// outside).
/// </summary>
sealed class MeshData
{
    public List<Vector3> Positions { get; } = [];
    public List<Vector3> Normals { get; } = [];
    public List<uint> Indices { get; } = [];

    uint Vertex(Vector3 p, Vector3 n)
    {
        Positions.Add(p);
        Normals.Add(n);
        return (uint)(Positions.Count - 1);
    }

    void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)
    {
        var (ia, ib, ic, id) = (Vertex(a, n), Vertex(b, n), Vertex(c, n), Vertex(d, n));
        Indices.AddRange([ia, ib, ic, ia, ic, id]);
    }

    public static MeshData Of(MeshKind kind) =>
        kind switch
        {
            MeshKind.Box => Box(),
            MeshKind.Cylinder => Cylinder(0.5f, 0.5f, 1, 20),
            MeshKind.Base => Cylinder(0.13f, 0.15f, 0.10f, 20),
            MeshKind.Ring => Torus(0.26f, 0.32f, 32, 8),
            _ => Plane(),
        };

    public static MeshData Box()
    {
        var m = new MeshData();
        const float H = 0.5f;
        for (var axis = 0; axis < 3; axis++)
            foreach (var sign in new[] { 1f, -1f })
            {
                var n = Vector3.Zero;
                n[axis] = sign;
                // Two axes across the face, ordered so its corners go anticlockwise from outside.
                var u = Vector3.Zero;
                var v = Vector3.Zero;
                u[(axis + 1) % 3] = 1;
                v[(axis + 2) % 3] = 1;
                if (sign < 0)
                    (u, v) = (v, u);
                var c = n * H;
                m.Quad(c - u * H - v * H, c + u * H - v * H, c + u * H + v * H, c - u * H + v * H, n);
            }
        return m;
    }

    public static MeshData Plane()
    {
        var m = new MeshData();
        m.Quad(new(-0.5f, 0, 0.5f), new(0.5f, 0, 0.5f), new(0.5f, 0, -0.5f), new(-0.5f, 0, -0.5f), Vector3.UnitY);
        return m;
    }

    /// <summary>
    /// A closed cylinder (a frustum when the radii differ) along Y about its centre.
    /// </summary>
    public static MeshData Cylinder(float top, float bottom, float height, int segments)
    {
        var m = new MeshData();
        var h = height / 2;
        // The side's normals lean with its slope.
        var slope = (bottom - top) / height;
        for (var i = 0; i < segments; i++)
        {
            var a0 = MathF.Tau * i / segments;
            var a1 = MathF.Tau * (i + 1) / segments;
            Vector3 Dir(float a) => new(MathF.Sin(a), 0, MathF.Cos(a));
            var (d0, d1) = (Dir(a0), Dir(a1));
            var n0 = Vector3.Normalize(d0 + new Vector3(0, slope, 0));
            var n1 = Vector3.Normalize(d1 + new Vector3(0, slope, 0));
            var b0 = m.Vertex(d0 * bottom + new Vector3(0, -h, 0), n0);
            var b1 = m.Vertex(d1 * bottom + new Vector3(0, -h, 0), n1);
            var t1 = m.Vertex(d1 * top + new Vector3(0, h, 0), n1);
            var t0 = m.Vertex(d0 * top + new Vector3(0, h, 0), n0);
            m.Indices.AddRange([b0, b1, t1, b0, t1, t0]);
            // The ends.
            var ct = m.Vertex(new Vector3(0, h, 0), Vector3.UnitY);
            var e0 = m.Vertex(d0 * top + new Vector3(0, h, 0), Vector3.UnitY);
            var e1 = m.Vertex(d1 * top + new Vector3(0, h, 0), Vector3.UnitY);
            m.Indices.AddRange([ct, e0, e1]);
            var cb = m.Vertex(new Vector3(0, -h, 0), -Vector3.UnitY);
            var f0 = m.Vertex(d0 * bottom + new Vector3(0, -h, 0), -Vector3.UnitY);
            var f1 = m.Vertex(d1 * bottom + new Vector3(0, -h, 0), -Vector3.UnitY);
            m.Indices.AddRange([cb, f1, f0]);
        }
        return m;
    }

    public static MeshData Torus(float inner, float outer, int rings, int sides)
    {
        var m = new MeshData();
        var centre = (inner + outer) / 2;
        var tube = (outer - inner) / 2;
        for (var i = 0; i <= rings; i++)
        {
            var a = MathF.Tau * i / rings;
            var dir = new Vector3(MathF.Sin(a), 0, MathF.Cos(a));
            for (var j = 0; j <= sides; j++)
            {
                var b = MathF.Tau * j / sides;
                var n = dir * MathF.Cos(b) + Vector3.UnitY * MathF.Sin(b);
                m.Vertex(dir * centre + n * tube, n);
            }
        }
        var row = (uint)(sides + 1);
        for (var i = 0; i < rings; i++)
            for (var j = 0; j < sides; j++)
            {
                var a = (uint)(i * row + j);
                var b = a + row;
                m.Indices.AddRange([a, b, b + 1, a, b + 1, a + 1]);
            }
        return m;
    }
}
