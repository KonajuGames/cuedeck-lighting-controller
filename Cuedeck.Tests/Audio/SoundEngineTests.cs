using Cuedeck.Audio;
using Cuedeck.Show;

namespace Cuedeck.Tests.Audio;

/// <summary>
/// The live audio analysis (sLight's Sound autoload): band levels, smoothing, beats, the spectrogram's bands, and its
/// part in the show, against a stand-in input playing made-up signals.
/// </summary>
public class SoundEngineTests
{
    /// <summary>
    /// An input whose latest samples are <see cref="Signal"/> (seconds → sample), the same on both channels.
    /// </summary>
    sealed class FakeInput : IAudioCapture
    {
        public Func<double, float> Signal { get; set; } = _ => 0;
        public List<string> Started { get; } = [];
        public bool Listening { get; private set; }

        public IReadOnlyList<string> Devices() => ["Line In", "Microphone"];

        public bool Start(string device)
        {
            Started.Add(device);
            Listening = true;
            return true;
        }

        public void Stop() => Listening = false;

        public int SampleRate => 48000;

        public string Error => "";

        public void Latest(Span<float> left, Span<float> right)
        {
            for (var i = 0; i < left.Length; i++)
                left[i] = right[i] = Signal((double)i / SampleRate);
        }

        public void Dispose() { }
    }

    static Func<double, float> Sine(double hz, double amplitude) => t => (float)(amplitude * Math.Sin(Math.Tau * hz * t));

    const double FRAME = 1.0 / 30;

    [Fact]
    public void Idle_it_neither_listens_nor_hears()
    {
        var input = new FakeInput { Signal = Sine(100, 0.5) };
        var sound = new SoundEngine(new SoundSettings(), input);
        Assert.False(sound.Update(FRAME));
        Assert.Equal((0.0, 0.0), (sound.Bass, sound.Level));
        Assert.Empty(input.Started);
        Assert.All(sound.Spectrum(8), v => Assert.Equal(0, v));
    }

    [Fact]
    public void A_low_tone_lights_the_bass()
    {
        var input = new FakeInput { Signal = Sine(100, 0.5) };
        var sound = new SoundEngine(new SoundSettings(), input) { Active = true };
        Assert.Equal([""], input.Started); // the default input
        sound.Update(FRAME);
        // A half-scale tone: about -12 dB after the window, +6 → 0.8 on the -60..0 dB scale.
        Assert.InRange(sound.Bass, 0.7, 0.85);
        Assert.True(sound.Mid < sound.Bass * 0.6, $"mid {sound.Mid}");
        Assert.True(sound.Treble < 0.1, $"treble {sound.Treble}");
        // The level: the loudest band and the three's average, mixed.
        Assert.Equal(Math.Clamp(sound.Bass * 0.6 + (sound.Bass + sound.Mid + sound.Treble) / 3 * 0.7, 0, 1), sound.Level, 6);

        var spectrum = sound.Spectrum(40);
        Assert.Equal(40, spectrum.Length);
        Assert.Equal(Array.IndexOf(spectrum, spectrum.Max()), Array.IndexOf(spectrum, spectrum.Take(10).Max())); // the peak's low
    }

    [Fact]
    public void Gain_and_a_high_tone()
    {
        var settings = new SoundSettings { Gain = 0.5 };
        var input = new FakeInput { Signal = Sine(5000, 0.5) };
        var sound = new SoundEngine(settings, input) { Active = true };
        sound.Update(FRAME);
        Assert.InRange(sound.Treble, 0.35, 0.43); // halved by the gain
        Assert.True(sound.Bass < 0.05);
    }

    [Fact]
    public void Levels_rise_fast_and_fall_slowly_and_go_when_it_stops()
    {
        var settings = new SoundSettings { Response = 0 };
        var input = new FakeInput { Signal = Sine(100, 0.5) };
        var sound = new SoundEngine(settings, input) { Active = true };
        sound.Update(FRAME);
        var first = sound.Bass;
        Assert.InRange(first, 0.5, 0.65); // 0.7 of the way at the slowest response
        input.Signal = _ => 0;
        sound.Update(FRAME);
        Assert.InRange(sound.Bass, first * 0.9, first * 0.93); // 8% of the way down

        sound.Active = false;
        Assert.False(input.Listening);
        Assert.Equal(0, sound.Bass);
    }

    [Fact]
    public void A_kick_over_quiet_bass_is_a_beat_at_most_nine_a_second()
    {
        var input = new FakeInput { Signal = Sine(80, 0.01) };
        var sound = new SoundEngine(new SoundSettings(), input) { Active = true };
        var beats = 0;
        sound.Beat += () => beats++;
        for (var i = 0; i < 60; i++)
            sound.Update(FRAME); // from silence, the quiet bass is a beat itself at first
        Assert.False(sound.Update(FRAME));
        beats = 0;

        input.Signal = Sine(80, 0.8);
        Assert.True(sound.Update(FRAME));
        Assert.Equal((1, 1.0), (beats, sound.BeatPulse));
        Assert.False(sound.Update(0.05)); // still cooling down
        sound.Update(FRAME);
        Assert.True(sound.BeatPulse < 1);
        Assert.Equal(1, beats); // and the average has caught up some way
    }

    [Fact]
    public void Choosing_another_input_listens_to_it()
    {
        var settings = new SoundSettings();
        var input = new FakeInput();
        var sound = new SoundEngine(settings, input) { Active = true };
        settings.Device = "Line In";
        sound.Update(FRAME);
        Assert.Equal(["", "Line In"], input.Started);
        Assert.Equal(["Line In", "Microphone"], sound.Devices());
    }

    [Fact]
    public void The_show_listens_in_sound_reactive_and_steps_beat_chases_on_beats()
    {
        var input = new FakeInput { Signal = Sine(80, 0.01) };
        using var show = new LightingShow(input);
        show.Patch.Add(0, Profiles.Simple("dim", "Dim", ("Dimmer", "DIMMER")), 0);
        var chase = new Chase { Name = "C", BeatSync = true, Running = true };
        show.Dmx.Universes[0].SetChannel(0, 255);
        chase.CaptureStep(show.Dmx);
        show.Dmx.Universes[0].SetChannel(0, 0);
        chase.CaptureStep(show.Dmx);
        show.Effects.Chases.Add(chase);

        show.Tick(FRAME, false);
        Assert.False(input.Listening);
        show.RunMode = RunMode.SoundReactive;
        Assert.True(input.Listening);
        Assert.Same(show.Audio, show.Effects.Sound);
        for (var i = 0; i < 60; i++)
            show.Tick(FRAME, false);
        var step = chase.Position;
        input.Signal = Sine(80, 0.8);
        show.Tick(FRAME, false);
        Assert.NotEqual(step, chase.Position);

        show.RunMode = RunMode.Cues;
        Assert.False(input.Listening);
    }

    [Fact]
    public void The_fft_finds_a_tone()
    {
        var samples = new float[1024];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(Math.Tau * 32 * i / 1024);
        var mag = Fft.Magnitude(samples, 0, 1024);
        Assert.Equal(513, mag.Length);
        Assert.Equal(32, Array.IndexOf(mag, mag.Max()));
    }

    [Fact]
    public void The_capture_buffer_keeps_the_latest_samples()
    {
        var buffer = new CaptureBuffer(8);
        buffer.Write([1, 2, 3, 4, 5, 6], [-1, -2, -3, -4, -5, -6]);
        buffer.Write([7, 8, 9, 10], [-7, -8, -9, -10]);
        var l = new float[3];
        var r = new float[3];
        buffer.Latest(l, r);
        Assert.Equal([8, 9, 10], l);
        Assert.Equal([-8, -9, -10], r);
    }
}
