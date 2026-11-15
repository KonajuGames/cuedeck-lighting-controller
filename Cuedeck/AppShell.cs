using Cuedeck.Common;
using Cuedeck.Show;
using Cuedeck.UI;

namespace Cuedeck;

/// <summary>
/// What the app does around the show as a document: New, Save, Save As and Load (asking first about unsaved changes),
/// adding and removing universes, Blackout All, reopening the last show at start and offering an autosave back after a
/// crash. Owns no window: the window's handle is given for its dialogs, and what to say goes to the status line.
/// </summary>
sealed class AppShell
{
    readonly ShowSession _session;
    readonly MainScreen _screen;
    readonly ConfirmDialog _confirm;
    readonly Func<nint> _owner;

    public AppShell(ShowSession session, MainScreen screen, ConfirmDialog confirm, Func<nint> owner)
    {
        _session = session;
        _screen = screen;
        _confirm = confirm;
        _owner = owner;

        var top = screen.TopBar;
        top.NewShow += New;
        top.SaveShow += () => Save();
        top.SaveShowAs += () => SaveAs();
        top.LoadShow += Load;
        top.SavePreset += SavePreset;
        top.LoadPreset += LoadPreset;
        top.BlackoutAll += BlackoutAll;
        top.AddUniverse += AddUniverse;
        top.RemoveUniverse += ConfirmRemoveUniverse;
        Show.Cues.Status += screen.Log;
    }

    LightingShow Show => _session.Show;

    /// <summary>
    /// The app may close now: nothing's unsaved, or the person said what to do about it.
    /// </summary>
    public bool MayClose { get; private set; }

    /// <summary>
    /// Asks to close. Closes straight away with nothing unsaved; otherwise asks whether to save first.
    /// </summary>
    public void RequestClose() => ConfirmUnsaved("closing", () => MayClose = true);

    /// <summary>
    /// Runs <paramref name="then"/>, first asking whether to save the show if it has unsaved changes: Save saves it
    /// (asking where if it's never been saved) and goes on, Don't Save goes on, Cancel doesn't.
    /// </summary>
    public void ConfirmUnsaved(string doing, Action then)
    {
        if (!_session.HasUnsavedChanges)
        {
            then();
            return;
        }
        var name = _session.Path == "" ? "this new show" : _session.Name;
        _confirm.Show(
            "Unsaved Changes",
            $"Save the changes to {name} before {doing}? They'll be lost otherwise.",
            "Save",
            () =>
            {
                if (Save())
                    then();
            },
            "Don't Save",
            then
        );
    }

    public void New() =>
        ConfirmUnsaved(
            "starting a new show",
            () =>
            {
                _session.New();
                _screen.Console.Patch.Reload();
                _screen.Log("New show (not saved yet).");
            }
        );

    /// <summary>
    /// Saves to the show's file, or asks where for a show never saved. Returns whether it saved.
    /// </summary>
    public bool Save()
    {
        if (_session.Path == "")
            return SaveAs();
        if (_session.Save(_session.Path) is { } error)
        {
            _screen.Log("Show save failed: " + error);
            return false;
        }
        _screen.Log(
            $"Show saved to {Path.GetFileName(_session.Path)} ({Show.Patch.Universes.Count} universes, {Show.Cues.Cues.Count} cues, {Show.Effects.Chases.Count} chases, {Show.Effects.Effects.Count} effects)."
        );
        return true;
    }

    /// <summary>
    /// The show dialogs' last folder ("show"), kept between runs by the app.
    /// </summary>
    public RecentFolders Recent { get; set; } = new(null);

    public bool SaveAs()
    {
        var folder = Recent.For("show") ?? (_session.Path != "" ? Path.GetDirectoryName(_session.Path) : null);
        var name = _session.Path != "" ? Path.GetFileName(_session.Path) : "Show." + ShowFile.Extension;
        if (WindowsShell.AskWhereToSave(_owner(), "Save Show As", [("sLight show", "*." + ShowFile.Extension)], name, folder) is not { } chosen)
            return false;
        Recent.Remember("show", chosen.Path);
        if (_session.Save(chosen.Path) is { } error)
        {
            _screen.Log("Show save failed: " + error);
            return false;
        }
        _screen.Log($"Show saved to {Path.GetFileName(chosen.Path)}.");
        return true;
    }

    public void Load() =>
        ConfirmUnsaved(
            "opening another show",
            () =>
            {
                var current = _session.Path != "" ? Path.GetDirectoryName(_session.Path) : null;
                if (Recent.Ask("show", folder => WindowsShell.AskForFile(_owner(), "Load Show", ("sLight show", "*." + ShowFile.Extension + ";*.json"), folder), current) is not { } path)
                    return;
                LoadFrom(path);
            }
        );

    void LoadFrom(string path)
    {
        if (_session.Load(path) is { } error)
        {
            _screen.Log(error);
            return;
        }
        _screen.Console.Patch.Reload();
        _screen.Log($"Loaded {Path.GetFileName(path)}.");
    }

    /// <summary>
    /// Where the preset is kept: one, as in sLight.
    /// </summary>
    public string PresetPath { get; set; } = Path.Combine(AppPaths.Folder, "dmx_preset.json");

    /// <summary>
    /// Keeps every universe's live levels, to put back with <see cref="LoadPreset"/>.
    /// </summary>
    public void SavePreset()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PresetPath)!);
            File.WriteAllText(PresetPath, Json.Stringify(Show.PresetToJson()));
            _screen.Log($"Preset saved ({Show.Dmx.UniverseCount} universe{(Show.Dmx.UniverseCount == 1 ? "" : "s")}).");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _screen.Log("Preset save failed: " + e.Message);
        }
    }

    public void LoadPreset()
    {
        if (!File.Exists(PresetPath))
        {
            _screen.Log("No preset found.");
            return;
        }
        if (Show.ApplyPreset(Json.Parse(File.ReadAllText(PresetPath))) is { } error)
        {
            _screen.Log(error);
            return;
        }
        _screen.Log("Preset loaded.");
    }

    public void BlackoutAll()
    {
        Show.BlackoutAll();
        _screen.Log("Blackout — all universes, chases, effects and reactors stopped.");
    }

    public void AddUniverse()
    {
        if (Show.Patch.AddUniverse() is null)
        {
            _screen.Log($"Already at the limit of {Dmx.DmxOutput.MaxUniverses} universes.");
            return;
        }
        _screen.Log($"Added universe {Show.Patch.Universes.Count}.");
    }

    public void ConfirmRemoveUniverse()
    {
        if (Show.Patch.Universes.Count <= 1)
        {
            _screen.Log("That's the only universe: there's nothing to remove.");
            return;
        }
        var index = _screen.Console.Patch.CurrentUniverse;
        var fixtures = Show.Patch.Universes[index].Fixtures.Count;
        _confirm.Show(
            $"Remove Universe {index + 1}?",
            fixtures == 0 ? "It has nothing patched in it." : $"Its {fixtures} fixture{(fixtures == 1 ? "" : "s")} will be unpatched and leave their groups.",
            "Remove",
            () =>
            {
                Show.RemoveUniverse(index);
                Show.Retarget();
                _screen.Console.Patch.Reload();
                _screen.Log($"Removed a universe ({Show.Patch.Universes.Count} left).");
            }
        );
    }

    /// <summary>
    /// At start: opens <paramref name="path"/> if given (a show file opened with the app), else reopens the show that
    /// was current last time; then offers back an autosave a crash left that holds more than it does.
    /// </summary>
    public void Start(string? path = null)
    {
        var remembered = path ?? _session.Remembered;
        if (remembered != "")
        {
            if (_session.Load(remembered) is { } error)
                _screen.Log("Couldn't reopen the last show: " + error);
            else
                _screen.Log(path is null ? $"Reopened the last show, {Path.GetFileName(remembered)}." : $"Opened {Path.GetFileName(remembered)}.");
            _screen.Console.Patch.Reload();
        }
        if (_session.RecoveryOffer(_session.Autosave.Begin()) is { } snapshot)
        {
            var showPath = snapshot.Str("show_path", "");
            var name = showPath != "" ? Path.GetFileName(showPath) : "the unsaved show";
            _confirm.Show(
                "Restore Autosaved Show",
                $"Cuedeck didn't close properly last time. Restore {name} as it was autosaved at {_session.Autosave.When(snapshot)}? Its changes since it was last saved would otherwise be lost.",
                "Restore",
                () =>
                {
                    if (_session.Restore(snapshot) is { } error)
                    {
                        _screen.Log(error);
                        return;
                    }
                    _screen.Console.Patch.Reload();
                    _screen.Log($"Restored {name} as it was autosaved at {_session.Autosave.When(snapshot)} — not saved yet.");
                },
                "Discard",
                () =>
                {
                    _session.Autosave.Discard();
                    _screen.Log("Autosaved show discarded.");
                }
            );
        }
    }

    /// <summary>
    /// A clean quit: nothing to recover next time.
    /// </summary>
    public void End() => _session.Autosave.End();
}
