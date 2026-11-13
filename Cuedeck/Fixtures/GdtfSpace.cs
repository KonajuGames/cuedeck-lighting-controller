namespace Cuedeck.Fixtures;

/// <summary>
/// A 3D vector in doubles, for placing a GDTF fixture's parts.
/// </summary>
readonly record struct Vec3(double X, double Y, double Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);

    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public static Vec3 operator /(Vec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    public double Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;

    public Vec3 Normalized()
    {
        var length = Math.Sqrt(Dot(this));
        return length == 0 ? this : this / length;
    }
}

/// <summary>
/// A rotation (and scale) as three column axes: Godot's <c>Basis</c>, with just what the GDTF import uses.
/// </summary>
readonly record struct Basis(Vec3 X, Vec3 Y, Vec3 Z)
{
    public static readonly Basis Identity = new(new(1, 0, 0), new(0, 1, 0), new(0, 0, 1));

    /// <summary>
    /// Row <paramref name="r"/>, column <paramref name="c"/>.
    /// </summary>
    public double this[int r, int c]
    {
        get
        {
            var col = c switch
            {
                0 => X,
                1 => Y,
                _ => Z,
            };
            return r switch
            {
                0 => col.X,
                1 => col.Y,
                _ => col.Z,
            };
        }
    }

    public static Vec3 operator *(Basis b, Vec3 v) => b.X * v.X + b.Y * v.Y + b.Z * v.Z;

    public static Basis operator *(Basis a, Basis b) => new(a * b.X, a * b.Y, a * b.Z);

    public double Determinant =>
        this[0, 0] * (this[1, 1] * this[2, 2] - this[2, 1] * this[1, 2])
        - this[1, 0] * (this[0, 1] * this[2, 2] - this[2, 1] * this[0, 2])
        + this[2, 0] * (this[0, 1] * this[1, 2] - this[1, 1] * this[0, 2]);

    /// <summary>
    /// Gram-Schmidt on the columns, as Godot's <c>orthonormalized()</c>.
    /// </summary>
    public Basis Orthonormalized()
    {
        var x = X.Normalized();
        var y = (Y - x * x.Dot(Y)).Normalized();
        var z = (Z - x * x.Dot(Z) - y * y.Dot(Z)).Normalized();
        return new Basis(x, y, z);
    }

    public Basis Inverse()
    {
        var det = Determinant;
        if (det == 0)
            return Identity;
        var m = this;
        double Co(int r0, int c0, int r1, int c1) => m[r0, c0] * m[r1, c1] - m[r0, c1] * m[r1, c0];
        // Rows of the inverse are the cofactors' columns, over the determinant.
        var r0v = new Vec3(Co(1, 1, 2, 2), Co(0, 2, 2, 1), Co(0, 1, 1, 2)) / det;
        var r1v = new Vec3(Co(1, 2, 2, 0), Co(0, 0, 2, 2), Co(0, 2, 1, 0)) / det;
        var r2v = new Vec3(Co(1, 0, 2, 1), Co(0, 1, 2, 0), Co(0, 0, 1, 1)) / det;
        return new Basis(new(r0v.X, r1v.X, r2v.X), new(r0v.Y, r1v.Y, r2v.Y), new(r0v.Z, r1v.Z, r2v.Z));
    }

    /// <summary>
    /// Euler angles in radians in Godot's default YXZ order, as <c>get_euler()</c>.
    /// </summary>
    public Vec3 Euler()
    {
        const double EPSILON = 0.00001;
        var m12 = this[1, 2];
        if (m12 < 1 - EPSILON)
        {
            if (m12 > -(1 - EPSILON))
            {
                // A pure X rotation, in its simplest form.
                if (this[1, 0] == 0 && this[0, 1] == 0 && this[0, 2] == 0 && this[2, 0] == 0 && this[0, 0] == 1)
                    return new Vec3(Math.Atan2(-m12, this[1, 1]), 0, 0);
                return new Vec3(Math.Asin(-m12), Math.Atan2(this[0, 2], this[2, 2]), Math.Atan2(this[1, 0], this[1, 1]));
            }
            return new Vec3(Math.PI * 0.5, Math.Atan2(this[0, 1], this[0, 0]), 0);
        }
        return new Vec3(-Math.PI * 0.5, -Math.Atan2(this[0, 1], this[0, 0]), 0);
    }
}

/// <summary>
/// A rotation and a position: Godot's <c>Transform3D</c>, with just what the GDTF import uses.
/// </summary>
readonly record struct Xform(Basis Basis, Vec3 Origin)
{
    public static readonly Xform Identity = new(Basis.Identity, Vec3.Zero);

    public static Xform operator *(Xform a, Xform b) => new(a.Basis * b.Basis, a.Basis * b.Origin + a.Origin);

    public Xform AffineInverse()
    {
        var inv = Basis.Inverse();
        return new Xform(inv, -(inv * Origin));
    }
}
