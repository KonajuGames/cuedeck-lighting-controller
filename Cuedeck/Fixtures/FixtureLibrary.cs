using System.Text.Json.Nodes;
using Cuedeck.Common;

namespace Cuedeck.Fixtures;

/// <summary>
/// One fixture in the library's catalogue: enough to search and list it without loading its profile.
/// </summary>
/// <param name="Category">The app's category for it: moving_head, wash, par, beam, strip, blinder or generic.</param>
/// <param name="OflCategories">The Open Fixture Library's categories for it.</param>
/// <param name="Modes">Each mode's name and channel count.</param>
/// <param name="PanTilt">Whether it pans and tilts.</param>
/// <param name="Approximated">How many of its channels were approximated when it was converted.</param>
/// <param name="Shard">The file its profile is in: one per manufacturer.</param>
sealed record LibraryEntry(
    string Id,
    string Maker,
    string Model,
    string Category,
    IReadOnlyList<string> OflCategories,
    IReadOnlyList<(string Name, int Channels)> Modes,
    bool PanTilt,
    int Approximated,
    IReadOnlyList<string> Authors,
    string Shard
)
{
    /// <summary>
    /// What searching looks in: maker, model and categories, in lower case.
    /// </summary>
    public string Haystack { get; } = $"{Maker} {Model} {string.Join(" ", OflCategories)}".ToLowerInvariant();

    /// <summary>
    /// Its narrowest mode's channel count (0 if it has no modes).
    /// </summary>
    public int NarrowestMode => Modes.Count == 0 ? 0 : Modes.Min(m => m.Channels);

    /// <summary>
    /// From an online source (the OFL on GitHub, or GDTF-Share), downloaded when it's wanted.
    /// </summary>
    public bool Online { get; init; }

    /// <summary>
    /// From GDTF-Share.
    /// </summary>
    public bool Gdtf { get; init; }

    /// <summary>
    /// An online fixture that's in the bundled library too.
    /// </summary>
    public bool Bundled { get; init; }

    /// <summary>
    /// An OFL fixture's file in the OFL's fixtures folder ("cameo/auro-beam-150.json").
    /// </summary>
    public string Path { get; init; } = "";

    /// <summary>
    /// A GDTF-Share fixture's revision id, to download it by, and its version.
    /// </summary>
    public string Rid { get; init; } = "";

    public string Version { get; init; } = "";
}

/// <summary>
/// The bundled fixture library: some 640 real fixtures converted from the Open Fixture Library (see the library
/// folder's ATTRIBUTION.md). A small catalogue (<c>index.json</c>) is read at start; each fixture's full profile is in
/// its manufacturer's file in <c>lib/</c>, read and kept the first time one of its fixtures is wanted, so the 640
/// profiles never all load. sLight's <c>Library</c> autoload; its online sources are <see cref="OnlineLibrary"/>.
/// </summary>
sealed class FixtureLibrary
{
    readonly string _folder;
    readonly List<LibraryEntry> _entries = [];
    readonly Dictionary<string, LibraryEntry> _byId = [];
    readonly Dictionary<string, JsonObject> _shards = [];
    readonly Dictionary<string, FixtureProfile> _profiles = [];
    readonly Dictionary<string, string> _meta = [];

    /// <summary>
    /// The library folder shipped beside the app.
    /// </summary>
    public static string DefaultFolder => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Library");

    public FixtureLibrary(string folder)
    {
        _folder = folder;
        LoadIndex();
    }

    public bool Available => _entries.Count > 0;

    public int Count => _entries.Count;

    public IReadOnlyList<LibraryEntry> Entries => _entries;

    /// <summary>
    /// Every manufacturer, sorted.
    /// </summary>
    public IReadOnlyList<string> Manufacturers { get; private set; } = [];

    /// <summary>
    /// The app categories the library has, sorted.
    /// </summary>
    public IReadOnlyList<string> Categories { get; private set; } = [];

    /// <summary>
    /// A note for the browser's footer: "638 fixtures · OFL 6c52a67 · 2026-09-11".
    /// </summary>
    public string SourceNote
    {
        get
        {
            if (!Available)
                return "no bundled library found";
            List<string> parts = [$"{_entries.Count} fixtures"];
            if (_meta.TryGetValue("ofl_commit", out var commit))
                parts.Add($"OFL {commit[..Math.Min(7, commit.Length)]}");
            if (_meta.TryGetValue("generated", out var generated))
                parts.Add(generated[..Math.Min(10, generated.Length)]);
            return string.Join(" · ", parts);
        }
    }

    void LoadIndex()
    {
        var path = Path.Combine(_folder, "index.json");
        if (!File.Exists(path) || Json.Parse(File.ReadAllText(path)) is not JsonObject index || index.Get("fixtures") is not JsonArray fixtures)
            return;
        foreach (var key in new[] { "generated", "count", "ofl_commit", "source" })
            if (index.Has(key))
                _meta[key] = index.Str(key);
        foreach (var e in fixtures)
        {
            if (e is not JsonObject || !e.Has("id"))
                continue;
            var entry = new LibraryEntry(
                e.Str("id"),
                e.Str("maker", "?"),
                e.Str("model", ""),
                e.Str("cat", "generic"),
                [.. e.Arr("ofl_cat").Select(c => Json.AsString(c))],
                [.. e.Arr("modes").Select(m => (m.Str("name", "Mode"), m.Int("ch", 0)))],
                e.Bool("pt", false),
                e.Int("approx", 0),
                [.. e.Arr("authors").Select(a => Json.AsString(a))],
                e.Str("shard", "")
            );
            _entries.Add(entry);
            _byId[entry.Id] = entry;
        }
        Manufacturers = [.. _entries.Select(e => e.Maker).Distinct().Order(StringComparer.OrdinalIgnoreCase)];
        Categories = [.. _entries.Select(e => e.Category).Distinct().Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The catalogue entries matching every filter given, in catalogue order (manufacturer, then model).
    /// </summary>
    /// <param name="text">Space-separated words, all of which must be in the maker, model or categories.</param>
    /// <param name="maker">A manufacturer exactly, or "" for any.</param>
    /// <param name="category">An app category exactly, or "" for any.</param>
    /// <param name="maxChannels">Only fixtures with a mode this narrow or narrower; 0 for any.</param>
    /// <param name="needsPanTilt">Only fixtures that pan and tilt.</param>
    public List<LibraryEntry> Search(string text = "", string maker = "", string category = "", int maxChannels = 0, bool needsPanTilt = false)
    {
        var terms = text.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return
        [
            .. _entries.Where(e =>
                (maker == "" || e.Maker == maker)
                && (category == "" || e.Category == category)
                && (!needsPanTilt || e.PanTilt)
                && (maxChannels <= 0 || e.NarrowestMode <= maxChannels)
                && terms.All(e.Haystack.Contains)
            ),
        ];
    }

    public LibraryEntry? Entry(string id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// The full profile for <paramref name="id"/>, from its manufacturer's file (read the first time); null if
    /// there's no such fixture.
    /// </summary>
    public FixtureProfile? Resolve(string id)
    {
        if (_profiles.TryGetValue(id, out var cached))
            return cached;
        if (!_byId.TryGetValue(id, out var e))
            return null;
        if (Shard(e.Shard).Get(id) is not JsonObject data)
            return null;
        var p = FixtureProfile.FromJson(data);
        NamesFrom(p, e);
        _profiles[id] = p;
        return p;
    }

    /// <summary>
    /// A profile saved before manufacturer and model (its one name read as the model) takes both from its
    /// catalogue entry.
    /// </summary>
    public static void NamesFrom(FixtureProfile p, LibraryEntry e)
    {
        if (p.Manufacturer != "")
            return;
        p.Manufacturer = e.Maker;
        p.Model = e.Model;
    }

    JsonObject Shard(string key)
    {
        if (_shards.TryGetValue(key, out var shard))
            return shard;
        var path = Path.Combine(_folder, "lib", key + ".json");
        shard = File.Exists(path) && Json.Parse(File.ReadAllText(path)) is JsonObject parsed ? parsed : [];
        _shards[key] = shard;
        return shard;
    }
}
