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
