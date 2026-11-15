using System.Text.Json.Nodes;
using Cuedeck.Common;

namespace Cuedeck.UI.Visualizer;

/// <summary>
/// Where the 3D Visualizer is between runs: in its tab, or popped out into a window of its own and where that is;
/// and whether its controls are shown. This machine's screen layout rather than the show's, so it's kept in
/// <c>ui_state.json</c> in the app's folder, in sLight's form.
/// </summary>
sealed record VizWindowState(bool Floating = false, (int X, int Y, int Width, int Height)? Rect = null, bool Ui = true)
{
    public static string DefaultPath => Path.Combine(AppPaths.Folder, "ui_state.json");

    public static VizWindowState Load(string path)
    {
        JsonNode? d = null;
        try
        {
            if (File.Exists(path))
                d = Json.Parse(File.ReadAllText(path));
        }
        catch (IOException) { }
        if (d is not JsonObject)
            return new VizWindowState();
        var r = d.Arr("rect");
        (int, int, int, int)? rect = r.Count == 4 ? (Json.AsInt(r[0]), Json.AsInt(r[1]), Json.AsInt(r[2]), Json.AsInt(r[3])) : null;
        return new VizWindowState(d.Bool("floating"), rect, d.Bool("ui", true));
    }

    public void Save(string path)
    {
        var d = new JsonObject { ["floating"] = Floating, ["ui"] = Ui };
        if (Floating && Rect is { } r)
            d["rect"] = new JsonArray(r.X, r.Y, r.Width, r.Height);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Json.StringifyIndented(d));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
