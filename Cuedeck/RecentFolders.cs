using System.Text.Json.Nodes;
using Cuedeck.Common;

namespace Cuedeck;

/// <summary>
/// Each file dialog's own last-used folder, by a short key ("show", "fixture_import", "gltf_model", "mvr"), kept
/// between runs in <c>recent_dirs.json</c> in the app's folder: so the shows, the fixture imports, the glTF models and
/// the MVR rigs don't have to share one "last folder". sLight's <c>RecentDirs</c>, in its form.
/// </summary>
sealed class RecentFolders
{
    readonly string? _path;
    readonly Dictionary<string, string> _folders = [];

    public static string DefaultPath => Path.Combine(AppPaths.Folder, "recent_dirs.json");

    /// <summary>
    /// The folders kept in <paramref name="path"/>, or only for this run if it's null.
    /// </summary>
    public RecentFolders(string? path)
    {
        _path = path;
        Reload();
    }

    /// <summary>
    /// Reads the folders from the file again (an import added some).
    /// </summary>
    public void Reload()
    {
        if (_path is not { } path)
            return;
        try
        {
            if (File.Exists(path) && Json.Parse(File.ReadAllText(path)) is JsonObject d)
                foreach (var (key, value) in d)
                    if (Json.AsString(value) is { Length: > 0 } folder)
                        _folders[key] = folder;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Where <paramref name="key"/>'s dialog was last used, if that folder's still there.
    /// </summary>
    public string? For(string key) => _folders.TryGetValue(key, out var folder) && Directory.Exists(folder) ? folder : null;

    /// <summary>
    /// Remembers the folder of <paramref name="file"/>, chosen in <paramref name="key"/>'s dialog.
    /// </summary>
    public void Remember(string key, string? file)
    {
        if (Path.GetDirectoryName(file) is not { Length: > 0 } folder || _folders.GetValueOrDefault(key) == folder)
            return;
        _folders[key] = folder;
        Save();
    }

    /// <summary>
    /// Shows a dialog through <paramref name="ask"/>, starting in <paramref name="key"/>'s folder (or
    /// <paramref name="fallback"/> the first time), and remembers where the file it gives back is. Null if cancelled.
    /// </summary>
    public string? Ask(string key, Func<string?, string?> ask, string? fallback = null)
    {
        var file = ask(For(key) ?? fallback);
        Remember(key, file);
        return file;
    }

    void Save()
    {
        if (_path is null)
            return;
        var d = new JsonObject();
        foreach (var (key, folder) in _folders)
            d[key] = folder;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, Json.Stringify(d));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
