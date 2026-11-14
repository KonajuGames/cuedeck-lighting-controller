using System.Numerics;
using System.Text;
using Cuedeck.Visualizer;

namespace Cuedeck.Tests.Visualizer;

/// <summary>
/// Reading glTF models (GDTF fixtures' parts, set pieces) without the GPU: binary and JSON, nodes' transforms,
/// materials' colours, indices or none, normals or flat ones.
/// </summary>
public sealed class GltfModelTests : IDisposable
{
    readonly TempFolder _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>
    /// A triangle's buffer: three positions, then (if <paramref name="indexed"/>) three 16-bit indices, padded.
    /// </summary>
    internal static byte[] TriangleBuffer(bool indexed)
    {
        var b = new List<byte>();
        foreach (var f in new float[] { 0, 0, 0, 1, 0, 0, 0, 0, 1 })
            b.AddRange(BitConverter.GetBytes(f));
        if (indexed)
        {
            foreach (var i in new ushort[] { 0, 2, 1 })
                b.AddRange(BitConverter.GetBytes(i));
            b.AddRange([0, 0]);
        }
        return [.. b];
    }

    internal static string Doc(string buffer, bool indexed, string node)
    {
        var indices = indexed ? ", \"indices\": 1" : "";
        var view = indexed ? ", {\"buffer\": 0, \"byteOffset\": 36, \"byteLength\": 6}" : "";
        var accessor = indexed ? ", {\"bufferView\": 1, \"componentType\": 5123, \"count\": 3, \"type\": \"SCALAR\"}" : "";
        return "{\"asset\": {\"version\": \"2.0\"}, \"scene\": 0, \"scenes\": [{\"nodes\": [0]}],"
            + " \"nodes\": [" + node + "],"
            + " \"meshes\": [{\"primitives\": [{\"attributes\": {\"POSITION\": 0}" + indices + ", \"material\": 0}]}],"
            + " \"materials\": [{\"pbrMetallicRoughness\": {\"baseColorFactor\": [1, 0.5, 0, 1]}}],"
            + " \"buffers\": [" + buffer + "],"
            + " \"bufferViews\": [{\"buffer\": 0, \"byteOffset\": 0, \"byteLength\": 36}" + view + "],"
            + " \"accessors\": [{\"bufferView\": 0, \"componentType\": 5126, \"count\": 3, \"type\": \"VEC3\"}" + accessor + "]}";
    }

    internal static byte[] Glb(string json, byte[] bin)
    {
        var j = Encoding.UTF8.GetBytes(json);
        var jPad = j.Concat(Enumerable.Repeat((byte)' ', (4 - j.Length % 4) % 4)).ToArray();
        var bPad = bin.Concat(new byte[(4 - bin.Length % 4) % 4]).ToArray();
        var output = new List<byte>();
        output.AddRange(BitConverter.GetBytes(0x46546C67u));
        output.AddRange(BitConverter.GetBytes(2u));
        output.AddRange(BitConverter.GetBytes((uint)(12 + 8 + jPad.Length + 8 + bPad.Length)));
        output.AddRange(BitConverter.GetBytes((uint)jPad.Length));
        output.AddRange(BitConverter.GetBytes(0x4E4F534Au));
        output.AddRange(jPad);
        output.AddRange(BitConverter.GetBytes((uint)bPad.Length));
        output.AddRange(BitConverter.GetBytes(0x004E4942u));
        output.AddRange(bPad);
        return [.. output];
    }

    /// <summary>
    /// A one-triangle model file.
    /// </summary>
    internal static byte[] Triangle() => Glb(Doc("{\"byteLength\": 44}", true, "{\"mesh\": 0}"), TriangleBuffer(indexed: true));

    [Fact]
    public void A_binary_model()
    {
        var bin = TriangleBuffer(indexed: true);
        var path = _dir.File("part.glb");
        File.WriteAllBytes(path, Glb(Doc("""{"byteLength": 44}""", true, """{"mesh": 0, "translation": [1, 2, 3]}"""), bin));
        var model = GltfModel.Load(path)!;
        var part = Assert.Single(model.Parts);
        Assert.Equal(3, part.Mesh.Positions.Count);
        // Its indices followed (corners 0, 2, 1), unrolled for their flat normals.
        Assert.Equal([new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 0)], part.Mesh.Positions);
        Assert.Equal((1f, 0.5f, 0f), (part.Colour.R, part.Colour.G, part.Colour.B));
        Assert.Equal(new Vector3(2, 2, 3), Vector3.Transform(new Vector3(1, 0, 0), part.Transform)); // placed by its node
        Assert.Equal((new Vector3(1, 2, 3), new Vector3(2, 2, 4)), model.Bounds);
        // No normals given: flat ones, facing the way its corners turn.
        Assert.All(part.Mesh.Normals, n => Assert.True(Vector3.Distance(n, Vector3.UnitY) < 1e-5f || Vector3.Distance(n, -Vector3.UnitY) < 1e-5f));
    }

    [Fact]
    public void A_JSON_model_with_its_buffer_inside_and_a_matrix()
    {
        var uri = "data:application/octet-stream;base64," + Convert.ToBase64String(TriangleBuffer(indexed: false));
        // A column-major matrix: scaled 2 and moved 5 along X.
        var path = _dir.File("set.gltf");
        File.WriteAllText(path, Doc("{\"byteLength\": 36, \"uri\": \"" + uri + "\"}", false, """{"mesh": 0, "matrix": [2,0,0,0, 0,2,0,0, 0,0,2,0, 5,0,0,1]}"""));
        var part = Assert.Single(GltfModel.Load(path)!.Parts);
        Assert.Equal(3, part.Mesh.Indices.Count); // not indexed: every corner in order
        Assert.Equal(new Vector3(7, 0, 0), Vector3.Transform(new Vector3(1, 0, 0), part.Transform));
    }

    [Fact]
    public void A_buffer_in_a_file_beside_it_and_one_that_isnt_a_model()
    {
        File.WriteAllBytes(_dir.File("mesh data.bin"), TriangleBuffer(indexed: true));
        var path = _dir.File("prop.gltf");
        File.WriteAllText(path, Doc("""{"byteLength": 44, "uri": "mesh%20data.bin"}""", true, """{"mesh": 0}"""));
        Assert.Single(GltfModel.Load(path)!.Parts);

        File.WriteAllText(_dir.File("junk.glb"), "not a model");
        Assert.Null(GltfModel.Load(_dir.File("junk.glb")));
        Assert.Null(ModelCache.Get(_dir.File("missing.glb")));
    }
}
