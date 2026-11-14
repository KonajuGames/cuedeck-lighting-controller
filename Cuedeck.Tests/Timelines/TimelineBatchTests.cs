using System.Diagnostics;
using Cuedeck.Audio;
using Cuedeck.Dj;
using Cuedeck.Timelines;

namespace Cuedeck.Tests.Timelines;

/// <summary>
/// Regenerating a list of songs' timelines, from music_browser_test.gd's Regenerate checks (the list's menu and bars
/// come with the music browser): a few at a time, analysing those not analysed yet on worker threads, saying why one
/// couldn't be done, and dropping the queue when cancelled.
/// </summary>
public sealed class TimelineBatchTests : IDisposable
{
    readonly TempFolder _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>
    /// Stands in for DJ Edit Mode: a structure cache, and a Generate that notes which songs it made shows for.
    /// </summary>
    sealed class FakeTimelines : ISongTimelines
    {
        public Dictionary<string, SongAnalysis> Cache { get; } = [];
        public List<string> Generated { get; } = [];

        public SongAnalysis? CachedStructure(string path) => Cache.GetValueOrDefault(path);

        public void AddStructure(string path, SongAnalysis analysis, DjSong? song) => Cache[path] = analysis;

        public string GenerateInto(string path, DjSong? song)
        {
            if (!Cache.ContainsKey(path))
                return "its structure hasn't been analysed";
            lock (Generated)
                Generated.Add(path);
            return "";
        }
    }

    static SongAnalysis Analysis(string path) => new() { Path = path, Duration = 60, Bpm = 120 };

    static void Run(TimelineBatch batch)
    {
        var sw = Stopwatch.StartNew();
        while (batch.IsBusy && sw.Elapsed.TotalSeconds < 30)
        {
            batch.Update();
            Thread.Sleep(2);
        }
        Assert.False(batch.IsBusy, "the batch didn't finish");
    }

    /// <summary>
    /// A 16-bit mono WAV of clicks at <paramref name="bpm"/>, <paramref name="seconds"/> long.
    /// </summary>
    string ClickWav(string name, double bpm, int seconds = 20)
    {
        const int RATE = 22050;
        var n = RATE * seconds;
        var samples = new short[n];
        var every = (int)(RATE * 60 / bpm);
        for (var i = 0; i < n; i++)
        {
            var into = i % every;
            if (into < RATE / 20)
                samples[i] = (short)(Math.Sin(2 * Math.PI * 880 * i / RATE) * 20000 * (1 - into / (RATE / 20.0)));
        }
        var path = _dir.File(name);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8);
        w.Write(36 + n * 2);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(RATE);
        w.Write(RATE * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(n * 2);
        foreach (var s in samples)
            w.Write(s);
        return path;
    }

    [Fact]
    public void Songs_analysed_already_are_generated_a_couple_at_a_time()
    {
        var tl = new FakeTimelines();
        var p = Enumerable.Range(0, 4).Select(k => _dir.File($"s{k}.mp3")).ToList();
        foreach (var path in p.Take(3))
            tl.Cache[path] = Analysis(path);
        var batch = new TimelineBatch(tl) { Workers = 2 };
        var most = 0;
        var saw = new Dictionary<string, double>();
        var allDone = 0;
        batch.Progress += (path, f) =>
        {
            most = Math.Max(most, batch.Running);
            saw[path] = Math.Max(saw.GetValueOrDefault(path, -1), f);
        };
        batch.AllDone += () => allDone++;
        batch.Add(p.Take(3));
        Assert.True(batch.IsBusy && saw.Count == 3, "all three under way or queued, each with a bar");
        Assert.True(batch.Has(p[2]) && !batch.Has(p[3]));
        batch.Add([p[0]]); // already queued: not twice
        Run(batch);
        Assert.Equal(p.Take(3), tl.Generated); // each song's show generated, an unselected one left alone
        Assert.True(most <= 2 && saw.Values.All(f => f == 1.0), $"at most two at a time ({most}), each bar run to full");
        Assert.Equal(1, allDone);
    }

    [Fact]
    public void Songs_that_cant_be_analysed_say_why()
    {
        var tl = new FakeTimelines();
        var junk = _dir.File("junk.wav");
        File.WriteAllText(junk, "not a wav at all, but long enough to be read as one ..........");
        var missing = _dir.File("gone.wav");
        var errors = new Dictionary<string, string>();
        var batch = new TimelineBatch(tl);
        batch.Done += (path, e) => errors[path] = e;
        batch.Add([junk, missing]);
        Run(batch);
        Assert.Equal("Couldn't analyse this track: not a WAV file", errors[junk]);
        Assert.Equal("file not found", errors[missing]);
        Assert.Empty(tl.Generated);
    }

    [Fact]
    public void Songs_not_analysed_yet_are_analysed_side_by_side_then_generated()
    {
        var tl = new FakeTimelines();
        string[] wavs = [ClickWav("beat a.wav", 120), ClickWav("beat b.wav", 100)];
        var batch = new TimelineBatch(tl) { Workers = 2 };
        var errors = new List<string>();
        var progress = new Dictionary<string, List<double>>();
        batch.Done += (path, e) =>
        {
            if (e != "")
                errors.Add(e);
        };
        batch.Progress += (path, f) =>
        {
            if (!progress.TryGetValue(path, out var list))
                progress[path] = list = [];
            list.Add(f);
        };
        batch.Add(wavs);
        Assert.Equal(2, batch.Running); // two at once
        Run(batch);
        Assert.Empty(errors);
        Assert.Equal(wavs.Order(), tl.Generated.Order());
        Assert.All(wavs, w => Assert.NotNull(tl.CachedStructure(w)));
        Assert.All(progress.Values, fs => Assert.True(fs.SequenceEqual(fs.Order()) && fs[^1] == 1.0, "each bar only goes up, to full"));
        Assert.Contains(progress[wavs[0]], f => f > 0 && f < TimelineBatch.ANALYSIS_SHARE);
    }

    [Fact]
    public void Cancelling_drops_the_queue_and_any_analysis_under_way()
    {
        var tl = new FakeTimelines();
        var started = new ManualResetEventSlim();
        var stop = new ManualResetEventSlim();
        var paths = Enumerable.Range(0, 3).Select(k => _dir.File($"c{k}.wav")).ToList();
        foreach (var path in paths)
            File.WriteAllText(path, "x");
        var batch = new TimelineBatch(tl)
        {
            Workers = 1,
            Analyse = (path, progress, cancel) =>
            {
                started.Set();
                stop.Wait(TimeSpan.FromSeconds(10));
                return Analysis(path);
            },
        };
        batch.Add(paths);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        batch.Cancel();
        Assert.False(batch.IsBusy); // the queue's dropped
        stop.Set();
        Thread.Sleep(100);
        batch.Update();
        Assert.Empty(tl.Generated); // the analysis's result unused
        Assert.Empty(tl.Cache);
    }

    [Fact]
    public void A_generator_can_stand_in_for_generate()
    {
        var tl = new FakeTimelines();
        var path = _dir.File("x.mp3");
        tl.Cache[path] = Analysis(path);
        var song = new DjSong { Path = path };
        var asked = new List<(string, DjSong?)>();
        var batch = new TimelineBatch(tl)
        {
            SongLookup = p => p == path ? song : null,
            Generator = (p, s) =>
            {
                asked.Add((p, s));
                return "no show";
            },
        };
        string? error = null;
        batch.Done += (_, e) => error = e;
        batch.Add([path]);
        Run(batch);
        Assert.Equal([(path, (DjSong?)song)], asked);
        Assert.Equal("no show", error);
        Assert.Empty(tl.Generated);
    }
}
