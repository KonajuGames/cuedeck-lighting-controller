using System.Diagnostics;
using Brutal.SdlApi;
using Brutal.SdlApi.Mixer;

namespace Cuedeck.Audio;

/// <summary>
/// Plays one song for DJ Edit Mode's timeline: play from a point, stop where it is, seek, and say where it's got to.
/// Played to its end, it stops by itself.
/// </summary>
interface ISongPlayer : IDisposable
{
    /// <summary>
    /// Takes <paramref name="audio"/> to play, in place of what it had (stopped). Returns "" or why it can't.
    /// </summary>
    string Load(PcmAudio audio);

    void Unload();

    bool IsLoaded { get; }

    /// <summary>
    /// The loaded song's length, seconds (0 with none).
    /// </summary>
    double Length { get; }

    bool IsPlaying { get; }

    /// <summary>
    /// Where it's playing, seconds, as heard.
    /// </summary>
    double Position { get; }

    void Play(double from);

    void Stop();

    void Seek(double t);
}

/// <summary>
/// Plays a song through SDL_mixer on the default output device, straight from its decoded samples.
/// </summary>
sealed class SdlSongPlayer : ISongPlayer
{
    /// <summary>
    /// SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK.
    /// </summary>
    const uint DEFAULT_PLAYBACK = 0xFFFFFFFF;

    MixMixer? _mixer;
    MixTrack? _track;
    MixAudio? _audio;
    PcmAudio? _pcm;
    string _error = "";

    // The position the mixer last reported, and when, to move the playhead on smoothly between its updates.
    long _lastFrame = -1;
    readonly Stopwatch _sinceFrame = new();

    string Open()
    {
        if (_mixer is not null || _error != "")
            return _error;
        try
        {
            Sdl.Init.Initialize(SdlInitFlags.Audio);
            SdlMixer.Init();
            unsafe
            {
                var spec = new SdlAudioSpec { format = SdlAudioFormat.F32, channels = 2, freq = 48000 };
                _mixer = SdlMixer.CreateMixerDevice(DEFAULT_PLAYBACK, &spec);
            }
            _track = _mixer.Value.CreateTrack();
        }
        catch (Exception e)
        {
            _error = $"no sound output ({e.Message.TrimEnd('.')})";
        }
        return _error;
    }

    public string Load(PcmAudio audio)
    {
        if (Open() is { Length: > 0 } error)
            return error;
        Unload();
        try
        {
            _pcm = audio.Pinned();
            _audio = _pcm.Play(_mixer!.Value, 0, _pcm.Frames);
            _track!.Value.SetAudio(_audio.Value);
            return "";
        }
        catch (Exception e)
        {
            _pcm = null;
            return e.Message.TrimEnd('.');
        }
    }

    public void Unload()
    {
        if (_track is { } track)
        {
            track.Stop(0);
            track.SetAudio(default);
        }
        _audio?.Dispose();
        _audio = null;
        _pcm = null;
        _lastFrame = -1;
    }

    public bool IsLoaded => _pcm is not null;

    public double Length => _pcm is { } p ? (double)p.Frames / p.SampleRate : 0;

    public bool IsPlaying => _pcm is not null && _track is { } t && t.Playing() && !t.Paused();

    public double Position
    {
        get
        {
            if (_pcm is not { } p || _track is not { } t)
                return 0;
            var frame = Math.Max(0, t.GetPlaybackPosition());
            if (frame != _lastFrame)
            {
                _lastFrame = frame;
                _sinceFrame.Restart();
            }
            var at = (double)frame / p.SampleRate;
            if (IsPlaying)
                at += Math.Min(_sinceFrame.Elapsed.TotalSeconds, 0.1);
            return Math.Clamp(at, 0, Length);
        }
    }

    public void Play(double from)
    {
        if (_pcm is not { } p || _track is not { } t)
            return;
        var frame = (long)(Math.Clamp(from, 0, Length) * p.SampleRate);
        if (!t.Playing() && !t.Paused())
            t.Play(0);
        t.SetPlaybackPosition(frame);
        t.Resume();
        _lastFrame = -1;
    }

    public void Stop()
    {
        if (_track is { } t && t.Playing())
            t.Pause();
    }

    public void Seek(double t)
    {
        if (_pcm is not { } p || _track is not { } track || !(track.Playing() || track.Paused()))
            return;
        track.SetPlaybackPosition((long)(Math.Clamp(t, 0, Length) * p.SampleRate));
        _lastFrame = -1;
    }

    public void Dispose()
    {
        Unload();
        _track?.Dispose();
        _mixer?.Dispose();
        _track = null;
        _mixer = null;
    }
}
