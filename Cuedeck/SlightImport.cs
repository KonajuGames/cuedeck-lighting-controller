using System.Text.Json.Nodes;
using Cuedeck.Common;

namespace Cuedeck;

/// <summary>
/// What an import from sLight brought in: how many fixture profiles, model folders and settings (keys of the settings,
/// remembered folders and window places), whether its preset, the show it kept untitled (where it's gone now), and
/// the last show it had open (as Cuedeck will reopen it).
/// </summary>
sealed record SlightImportResult(int Profiles, int Models, int Settings, bool Preset, string? UntitledShow, string? LastShow)
{
    public bool Any => Profiles > 0 || Models > 0 || Settings > 0 || Preset || UntitledShow is not null || LastShow is not null;

    static string Count(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";

    public override string ToString()
    {
        var parts = new List<string>();
        if (Profiles > 0)
            parts.Add(Count(Profiles, "fixture profile"));
        if (Models > 0)
            parts.Add(Count(Models, "fixture model"));
        if (Settings > 0)
            parts.Add(Count(Settings, "setting"));
        if (Preset)
            parts.Add("its preset");
        if (UntitledShow is not null)
            parts.Add($"its untitled show ({Path.GetFileName(UntitledShow)})");
        if (parts.Count == 0)
            return LastShow is null ? "Nothing new to import from sLight." : "Imported from sLight: the last show it had open.";
        var list = parts.Count == 1 ? parts[0] : string.Join(", ", parts[..^1]) + " and " + parts[^1];
        return $"Imported from sLight: {list}.";
    }
}

/// <summary>
/// Brings sLight's own files from Godot's user:// folder (<c>%APPDATA%\Godot\app_userdata\sLight</c>) into Cuedeck's
/// app folder, which keeps them by the same names in the same formats: its custom fixture profiles and their GDTF
/// models, the online library's cache, its settings, remembered folders and window places, its preset, the show it
/// saved untitled (<c>dmx_show.json</c>, with its songs' timelines) and the last show it had open. Nothing Cuedeck
/// already has is overwritten (settings are merged a key at a time), and sLight's folder isn't touched. Its autosave,
/// renders, logs and caches stay behind: they were sLight's alone.
/// </summary>
sealed class SlightImport(string to)
{
    /// <summary>
    /// sLight's project names, newest first: it was "DMX Lighting Controller" before 2026-09-06, and Godot names the
    /// user:// folder after the project.
    /// </summary>
    static readonly string[] Names = ["sLight", "DMX Lighting Controller"];

    /// <summary>
    /// The JSON objects merged a key at a time: settings, remembered folders, window places.
    /// </summary>
    static readonly string[] Merged = ["settings.json", "recent_dirs.json", "ui_state.json"];

    const string UNTITLED = "dmx_show.json";

    /// <summary>
    /// sLight's user:// folders that are on this computer, newest name first, under <paramref name="appData"/>
    /// (Roaming AppData by default).
    /// </summary>
    public static IReadOnlyList<string> Folders(string? appData = null)
    {
        appData ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return [.. Names.Select(n => Path.Combine(appData, "Godot", "app_userdata", n)).Where(Directory.Exists)];
    }

    /// <summary>
    /// Imports from each of <paramref name="from"/> in turn, the first's files winning over the later ones'.
    /// </summary>
    public SlightImportResult Import(IReadOnlyList<string> from)
    {
        int profiles = 0,
            models = 0,
            settings = 0;
        var preset = false;
        string? untitled = null,
            lastShow = null;
        foreach (var folder in from)
        {
            profiles += CopyFiles(folder, "fixture_profiles", "*.json");
            models += CopyFolders(folder, "fixture_models");
            CopyTree(Path.Combine(folder, "fixture_online"), Path.Combine(to, "fixture_online"), skip: "_gdtf_download.gdtf");
            foreach (var name in Merged)
                settings += Merge(folder, name);
            preset |= CopyFile(Path.Combine(folder, "dmx_preset.json"), Path.Combine(to, "dmx_preset.json"));
            if (CopyFile(Path.Combine(folder, UNTITLED), Path.Combine(to, UNTITLED)))
            {
                untitled = Path.Combine(to, UNTITLED);
                CopyTree(Path.Combine(folder, "dmx_show.timelines"), Path.Combine(to, "dmx_show.timelines"));
            }
            lastShow ??= LastShow(folder);
        }
        return new SlightImportResult(profiles, models, settings, preset, untitled, lastShow);
    }

    /// <summary>
    /// A path in sLight's user:// as the same place in Cuedeck's folder; any other as it is.
    /// </summary>
    string Mapped(string path) => path.StartsWith("user://", StringComparison.Ordinal) ? Path.Combine(to, path["user://".Length..].Replace('/', Path.DirectorySeparatorChar)) : path;

    static bool CopyFile(string from, string target)
    {
        if (!File.Exists(from) || File.Exists(target))
            return false;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(from, target);
        return true;
    }

    /// <summary>
    /// Copies the files in <paramref name="from"/> into <paramref name="target"/>, and its folders' files, where
    /// there's nothing there already. Returns how many.
    /// </summary>
    static int CopyTree(string from, string target, string? skip = null)
    {
        if (!Directory.Exists(from))
            return 0;
        var n = 0;
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            if (Path.GetFileName(file) != skip && CopyFile(file, Path.Combine(target, Path.GetRelativePath(from, file))))
                n++;
        return n;
    }

    int CopyFiles(string folder, string sub, string pattern)
    {
        var from = Path.Combine(folder, sub);
        return Directory.Exists(from) ? Directory.EnumerateFiles(from, pattern).Count(f => CopyFile(f, Path.Combine(to, sub, Path.GetFileName(f)))) : 0;
    }

    /// <summary>
    /// Copies each folder in <paramref name="sub"/> (a fixture's models) that Cuedeck hasn't one of. Returns how many.
    /// </summary>
    int CopyFolders(string folder, string sub)
    {
        var from = Path.Combine(folder, sub);
        if (!Directory.Exists(from))
            return 0;
        var n = 0;
        foreach (var dir in Directory.EnumerateDirectories(from))
        {
            var target = Path.Combine(to, sub, Path.GetFileName(dir));
            if (Directory.Exists(target))
                continue;
            CopyTree(dir, target);
            n++;
        }
        return n;
    }

    static JsonObject? ReadObject(string path)
    {
        try
        {
            return File.Exists(path) && Json.Parse(File.ReadAllText(path)) is JsonObject o ? o : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Adds sLight's keys of <paramref name="name"/> that Cuedeck's hasn't, its user:// paths made Cuedeck's. Returns
    /// how many.
    /// </summary>
    int Merge(string folder, string name)
    {
        if (ReadObject(Path.Combine(folder, name)) is not { } theirs)
            return 0;
        var path = Path.Combine(to, name);
        var ours = ReadObject(path) ?? [];
        var n = 0;
        foreach (var (key, value) in theirs)
        {
            if (ours.ContainsKey(key))
                continue;
            ours[key] = value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String ? JsonValue.Create(Mapped(v.GetValue<string>())) : value?.DeepClone();
            n++;
        }
        if (n > 0)
        {
            Directory.CreateDirectory(to);
            File.WriteAllText(path, Json.StringifyIndented(ours));
        }
        return n;
    }

    /// <summary>
    /// Remembers sLight's last show as Cuedeck's, if Cuedeck has none and the show's there. Returns its path then.
    /// </summary>
    string? LastShow(string folder)
    {
        var store = Path.Combine(to, "last_show.json");
        if (File.Exists(store) || ReadObject(Path.Combine(folder, "last_show.json"))?.Str("path", "") is not { Length: > 0 } path)
            return null;
        var mapped = Mapped(path);
        if (!File.Exists(mapped))
            return null;
        Directory.CreateDirectory(to);
        File.WriteAllText(store, Json.Stringify(new JsonObject { ["path"] = mapped }));
        return mapped;
    }
}
