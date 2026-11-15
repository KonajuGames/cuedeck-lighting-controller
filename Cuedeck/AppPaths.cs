namespace Cuedeck;

/// <summary>
/// Where the app keeps what it writes for itself: settings, autosaves, fixture profiles and the error log. sLight kept
/// these in Godot's user:// folder; Cuedeck keeps them in its own folder under Local AppData.
/// </summary>
static class AppPaths
{
    /// <summary>
    /// %LOCALAPPDATA%\Cuedeck, unless <see cref="UseFolder"/> said otherwise.
    /// </summary>
    public static string Folder { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cuedeck");

    /// <summary>
    /// Keeps everything in <paramref name="folder"/> instead (the screenshot tool's scratch folder), before anything's
    /// read from or written to the usual one.
    /// </summary>
    public static void UseFolder(string folder) => Folder = folder;
}
