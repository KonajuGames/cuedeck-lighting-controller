using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Dmx;

namespace Cuedeck.Show;

/// <summary>
/// The cue list and its playback: GO steps through the cues, each crossfading every universe from where it is to the
/// look the list leaves standing at that cue. What sLight's Cues tab did, without the tab.
/// <para>
/// Playback folds cues 1 to n together (blocks wipe first, tracking cues merge on top), so a fade's target is always
/// the complete look at that point in the list. A cue's delay holds its fade back after GO; with follow on, once its
/// fade finishes (and its wait after that) the next cue GOes by itself. GO, Back or a Go-to while one's waiting takes
/// over from it; <see cref="Halt"/> stops the fade and anything waiting, and <see cref="Clear"/> fades the live cue
/// out and leaves none live.
/// </para>
/// </summary>
sealed class CueList
{
    readonly DmxOutput _dmx;
    readonly PaletteLibrary _palettes;
    readonly Patch _patch;

    int _current = -1;
    int _next;
    double _delayLeft = -1;
    double _followLeft = -1;
    int _fading = -1;
    bool _clearing;

    /// <summary>
    /// The fade down time of the last cue put up, while its look may still be on stage (even once that cue's been
    /// deleted); null once it's cleared, or when no cue was put up.
    /// </summary>
    double? _shownFadeDown;

    public CueList(DmxOutput dmx, PaletteLibrary palettes, Patch patch)
    {
        _dmx = dmx;
        _palettes = palettes;
        _patch = patch;
        _dmx.FadeFinished += OnFadeFinished;
    }

    public List<Cue> Cues { get; } = [];

    /// <summary>
    /// Whether Record makes tracking cues (rather than blocks).
    /// </summary>
    public bool TrackingEnabled { get; set; } = true;

    /// <summary>
    /// The live cue's index, or -1 for none.
    /// </summary>
    public int Current => _current;

    /// <summary>
    /// The index GO will fire.
    /// </summary>
    public int Next => _next;

    /// <summary>
    /// What playback just did, for the status line ("GO — cue 3 ...").
    /// </summary>
    public event Action<string>? Status;

    /// <summary>
    /// The cues were added to, edited, reordered or removed.
    /// </summary>
    public event Action? CuesChanged;

    // ------------------------------------------------------------ tracking --

    /// <summary>
    /// Cues 0 to <paramref name="last"/> folded into the look they leave standing (values may be 0). A block wipes the
    /// rig before its channels go on; a tracking cue's moves go over whatever the cues before left. Palette references
    /// are looked up as the palettes are now.
    /// </summary>
    public Look Fold(int last)
    {
        var count = _dmx.UniverseCount;
        var output = Empty(count);
        Dictionary<string, PatchedFixture>? byUid = null;
        for (var i = 0; i < Math.Min(last + 1, Cues.Count); i++)
        {
            var c = Cues[i];
            if (!c.Tracking)
                output = Empty(count);
            for (var u = 0; u < count && u < c.Levels.Universes.Count; u++)
                foreach (var (k, v) in c.Levels.Universes[u])
                    output.Universes[u][k] = Math.Clamp(v, 0, 255);
            if (c.Refs.Count == 0)
                continue;
            byUid ??= FixturesByUid();
            foreach (var (uid, per) in c.Refs)
            {
                if (!byUid.TryGetValue(uid, out var fx) || fx.Universe >= count)
                    continue; // no longer patched
                foreach (var id in per.Values)
                    foreach (var (ch, v) in _palettes.ChannelsFor(id, fx))
                        output.Universes[fx.Universe][ch] = v;
            }
        }
        return output;
    }

    static Look Empty(int universes) => new(Enumerable.Range(0, universes).Select(_ => new Dictionary<int, int>()));

    Dictionary<string, PatchedFixture> FixturesByUid()
    {
        var output = new Dictionary<string, PatchedFixture>();
        foreach (var f in _patch.All)
            output[f.Uid] = f;
        return output;
    }

    /// <summary>
    /// Links cue <paramref name="c"/>, just captured from the live levels, to the palettes its fixtures match: for
    /// each fixture and kind, if the live values of its channels of that kind are exactly a palette's, the cue refers
    /// to the palette and drops the values. A tracking cue links only what it changes (a kind with a channel in its
    /// levels); a block, any kind with a channel lit.
    /// </summary>
    void LinkPalettes(Cue c)
    {
        c.Refs = [];
        if (_palettes.Palettes.Count == 0)
            return;
        foreach (var fx in _patch.All)
        {
            var u = fx.Universe;
            if (u >= c.Levels.Universes.Count || u >= _dmx.UniverseCount)
                continue;
            var b = _dmx.Universes[u].Data;
            var levels = c.Levels.Universes[u];
            for (var kind = 0; kind < PaletteLibrary.Kinds.Length; kind++)
            {
                var offsets = PaletteLibrary.Offsets(fx, (PaletteKind)kind);
                var touched = offsets.Any(o => levels.TryGetValue(fx.Start + o, out var v) && (c.Tracking || v != 0));
                if (!touched)
                    continue;
                var id = _palettes.MatchFixture((PaletteKind)kind, fx, b);
                if (id == "")
                    continue;
                if (!c.Refs.TryGetValue(fx.Uid, out var per))
                    c.Refs[fx.Uid] = per = [];
                per[PaletteLibrary.KindKeys[kind]] = id;
                foreach (var o in offsets)
                    levels.Remove(fx.Start + o);
            }
        }
    }

    /// <summary>
    /// Before palette <paramref name="id"/> is deleted: every cue that refers to it takes its values instead, so no
    /// cue's look changes. Returns how many cues did.
    /// </summary>
    public int BakePalette(string id)
    {
        var byUid = FixturesByUid();
        var n = 0;
        foreach (var c in Cues)
        {
            var used = false;
            foreach (var uid in c.Refs.Keys.ToList())
            {
                var per = c.Refs[uid];
                foreach (var kindKey in per.Keys.ToList())
                {
                    if (per[kindKey] != id)
                        continue;
                    used = true;
                    if (byUid.TryGetValue(uid, out var fx))
                    {
                        while (c.Levels.Universes.Count <= fx.Universe)
                            c.Levels.Universes.Add([]);
                        foreach (var (ch, v) in _palettes.ChannelsFor(id, fx))
                            if (c.Tracking || v != 0)
                                c.Levels.Universes[fx.Universe][ch] = v;
                    }
                    per.Remove(kindKey);
                }
                if (per.Count == 0)
                    c.Refs.Remove(uid);
            }
            if (used)
                n++;
        }
        if (n > 0)
            CuesChanged?.Invoke();
        return n;
    }

    /// <summary>
    /// How many cues refer to palette <paramref name="id"/>.
    /// </summary>
    public int PaletteUsers(string id) => Cues.Count(c => c.Refs.Values.Any(per => per.ContainsValue(id)));

    /// <summary>
    /// The names of the palettes cue <paramref name="c"/> uses, in the order it first uses them.
    /// </summary>
    public List<string> PaletteNames(Cue c)
    {
        var output = new List<string>();
        foreach (var per in c.Refs.Values)
            foreach (var id in per.Values)
                if (_palettes.Find(id) is { } p && !output.Contains(p.Name))
                    output.Add(p.Name);
        return output;
    }

    /// <summary>
    /// The complete look cue <paramref name="number"/> (1-based) leaves standing, as GO would; null if there's no such
    /// cue. For cue faders.
    /// </summary>
    public Look? StandingLook(int number) => number < 1 || number > Cues.Count ? null : Fold(number - 1);

    // ------------------------------------------------------------ playback --

    /// <summary>
    /// The live cue's number, 1-based, or 0 for none. For MIDI feedback.
    /// </summary>
    public int CurrentNumber => _current >= 0 ? _current + 1 : 0;

    public void Go()
    {
        if (Cues.Count > 0)
            Fire(Math.Clamp(_next, 0, Cues.Count - 1));
    }

    public void GoBack()
    {
        if (Cues.Count == 0)
            return;
        var from = _current >= 0 ? _current : 0;
        Fire(Math.Clamp(from - 1, 0, Cues.Count - 1));
    }

    /// <summary>
    /// Jumps straight to cue <paramref name="number"/> (1-based), as MIDI and OSC triggers do.
    /// </summary>
    public void GoTo(int number)
    {
        if (Cues.Count > 0)
            Fire(Math.Clamp(number - 1, 0, Cues.Count - 1));
    }

    /// <summary>
    /// Stops the fade and anything waiting (a cue's delay, a follow).
    /// </summary>
    public void Halt()
    {
        _dmx.StopFade();
        CancelTimers();
        Status?.Invoke("Halted.");
    }

    /// <summary>
    /// Fades the light out over the live cue's fade down time and leaves no cue live, so the next GO starts the list
    /// from cue 1. Anything waiting (a delay, a follow) is dropped. Only intensity goes (<see cref="DarkLook"/>), so
    /// movers don't swing as they dim. A deleted live cue's look still fades out; with no cue put up, nothing changes.
    /// Not in sLight, which had no way back to no cue.
    /// </summary>
    public void Clear()
    {
        var fade = _current >= 0 && _current < Cues.Count ? Cues[_current].FadeDown : _shownFadeDown;
        CancelTimers();
        _current = -1;
        _next = 0;
        _shownFadeDown = null;
        if (fade is not { } time)
        {
            Status?.Invoke("No cue to clear.");
            return;
        }
        _clearing = true;
        Status?.Invoke($"Clear — fading out over {time:F1}s.");
        _dmx.StartFade(DarkLook(), time, time);
    }

    /// <summary>
    /// What's on stage with the light taken out: every patched fixture's intensity channels (its dimmer, or its
    /// colours without one) at 0, and every channel no fixture is patched on, which could be anything; position,
    /// colour on a dimmed fixture, gobos and the rest hold where they are.
    /// </summary>
    List<byte[]> DarkLook()
    {
        var n = _dmx.UniverseCount;
        var intensity = _patch.IntensityChannels(n);
        var patched = Enumerable.Range(0, n).Select(_ => new HashSet<int>()).ToList();
        foreach (var f in _patch.All)
            if (f.Universe >= 0 && f.Universe < n)
                for (var k = 0; k < f.Channels.Count; k++)
                    patched[f.Universe].Add(f.Start + k);
        var output = new List<byte[]>(n);
        for (var u = 0; u < n; u++)
        {
            var buffer = (byte[])_dmx.Universes[u].Data.Clone();
            for (var c = 0; c < buffer.Length; c++)
                if (intensity[u].Contains(c) || !patched[u].Contains(c))
                    buffer[c] = 0;
            output.Add(buffer);
        }
        return output;
    }

    void CancelTimers()
    {
        _delayLeft = -1;
        _followLeft = -1;
        _fading = -1;
        _clearing = false;
    }

    void Fire(int index)
    {
        if (index < 0 || index >= Cues.Count)
            return;
        var cue = Cues[index];
        // This GO takes over from anything waiting, and from a Clear's fade.
        _followLeft = -1;
        _delayLeft = -1;
        _clearing = false;
        _current = index;
        _next = Math.Min(index + 1, Cues.Count - 1);
        _shownFadeDown = cue.FadeDown;
        var times = $"{cue.FadeUp:F1}s / {cue.FadeDown:F1}s";
        if (cue.Delay > 0)
            times = $"delay {cue.Delay:F1}s, " + times;
        Status?.Invoke($"GO — cue {index + 1} ({cue.Label}) [{(cue.Tracking ? "track" : "block")}], {times}");
        if (cue.Delay > 0)
        {
            _fading = -1; // the last cue's fade runs on, but its end follows nothing
            _delayLeft = cue.Delay;
        }
        else
        {
            StartFade(index);
        }
    }

    void StartFade(int index)
    {
        var cue = Cues[index];
        _fading = index;
        _dmx.StartFade(Fold(index).Buffers(_dmx.UniverseCount), cue.FadeUp, cue.FadeDown);
    }

    void OnFadeFinished()
    {
        if (_clearing)
        {
            _clearing = false;
            Status?.Invoke("Cleared.");
            return;
        }
        if (_fading < 0 || _fading != _current || _current >= Cues.Count)
            return;
        _fading = -1;
        var cue = Cues[_current];
        if (cue.Follow && _current < Cues.Count - 1)
        {
            _followLeft = cue.FollowWait; // GOes on the next Advance, even at 0
            Status?.Invoke($"Cue {_current + 1} complete — following on to cue {_current + 2}.");
        }
        else
        {
            Status?.Invoke($"Cue {_current + 1} complete.");
        }
    }

    /// <summary>
    /// Runs the timers on by <paramref name="delta"/> seconds: a delay ending starts its cue's fade, a follow's wait
    /// ending GOes the next cue. Call every frame.
    /// </summary>
    public void Advance(double delta)
    {
        // Something else (Load to Patch, loading a show) can stop a Clear's fade before it finishes.
        if (_clearing && !_dmx.IsFading)
            _clearing = false;
        if (_delayLeft >= 0)
        {
            _delayLeft -= delta;
            if (_delayLeft <= 0)
            {
                _delayLeft = -1;
                if (_current >= 0 && _current < Cues.Count)
                    StartFade(_current);
            }
        }
        else if (_followLeft >= 0)
        {
            _followLeft -= delta;
            if (_followLeft <= 0)
            {
                _followLeft = -1;
                if (_current >= 0 && _current < Cues.Count - 1)
                    Fire(_current + 1);
            }
        }
    }

    /// <summary>
    /// Whether a cue is waiting out its delay, or a follow its wait.
    /// </summary>
    public bool IsWaiting => _delayLeft >= 0 || _followLeft >= 0;

    /// <summary>
    /// The bar under the transport: the delay counting down, the fade's progress, then the follow's wait, as a value 0
    /// to 1 and what to write on it.
    /// </summary>
    public (double Value, string Text) Progress
    {
        get
        {
            if (_delayLeft >= 0 && _current >= 0 && _current < Cues.Count)
            {
                var d = Cues[_current].Delay;
                return (d > 0 ? 1 - _delayLeft / d : 1, $"Delay {Math.Max(_delayLeft, 0):F1}s");
            }
            if (_clearing && _dmx.IsFading)
            {
                var value = _dmx.FadeProgress;
                return (value, $"Clear  {Gd.RoundToInt(value * 100)}%  {_dmx.FadeRemaining:F1}s");
            }
            if (_fading >= 0 && _dmx.IsFading)
            {
                var value = _dmx.FadeProgress;
                return (value, $"Cue {_fading + 1}  {Gd.RoundToInt(value * 100)}%  {_dmx.FadeRemaining:F1}s");
            }
            if (_followLeft >= 0 && _current >= 0 && _current < Cues.Count)
            {
                var w = Cues[_current].FollowWait;
                return (w > 0 ? 1 - _followLeft / w : 1, $"Follow in {Math.Max(_followLeft, 0):F1}s");
            }
            return (_current >= 0 ? 1 : 0, "");
        }
    }

    // ------------------------------------------------------------- editing --

    /// <summary>
    /// Records the live levels as a new cue after <paramref name="after"/> (or at the end, for -1), with fade times of
    /// <paramref name="fade"/> seconds: a tracking cue or a block as <see cref="TrackingEnabled"/> says. Returns its
    /// index.
    /// </summary>
    public int Record(int after, double fade = 3)
    {
        var insertAt = after == -1 ? Cues.Count : after + 1;
        var c = new Cue($"Cue {Cues.Count + 1}", fade, fade) { Tracking = TrackingEnabled };
        if (c.Tracking)
            c.CaptureTracked(_dmx, Fold(insertAt - 1));
        else
            c.Capture(_dmx);
        LinkPalettes(c);
        Cues.Insert(insertAt, c);
        if (_current >= insertAt)
            _current++;
        _next = Math.Clamp(_next, 0, Cues.Count - 1);
        Status?.Invoke($"Recorded cue {insertAt + 1} ({(c.Tracking ? "tracking" : "block")}) — {c.MoveCount} moves.");
        CuesChanged?.Invoke();
        return insertAt;
    }

    /// <summary>
    /// Cue <paramref name="index"/>'s standing look as full buffers, for loading into the fixture controls.
    /// </summary>
    public List<byte[]> LookBuffers(int index) => Fold(index).Buffers(_dmx.UniverseCount);

    /// <summary>
    /// Records cue <paramref name="index"/> again from the live levels.
    /// </summary>
    public void Update(int index)
    {
        if (index < 0 || index >= Cues.Count)
            return;
        var c = Cues[index];
        if (c.Tracking)
            c.CaptureTracked(_dmx, Fold(index - 1));
        else
            c.Capture(_dmx);
        LinkPalettes(c);
        Status?.Invoke($"Updated cue {index + 1} from live output ({(c.Tracking ? "tracking" : "block")}) — {c.MoveCount} moves.");
        CuesChanged?.Invoke();
    }

    /// <summary>
    /// Puts a copy of cue <paramref name="index"/> ("... copy") after it.
    /// </summary>
    public void Duplicate(int index)
    {
        if (index < 0 || index >= Cues.Count)
            return;
        var c = Cues[index].Clone();
        c.Label += " copy";
        Cues.Insert(index + 1, c);
        if (_current > index)
            _current++;
        CuesChanged?.Invoke();
    }

    public void Delete(int index)
    {
        if (index < 0 || index >= Cues.Count)
            return;
        Cues.RemoveAt(index);
        if (_current == index)
        {
            _current = -1;
            CancelTimers();
        }
        else if (_current > index)
        {
            _current--;
        }
        _next = Math.Clamp(_next, 0, Math.Max(Cues.Count - 1, 0));
        Status?.Invoke($"Deleted a cue ({Cues.Count} left).");
        CuesChanged?.Invoke();
    }

    /// <summary>
    /// Makes cue <paramref name="index"/> a tracking cue or a block, playing the same look: as tracking, it stores
    /// what changes from the cues before it (a lit channel they leave, gone dark, as a 0); as a block, everything lit
    /// at that point. Its palette references stand, their channels kept out of its levels.
    /// </summary>
    public void SetTracking(int index, bool on)
    {
        if (index < 0 || index >= Cues.Count || Cues[index].Tracking == on)
            return;
        var through = Fold(index); // the complete standing look at this cue
        var before = Fold(index - 1); // what the earlier cues leave standing
        var cue = Cues[index];
        cue.Tracking = on;
        var levels = new Look();
        for (var u = 0; u < _dmx.UniverseCount; u++)
        {
            var t = u < through.Universes.Count ? through.Universes[u] : [];
            var b = u < before.Universes.Count ? before.Universes[u] : [];
            var d = new Dictionary<int, int>();
            if (on)
            {
                foreach (var (k, v) in t)
                    if (v != b.GetValueOrDefault(k))
                        d[k] = v;
                foreach (var (k, v) in b)
                    if (!t.ContainsKey(k) && v != 0)
                        d[k] = 0; // an earlier cue's value, moved to 0
            }
            else
            {
                foreach (var (k, v) in t)
                    if (v != 0)
                        d[k] = v;
            }
            levels.Universes.Add(d);
        }
        var byUid = FixturesByUid();
        foreach (var (uid, per) in cue.Refs)
        {
            if (!byUid.TryGetValue(uid, out var fx) || fx.Universe >= levels.Universes.Count)
                continue;
            foreach (var kindKey in per.Keys)
                foreach (var o in PaletteLibrary.Offsets(fx, (PaletteKind)Math.Max(Array.IndexOf(PaletteLibrary.KindKeys, kindKey), 0)))
                    levels.Universes[fx.Universe].Remove(fx.Start + o);
        }
        cue.Levels = levels;
        Status?.Invoke($"Cue {index + 1} is now a {(on ? "tracking" : "block")} cue.");
        CuesChanged?.Invoke();
    }

    public JsonObject ToJson() =>
        new()
        {
            ["cues"] = new JsonArray([.. Cues.Select(c => (JsonNode)c.ToJson())]),
            ["current"] = _current,
            ["next"] = _next,
            ["tracking"] = TrackingEnabled,
        };

    public void FromJson(JsonNode? d)
    {
        Cues.Clear();
        foreach (var e in d.Arr("cues"))
            if (e is JsonObject)
                Cues.Add(Cue.FromJson(e));
        _current = Math.Clamp(d.Int("current", -1), -1, Cues.Count - 1);
        _next = Math.Clamp(d.Int("next", 0), 0, Math.Max(Cues.Count - 1, 0));
        _shownFadeDown = null;
        CancelTimers();
        TrackingEnabled = d.Bool("tracking", true);
        CuesChanged?.Invoke();
    }
}
