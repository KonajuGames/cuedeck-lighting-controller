using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;
using Cuedeck.Audio;
using Cuedeck.Common;
using Cuedeck.Dmx;
using Cuedeck.Fixtures;
using Cuedeck.Perform;
using Cuedeck.Show;
using Cuedeck.Timelines;

namespace Cuedeck.Tests.Perform;

/// <summary>
/// DJ Perform Mode's engine following VirtualDJ, temporary timelines for songs without any, and DJ Edit Mode's playback
/// going live (the second half of sLight's tests/dj_perform_test.gd; its panel's come with the panel). The OS2L server
/// listens on loopback, and VirtualDJ's messages are handed to it directly.
/// </summary>
public sealed class DjPerformerTests : IDisposable
{
    readonly TempFolder _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>
    /// A file in the test's folder by the path the decks have (forward slashes, as VirtualDJ's are made).
    /// </summary>
    string Slashed(string name) => _dir.File(name).Replace('\\', '/');

    [Fact]
    public void A_decks_position()
    {
        var d = new Deck { Playing = true };
        DjPerformer.ReportTime(d, 10.0, 1000);
        DjPerformer.ReportTime(d, 10.2, 1200);
        Assert.Equal(1.0, d.Rate, 6); // reports a second apart: normal speed
        Assert.Equal(10.2, d.Time);
        DjPerformer.ReportTime(d, 10.46, 1400); // 1.3x
        Assert.InRange(d.Rate, 1.0001, 1.2999); // a pitched-up deck: the rate follows
        d.Playing = false;
        d.Time = 30;
        Assert.Equal(30, d.Position(99999)); // paused: stays put
    }

    void SongFile(string path, int seed)
    {
        var b = new byte[90000];
        for (var i = 0; i < b.Length; i += 17)
            b[i] = (byte)(i * seed);
        File.WriteAllBytes(path, b);
    }

    static void Vdj(DjPerformer perf, string trigger, JsonNode? value) =>
        perf.Os2l!.HandleMessage(new JsonObject { ["evt"] = "subscribed", ["trigger"] = trigger, ["value"] = value });

    static bool Pump(Func<bool> done, Action update, int timeoutMs = 10000)
    {
        var t = Stopwatch.StartNew();
        while (t.ElapsedMilliseconds < timeoutMs)
        {
            update();
            if (done())
                return true;
            Thread.Sleep(2);
        }
        return false;
    }

    static bool WaitLoads(DjPerformer perf) => Pump(perf.LoadsDone, perf.Update);

    [Fact]
    public void Following_VirtualDJ()
    {
        var song1 = _dir.File("one.song");
        var song2 = _dir.File("two.song");
        var song3 = _dir.File("three.song");
        SongFile(song1, 3);
        SongFile(song2, 5);
        SongFile(song3, 7);
        var store = new TimelineStore(_dir.File("show.timelines"));
        store.SaveSong(song1, Json.Parse("""{"tracks": {"master": {"dimmer": [[0.0, 0.25], [20.0, 0.25]]}}}""")!.AsObject());
        store.SaveSong(song2, Json.Parse("""{"tracks": {"a": {"dimmer": [[0.0, 0.8], [100.0, 0.8]]}}}""")!.AsObject());
        var fx = SongOutputTests.Fixtures();
        var dmx = new DmxOutput();
        dmx.Universes[0].Data = SongOutputTests.Bases()[0];
        TimelineStore? current = store;
        var perf = new DjPerformer(dmx)
        {
            BindAddress = "127.0.0.1",
            Port = 0,
            Advertise = false,
            FixturesProvider = () => fx,
            StoreProvider = () => current,
        };
        Assert.False(perf.IsActive);
        Assert.DoesNotContain((LayerProvider)perf.Layer, dmx.LayerProviders);
        Assert.Equal("", perf.Start());
        Assert.True(perf.IsListening, perf.Status);
        Assert.Contains((LayerProvider)perf.Layer, dmx.LayerProviders); // playing into the DMX output

        // Deck 1 loads song 1 (VirtualDJ reports Windows paths).
        Vdj(perf, "deck 1 get_filepath", song1.Replace('/', '\\'));
        Assert.Equal(song1.Replace('\\', '/'), perf.Deck(1).Path);
        Assert.Equal(LoadState.Loading, perf.Deck(1).LoadState); // its timelines load in the background
        Assert.True(WaitLoads(perf));
        Assert.Equal(LoadState.Loaded, perf.Deck(1).LoadState);
        Assert.True(perf.Deck(1).Tracks.ContainsKey("master"));
        Assert.Empty(perf.Layer(1)[0]); // not playing: nothing driven
        Vdj(perf, "deck 1 level", 1.0);
        Vdj(perf, "deck 1 play", "on");
        Vdj(perf, "deck 1 get_time elapsed absolute", 5000.0);
        Assert.True(perf.Deck(1).Live);
        Assert.InRange(perf.Position(perf.Deck(1)), 4.8, 5.2);
        var l = perf.Layer(1);
        Assert.Equal((64, 64), (l[0][0], l[0][42])); // the master's 25 % on the dimmers
        dmx.Tick(transmit: false);
        Assert.Equal(64, dmx.Universes[0].Output[0]); // in the DMX frame
        Vdj(perf, "deck 1 level", 0.0);
        Assert.Empty(perf.Layer(1)[0]); // the volume fader down (cueing in the headphones): not driven
        Vdj(perf, "deck 1 level", 0.9);

        // Deck 2: song 2 (par a at 80 %): the two mix, HTP.
        Vdj(perf, "deck 2 get_filepath", song2);
        WaitLoads(perf);
        Vdj(perf, "deck 2 play", "on");
        Vdj(perf, "deck 2 get_time elapsed absolute", 60000.0);
        l = perf.Layer(1);
        Assert.Equal((204, 64), (l[0][0], l[0][42]));
        Vdj(perf, "deck 2 play", "off");
        Assert.Equal(64, perf.Layer(1)[0][0]); // deck 2 paused: deck 1's again

        // Mute and solo come with a song's timelines, a track with just those too.
        perf.Loaded(
            2,
            perf.Deck(2).LoadId,
            false,
            DjPerformer.TracksFromSaved(Json.Parse("""{"tracks": {"master": {"dimmer": [[0, 0.5]]}, "a": {"mute": true}, "c/h1": {"solo": true}}}""")!.AsObject())
        );
        var mixed = perf.Deck(2).Tracks;
        Assert.True(mixed["a"].Mute && mixed["c/h1"].Solo && !mixed["master"].Mute);

        // A song without timelines; a song changed while loading.
        Vdj(perf, "deck 2 get_filepath", song3);
        Assert.True(WaitLoads(perf));
        Assert.Equal(LoadState.NoTimelines, perf.Deck(2).LoadState);
        Vdj(perf, "deck 2 get_filepath", song2);
        Vdj(perf, "deck 2 get_filepath", song3);
        WaitLoads(perf);
        Assert.Equal(song3.Replace('\\', '/'), perf.Deck(2).Path);
        Assert.Equal(LoadState.NoTimelines, perf.Deck(2).LoadState); // the newer song wins

        // No timelines folder (a new, unsaved show).
        current = null;
        perf.ReloadAll();
        Assert.Equal(LoadState.NoFolder, perf.Deck(1).LoadState);
        Assert.Empty(perf.Layer(1)[0]);
        current = store;
        perf.ReloadAll();
        WaitLoads(perf);
        Assert.Equal(LoadState.Loaded, perf.Deck(1).LoadState); // the show's folder back: reloaded

        // Decks 3 and 4 behind a 2-deck skin still report what was left on them.
        Assert.True(perf.Deck(3).Hidden && perf.Deck(4).Hidden && !perf.Deck(2).Hidden);
        Vdj(perf, "deck 3 get_filepath", song1);
        Vdj(perf, "deck 3 play", "on");
        Assert.True(perf.Deck(3).Path == "" && !perf.Deck(3).Playing); // a hidden deck's song isn't followed
        Vdj(perf, "get_decks", 4);
        Assert.True(!perf.Deck(3).Hidden && perf.Deck(3).Path == song1.Replace('\\', '/') && perf.Deck(3).Playing); // picks up what was reported
        WaitLoads(perf);
        Vdj(perf, "get_decks", "2");
        Assert.True(perf.Deck(3).Hidden && perf.Deck(3).Path == "" && !perf.Deck(3).Live); // back to 2 decks: forgotten

        // VirtualDJ's beats drive the tempo clock.
        var tempo = new TempoClock();
        perf.Tempo = tempo;
        perf.Os2l!.HandleMessage(new JsonObject { ["evt"] = "beat", ["change"] = false, ["pos"] = 3, ["bpm"] = 126.0, ["strength"] = 0.5 });
        Assert.Equal(TempoSource.Os2l, tempo.Source);

        // Stop.
        perf.Stop();
        Assert.False(perf.IsActive);
        Assert.DoesNotContain((LayerProvider)perf.Layer, dmx.LayerProviders);
        Assert.Equal("", perf.Deck(1).Path); // decks forgotten
        dmx.Tick(transmit: false);
        Assert.Equal(0, dmx.Universes[0].Output[0]); // nothing in the DMX frame any more
    }

    /// <summary>
    /// A ready-made structure for a 60 s song at 120 BPM (so no decoding).
    /// </summary>
    static SongAnalysis Analysis() =>
        new()
        {
            Duration = 60,
            Bpm = 120,
            BeatTimes = [.. Enumerable.Range(0, 120).Select(i => i * 0.5f)],
            Downbeats = [.. Enumerable.Range(0, 30).Select(i => i * 2.0f)],
            Sections = [new(0, 16, "Intro", 0.2, 0), new(16, 44, "Chorus", 0.9, 1), new(44, 60, "Outro", 0.3, 2)],
        };

    static bool WaitGenerated(DjPerformer perf, SongTimelines tl) =>
        Pump(
            () => perf.LoadsDone() && !perf.IsGenerating,
            () =>
            {
                perf.Update();
                tl.Update();
            },
            30000
        );

    /// <summary>
    /// A silent 6 s WAV.
    /// </summary>
    static string SilentWav(string path)
    {
        const int rate = 22050;
        var data = new byte[rate * 6 * 2];
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8);
        w.Write(36 + data.Length);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(data.Length);
        w.Write(data);
        return path;
    }

    [Fact]
    public void Temporary_timelines_for_songs_without_any()
    {
        var cached = Slashed("cached.mp3"); // analysed already this session
        var junk = Slashed("junk.mp3"); // can't be analysed
        var own = Slashed("own.mp3"); // has timelines of its own
        var unsaved = Slashed("unsaved.mp3");
        SongFile(cached, 3);
        SongFile(junk, 5);
        SongFile(own, 7);
        SongFile(unsaved, 11);
        var store = new TimelineStore(_dir.File("show.timelines"));
        store.SaveSong(own, Json.Parse("""{"tracks": {"master": {"dimmer": [[0.0, 0.5], [60.0, 0.5]]}}}""")!.AsObject());
        var fx = SongOutputTests.Fixtures();
        var patched = fx.Select(f => new PatchedFixture { Uid = f.Uid, Name = f.Uid, Profile = f.Profile }).ToList();
        var tl = new SongTimelines { Fixtures = () => patched, AnalyseStructure = (_, _) => throw new InvalidDataException("no") };
        tl.RefreshFixtures();
        tl.SetTimelineStore(store);
        tl.AddStructure(cached, Analysis(), null);
        tl.AddStructure(unsaved, Analysis(), null);

        var perf = new DjPerformer(new DmxOutput())
        {
            BindAddress = "127.0.0.1",
            Port = 0,
            Advertise = false,
            FixturesProvider = () => fx,
            StoreProvider = () => tl.Store,
            Timeline = tl,
            // The junk can't be decoded; the WAV is really analysed.
            Analyse = (path, progress, cancel) => path.EndsWith(".wav") ? SongAnalyzer.Analyse(path, progress, cancel) : throw new InvalidDataException("can't decode it"),
        };
        perf.Start();
        Vdj(perf, "get_decks", 4);

        // Analysed already: its show at once, saved flagged temporary.
        Vdj(perf, "deck 1 get_filepath", cached);
        Assert.True(WaitGenerated(perf, tl));
        var d1 = perf.Deck(1);
        Assert.True(d1.LoadState == LoadState.Loaded && d1.Temporary && !d1.Analysing && d1.Tracks.ContainsKey("master") && d1.Tracks.Count > 1);
        Assert.True(store.LoadSong(cached).Bool("temporary"));
        Assert.True(store.IsTemporary(cached) && new TimelineStore(store.Dir).IsTemporary(cached)); // in the index too
        Vdj(perf, "deck 1 level", 1.0);
        Vdj(perf, "deck 1 play", "on");
        Vdj(perf, "deck 1 get_time elapsed absolute", 30000.0);
        Assert.NotEmpty(perf.Layer(1)[0]); // and it plays into the lights

        // Not analysed, and it can't be: a stand-in on the beat, kept.
        perf.Os2l!.HandleMessage(new JsonObject { ["evt"] = "subscribed", ["trigger"] = "deck 2 get_bpm", ["value"] = 128.0 });
        Vdj(perf, "deck 2 get_filepath", junk);
        var d2 = perf.Deck(2);
        WaitGenerated(perf, tl);
        Assert.True(d2.LoadState == LoadState.Loaded && d2.Temporary && d2.Tracks.Count > 1); // made on a worker thread
        Assert.False(d2.Analysing);
        Assert.Equal("can't decode it", d2.Note); // the stand-in stays, not saved
        Assert.False(store.HasSong(junk));

        // A song with its own timelines: those, not temporary.
        Vdj(perf, "deck 1 get_filepath", own);
        WaitGenerated(perf, tl);
        Assert.True(d1.LoadState == LoadState.Loaded && !d1.Temporary && d1.Tracks.Count == 1);
        tl.AddStructure(own, Analysis(), null);
        Assert.NotEqual("", tl.PutGenerated(own, tl.GenerateFor(own)!, temporary: true)); // never over a song's own
        Assert.False(store.IsTemporary(own));
        // The temporary ones again, from the show.
        Vdj(perf, "deck 1 get_filepath", cached);
        WaitGenerated(perf, tl);
        Assert.True(d1.LoadState == LoadState.Loaded && d1.Temporary && !d1.Analysing);

        // Edited in DJ Edit Mode, they're the user's.
        tl.Open(cached);
        tl.Master.DimmerNodes = [new Vector2(0, 0.4f), new Vector2(60, 0.4f)];
        tl.MarkEdited();
        Assert.Equal("", tl.Flush());
        Assert.False(store.IsTemporary(cached));
        Assert.False(store.LoadSong(cached).Has("temporary"));

        // No timelines folder: generated all the same, just not saved.
        tl.SetTimelineStore(null);
        Vdj(perf, "deck 3 get_filepath", unsaved);
        WaitGenerated(perf, tl);
        var d3 = perf.Deck(3);
        Assert.True(d3.LoadState == LoadState.Loaded && d3.Temporary && d3.Tracks.Count > 1);
        Assert.Contains("not saved", d3.Note);
        Assert.False(store.HasSong(unsaved));

        // A song really analysed: its show generated, saved temporary.
        tl.SetTimelineStore(store);
        var wav = SilentWav(Slashed("real.wav"));
        var d4 = perf.Deck(4);
        Vdj(perf, "deck 4 get_filepath", wav);
        Assert.True(WaitGenerated(perf, tl));
        Assert.Equal("", d4.Note);
        Assert.True(!d4.Analysing && d4.Tracks.Count > 1 && tl.CachedStructure(wav) is not null);
        Assert.True(store.IsTemporary(wav));
        perf.Stop();
    }

    [Fact]
    public void DJ_Edit_Modes_playback_goes_live()
    {
        using var show = new LightingShow();
        var spot = show.Patch.Add(0, Profiles.Simple("mover", "mover", ("Pan", "PAN"), ("Tilt", "TILT"), ("Dimmer", "DIMMER")), 0, name: "Spot");
        var tl = new SongTimelines { Fixtures = () => [.. show.Patch.All] };
        tl.RefreshFixtures();
        tl.Open(_dir.File("edit.wav"));
        bool canPlay = true,
            playing = false,
            shown = false;
        double pos = 0;
        var perf = new DjPerformer(show.Dmx) { BindAddress = "127.0.0.1", Port = 0, Advertise = false };
        var edit = new EditPlayback(show.Dmx, show.Patch, show.Positions, tl)
        {
            CanPlay = () => canPlay,
            IsPlaying = () => playing,
            IsShown = () => shown,
            PlaybackPosition = () => pos,
            Performer = perf,
        };
        Assert.Contains((LayerProvider)edit.Layer, show.Dmx.LayerProviders); // a layer of the DMX output
        tl.Master.DimmerNodes = [new(0, 0.6f), new(6, 0.6f)];
        Assert.Empty(edit.Layer(1)[0]); // not playing, in the console view: nothing goes out
        playing = true;
        pos = 0.2;
        Assert.Equal(Gd.RoundToInt(0.6 * 255), edit.Layer(1)[0][2]); // playing: the timelines at the playhead
        tl.Master.DimmerNodes = [new(0, 0.2f), new(6, 0.2f)];
        Assert.Equal(Gd.RoundToInt(0.2 * 255), edit.Layer(1)[0][2]); // edits show straight away
        playing = false;
        Assert.Empty(edit.Layer(1)[0]); // paused in another view: the console has the rig
        shown = true; // its timeline showing
        tl.Master.DimmerNodes = [new(0, 0.2f), new(3, 0.2f), new(3, 0.9f), new(6, 0.9f)];
        pos = 1;
        Assert.Equal(Gd.RoundToInt(0.2 * 255), edit.Layer(1)[0][2]); // paused in DJ Edit Mode: the look held
        pos = 4;
        Assert.InRange(edit.Layer(1)[0][2], 229, 231); // ...following the playhead as it's moved
        shown = false; // the Positions editor open in its place
        Assert.Empty(edit.Layer(1)[0]);
        playing = true; // playing on behind it: a highlighted fixture keeps its highlight
        show.Patch.SetHighlight(spot, true);
        Assert.Empty(edit.Layer(1)[0]);
        show.Patch.SetHighlight(spot, false);
        Assert.NotEmpty(edit.Layer(1)[0]); // ...and follows the timelines again once it's not
        perf.Start(); // DJ Perform Mode on: the live decks own the rig
        Assert.Empty(edit.Layer(1)[0]);
        perf.Stop();
        canPlay = false; // no song loaded
        Assert.Empty(edit.Layer(1)[0]);
        edit.Dispose();
        Assert.DoesNotContain((LayerProvider)edit.Layer, show.Dmx.LayerProviders);
    }
}
