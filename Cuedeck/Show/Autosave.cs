using System.Globalization;
using System.Text.Json.Nodes;
using Cuedeck.Common;

namespace Cuedeck.Show;

/// <summary>
/// Autosave and crash recovery for the show.
/// <para>
/// While the app runs, <see cref="Tick"/> is called every <see cref="Interval"/> seconds: when the show differs from
/// the one last saved, loaded or autosaved, a snapshot of it goes to <see cref="File"/>, with the show file it belongs
/// to and when, never touching the show file itself. <see cref="SessionFile"/> says whether the app is running:
/// <see cref="Begin"/> marks it at startup and <see cref="End"/> clears it (and the snapshot) on a clean quit. So at
/// the next start, finding it still marked means the app didn't close properly, and the snapshot is offered back.
/// </para>
/// </summary>
sealed class Autosave
{
    public const double Interval = 60;

    string _last = "";

    /// <summary>
    /// The snapshot: {"format", "saved_at" (Unix seconds), "show_path", "show"}.
    /// </summary>
    public string File { get; set; } = Path.Combine(AppPaths.Folder, "autosave.slight");

    /// <summary>
    /// {"running": true} from <see cref="Begin"/> until <see cref="End"/>.
    /// </summary>
    public string SessionFile { get; set; } = Path.Combine(AppPaths.Folder, "session.json");

    /// <summary>
    /// The time snapshots are stamped with. Replace it in tests.
    /// </summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.Now;

    /// <summary>
    /// At startup: the snapshot left by a run that didn't end cleanly (null if the last one did, or left none). Then
    /// this run is marked running.
    /// </summary>
    public JsonObject? Begin()
    {
        var crashed = System.IO.File.Exists(SessionFile) && Json.Parse(ReadText(SessionFile)) is JsonObject s && s.Bool("running", false);
        var snapshot = crashed ? Read() : null;
        Write(SessionFile, new JsonObject { ["running"] = true });
        return snapshot;
    }

    /// <summary>
    /// A clean quit: nothing to recover next time.
    /// </summary>
    public void End()
    {
        Write(SessionFile, new JsonObject { ["running"] = false });
        Discard();
    }

    /// <summary>
    /// The snapshot in <see cref="File"/>, or null if there's none or it's unreadable.
    /// </summary>
    public JsonObject? Read()
    {
        if (!System.IO.File.Exists(File))
            return null;
        return Json.Parse(ReadText(File)) is JsonObject parsed && parsed.Get("show") is JsonObject ? parsed : null;
    }

    public void Discard()
    {
        try
        {
            System.IO.File.Delete(File);
        }
        catch (IOException) { }
    }

    /// <summary>
    /// The show as it stands is saved, or was just loaded: not worth an autosave until it changes.
    /// </summary>
    public void MarkSaved(JsonNode show) => _last = Json.Stringify(show);

    /// <summary>
    /// Whether <paramref name="show"/> differs from the one last saved, loaded or autosaved.
    /// </summary>
    public bool ChangedSince(JsonNode show) => Json.Stringify(show) != _last;

    /// <summary>
    /// Autosaves <paramref name="show"/> (belonging to show file <paramref name="showPath"/>, "" if it's never been
    /// saved) if it's changed. Returns whether it wrote one.
    /// </summary>
    public bool Tick(JsonNode show, string showPath)
    {
        var text = Json.Stringify(show);
        if (text == _last)
            return false;
        var snapshot = new JsonObject
        {
            ["format"] = "sLight autosave",
            ["saved_at"] = Now().ToUnixTimeSeconds(),
            ["show_path"] = showPath,
            ["show"] = show.DeepClone(),
        };
        if (!Write(File, snapshot))
            return false;
        _last = text;
        return true;
    }

    /// <summary>
    /// When a snapshot was taken, in local time: "HH:mm" for today, else "yyyy-MM-dd HH:mm".
    /// </summary>
    public string When(JsonNode snapshot)
    {
        var t = DateTimeOffset.FromUnixTimeSeconds(snapshot.Int("saved_at", 0)).ToLocalTime();
        var today = Now().ToLocalTime();
        var hm = t.ToString("HH:mm", CultureInfo.InvariantCulture);
        return t.Date == today.Date ? hm : $"{t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} {hm}";
    }

    static string ReadText(string path)
    {
        try
        {
            return System.IO.File.ReadAllText(path);
        }
        catch (IOException)
        {
            return "";
        }
    }

    /// <summary>
    /// Written beside the target and moved over it, so a crash mid-write never leaves half a file where the last good
    /// one was.
    /// </summary>
    static bool Write(string path, JsonNode data)
    {
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
                Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            System.IO.File.WriteAllText(tmp, Json.Stringify(data));
            System.IO.File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
