using System.Text.Json.Nodes;
using Cuedeck.Common;

namespace Cuedeck;

/// <summary>
/// The DJ software whose music library, lists and beat grids DJ Edit and DJ Perform Mode read. Both talk OS2L.
/// </summary>
enum DjSoftware
{
    VirtualDj,
    Cuepoint,
}

/// <summary>
/// This machine's options (the Options dialog), kept between runs in a small JSON file under
/// <see cref="AppPaths.Folder"/>: not in the show, as they're about this computer's setup rather than the lighting.
/// sLight's <c>AppSettings</c>, in the same format.
/// </summary>
sealed class AppSettings(string path)
{
    static readonly string[] Keys = ["virtualdj", "cuepoint"];
    static readonly string[] Names = ["VirtualDJ", "Cuepoint"];

    JsonObject? _values;

    public static string DefaultPath => Path.Combine(AppPaths.Folder, "settings.json");

    public string FilePath { get; } = path;

    /// <summary>
    /// Reads the file again next time a setting's asked for (another program, or an import, changed it).
    /// </summary>
    public void Reload() => _values = null;

    JsonObject Values
    {
        get
        {
            if (_values is not null)
                return _values;
            try
            {
                _values = File.Exists(FilePath) && Json.Parse(File.ReadAllText(FilePath)) is JsonObject o ? o : [];
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
            {
                _values = [];
            }
            return _values;
        }
    }

    /// <summary>
    /// VirtualDJ unless Cuepoint's chosen.
    /// </summary>
    public DjSoftware DjSoftware
    {
        get => Array.IndexOf(Keys, Values.Str("dj_software", "virtualdj")) is var i && i >= 0 ? (DjSoftware)i : DjSoftware.VirtualDj;
        set
        {
            if (value == DjSoftware && Values.Has("dj_software"))
                return;
            Values["dj_software"] = Keys[(int)value];
            Save();
        }
    }

    /// <summary>
    /// "VirtualDJ" or "Cuepoint", to show.
    /// </summary>
    public string DjSoftwareName => Names[(int)DjSoftware];

    static readonly string[] Looks = ["dark", "light", "high_contrast"];

    /// <summary>
    /// The look chosen, taken on as the app starts: dark unless another's been chosen.
    /// </summary>
    public ThemeKind Theme
    {
        get => Array.IndexOf(Looks, Values.Str("theme", "dark")) is var i && i >= 0 ? (ThemeKind)i : ThemeKind.Dark;
        set
        {
            if (value == Theme && Values.Has("theme"))
                return;
            Values["theme"] = Looks[(int)value];
            Save();
        }
    }

    /// <summary>
    /// How big everything's drawn, as a share of its designed size, taken on as the app starts; null for Windows' own
    /// scale for the screen.
    /// </summary>
    public float? ScreenScale
    {
        get => Values.Has("screen_scale") ? (float)Values.Num("screen_scale", 1) : null;
        set
        {
            if (value == ScreenScale)
                return;
            if (value is { } scale)
                Values["screen_scale"] = scale;
            else
                Values.Remove("screen_scale");
            Save();
        }
    }

    public static string NameOf(DjSoftware software) => Names[(int)software];

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, Json.StringifyIndented(Values));
        }
        catch (IOException) { }
    }
}
