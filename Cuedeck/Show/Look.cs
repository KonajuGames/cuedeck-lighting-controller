using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Dmx;

namespace Cuedeck.Show;

/// <summary>
/// A stored look, as cues and chase steps hold one: for each universe slot, the channels it sets (by channel number,
/// 0-based) and their values. Saved as sLight's [{"channel": value}, ...] with the channels as strings.
/// </summary>
sealed class Look
{
    public List<Dictionary<int, int>> Universes { get; } = [];

    public Look() { }

    public Look(IEnumerable<Dictionary<int, int>> universes) => Universes.AddRange(universes);

    /// <summary>
    /// The value <paramref name="universe"/>'s <paramref name="channel"/> is set to, or 0.
    /// </summary>
    public int Level(int universe, int channel) =>
        universe >= 0 && universe < Universes.Count ? Universes[universe].GetValueOrDefault(channel) : 0;

    /// <summary>
    /// How many channel values it holds, across every universe.
    /// </summary>
    public int Count => Universes.Sum(u => u.Count);

    /// <summary>
    /// Every universe's live levels, the channels that aren't 0.
    /// </summary>
    public static Look Capture(DmxOutput dmx)
    {
        var look = new Look();
        foreach (var u in dmx.Universes)
        {
            var d = new Dictionary<int, int>();
            for (var c = 0; c < DmxUniverse.Size; c++)
                if (u.Data[c] != 0)
                    d[c] = u.Data[c];
            look.Universes.Add(d);
        }
        return look;
    }

    /// <summary>
    /// The look as a full 512-byte buffer for each of <paramref name="universes"/> slots, channels it doesn't set at
    /// 0.
    /// </summary>
    public List<byte[]> Buffers(int universes)
    {
        var output = new List<byte[]>(universes);
        for (var u = 0; u < universes; u++)
        {
            var buffer = new byte[DmxUniverse.Size];
            if (u < Universes.Count)
                foreach (var (c, v) in Universes[u])
                    if (c is >= 0 and < DmxUniverse.Size)
                        buffer[c] = (byte)Math.Clamp(v, 0, 255);
            output.Add(buffer);
        }
        return output;
    }

    public Look Clone() => new(Universes.Select(u => new Dictionary<int, int>(u)));

    public JsonArray ToJson() => [.. Universes.Select(u => (JsonNode)ChannelsToJson(u))];

    /// <summary>
    /// A universe's channels as sLight's {"channel": value} object.
    /// </summary>
    public static JsonObject ChannelsToJson(IReadOnlyDictionary<int, int> channels)
    {
        var o = new JsonObject();
        foreach (var (c, v) in channels)
            o[c.ToString()] = v;
        return o;
    }

    /// <summary>
    /// A {"channel": value} object, values clamped to 0 to 255; channels that aren't numbers are left out.
    /// </summary>
    public static Dictionary<int, int> ChannelsFromJson(JsonNode? node)
    {
        var d = new Dictionary<int, int>();
        if (node is JsonObject o)
            foreach (var (k, v) in o)
                if (Json.IsValidInt(k))
                    d[int.Parse(k)] = Math.Clamp(Json.AsInt(v), 0, 255);
        return d;
    }

    public static Look FromJson(JsonNode? node)
    {
        var look = new Look();
        if (node is JsonArray a)
            foreach (var entry in a)
                look.Universes.Add(ChannelsFromJson(entry));
        return look;
    }
}
