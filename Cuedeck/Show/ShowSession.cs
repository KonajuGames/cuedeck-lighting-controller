using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Timelines;

namespace Cuedeck.Show;

/// <summary>
/// The show being worked on as a document: the file it's saved in, whether it has unsaved changes, the show to reopen
/// at the next start, and its autosave. What sLight's shell did around the show, without the dialogs.
/// </summary>
sealed class ShowSession
{
    string _saved = "";

    public ShowSession(LightingShow show)
    {
        Show = show;
        // The show as it starts out isn't worth an autosave until it changes.
        MarkSaved();
    }

    public LightingShow Show { get; }

    /// <summary>
    /// The show file Save writes to: the last one saved or loaded; "" for a new show not saved yet (Save asks where).
    /// </summary>
    public string Path { get; private set; } = "";

    public Autosave Autosave { get; } = new();

    /// <summary>
    /// DJ Edit Mode's song timelines, which follow the show: each show's are kept in a folder beside its file
    /// ("&lt;show&gt;.timelines", or the one it names), copied there by Save As, saved with it, and swapped when another
    /// is loaded. None for a show never saved.
    /// </summary>
    public SongTimelines? Timelines { get; set; }

    /// <summary>
    /// Where the current show file's path is kept between runs, so it's reopened at the next start.
    /// </summary>
    public string LastShowStore { get; set; } = System.IO.Path.Combine(AppPaths.Folder, "last_show.json");

    /// <summary>
    /// Whether USB DMX interfaces can be sent to, so a show's USB universes stay USB.
    /// </summary>
    public bool UsbAvailable { get; set; }

    /// <summary>
    /// Whether the show has changed since it was last saved or loaded. Playing it (the live levels, the faders'
    /// positions, the run mode, the camera) doesn't count.
    /// </summary>
    public bool HasUnsavedChanges => ShowFile.Authored(Show) != _saved;

    /// <summary>
    /// The show file's name without its extension, or "Untitled".
    /// </summary>
    public string Name => Path == "" ? "Untitled" : System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>
    /// The window's title: "*Friday — Cuedeck" with unsaved changes.
    /// </summary>
    public string Title => $"{(HasUnsavedChanges ? "*" : "")}{Name} — Cuedeck";

    void MarkSaved()
    {
        Autosave.MarkSaved(ShowFile.ToJson(Show));
        _saved = ShowFile.Authored(Show);
    }

    /// <summary>
    /// Saves the show to <paramref name="path"/>, which becomes the current show file. Returns what went wrong, or
    /// null.
    /// </summary>
    public string? Save(string path)
    {
        if (Timelines is { } tl)
        {
            var dir = path == Path ? TimelinesFolderOf(path) : TimelineStore.FolderForShow(path);
            if (tl.Store is null)
                tl.SetTimelineStore(new TimelineStore(dir), reload: false);
            else if (tl.Store.Dir != dir)
            {
                tl.SetTimelineStore(tl.Store.CopyTo(dir), reload: false);
                Show.TimelinesFolder = null; // beside the new file, by its name
            }
            tl.Flush();
        }
        if (ShowFile.Save(Show, path) is { } error)
            return error;
        Path = path;
        Remember(path);
        MarkSaved();
        return null;
    }

    /// <summary>
    /// Replaces the show with the one at <paramref name="path"/>, which becomes the current show file. Returns what
    /// went wrong, or null (the show is left alone then).
    /// </summary>
    public string? Load(string path)
    {
        Timelines?.Flush(); // the outgoing show's open song
        if (ShowFile.Load(Show, path, UsbAvailable) is { } error)
            return error;
        Path = path;
        OpenTimelines();
        Remember(path);
        MarkSaved();
        return null;
    }

    /// <summary>
    /// Starts a new, untitled show.
    /// </summary>
    public void New()
    {
        Timelines?.Flush();
        Show.NewShow();
        Path = "";
        Timelines?.SetTimelineStore(null);
        MarkSaved();
    }

    /// <summary>
    /// Points the timelines at the show file's folder: the one it names, else the default beside it; none for a show
    /// never saved.
    /// </summary>
    void OpenTimelines()
    {
        if (Timelines is not { } tl)
            return;
        if (Path == "")
        {
            tl.SetTimelineStore(null);
            return;
        }
        tl.SetTimelineStore(new TimelineStore(TimelinesFolderOf(Path)));
    }

    /// <summary>
    /// The timelines folder of the show file at <paramref name="path"/>: the one the show names (beside it), else the
    /// default.
    /// </summary>
    string TimelinesFolderOf(string path)
    {
        if (Show.TimelinesFolder is not { } named)
            return TimelineStore.FolderForShow(path);
        var p = path.Replace('\\', '/');
        var slash = p.LastIndexOf('/');
        return Gd.PathJoin(slash >= 0 ? p[..slash] : "", named);
    }

    /// <summary>
    /// The show file to reopen at start: the last one saved or loaded, or "" for none.
    /// </summary>
    public string Remembered => File.Exists(LastShowStore) && Json.Parse(File.ReadAllText(LastShowStore)) is JsonObject o ? o.Str("path", "") : "";

    void Remember(string path)
    {
        try
        {
            if (System.IO.Path.GetDirectoryName(LastShowStore) is { Length: > 0 } dir)
                Directory.CreateDirectory(dir);
            File.WriteAllText(LastShowStore, Json.Stringify(new JsonObject { ["path"] = path }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Autosaves the show if it's changed since it was last saved, loaded or autosaved. Returns whether it did.
    /// </summary>
    public bool AutosaveNow() => Autosave.Tick(ShowFile.ToJson(Show), Path);

    /// <summary>
    /// The snapshot a run that didn't close properly left, if it holds anything the show as it now stands doesn't;
    /// otherwise null. What startup offers to restore.
    /// </summary>
    public JsonObject? RecoveryOffer(JsonObject? snapshot) =>
        snapshot is not null && !Json.SameTree(snapshot.Get("show"), ShowFile.ToJson(Show)) ? snapshot : null;

    /// <summary>
    /// Puts back an autosaved show, belonging to its show file, but unsaved: what it's measured against is still the
    /// file as last saved or loaded. Returns what went wrong, or null.
    /// </summary>
    public string? Restore(JsonObject snapshot)
    {
        if (snapshot.Get("show") is not JsonObject show || !show.ContainsKey("universes"))
            return "The autosave isn't a show.";
        ShowFile.Apply(Show, show, UsbAvailable);
        Path = snapshot.Str("show_path", "");
        if (Path != "")
            Remember(Path);
        OpenTimelines();
        return null;
    }
}
