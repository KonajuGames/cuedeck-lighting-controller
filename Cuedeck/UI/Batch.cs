using System.Runtime.InteropServices;
using Brutal.Numerics;

namespace Cuedeck.UI;

/// <summary>
/// The vertex layout of the UI pipeline: a position in pixels from the top-left of the screen, a UV into the font
/// atlas, and a linear-space colour with alpha.
/// </summary>
struct Vertex(float2 position, float2 uv, float4 color)
{
    public float2 Position = position;
    public float2 Uv = uv;
    public float4 Color = color;
}

/// <summary>
/// Colours for the UI. Written as sRGB (as colour pickers and hex codes are) and converted to the linear values the
/// sRGB swapchain expects.
/// </summary>
static class Colors
{
    /// <summary>
    /// A colour from a 0xRRGGBB hex value.
    /// </summary>
    public static float4 Hex(uint rgb, float alpha = 1)
    {
        return new float4(
            ToLinear((rgb >> 16) & 0xFF),
            ToLinear((rgb >> 8) & 0xFF),
            ToLinear(rgb & 0xFF),
            alpha
        );
    }

    static float ToLinear(uint channel) => ToLinear(channel / 255f);

    static float ToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    /// <summary>
    /// A colour from sRGB components, 0 to 1 each.
    /// </summary>
    public static float4 FromSrgb(float r, float g, float b, float alpha = 1) =>
        new(ToLinear(Math.Clamp(r, 0, 1)), ToLinear(Math.Clamp(g, 0, 1)), ToLinear(Math.Clamp(b, 0, 1)), alpha);

    public static readonly float4 White = Hex(0xFFFFFF);
    public static readonly float4 Transparent = new(0, 0, 0, 0);
}

/// <summary>
/// Collects the triangles for one frame of UI: solid rectangles, text and images, drawn in the order they're added.
/// </summary>
sealed class Batch
{
    readonly List<Vertex> _vertices = [];
    readonly FontAtlas _atlas;

    public Batch(FontAtlas atlas) => _atlas = atlas;

    /// <summary>
    /// Clip rectangles as (min, max) corners; the top one applies to everything added.
    /// </summary>
    readonly Stack<(float2 Min, float2 Max)> _clips = [];

    public ReadOnlySpan<Vertex> Vertices => CollectionsMarshal.AsSpan(_vertices);

    public void Clear()
    {
        _vertices.Clear();
        _clips.Clear();
        _textures.Clear();
    }

    /// <summary>
    /// Where the texture drawn from changes: from vertex Start on, texture Texture (0 is the font atlas, others are
    /// what the renderer's given those numbers, such as the 3D view's image).
    /// </summary>
    readonly List<(int Start, int Texture)> _textures = [];

    public IReadOnlyList<(int Start, int Texture)> TextureChanges => _textures;

    /// <summary>
    /// Draws all of texture <paramref name="texture"/> over the rectangle (clipped as anything else), then goes back
    /// to the atlas.
    /// </summary>
    public void AddTexture(int texture, float2 position, float2 size)
    {
        var start = _vertices.Count;
        AddQuad(position, position + size, new float2(0, 0), new float2(1, 1), Colors.White);
        if (_vertices.Count == start)
            return;
        _textures.Add((start, texture));
        _textures.Add((_vertices.Count, 0));
    }

    /// <summary>
    /// Clips everything added until the matching <see cref="PopClip"/> to this rectangle, within any clip already
    /// in effect.
    /// </summary>
    public void PushClip(float2 position, float2 size)
    {
        var min = position;
        var max = position + size;
        if (_clips.TryPeek(out var outer))
        {
            min = float2.Max(min, outer.Min);
            max = float2.Min(max, outer.Max);
        }
        _clips.Push((min, float2.Max(min, max)));
    }

    public void PopClip() => _clips.Pop();

    /// <summary>
    /// The clip rectangle in effect as (min, max) corners, or null if nothing is clipped. Elements with many parts,
    /// such as a long list, use it to skip drawing what can't be seen.
    /// </summary>
    public (float2 Min, float2 Max)? ClipBounds => _clips.TryPeek(out var clip) ? clip : null;

    public void AddRect(float2 position, float2 size, float4 color)
    {
        if (color.W <= 0 || size.X <= 0 || size.Y <= 0)
            return;
        AddQuad(position, position + size, _atlas.WhiteUv, _atlas.WhiteUv, color);
    }

    /// <summary>
    /// A rectangle outline <paramref name="thickness"/> pixels wide, drawn inside the given bounds.
    /// </summary>
    public void AddBorder(float2 position, float2 size, float thickness, float4 color)
    {
        if (thickness <= 0)
            return;
        var t = MathF.Min(thickness, MathF.Min(size.X, size.Y) / 2);
        AddRect(position, new float2(size.X, t), color);
        AddRect(new float2(position.X, position.Y + size.Y - t), new float2(size.X, t), color);
        AddRect(new float2(position.X, position.Y + t), new float2(t, size.Y - 2 * t), color);
        AddRect(new float2(position.X + size.X - t, position.Y + t), new float2(t, size.Y - 2 * t), color);
    }

    /// <summary>
    /// Draws <paramref name="text"/> with its top-left at <paramref name="position"/>; '\n' starts a new line.
    /// </summary>
    public void AddText(Font font, ReadOnlySpan<char> text, float2 position, float4 color)
    {
        // Whole pixels, so glyphs land on texel boundaries and stay sharp.
        var origin = new float2(MathF.Round(position.X), MathF.Round(position.Y));
        var penX = origin.X;
        var baseline = origin.Y + font.Ascender;

        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == '\n')
            {
                penX = origin.X;
                baseline += font.LineHeight;
                continue;
            }

            var glyph = font.GetGlyph(rune.Value);
            if (glyph.Size.X > 0 && glyph.Size.Y > 0)
            {
                var min = new float2(MathF.Round(penX) + glyph.Bearing.X, baseline - glyph.Bearing.Y);
                AddQuad(min, min + new float2(glyph.Size.X, glyph.Size.Y), glyph.UvMin, glyph.UvMax, color);
            }
            penX += glyph.Advance;
        }
    }

    /// <summary>
    /// Draws <paramref name="image"/> with its top-left at <paramref name="position"/>, at its own size unless given
    /// another, times <paramref name="tint"/>: white leaves a colour image as it is, and colours a white icon.
    /// </summary>
    public void AddImage(Image image, float2 position, float4 tint, float2? size = null)
    {
        // Whole pixels at its own size, so it maps texel for texel and stays sharp.
        var drawn = size ?? new float2(image.Size.X, image.Size.Y);
        if (size is null)
            position = new float2(MathF.Round(position.X), MathF.Round(position.Y));
        if (tint.W <= 0 || drawn.X <= 0 || drawn.Y <= 0)
            return;
        AddQuad(position, position + drawn, image.UvMin, image.UvMax, tint);
    }

    /// <summary>
    /// Draws <paramref name="text"/> turned a quarter turn anticlockwise, reading bottom to top, its line's top-left
    /// (before turning) at <paramref name="origin"/>: so the text runs up from there, its lines' tops to the left.
    /// </summary>
    public void AddTextUp(Font font, ReadOnlySpan<char> text, float2 origin, float4 color)
    {
        origin = new float2(MathF.Round(origin.X), MathF.Round(origin.Y));
        var pen = 0f;
        var top = 0f;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == '\n')
            {
                pen = 0;
                top += font.LineHeight;
                continue;
            }
            var glyph = font.GetGlyph(rune.Value);
            if (glyph.Size.X > 0 && glyph.Size.Y > 0)
            {
                // In the text's own frame: u along the line, v down from its top.
                float u0 = MathF.Round(pen) + glyph.Bearing.X, v0 = top + font.Ascender - glyph.Bearing.Y;
                float u1 = u0 + glyph.Size.X, v1 = v0 + glyph.Size.Y;
                // Turned: u runs up the screen, v to the right.
                Span<Vertex> quad =
                [
                    new(new float2(origin.X + v0, origin.Y - u1), new float2(glyph.UvMax.X, glyph.UvMin.Y), color),
                    new(new float2(origin.X + v1, origin.Y - u1), glyph.UvMax, color),
                    new(new float2(origin.X + v1, origin.Y - u0), new float2(glyph.UvMin.X, glyph.UvMax.Y), color),
                    new(new float2(origin.X + v0, origin.Y - u0), glyph.UvMin, color),
                ];
                AddPolygon(quad);
            }
            pen += glyph.Advance;
        }
    }

    /// <summary>
    /// A solid triangle, its colour blended between its corners'.
    /// </summary>
    public void AddTriangle(float2 a, float2 b, float2 c, float4 ca, float4 cb, float4 cc)
    {
        if (ca.W <= 0 && cb.W <= 0 && cc.W <= 0)
            return;
        var uv = _atlas.WhiteUv;
        Span<Vertex> tri = [new(a, uv, ca), new(b, uv, cb), new(c, uv, cc)];
        AddPolygon(tri);
    }

    public void AddTriangle(float2 a, float2 b, float2 c, float4 color) => AddTriangle(a, b, c, color, color, color);

    /// <summary>
    /// A filled convex polygon in one colour.
    /// </summary>
    public void AddConvex(ReadOnlySpan<float2> points, float4 color)
    {
        if (color.W <= 0 || points.Length < 3)
            return;
        for (var i = 1; i + 1 < points.Length; i++)
            AddTriangle(points[0], points[i], points[i + 1], color);
    }

    /// <summary>
    /// A straight line <paramref name="width"/> pixels thick, from <paramref name="a"/> to <paramref name="b"/>.
    /// </summary>
    public void AddLine(float2 a, float2 b, float width, float4 color)
    {
        var d = b - a;
        var length = MathF.Sqrt(d.X * d.X + d.Y * d.Y);
        if (length <= 0 || color.W <= 0)
            return;
        var n = new float2(-d.Y, d.X) / length * (width / 2);
        AddTriangle(a + n, b + n, b - n, color);
        AddTriangle(a + n, b - n, a - n, color);
    }

    /// <summary>
    /// Lines through <paramref name="points"/> in turn.
    /// </summary>
    public void AddPolyline(ReadOnlySpan<float2> points, float width, float4 color)
    {
        for (var i = 0; i + 1 < points.Length; i++)
            AddLine(points[i], points[i + 1], width, color);
    }

    /// <summary>
    /// A line in dashes <paramref name="dash"/> pixels long, with gaps as long.
    /// </summary>
    public void AddDashedLine(float2 a, float2 b, float width, float dash, float4 color)
    {
        var d = b - a;
        var length = MathF.Sqrt(d.X * d.X + d.Y * d.Y);
        if (length <= 0 || dash <= 0)
            return;
        var step = d / length;
        for (var s = 0f; s < length; s += dash * 2)
            AddLine(a + step * s, a + step * MathF.Min(s + dash, length), width, color);
    }

    /// <summary>
    /// A filled circle.
    /// </summary>
    public void AddCircle(float2 centre, float radius, float4 color, int segments = 16)
    {
        for (var i = 0; i < segments; i++)
        {
            var a0 = MathF.Tau * i / segments;
            var a1 = MathF.Tau * (i + 1) / segments;
            AddTriangle(centre, centre + new float2(MathF.Cos(a0), MathF.Sin(a0)) * radius, centre + new float2(MathF.Cos(a1), MathF.Sin(a1)) * radius, color);
        }
    }

    /// <summary>
    /// An arc of a circle from angle <paramref name="from"/> to <paramref name="to"/> (radians, clockwise on screen
    /// from the right), drawn as a line.
    /// </summary>
    public void AddArc(float2 centre, float radius, float from, float to, float width, float4 color, int segments = 12)
    {
        for (var i = 0; i < segments; i++)
        {
            var a0 = from + (to - from) * i / segments;
            var a1 = from + (to - from) * (i + 1) / segments;
            AddLine(centre + new float2(MathF.Cos(a0), MathF.Sin(a0)) * radius, centre + new float2(MathF.Cos(a1), MathF.Sin(a1)) * radius, width, color);
        }
    }

    /// <summary>
    /// A convex polygon, cut to the clip rectangle (its colours and texture coordinates cut with it), as a fan of
    /// triangles.
    /// </summary>
    void AddPolygon(Span<Vertex> polygon)
    {
        var points = polygon;
        if (_clips.TryPeek(out var clip))
        {
            // Sutherland-Hodgman against each edge of the clip rectangle in turn.
            Span<Vertex> a = stackalloc Vertex[polygon.Length + 8];
            Span<Vertex> b = stackalloc Vertex[polygon.Length + 8];
            polygon.CopyTo(a);
            var n = polygon.Length;
            for (var edge = 0; edge < 4 && n > 0; edge++)
            {
                var m = 0;
                for (var i = 0; i < n; i++)
                {
                    var p = a[i];
                    var q = a[(i + 1) % n];
                    var dp = Inside(p.Position, edge, clip);
                    var dq = Inside(q.Position, edge, clip);
                    if (dp >= 0)
                        b[m++] = p;
                    if (dp >= 0 != dq >= 0)
                    {
                        var t = dp / (dp - dq);
                        b[m++] = new Vertex(p.Position + (q.Position - p.Position) * t, p.Uv + (q.Uv - p.Uv) * t, p.Color + (q.Color - p.Color) * t);
                    }
                }
                b[..m].CopyTo(a);
                n = m;
            }
            if (n < 3)
                return;
            points = a[..n].ToArray();
        }
        for (var i = 1; i + 1 < points.Length; i++)
        {
            _vertices.Add(points[0]);
            _vertices.Add(points[i]);
            _vertices.Add(points[i + 1]);
        }
    }

    /// <summary>
    /// How far inside edge <paramref name="edge"/> (left, right, top, bottom) of the clip rectangle a point is:
    /// negative outside.
    /// </summary>
    static float Inside(float2 p, int edge, (float2 Min, float2 Max) clip) =>
        edge switch
        {
            0 => p.X - clip.Min.X,
            1 => clip.Max.X - p.X,
            2 => p.Y - clip.Min.Y,
            _ => clip.Max.Y - p.Y,
        };

    void AddQuad(float2 min, float2 max, float2 uvMin, float2 uvMax, float4 color)
    {
        // Every quad is axis-aligned, so clipping is just trimming it and its UVs by the same fraction.
        if (_clips.TryPeek(out var clip))
        {
            var clippedMin = float2.Max(min, clip.Min);
            var clippedMax = float2.Min(max, clip.Max);
            if (clippedMin.X >= clippedMax.X || clippedMin.Y >= clippedMax.Y)
                return;

            var size = max - min;
            var uvSize = uvMax - uvMin;
            (uvMin, uvMax) = (uvMin + (clippedMin - min) / size * uvSize, uvMin + (clippedMax - min) / size * uvSize);
            (min, max) = (clippedMin, clippedMax);
        }

        var topRight = new Vertex(new float2(max.X, min.Y), new float2(uvMax.X, uvMin.Y), color);
        var bottomLeft = new Vertex(new float2(min.X, max.Y), new float2(uvMin.X, uvMax.Y), color);

        _vertices.Add(new Vertex(min, uvMin, color));
        _vertices.Add(topRight);
        _vertices.Add(bottomLeft);
        _vertices.Add(bottomLeft);
        _vertices.Add(topRight);
        _vertices.Add(new Vertex(max, uvMax, color));
    }
}
