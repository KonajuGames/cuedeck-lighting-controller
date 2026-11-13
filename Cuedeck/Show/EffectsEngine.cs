namespace Cuedeck.Show;

/// <summary>
/// Runs the chases, waveform effects and sound reactors, and puts their output together into one layer per universe
/// for <see cref="Dmx.DmxOutput.Compose"/>. sLight's <c>Fx</c> autoload.
/// <para>
/// Beat-sync chases step on the tempo clock's beats whenever it's running, in any run mode; with it off, on the beats
/// heard in the audio in Sound Reactive; otherwise at their own rate. Tempo-synced effects run at the clock's tempo
/// while it's running. Reactors only run in Sound Reactive.
/// </para>
/// </summary>
sealed class EffectsEngine
{
    readonly TempoClock _tempo;

    public List<Chase> Chases { get; } = [];
    public List<WaveEffect> Effects { get; } = [];
    public List<SoundReactor> Reactors { get; } = SoundReactor.Defaults();

    /// <summary>
    /// Whether the console's in Sound Reactive run mode.
    /// </summary>
    public bool SoundReactive { get; set; }

    /// <summary>
    /// The live audio's levels, for the reactors.
    /// </summary>
    public ISoundLevels Sound { get; set; } = Silence.Instance;

    /// <summary>
    /// The patched fixtures' intensity channels per universe, which a chase's level (set by a playback fader) scales.
    /// </summary>
    public Func<int, IReadOnlyList<IReadOnlySet<int>>>? IntensityProvider { get; set; }

    public EffectsEngine(TempoClock tempo)
    {
        _tempo = tempo;
        _tempo.Beat += OnTempoBeat;
    }

    bool BeatDriven => _tempo.Running || SoundReactive;

    /// <summary>
    /// Runs everything on by <paramref name="delta"/> seconds. Call every frame.
    /// </summary>
    public void Advance(double delta)
    {
        foreach (var c in Chases)
            if (c.Running && !(BeatDriven && c.BeatSync))
                c.Advance(delta);
        var tempo = _tempo.Running ? _tempo.Bpm : 0;
        foreach (var e in Effects)
            if (e.Running)
                e.Advance(delta, tempo);
        if (SoundReactive)
            foreach (var r in Reactors)
                if (r.Running)
                    r.Advance(delta, false, Sound);
    }

    /// <summary>
    /// A beat heard in the live audio: pulse reactors fire, and beat-sync chases step unless the tempo clock is
    /// stepping them.
    /// </summary>
    public void OnSoundBeat()
    {
        if (!SoundReactive)
            return;
        foreach (var r in Reactors)
            if (r.Running && r.Mode == ReactorMode.Pulse)
                r.Advance(0, true, Sound);
        if (!_tempo.Running)
            StepBeatChases();
    }

    void OnTempoBeat()
    {
        StepBeatChases();
        foreach (var e in Effects)
            if (e.Running)
                e.OnBeat(_tempo.Running ? _tempo.Bpm : 0);
    }

    void StepBeatChases()
    {
        foreach (var c in Chases)
            if (c.Running && c.BeatSync)
                c.BeatStep();
    }

    /// <summary>
    /// One {channel: value} map per universe, merged highest-takes-precedence across every running chase, effect and
    /// (in Sound Reactive) reactor. <paramref name="bases"/> (each universe's levels) feeds Pickup effects. Channels
    /// nothing drives aren't in it.
    /// </summary>
    public IReadOnlyList<Dictionary<int, int>> Compose(int n, IReadOnlyList<byte[]> bases)
    {
        var layers = new List<Dictionary<int, int>>(n);
        for (var i = 0; i < n; i++)
            layers.Add([]);
        IReadOnlyList<IReadOnlySet<int>>? intensity = null;
        if (Chases.Any(c => c.Running && c.Level < 1))
            intensity = IntensityProvider?.Invoke(n);
        foreach (var c in Chases)
            if (c.Running)
                c.WriteInto(layers, intensity);
        foreach (var e in Effects)
            if (e.Running)
                e.WriteInto(layers, bases);
        if (SoundReactive)
            foreach (var r in Reactors)
                if (r.Running)
                    r.WriteInto(layers);
        return layers;
    }

    public bool AnyRunning => Chases.Any(c => c.Running) || Effects.Any(e => e.Running) || Reactors.Any(r => r.Running);

    public void StopAll()
    {
        foreach (var c in Chases)
            c.Running = false;
        foreach (var e in Effects)
            e.Running = false;
        foreach (var r in Reactors)
            r.Running = false;
    }

    public Chase? FindChase(string name)
    {
        var low = name.Trim().ToLowerInvariant();
        return Chases.FirstOrDefault(c => c.Name.ToLowerInvariant() == low);
    }

    public WaveEffect? FindEffect(string name)
    {
        var low = name.Trim().ToLowerInvariant();
        return Effects.FirstOrDefault(e => e.Name.ToLowerInvariant() == low);
    }
}
