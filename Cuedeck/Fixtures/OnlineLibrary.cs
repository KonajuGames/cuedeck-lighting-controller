using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Cuedeck.Common;

namespace Cuedeck.Fixtures;

/// <summary>
/// The fixture library's online sources, as sLight's <c>Library</c> had them:
/// <list type="bullet">
/// <item>the current Open Fixture Library, listed from GitHub in one request, each fixture downloaded and converted
/// when it's wanted;</item>
/// <item>GDTF-Share (gdtf-share.com), which works the same way but needs a (free) account.</item>
/// </list>
/// Downloaded fixtures are kept in <see cref="CacheFolder"/>, so they work offline afterwards. The GDTF-Share password
/// is only ever sent in the login request and never kept; the session cookie and user name it gives are held in
/// memory for this run alone, so logging in is once a launch. Nothing about the account is written to disk.
/// Requests run in the background: start one, and check its task each frame.
/// </summary>
sealed class OnlineLibrary
{
    /// <summary>
    /// The OFL on GitHub: the tree call lists every fixture in one request; raw.githubusercontent serves the files.
    /// </summary>
    const string OFL_TREE_URL = "https://api.github.com/repos/OpenLightingProject/open-fixture-library/git/trees/master?recursive=1";
    const string OFL_RAW = "https://raw.githubusercontent.com/OpenLightingProject/open-fixture-library/master/fixtures/";

    /// <summary>
    /// GDTF-Share's public API: login.php sets a session cookie (about two hours) that getList.php and
    /// downloadFile.php want back.
    /// </summary>
    const string GDTF_LOGIN_URL = "https://gdtf-share.com/apis/public/login.php";
    const string GDTF_LIST_URL = "https://gdtf-share.com/apis/public/getList.php";
    const string GDTF_DOWNLOAD_URL = "https://gdtf-share.com/apis/public/downloadFile.php";
    const string USER_AGENT = "Cuedeck-fixture-library";

    readonly FixtureLibrary _bundled;
    readonly HttpClient _http;
    readonly object _lock = new();
    readonly Dictionary<string, FixtureProfile> _profiles = [];

    // Each list is built in full and then swapped in, so the UI never sees one half made.
    volatile List<LibraryEntry> _ofl = [];
    volatile List<LibraryEntry> _gdtf = [];
    volatile Dictionary<string, LibraryEntry> _byId = [];

    // GDTF-Share's session: in memory only, never saved.
    volatile string _cookie = "";
    volatile string _user = "";

    public string CacheFolder { get; }

    /// <param name="handler">How requests go out; a stand-in for tests. Cookies aren't kept by it: the session's is
    /// carried by hand, so it's never anywhere but here.</param>
    public OnlineLibrary(FixtureLibrary bundled, string cacheFolder, HttpMessageHandler? handler = null)
    {
        _bundled = bundled;
        CacheFolder = cacheFolder;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(USER_AGENT);
    }

    // ================================================================== OFL ==

    public bool OflReady { get; private set; }
    public string OflError { get; private set; } = "";
    public int OflCount => _ofl.Count;
    public IReadOnlyList<string> OflManufacturers { get; private set; } = [];

    /// <summary>
    /// Fetches the current OFL fixture list from GitHub (the tree, and manufacturers.json for their names). False if
    /// it couldn't; then see <see cref="OflError"/>.
    /// </summary>
    public async Task<bool> RefreshOfl()
    {
        OflError = "";
        var (tree, why) = await GetJson(OFL_TREE_URL);
        if (tree.Get("tree") is not JsonArray nodes)
        {
            OflError = $"couldn't reach GitHub ({why})";
            return false;
        }
        var names = await MakerNames();

        var list = new List<LibraryEntry>();
        var makers = new HashSet<string>();
        foreach (var node in nodes)
        {
            var path = node.Str("path");
            if (!path.StartsWith("fixtures/", StringComparison.Ordinal) || !path.EndsWith(".json", StringComparison.Ordinal) || path == "fixtures/manufacturers.json")
                continue;
            var rel = path[9..]; // "<manufacturer>/<key>.json"
            var slash = rel.IndexOf('/');
            if (slash < 1)
                continue;
            var makerKey = rel[..slash];
            var stem = Path.GetFileNameWithoutExtension(rel[(slash + 1)..]);
            var id = Sanitise(makerKey + "-" + stem);
            var maker = names.GetValueOrDefault(makerKey, Gd.Capitalize(makerKey));
            var model = Gd.Capitalize(stem.Replace('-', ' '));
            list.Add(
                new LibraryEntry(id, maker, model, "", [], [], false, 0, [], "")
                {
                    Online = true,
                    Bundled = _bundled.Entry(id) is not null,
                    Path = rel,
                }
            );
            makers.Add(maker);
        }
        list.Sort(ByMakerThenModel);
        _ofl = list;
        IndexEntries();
        OflManufacturers = [.. makers.Order(StringComparer.Ordinal)];
        OflReady = true;
        return true;
    }

    /// <summary>
    /// OFL fixtures matching every word of <paramref name="text"/> and the manufacturer (or any for ""). There's
    /// nothing else to filter by until a fixture's downloaded.
    /// </summary>
    public List<LibraryEntry> SearchOfl(string text = "", string maker = "") => Search(_ofl, text, maker);

    public int CachedOflCount => CachedFiles().Count(f => !Path.GetFileName(f).StartsWith("gdtf-", StringComparison.Ordinal));

    /// <summary>
    /// Deletes the OFL downloads kept on disk.
    /// </summary>
    public void ClearOflCache()
    {
        foreach (var f in CachedFiles().Where(f => !Path.GetFileName(f).StartsWith("gdtf-", StringComparison.Ordinal)))
            File.Delete(f);
        lock (_lock)
            foreach (var e in _ofl)
                _profiles.Remove(e.Id);
    }

    async Task<Dictionary<string, string>> MakerNames()
    {
        var (d, _) = await GetJson(OFL_RAW + "manufacturers.json");
        var output = new Dictionary<string, string>();
        if (d is JsonObject o)
        {
            foreach (var (key, value) in o)
            {
                if (value is not JsonObject || !value.Has("name"))
                    continue;
                var name = value.Str("name");
                output[key] = name.Equals(key, StringComparison.OrdinalIgnoreCase) ? Gd.Capitalize(key) : name;
            }
        }
        return output;
    }

    // =========================================================== GDTF-Share ==

    public bool GdtfLoggedIn => _cookie != "";

    /// <summary>
    /// Who's logged in, to show; "" when no one is.
    /// </summary>
    public string GdtfUser => _user;

    public bool GdtfReady { get; private set; }
    public string GdtfError { get; private set; } = "";
    public int GdtfCount => _gdtf.Count;
    public IReadOnlyList<string> GdtfManufacturers { get; private set; } = [];

    /// <summary>
    /// Swaps a GDTF-Share user name and password for a session cookie. The password goes in this one request and
    /// isn't kept; the user name and cookie last until the app closes. False if it failed; then see
    /// <see cref="GdtfError"/>.
    /// </summary>
    public async Task<bool> GdtfLogin(string user, string password)
    {
        GdtfError = "";
        var body = Json.Stringify(new JsonObject { ["user"] = user, ["password"] = password });
        using var request = new HttpRequestMessage(HttpMethod.Post, GDTF_LOGIN_URL) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        var (ok, _, headers, bytes, why) = await Send(request);
        var cookie = CookieFrom(headers);
        var parsed = ParseJson(bytes);
        if (!ok || cookie == "" || parsed is not JsonObject || !parsed.Bool("result"))
        {
            GdtfError = parsed is JsonObject && parsed.Has("error") ? parsed.Str("error") : $"login failed ({why})";
            return false;
        }
        _cookie = cookie;
        _user = user;
        return true;
    }

    /// <summary>
    /// Forgets the session and its fixture list, as if no one had logged in. Downloaded fixtures stay.
    /// </summary>
    public void GdtfLogout()
    {
        _cookie = "";
        _user = "";
        _gdtf = [];
        IndexEntries();
        GdtfManufacturers = [];
        GdtfReady = false;
    }

    /// <summary>
    /// Fetches GDTF-Share's current list of fixtures, once logged in. False if it couldn't; then see
    /// <see cref="GdtfError"/>.
    /// </summary>
    public async Task<bool> RefreshGdtf()
    {
        GdtfError = "";
        if (_cookie == "")
        {
            GdtfError = "not logged in";
            return false;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, GDTF_LIST_URL);
        request.Headers.Add("Cookie", _cookie);
        var (ok, code, _, bytes, why) = await Send(request);
        if (!ok)
        {
            if (code == 401)
                _cookie = ""; // the session's run out: log in again
            GdtfError = $"couldn't reach GDTF-Share ({why})";
            return false;
        }
        var parsed = ParseJson(bytes);
        if ((parsed.Get("list") ?? parsed.Get("fixtures")) is not JsonArray rows)
        {
            GdtfError = "unexpected response from GDTF-Share";
            return false;
        }

        var list = new List<LibraryEntry>();
        var makers = new HashSet<string>();
        foreach (var row in rows)
        {
            if (row is not JsonObject || row.Get("rid") is not { } ridNode)
                continue;
            var maker = row.Str("manufacturer", "?");
            var model = row.Str("fixture", "?");
            // GDTF-Share's numbers come as floats: a whole number for the download's "rid".
            var rid = ((long)Json.AsDouble(ridNode, Gd.ToInt(Json.AsString(ridNode)))).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var id = "gdtf-" + Sanitise($"{maker}-{model}-{rid}");
            var modes = row.Arr("modes").OfType<JsonObject>().Select(m => (m.Str("name"), Json.AsInt(m.Get("dmxfootprint")))).ToList();
            var author = row.Str("creator", row.Str("uploader"));
            list.Add(
                new LibraryEntry(id, maker, model, "", [], modes, false, 0, [author], "")
                {
                    Online = true,
                    Gdtf = true,
                    Bundled = _bundled.Entry(id) is not null,
                    Rid = rid,
                    Version = row.Str("version"),
                }
            );
            makers.Add(maker);
        }
        list.Sort(ByMakerThenModel);
        _gdtf = list;
        IndexEntries();
        GdtfManufacturers = [.. makers.Order(StringComparer.Ordinal)];
        GdtfReady = true;
        return true;
    }

    public List<LibraryEntry> SearchGdtf(string text = "", string maker = "") => Search(_gdtf, text, maker);

    public int CachedGdtfCount => CachedFiles().Count(f => Path.GetFileName(f).StartsWith("gdtf-", StringComparison.Ordinal));

    public void ClearGdtfCache()
    {
        foreach (var f in CachedFiles().Where(f => Path.GetFileName(f).StartsWith("gdtf-", StringComparison.Ordinal)))
            File.Delete(f);
        lock (_lock)
            foreach (var e in _gdtf)
                _profiles.Remove(e.Id);
    }

    /// <summary>
    /// Joins the response's cookies into one "name=value; …" for the next request's Cookie header.
    /// </summary>
    static string CookieFrom(IEnumerable<string> setCookies) =>
        string.Join(
            "; ",
            setCookies.Select(v =>
            {
                var semi = v.IndexOf(';');
                return (semi >= 0 ? v[..semi] : v).Trim();
            })
        );

    // ============================================================ Fixtures ==

    public LibraryEntry? Entry(string id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// An online fixture's profile if it's been downloaded, this run or before (from the cache); else null.
    /// </summary>
    public FixtureProfile? Resolve(string id)
    {
        lock (_lock)
            if (_profiles.TryGetValue(id, out var p))
                return p;
        var file = Path.Combine(CacheFolder, id + ".json");
        if (!File.Exists(file))
            return null;
        try
        {
            if (Json.Parse(File.ReadAllText(file)) is not JsonObject d)
                return null;
            var p = FixtureProfile.FromJson(d);
            if (Entry(id) is { } e)
                FixtureLibrary.NamesFrom(p, e);
            lock (_lock)
                _profiles[id] = p;
            return p;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads and converts an online fixture (or takes it from the cache), keeping it in the cache. Null if it
    /// couldn't; then see <see cref="OflError"/> or <see cref="GdtfError"/>.
    /// </summary>
    public async Task<FixtureProfile?> Download(string id)
    {
        if (Resolve(id) is { } cached)
            return cached;
        if (Entry(id) is not { } e)
        {
            if (id.StartsWith("gdtf-", StringComparison.Ordinal))
                GdtfError = "unknown fixture id";
            else
                OflError = "unknown fixture id";
            return null;
        }
        return e.Gdtf ? await DownloadGdtf(e) : await DownloadOfl(e);
    }

    async Task<FixtureProfile?> DownloadOfl(LibraryEntry e)
    {
        var (parsed, why) = await GetJson(OFL_RAW + e.Path);
        // The OFL keeps a stub where a fixture's been renamed: one hop is followed.
        if (parsed is JsonObject && parsed.Has("redirectTo"))
            (parsed, why) = await GetJson(OFL_RAW + parsed.Str("redirectTo") + ".json");
        if (parsed is not JsonObject)
        {
            OflError = $"download failed ({why})";
            return null;
        }
        var result = FixtureImport.FromOfl(parsed);
        if (result.Profile is not { } p)
        {
            OflError = result.Error ?? "";
            return null;
        }
        p.Id = e.Id;
        p.Manufacturer = e.Maker;
        p.Model = parsed.Str("name", e.Model);
        Keep(p);
        return p;
    }

    /// <summary>
    /// Downloads a GDTF-Share revision (a .gdtf) and reads it as an imported file would be. The download is on disk
    /// only while it's read; the converted profile's what's kept.
    /// </summary>
    async Task<FixtureProfile?> DownloadGdtf(LibraryEntry e)
    {
        if (_cookie == "")
        {
            GdtfError = "not logged in";
            return null;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{GDTF_DOWNLOAD_URL}?rid={Uri.EscapeDataString(e.Rid)}");
        request.Headers.Add("Cookie", _cookie);
        var (ok, code, _, bytes, why) = await Send(request);
        if (!ok)
        {
            if (code == 401)
                _cookie = "";
            GdtfError = $"download failed ({why})";
            return null;
        }
        Directory.CreateDirectory(CacheFolder);
        var temp = Path.Combine(CacheFolder, $"_download_{Guid.NewGuid():N}.gdtf");
        ImportResult result;
        try
        {
            await File.WriteAllBytesAsync(temp, bytes);
            result = FixtureImport.FromPath(temp);
        }
        catch (IOException x)
        {
            GdtfError = $"couldn't keep the download ({x.Message})";
            return null;
        }
        finally
        {
            File.Delete(temp);
        }
        if (result.Profile is not { } p)
        {
            GdtfError = result.Error ?? "";
            return null;
        }
        p.Id = e.Id;
        p.Manufacturer = e.Maker;
        p.Model = e.Model;
        Keep(p);
        return p;
    }

    void Keep(FixtureProfile p)
    {
        lock (_lock)
            _profiles[p.Id] = p;
        try
        {
            Directory.CreateDirectory(CacheFolder);
            File.WriteAllText(Path.Combine(CacheFolder, p.Id + ".json"), Json.Stringify(p.ToJson()));
        }
        catch (IOException) { }
    }

    // ============================================================= Helpers ==

    IEnumerable<string> CachedFiles() => Directory.Exists(CacheFolder) ? Directory.EnumerateFiles(CacheFolder, "*.json") : [];

    void IndexEntries()
    {
        var byId = new Dictionary<string, LibraryEntry>();
        foreach (var e in _ofl)
            byId[e.Id] = e;
        foreach (var e in _gdtf)
            byId[e.Id] = e;
        _byId = byId;
    }

    static int ByMakerThenModel(LibraryEntry a, LibraryEntry b)
    {
        var c = Gd.NaturalCompare(a.Maker, b.Maker);
        return c != 0 ? c : Gd.NaturalCompare(a.Model, b.Model);
    }

    static List<LibraryEntry> Search(List<LibraryEntry> entries, string text, string maker)
    {
        var terms = text.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return [.. entries.Where(e => (maker == "" || e.Maker == maker) && terms.All(e.Haystack.Contains))];
    }

    /// <summary>
    /// An id-safe key: lower case letters, digits and single dashes, the same as the bundled library's ids, so an
    /// online fixture's id matches its bundled one.
    /// </summary>
    public static string Sanitise(string s)
    {
        var o = new StringBuilder();
        foreach (var ch in s.ToLowerInvariant())
            o.Append(ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' ? ch : '-');
        var id = o.ToString();
        while (id.Contains("--", StringComparison.Ordinal))
            id = id.Replace("--", "-", StringComparison.Ordinal);
        id = id.Trim();
        if (id.StartsWith('-'))
            id = id[1..];
        if (id.EndsWith('-'))
            id = id[..^1];
        return id;
    }

    static JsonNode? ParseJson(byte[] bytes)
    {
        try
        {
            return Json.Parse(Encoding.UTF8.GetString(bytes));
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    async Task<(JsonNode? Json, string Why)> GetJson(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var (ok, _, _, bytes, why) = await Send(request);
        return ok ? (ParseJson(bytes), why) : (null, why);
    }

    /// <summary>
    /// Sends a request: whether it succeeded, its status code, its Set-Cookie headers, its body, and a few words on
    /// how it went ("HTTP 404", "connection failed (…)").
    /// </summary>
    async Task<(bool Ok, int Code, IReadOnlyList<string> SetCookies, byte[] Body, string Why)> Send(HttpRequestMessage request)
    {
        try
        {
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            var body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : [];
            return (response.IsSuccessStatusCode, code, cookies, body, $"HTTP {code}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return (false, 0, [], [], $"connection failed ({e.Message})");
        }
    }
}
