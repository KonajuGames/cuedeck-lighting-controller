using System.Text.Json.Nodes;
using Cuedeck.Common;

namespace Cuedeck.UI.Visualizer;

/// <summary>
/// Where the 3D Visualizer is between runs: docked on the right of the main window, or popped out into a window of
/// its own and where that is; whether its controls are shown; and whether it's folded away in the dock. This
/// machine's screen layout rather than the show's, so it's kept in <c>ui_state.json</c> in the app's folder, in
/// sLight's form with <c>collapsed</c> added.
/// </summary>
sealed record VizWindowState(bool Floating = false, (int X, int Y, int Width, int Height)? Rect = null, bool Ui = true, bool Collapsed = false)
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
        return new VizWindowState(d.Bool("floating"), rect, d.Bool("ui", true), d.Bool("collapsed"));
    }

    public void Save(string path)
    {
        var d = new JsonObject { ["floating"] = Floating, ["ui"] = Ui, ["collapsed"] = Collapsed };
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
