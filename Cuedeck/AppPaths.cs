namespace Cuedeck;

/// <summary>
/// Where the app keeps what it writes for itself: settings, autosaves, fixture profiles and the error log. sLight kept
/// these in Godot's user:// folder; Cuedeck keeps them in its own folder under Local AppData.
/// </summary>
static class AppPaths
{
    /// <summary>
    /// %LOCALAPPDATA%\Cuedeck.
    /// </summary>
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cuedeck");
}
