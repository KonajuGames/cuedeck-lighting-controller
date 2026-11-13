using Cuedeck.Common;

namespace Cuedeck.Dmx;

/// <summary>
/// Something that lays channel values over the universes each frame: given how many universes there are, one
/// {channel: value} map for each.
/// </summary>
delegate IReadOnlyList<Dictionary<int, int>> LayerProvider(int universes);

/// <summary>
/// Something that scales channels each frame (group masters): one {channel: factor 0..1} map per universe.
/// </summary>
delegate IReadOnlyList<Dictionary<int, double>> ScaleProvider(int universes);

/// <summary>
/// Every universe slot, the grand master, the cue crossfade, and putting it all together each frame: the
/// operator's levels, then the song timelines (<see cref="LayerProviders"/>) under the chases and effects
/// (<see cref="Compose"/>), then the virtual dimmers, the group masters (<see cref="ScaleProviders"/>), the live
/// flashes (<see cref="TopProviders"/>) and last the grand master. sLight's <c>ArtNet</c> autoload.
/// </summary>
sealed class DmxOutput : IDisposable
{
    public const int MaxUniverses = 8;

    readonly List<DmxUniverse> _universes = [];

    /// <summary>
    /// The grand master, 0 to 1, scaling every universe's output.
    /// </summary>
    public double Master { get; set; } = 1;

    /// <summary>
    /// The source CID every sACN universe is sent from. Kept in the show file, so receivers see one stable source.
    /// </summary>
    public byte[] SacnCid { get; set; } = Sacn.RandomCid();

    public IReadOnlyList<DmxUniverse> Universes => _universes;

    public int UniverseCount => _universes.Count;

    /// <summary>
    /// The chases', effects' and sound reactors' layer, given the universe count and each universe's levels (for
    /// effects that swing around them): the layer every other playback goes under.
    /// </summary>
    public Func<int, IReadOnlyList<byte[]>, IReadOnlyList<Dictionary<int, int>>>? Compose { get; set; }

    /// <summary>
    /// More playback layers (song timelines, cue faders), under the chases and effects; the first listed wins where
    /// they overlap.
    /// </summary>
    public List<LayerProvider> LayerProviders { get; } = [];

    /// <summary>
    /// Layers over everything else (the live flashes), after the virtual dimmers and before the grand master; the
    /// first listed wins where they overlap.
    /// </summary>
    public List<LayerProvider> TopProviders { get; } = [];

    /// <summary>
    /// Scale layers (group masters), after the virtual dimmers and before the top layers; the lowest factor wins.
    /// </summary>
    public List<ScaleProvider> ScaleProviders { get; } = [];

    /// <summary>
    /// Sends a frame to a USB DMX interface by its serial number, for universes set to <see cref="OutputMode.Usb"/>.
    /// Without one, they're worked out but not sent.
    /// </summary>
    public Action<string, byte[]>? SendUsb { get; set; }

    /// <summary>
    /// A USB universe is let go of (removed, or switched to another output).
    /// </summary>
    public Action<string>? ReleaseUsb { get; set; }

    /// <summary>
    /// A running crossfade has reached all its targets.
    /// </summary>
    public event Action? FadeFinished;

    bool _fadeActive;
    double _fadeElapsed;
    double _fadeUp;
    double _fadeDown;
    double _fadeTotal;
    List<byte[]> _fadeFrom = [];
    IReadOnlyList<byte[]> _fadeTo = [];

    public DmxOutput() => AddUniverse(); // always at least one

    public DmxUniverse? GetUniverse(int index) => index >= 0 && index < _universes.Count ? _universes[index] : null;

    /// <summary>
    /// A new universe slot, numbered with the first Art-Net number not in use; null at <see cref="MaxUniverses"/>.
    /// </summary>
    public DmxUniverse? AddUniverse()
    {
        if (_universes.Count >= MaxUniverses)
            return null;
        var used = _universes.Select(u => u.Number).ToHashSet();
        var n = 0;
        while (used.Contains(n))
            n++;
        var universe = new DmxUniverse { Number = n };
        _universes.Add(universe);
        return universe;
    }

    /// <summary>
    /// Removes slot <paramref name="index"/>, but never the last one.
    /// </summary>
    public void RemoveUniverse(int index)
    {
        if (index < 0 || index >= _universes.Count || _universes.Count <= 1)
            return;
        Close(_universes[index]);
        _universes.RemoveAt(index);
    }

    /// <summary>
    /// Trims or grows the slots to <paramref name="n"/> (1 to <see cref="MaxUniverses"/>), as loading a show does.
    /// </summary>
    public void SetUniverseCount(int n)
    {
        n = Math.Clamp(n, 1, MaxUniverses);
        while (_universes.Count < n)
            AddUniverse();
        while (_universes.Count > n)
        {
            Close(_universes[^1]);
            _universes.RemoveAt(_universes.Count - 1);
        }
    }

    void Close(DmxUniverse universe)
    {
        if (universe.Mode == OutputMode.Usb)
            ReleaseUsb?.Invoke(universe.UsbSerial);
        universe.Dispose();
    }

    /// <summary>
    /// Works out every universe's output and, with <paramref name="transmit"/>, sends it. Call every refresh: working
    /// it out keeps the 3D view live even while sending is off.
    /// </summary>
    public void Tick(bool transmit = true)
    {
        var n = _universes.Count;
        var bases = _universes.Select(u => u.Data).ToList();
        var composed = Compose?.Invoke(n, bases);
        var layers = new List<Dictionary<int, int>>(n);
        for (var i = 0; i < n; i++)
            layers.Add(composed is not null && i < composed.Count ? new Dictionary<int, int>(composed[i]) : []);

        // Other playback layers (song timelines, cue faders), in order, under the chases and effects.
        foreach (var provider in LayerProviders)
            MergeUnder(layers, provider(n));
        var tops = new List<Dictionary<int, int>>(n);
        for (var i = 0; i < n; i++)
            tops.Add([]);
        foreach (var provider in TopProviders)
            MergeUnder(tops, provider(n));
        var scales = new List<Dictionary<int, double>>(n);
        for (var i = 0; i < n; i++)
            scales.Add([]);
        foreach (var provider in ScaleProviders)
        {
            var layer = provider(n);
            for (var i = 0; i < Math.Min(n, layer.Count); i++)
                foreach (var (ch, factor) in layer[i])
                    scales[i][ch] = Math.Min(scales[i].GetValueOrDefault(ch, 1.0), factor);
        }

        for (var i = 0; i < n; i++)
        {
            var u = _universes[i];
            u.ComputeOutput(Master, layers[i], tops[i], scales[i]);
            if (!transmit)
                continue;
            switch (u.Mode)
            {
                case OutputMode.Usb:
                    SendUsb?.Invoke(u.UsbSerial, u.Output);
                    break;
                case OutputMode.Sacn:
                    u.TransmitSacn(SacnCid);
                    break;
                default:
                    u.Transmit();
                    break;
            }
        }
    }

    /// <summary>
    /// Adds each channel of <paramref name="layer"/> to <paramref name="into"/> where it isn't set already.
    /// </summary>
    static void MergeUnder(List<Dictionary<int, int>> into, IReadOnlyList<Dictionary<int, int>> layer)
    {
        for (var i = 0; i < Math.Min(into.Count, layer.Count); i++)
            foreach (var (ch, v) in layer[i])
                into[i].TryAdd(ch, v);
    }

    // ------------------------------------------------------------ crossfade --

    public bool IsFading => _fadeActive;

    /// <summary>
    /// How far the running crossfade has got, 0 to 1 (1 when none is running).
    /// </summary>
    public double FadeProgress => !_fadeActive || _fadeTotal <= 0 ? 1 : Math.Clamp(_fadeElapsed / _fadeTotal, 0, 1);

    /// <summary>
    /// Seconds left in the running crossfade (0 when none is running).
    /// </summary>
    public double FadeRemaining => _fadeActive ? Math.Max(_fadeTotal - _fadeElapsed, 0) : 0;

    /// <summary>
    /// Crossfades every universe's levels towards <paramref name="targets"/> (512 bytes for each slot; slots past the
    /// list are left alone): channels rising over <paramref name="fadeUp"/> seconds, falling over
    /// <paramref name="fadeDown"/>. A running fade is replaced.
    /// </summary>
    public void StartFade(IReadOnlyList<byte[]> targets, double fadeUp, double fadeDown)
    {
        _fadeFrom = _universes.Select(u => (byte[])u.Data.Clone()).ToList();
        _fadeTo = targets;
        _fadeUp = Math.Max(fadeUp, 0);
        _fadeDown = Math.Max(fadeDown, 0);
        _fadeElapsed = 0;
        _fadeActive = true;
        // How long it runs: the longer of the times its moves actually use.
        bool rises = false, falls = false;
        for (var i = 0; i < Math.Min(_universes.Count, targets.Count) && !(rises && falls); i++)
        {
            var from = _fadeFrom[i];
            var to = targets[i];
            for (var c = 0; c < Math.Min(from.Length, to.Length); c++)
            {
                if (to[c] > from[c])
                    rises = true;
                else if (to[c] < from[c])
                    falls = true;
            }
        }
        _fadeTotal = Math.Max(rises ? _fadeUp : 0, falls ? _fadeDown : 0);
        // A fade of no time lands at once.
        if (_fadeUp == 0 && _fadeDown == 0)
            Step();
    }

    public void StopFade() => _fadeActive = false;

    /// <summary>
    /// Runs the crossfade on by <paramref name="delta"/> seconds. Call every frame.
    /// </summary>
    public void Advance(double delta)
    {
        if (!_fadeActive)
            return;
        _fadeElapsed += delta;
        Step();
    }

    void Step()
    {
        var stillGoing = false;
        for (var i = 0; i < _universes.Count && i < _fadeTo.Count; i++)
        {
            var from = i < _fadeFrom.Count ? _fadeFrom[i] : [];
            var to = _fadeTo[i];
            var buffer = new byte[DmxUniverse.Size];
            for (var c = 0; c < DmxUniverse.Size; c++)
            {
                var a = c < from.Length ? from[c] : 0;
                var b = c < to.Length ? to[c] : 0;
                if (a == b)
                {
                    buffer[c] = (byte)a;
                    continue;
                }
                var duration = b > a ? _fadeUp : _fadeDown;
                var t = duration <= 0 ? 1 : Math.Clamp(_fadeElapsed / duration, 0, 1);
                if (t < 1)
                    stillGoing = true;
                buffer[c] = (byte)Gd.RoundToInt(a + (b - a) * t);
            }
            _universes[i].Data = buffer;
        }
        if (!stillGoing)
        {
            _fadeActive = false;
            FadeFinished?.Invoke();
        }
    }

    public void Dispose()
    {
        foreach (var u in _universes)
            u.Dispose();
    }
}
