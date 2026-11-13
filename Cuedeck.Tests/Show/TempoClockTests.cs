using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Show;

namespace Cuedeck.Tests.Show;

/// <summary>
/// sLight's tempo_clock_test.gd: tap tempo, MIDI clock, OS2L beats, running freely between them, and tempo-synced
/// effects. Each test drives a fresh clock with explicit times.
/// </summary>
public class TempoClockTests
{
    static (TempoClock Clock, Func<int> Beats) Clock()
    {
        var t = new TempoClock();
        var n = 0;
        t.Beat += () => n++;
        return (t, () => n);
    }

    [Fact]
    public void Tap_tempo()
    {
        var (t, beats) = Clock();
        Assert.False(t.Running);
        Assert.Equal("— BPM", t.Describe());
        t.Tap(10.0);
        Assert.True(t.Running);
        Assert.Equal(TempoSource.Tap, t.Source);
        Assert.Equal(1, beats());
        Assert.True(Gd.ApproxEqual(t.Bpm, 120)); // the tempo it had

        t.Tap(10.5);
        t.Tap(11.0);
        t.Tap(11.5);
        Assert.True(Math.Abs(t.Bpm - 120) < 0.01, $"{t.Bpm}");
        Assert.Equal(4, beats());

        t.Tap(11.9);
        Assert.True(Math.Abs(t.Bpm - 60.0 / (1.9 / 4.0)) < 0.01, $"the average spacing: {t.Bpm}");

        t.Tap(20.0);
        t.Tap(20.4);
        Assert.True(Math.Abs(t.Bpm - 150) < 0.01, $"a pause starts a new count: {t.Bpm}");

        t.Tap(30.0);
        t.Tap(30.1);
        Assert.Equal(TempoClock.MaxBpm, t.Bpm);
        Assert.Contains("Tap", t.Describe());

        t.Stop();
        Assert.False(t.Running);
        Assert.Equal(TempoSource.None, t.Source);
    }

    [Fact]
    public void It_runs_freely_between_taps()
    {
        var (t, beats) = Clock();
        t.Tap(0.0);
        t.Tap(0.5); // 120 BPM, the next beat at 1.0
        t.Advance(0.9);
        Assert.Equal(2, beats());
        t.Advance(1.0);
        Assert.Equal(3, beats());
        Assert.True(Math.Abs(t.Phase(1.25) - 0.5) < 0.001);
        t.Advance(1.6);
        Assert.Equal(4, beats());
        t.Advance(5.2);
        Assert.Equal(5, beats()); // after a stall, one beat, not a burst
        t.Advance(5.4);
        Assert.Equal(5, beats()); // back in step, the next at 5.5
        t.Advance(5.5);
        Assert.Equal(6, beats());
    }

    [Fact]
    public void Midi_clock()
    {
        var (t, beats) = Clock();
        var dt = 60.0 / 125.0 / 24.0; // 125 BPM, 24 pulses a beat
        for (var i = 0; i < 24; i++)
            t.ClockPulse(100.0 + i * dt);
        Assert.False(t.Running); // a beat's worth of pulses before it has a tempo
        Assert.Equal(0, beats());
        for (var i = 24; i < 97; i++)
            t.ClockPulse(100.0 + i * dt);
        Assert.True(t.Running);
        Assert.Equal(TempoSource.MidiClock, t.Source);
        Assert.True(Math.Abs(t.Bpm - 125) < 0.05, $"{t.Bpm}");
        Assert.Equal(4, beats()); // 24, 48, 72, 96
        Assert.False(t.Tap(100.0 + 97 * dt)); // taps ignored while it's live

        var last = 100.0 + 96 * dt;
        t.Advance(last + 0.4);
        Assert.Equal(TempoSource.MidiClock, t.Source);
        t.Advance(last + 0.6);
        Assert.True(t.Running);
        Assert.Equal(TempoSource.Tap, t.Source); // gone quiet: runs on freely
        Assert.True(t.Tap(last + 1.0));
    }

    [Fact]
    public void Os2l_beats()
    {
        var (t, beats) = Clock();
        t.ExternalBeat(128, TempoSource.Os2l, 50.0);
        Assert.True(t.Running);
        Assert.Equal(TempoSource.Os2l, t.Source);
        Assert.Equal(1, beats());
        Assert.True(Gd.ApproxEqual(t.Bpm, 128));

        var p = 60.0 / 128.0;
        t.Advance(50.0 + p + 0.01);
        Assert.Equal(1, beats()); // a beat a little late isn't doubled
        t.ExternalBeat(128, TempoSource.Os2l, 50.0 + p + 0.02);
        Assert.Equal(2, beats());
        t.Advance(50.0 + p * 2.3);
        Assert.Equal(3, beats()); // one missed: filled in
        t.Advance(50.0 + p * 6.0);
        Assert.Equal(TempoSource.Tap, t.Source); // stopped coming: runs on freely
    }

    [Fact]
    public void Tempo_synced_effects()
    {
        var e = new WaveEffect { Bpm = 30 };
        Assert.Equal(30, e.Rate(128));
        e.TempoBeats = 2;
        Assert.Equal(30, e.Rate(0)); // following, but no clock
        Assert.True(Gd.ApproxEqual(e.Rate(128), 64));
        e.Role = "PAN";
        e.TempoBeats = 1;
        Assert.Equal(WaveEffect.MotorMaxBpm, e.Rate(128));
        Assert.Equal(1, WaveEffect.FromJson(e.ToJson()).TempoBeats);
        Assert.Equal(0, WaveEffect.FromJson(new JsonObject()).TempoBeats);
    }

    [Fact]
    public void Beat_sync_chases_step_on_the_clocks_beats()
    {
        var tempo = new TempoClock();
        var fx = new EffectsEngine(tempo);
        var chase = new Chase { BeatSync = true, Running = true };
        chase.Steps.Add(new Look([new() { [0] = 255 }]));
        chase.Steps.Add(new Look([new() { [1] = 255 }]));
        fx.Chases.Add(chase);
        tempo.Tap(0);
        Assert.Equal(1, chase.Position);
        fx.Advance(10); // its own rate doesn't move it while the clock runs
        Assert.Equal(1, chase.Position);
        tempo.Advance(0.5);
        Assert.Equal(0, chase.Position);
    }
}
