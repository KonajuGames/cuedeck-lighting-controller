using System.Numerics;
using System.Text.Json.Nodes;
using Cuedeck.Common;

namespace Cuedeck.Show;

/// <summary>
/// A named position for moving fixtures: a pan and tilt for each fixture, by uid, 0 to 1 over each axis' whole range
/// (tilt bottom-up), and a colour of its own for its icon.
/// </summary>
sealed class Position
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public Rgb Colour { get; set; }

    /// <summary>
    /// Fixture uid to its (pan, tilt).
    /// </summary>
    public Dictionary<string, Vector2> Points { get; } = [];
}

/// <summary>
/// A show's named positions for moving fixtures. Saved with the show.
/// </summary>
sealed class PositionLibrary
{
    /// <summary>
    /// Where a fixture with no point of its own in a position is: centred.
    /// </summary>
    public static readonly Vector2 DefaultPoint = new(0.5f, 0.5f);

    /// <summary>
    /// Hues step round by the golden ratio, so each new position's colour is well away from the ones before it.
    /// </summary>
    const double Golden = 0.6180339887;

    int _next;

    public List<Position> Positions { get; } = [];

    /// <summary>
    /// A position was added, renamed or removed, or a point moved.
    /// </summary>
    public event Action? Changed;

    static string NewId(int n) => $"p{n}_{Random.Shared.Next():x8}";

    /// <summary>
    /// Adds "Position N" in the next colour, and returns it.
    /// </summary>
    public Position Add()
    {
        _next++;
        var p = new Position { Id = NewId(_next), Name = $"Position {_next}", Colour = ColourFor(_next - 1) };
        Positions.Add(p);
        Changed?.Invoke();
        return p;
    }

    /// <summary>
    /// The <paramref name="n"/>th position's colour: a bright, saturated hue stepping round the wheel by the golden
    /// ratio, so no two match.
    /// </summary>
    public static Rgb ColourFor(int n) => Rgb.FromHsv(Gd.Fmod(0.08 + n * Golden, 1.0), 0.75, 1.0);

    public Position? Find(string id) => Positions.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// A copy of position <paramref name="id"/> ("... copy", the next colour, the same points), added after it.
    /// </summary>
    public Position? Duplicate(string id)
    {
        if (Find(id) is not { } source)
            return null;
        _next++;
        var p = new Position { Id = NewId(_next), Name = $"{source.Name} copy", Colour = ColourFor(_next - 1) };
        foreach (var (uid, v) in source.Points)
            p.Points[uid] = v;
        Positions.Insert(Positions.IndexOf(source) + 1, p);
        Changed?.Invoke();
        return p;
    }

    public void Remove(string id)
    {
        if (Positions.RemoveAll(p => p.Id == id) > 0)
            Changed?.Invoke();
    }

    public void Rename(string id, string name)
    {
        name = name.Trim();
        if (Find(id) is not { } p || name == "" || name == p.Name)
            return;
        p.Name = name;
        Changed?.Invoke();
    }

    /// <summary>
    /// A fixture's pan and tilt in a position (<see cref="DefaultPoint"/> if it has none).
    /// </summary>
    public Vector2 Point(string id, string uid) => Find(id)?.Points.GetValueOrDefault(uid, DefaultPoint) ?? DefaultPoint;

    public void SetPoint(string id, string uid, Vector2 v)
    {
        if (Find(id) is not { } p)
            return;
        p.Points[uid] = Vector2.Clamp(v, Vector2.Zero, Vector2.One);
        Changed?.Invoke();
    }

    /// <summary>
    /// Drops a fixture's point in a position: it goes back to <see cref="DefaultPoint"/>.
    /// </summary>
    public void ClearPoint(string id, string uid)
    {
        if (Find(id) is { } p && p.Points.Remove(uid))
            Changed?.Invoke();
    }

    public void Clear()
    {
        Positions.Clear();
        _next = 0;
        Changed?.Invoke();
    }

    public JsonObject ToJson()
    {
        var list = new JsonArray();
        foreach (var p in Positions)
        {
            var points = new JsonObject();
            foreach (var (uid, v) in p.Points)
                points[uid] = new JsonArray(Gd.Snapped(v.X, 0.0001), Gd.Snapped(v.Y, 0.0001));
            list.Add(new JsonObject { ["id"] = p.Id, ["name"] = p.Name, ["colour"] = p.Colour.ToHtml(), ["points"] = points });
        }
        return new JsonObject { ["positions"] = list, ["next"] = _next };
    }

    public void FromJson(JsonNode? d)
    {
        Positions.Clear();
        foreach (var e in d.Arr("positions"))
        {
            if (e is not JsonObject)
                continue;
            var p = new Position
            {
                Id = e.Has("id") ? e.Str("id") : $"p{Random.Shared.Next():x8}",
                Name = e.Str("name", "Position"),
                Colour = Rgb.FromString(e.Str("colour", ""), ColourFor(Positions.Count)),
            };
            if (e.Get("points") is JsonObject points)
                foreach (var (uid, a) in points)
                    if (a is JsonArray { Count: >= 2 } arr)
                        p.Points[uid] = Vector2.Clamp(new Vector2((float)Json.AsDouble(arr[0]), (float)Json.AsDouble(arr[1])), Vector2.Zero, Vector2.One);
            Positions.Add(p);
        }
        _next = Math.Max(d.Int("next", 0), Positions.Count);
        Changed?.Invoke();
    }
}
