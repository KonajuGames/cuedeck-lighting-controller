using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Cuedeck.Common;

namespace Cuedeck.Visualizer;

/// <summary>
/// One mesh of a model, placed where its node puts it (relative to the model), in its material's base colour.
/// </summary>
sealed record ModelPart(MeshData Mesh, Matrix4x4 Transform, Rgb Colour);

/// <summary>
/// A glTF model (a GDTF fixture's part, or a set piece), as the 3D view draws it: its meshes' triangles, placed by
/// their nodes, in their materials' base colours (textures aren't read). Reads binary glTF (.glb) and glTF JSON with
/// its buffers embedded as data URIs or in files beside it; triangles only, indexed or not; a mesh without normals
/// gets flat ones. glTF's metres and Y up, as the 3D view's.
/// </summary>
sealed class GltfModel
{
    const uint MAGIC = 0x46546C67; // "glTF"
    const uint CHUNK_JSON = 0x4E4F534A;
    const uint CHUNK_BIN = 0x004E4942;

    public List<ModelPart> Parts { get; } = [];

    /// <summary>
    /// The smallest box round every part, relative to the model.
    /// </summary>
    public (Vector3 Min, Vector3 Max) Bounds { get; private set; }

    /// <summary>
    /// The model in <paramref name="path"/>, or null if it can't be read.
    /// </summary>
    public static GltfModel? Load(string path)
    {
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            return Parse(File.ReadAllBytes(path), uri => File.ReadAllBytes(Path.Combine(folder, Uri.UnescapeDataString(uri))));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// A model from a file's bytes; <paramref name="external"/> reads a buffer given by a relative URI.
    /// </summary>
    public static GltfModel Parse(ReadOnlySpan<byte> file, Func<string, byte[]> external)
    {
        JsonNode? doc;
        byte[] bin = [];
        if (file.Length >= 12 && BitConverter.ToUInt32(file) == MAGIC)
        {
            doc = null;
            var at = 12;
            while (at + 8 <= file.Length)
            {
                var length = (int)BitConverter.ToUInt32(file[at..]);
                var type = BitConverter.ToUInt32(file[(at + 4)..]);
                var data = file.Slice(at + 8, length);
                if (type == CHUNK_JSON)
                    doc = Json.Parse(Encoding.UTF8.GetString(data));
                else if (type == CHUNK_BIN)
                    bin = data.ToArray();
                at += 8 + length;
            }
        }
        else
            doc = Json.Parse(Encoding.UTF8.GetString(file));
        if (doc is not JsonObject)
            throw new InvalidDataException("not a glTF model");

        var buffers = doc.Arr("buffers").Select(b => Buffer(b, bin, external)).ToList();
        var model = new GltfModel();
        var meshes = doc.Arr("meshes");
        var materials = doc.Arr("materials");
        var nodes = doc.Arr("nodes");
        var scenes = doc.Arr("scenes");
        var roots = scenes.Count > 0 ? scenes[Math.Clamp(doc.Int("scene"), 0, scenes.Count - 1)].Arr("nodes") : new JsonArray();
        IEnumerable<int> rootIndices = roots.Count > 0 ? roots.Select(n => Json.AsInt(n)) : Enumerable.Range(0, nodes.Count).Where(i => !nodes.Any(n => n.Arr("children").Any(c => Json.AsInt(c) == i)));

        void Visit(int index, Matrix4x4 parent, int depth)
        {
            if (index < 0 || index >= nodes.Count || depth > 64)
                return;
            var node = nodes[index];
            var world = LocalTransform(node) * parent;
            if (node.Has("mesh") && node.Int("mesh") is var m && m >= 0 && m < meshes.Count)
                foreach (var prim in meshes[m].Arr("primitives"))
                    if (Primitive(doc, buffers, prim) is { } mesh)
                    {
                        var mat = prim.Has("material") ? materials.ElementAtOrDefault(prim.Int("material")) : null;
                        var c = mat.Obj("pbrMetallicRoughness").Arr("baseColorFactor");
                        var colour = c.Count >= 3 ? new Rgb((float)Json.AsDouble(c[0]), (float)Json.AsDouble(c[1]), (float)Json.AsDouble(c[2])) : Rgb.White;
                        model.Parts.Add(new ModelPart(mesh, world, colour));
                    }
            foreach (var child in node.Arr("children"))
                Visit(Json.AsInt(child), world, depth + 1);
        }
        foreach (var r in rootIndices)
            Visit(r, Matrix4x4.Identity, 0);

        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var p in model.Parts)
            foreach (var v in p.Mesh.Positions)
            {
                var w = Vector3.Transform(v, p.Transform);
                (min, max) = (Vector3.Min(min, w), Vector3.Max(max, w));
            }
        model.Bounds = model.Parts.Count > 0 ? (min, max) : (Vector3.Zero, Vector3.Zero);
        return model;
    }

    static byte[] Buffer(JsonNode? b, byte[] bin, Func<string, byte[]> external)
    {
        var uri = b.Str("uri");
        if (uri == "")
            return bin;
        if (uri.StartsWith("data:", StringComparison.Ordinal))
            return Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]);
        return external(uri);
    }

    /// <summary>
    /// A node's own transform: its matrix (column-major), or its translation, rotation and scale.
    /// </summary>
    static Matrix4x4 LocalTransform(JsonNode? node)
    {
        var m = node.Arr("matrix");
        if (m.Count == 16)
        {
            float M(int i) => (float)Json.AsDouble(m[i]);
            // Column-major for column vectors is row-major for row vectors.
            return new Matrix4x4(M(0), M(1), M(2), M(3), M(4), M(5), M(6), M(7), M(8), M(9), M(10), M(11), M(12), M(13), M(14), M(15));
        }
        Vector3 V3(string key, float fallback)
        {
            var a = node.Arr(key);
            return a.Count >= 3 ? new Vector3((float)Json.AsDouble(a[0]), (float)Json.AsDouble(a[1]), (float)Json.AsDouble(a[2])) : new Vector3(fallback);
        }
        var r = node.Arr("rotation");
        var q = r.Count >= 4 ? new Quaternion((float)Json.AsDouble(r[0]), (float)Json.AsDouble(r[1]), (float)Json.AsDouble(r[2]), (float)Json.AsDouble(r[3])) : Quaternion.Identity;
        return Matrix4x4.CreateScale(V3("scale", 1)) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q)) * Matrix4x4.CreateTranslation(V3("translation", 0));
    }

    /// <summary>
    /// A primitive's triangles, or null for one that isn't triangles or has no positions.
    /// </summary>
    static MeshData? Primitive(JsonNode doc, List<byte[]> buffers, JsonNode? prim)
    {
        if (prim.Int("mode", 4) != 4 || !prim.Obj("attributes").Has("POSITION"))
            return null;
        var positions = Vectors(doc, buffers, prim.Obj("attributes").Int("POSITION"));
        var normals = prim.Obj("attributes").Has("NORMAL") ? Vectors(doc, buffers, prim.Obj("attributes").Int("NORMAL")) : null;
        var indices = prim.Has("indices") ? Indices(doc, buffers, prim.Int("indices")) : [.. Enumerable.Range(0, positions.Count).Select(i => (uint)i)];
        var mesh = new MeshData();
        if (normals is { } n && n.Count == positions.Count)
        {
            mesh.Positions.AddRange(positions);
            mesh.Normals.AddRange(n.Select(v => v.LengthSquared() > 0 ? Vector3.Normalize(v) : Vector3.UnitY));
            mesh.Indices.AddRange(indices.Where(i => i < positions.Count));
            mesh.Indices.RemoveRange(mesh.Indices.Count - mesh.Indices.Count % 3, mesh.Indices.Count % 3);
            return mesh;
        }
        // No normals: each triangle flat.
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            if (indices[t] >= positions.Count || indices[t + 1] >= positions.Count || indices[t + 2] >= positions.Count)
                continue;
            var (a, b, c) = (positions[(int)indices[t]], positions[(int)indices[t + 1]], positions[(int)indices[t + 2]]);
            var normal = Vector3.Cross(b - a, c - a);
            normal = normal.LengthSquared() > 0 ? Vector3.Normalize(normal) : Vector3.UnitY;
            foreach (var p in new[] { a, b, c })
            {
                mesh.Positions.Add(p);
                mesh.Normals.Add(normal);
                mesh.Indices.Add((uint)(mesh.Positions.Count - 1));
            }
        }
        return mesh;
    }

    /// <summary>
    /// An accessor's bytes: its buffer, from where it starts, and how far apart its elements are.
    /// </summary>
    static (byte[] Data, int Offset, int Stride, int Count) Accessor(JsonNode doc, List<byte[]> buffers, int index, int elementSize)
    {
        var acc = doc.Arr("accessors")[index];
        var view = doc.Arr("bufferViews")[acc.Int("bufferView")];
        var data = buffers[view.Int("buffer")];
        var stride = view.Int("byteStride", 0);
        return (data, view.Int("byteOffset") + acc.Int("byteOffset"), stride > 0 ? stride : elementSize, acc.Int("count"));
    }

    static List<Vector3> Vectors(JsonNode doc, List<byte[]> buffers, int index)
    {
        if (doc.Arr("accessors")[index].Int("componentType") != 5126)
            throw new InvalidDataException("positions and normals must be floats");
        var (data, offset, stride, count) = Accessor(doc, buffers, index, 12);
        var output = new List<Vector3>(count);
        for (var i = 0; i < count; i++)
        {
            var at = offset + i * stride;
            output.Add(new Vector3(BitConverter.ToSingle(data, at), BitConverter.ToSingle(data, at + 4), BitConverter.ToSingle(data, at + 8)));
        }
        return output;
    }

    static uint[] Indices(JsonNode doc, List<byte[]> buffers, int index)
    {
        var type = doc.Arr("accessors")[index].Int("componentType");
        var size = type switch
        {
            5121 => 1,
            5123 => 2,
            5125 => 4,
            _ => throw new InvalidDataException("indices must be unsigned"),
        };
        var (data, offset, stride, count) = Accessor(doc, buffers, index, size);
        var output = new uint[count];
        for (var i = 0; i < count; i++)
        {
            var at = offset + i * stride;
            output[i] = size switch
            {
                1 => data[at],
                2 => BitConverter.ToUInt16(data, at),
                _ => BitConverter.ToUInt32(data, at),
            };
        }
        return output;
    }
}

/// <summary>
/// Models read once each and kept by path (null for one that couldn't be read), for the 3D view's fixtures and set
/// pieces. On the UI thread.
/// </summary>
static class ModelCache
{
    static readonly Dictionary<string, GltfModel?> _models = [];

    public static GltfModel? Get(string path)
    {
        if (path == "")
            return null;
        if (!_models.TryGetValue(path, out var model))
            _models[path] = model = File.Exists(path) ? GltfModel.Load(path) : null;
        return model;
    }
}
