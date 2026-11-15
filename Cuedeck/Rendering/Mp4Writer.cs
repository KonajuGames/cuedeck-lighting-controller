using System.Runtime.InteropServices;

namespace Cuedeck.Rendering;

/// <summary>
/// Writes an MP4 with Windows' Media Foundation sink writer: H.264 video from top-down BGRA frames, and optionally
/// an AAC track from 16-bit stereo PCM. Frames and audio are timed by how many have been written (the video at a
/// fixed rate, the audio by its samples), so they stay in step however they arrive. Thread-safe: the audio comes
/// from its capture thread. Its COM objects are called through their vtables, so nothing's needed beyond Windows.
/// </summary>
sealed unsafe class Mp4Writer : IDisposable
{
    // Media Foundation's GUIDs.
    static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");
    static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");
    static readonly Guid MFAudioFormat_AAC = new("00001610-0000-0010-8000-00aa00389b71");
    static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00aa00389b71");

    const uint MF_VERSION = 0x00020070;

    // Vtable slots: IUnknown's Release; IMFAttributes' setters; IMFSinkWriter's, IMFSample's and IMFMediaBuffer's own.
    const int RELEASE = 2;
    const int SET_UINT32 = 21;
    const int SET_UINT64 = 22;
    const int SET_GUID = 24;
    const int ADD_STREAM = 3;
    const int SET_INPUT_MEDIA_TYPE = 4;
    const int BEGIN_WRITING = 5;
    const int WRITE_SAMPLE = 6;
    const int FINALIZE = 11;
    const int SET_SAMPLE_TIME = 36;
    const int SET_SAMPLE_DURATION = 38;
    const int ADD_BUFFER = 42;
    const int LOCK = 3;
    const int UNLOCK = 4;
    const int SET_CURRENT_LENGTH = 6;

    [DllImport("mfplat.dll")]
    static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll")]
    static extern int MFShutdown();

    [DllImport("mfplat.dll")]
    static extern int MFCreateMediaType(out nint mediaType);

    [DllImport("mfplat.dll")]
    static extern int MFCreateSample(out nint sample);

    [DllImport("mfplat.dll")]
    static extern int MFCreateMemoryBuffer(uint maxLength, out nint buffer);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    static extern int MFCreateSinkWriterFromURL(string url, nint byteStream, nint attributes, out nint sinkWriter);

    readonly Lock _lock = new();
    nint _writer;
    uint _video;
    uint _audio = uint.MaxValue;
    long _frames;
    long _audioSamples;
    bool _finished;

    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }

    /// <summary>
    /// The audio's rate and channels, if it has a track.
    /// </summary>
    public (int Rate, int Channels)? Audio { get; }

    public long FrameCount => Interlocked.Read(ref _frames);

    Mp4Writer(int width, int height, int fps, (int, int)? audio) => (Width, Height, Fps, Audio) = (width, height, fps, audio);

    /// <summary>
    /// Starts an MP4 at <paramref name="path"/>; <paramref name="audio"/> (44.1 or 48 kHz, 1 or 2 channels) adds an
    /// AAC track. Width and height must be even. Returns null, with why, if Media Foundation can't.
    /// </summary>
    public static (Mp4Writer? Writer, string Error) Start(string path, int width, int height, int fps = 30, int kbps = 12000, (int Rate, int Channels)? audio = null)
    {
        if (!OperatingSystem.IsWindows())
            return (null, "recording needs Windows' Media Foundation");
        var w = new Mp4Writer(width, height, fps, audio);
        try
        {
            Check(MFStartup(MF_VERSION, 0), "MFStartup");
            Check(MFCreateSinkWriterFromURL(path, 0, 0, out w._writer), "MFCreateSinkWriterFromURL");

            var output = Type(MFMediaType_Video, MFVideoFormat_H264);
            Set32(output, MF_MT_AVG_BITRATE, (uint)(kbps * 1000));
            Set32(output, MF_MT_INTERLACE_MODE, 2); // progressive
            Set64(output, MF_MT_FRAME_SIZE, (ulong)width << 32 | (uint)height);
            Set64(output, MF_MT_FRAME_RATE, (ulong)fps << 32 | 1u);
            Set64(output, MF_MT_PIXEL_ASPECT_RATIO, 1UL << 32 | 1u);
            uint video;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)Slot(w._writer, ADD_STREAM))(w._writer, output, &video), "AddStream");
            w._video = video;
            Release(output);

            var input = Type(MFMediaType_Video, MFVideoFormat_RGB32);
            Set32(input, MF_MT_INTERLACE_MODE, 2);
            Set64(input, MF_MT_FRAME_SIZE, (ulong)width << 32 | (uint)height);
            Set64(input, MF_MT_FRAME_RATE, (ulong)fps << 32 | 1u);
            Set64(input, MF_MT_PIXEL_ASPECT_RATIO, 1UL << 32 | 1u);
            Set32(input, MF_MT_DEFAULT_STRIDE, (uint)(width * 4)); // rows from the top
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)Slot(w._writer, SET_INPUT_MEDIA_TYPE))(w._writer, video, input, 0), "SetInputMediaType");
            Release(input);

            if (audio is { } a)
            {
                var aacOut = Type(MFMediaType_Audio, MFAudioFormat_AAC);
                Set32(aacOut, MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                Set32(aacOut, MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)a.Rate);
                Set32(aacOut, MF_MT_AUDIO_NUM_CHANNELS, (uint)a.Channels);
                Set32(aacOut, MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 24000); // 192 kbps
                uint stream;
                Check(((delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)Slot(w._writer, ADD_STREAM))(w._writer, aacOut, &stream), "AddStream (audio)");
                w._audio = stream;
                Release(aacOut);
                var pcm = Type(MFMediaType_Audio, MFAudioFormat_PCM);
                Set32(pcm, MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                Set32(pcm, MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)a.Rate);
                Set32(pcm, MF_MT_AUDIO_NUM_CHANNELS, (uint)a.Channels);
                Set32(pcm, MF_MT_AUDIO_BLOCK_ALIGNMENT, (uint)(a.Channels * 2));
                Set32(pcm, MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)(a.Rate * a.Channels * 2));
                Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)Slot(w._writer, SET_INPUT_MEDIA_TYPE))(w._writer, stream, pcm, 0), "SetInputMediaType (audio)");
                Release(pcm);
            }
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(w._writer, BEGIN_WRITING))(w._writer), "BeginWriting");
            return (w, "");
        }
        catch (Exception e) when (e is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            w.Dispose();
            return (null, e.Message);
        }
    }

    /// <summary>
    /// The next frame: top-down BGRA, <see cref="Width"/> by <see cref="Height"/>.
    /// </summary>
    public void PushFrame(ReadOnlySpan<byte> bgra)
    {
        lock (_lock)
        {
            if (_writer == 0 || _finished)
                return;
            var duration = 10_000_000L / Fps;
            Write(_video, bgra[..(Width * Height * 4)], _frames * duration, duration);
            _frames++;
        }
    }

    /// <summary>
    /// More audio: interleaved 16-bit samples at <see cref="Audio"/>'s rate and channels.
    /// </summary>
    public void PushAudio(ReadOnlySpan<short> pcm)
    {
        lock (_lock)
        {
            if (_writer == 0 || _finished || Audio is not { } a || pcm.Length < a.Channels)
                return;
            var frames = pcm.Length / a.Channels;
            Write(_audio, MemoryMarshal.AsBytes(pcm[..(frames * a.Channels)]), _audioSamples * 10_000_000L / a.Rate, frames * 10_000_000L / a.Rate);
            _audioSamples += frames;
        }
    }

    /// <summary>
    /// How far the audio's got, seconds (to fill a silence with silence).
    /// </summary>
    public double AudioSeconds
    {
        get
        {
            lock (_lock)
                return Audio is { } a ? (double)_audioSamples / a.Rate : 0;
        }
    }

    public double VideoSeconds => (double)FrameCount / Fps;

    void Write(uint stream, ReadOnlySpan<byte> data, long time, long duration)
    {
        Check(MFCreateMemoryBuffer((uint)data.Length, out var buffer), "MFCreateMemoryBuffer");
        try
        {
            byte* p;
            uint max,
                cur;
            Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot(buffer, LOCK))(buffer, &p, &max, &cur), "Lock");
            data.CopyTo(new Span<byte>(p, data.Length));
            ((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, UNLOCK))(buffer);
            ((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(buffer, SET_CURRENT_LENGTH))(buffer, (uint)data.Length);
            Check(MFCreateSample(out var sample), "MFCreateSample");
            try
            {
                ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(sample, ADD_BUFFER))(sample, buffer);
                ((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, SET_SAMPLE_TIME))(sample, time);
                ((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, SET_SAMPLE_DURATION))(sample, duration);
                Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Slot(_writer, WRITE_SAMPLE))(_writer, stream, sample), "WriteSample");
            }
            finally
            {
                Release(sample);
            }
        }
        finally
        {
            Release(buffer);
        }
    }

    /// <summary>
    /// Finishes the file. Returns what to say.
    /// </summary>
    public string Finish(string fileName)
    {
        lock (_lock)
        {
            if (_writer == 0 || _finished)
                return "";
            _finished = true;
            var hr = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(_writer, FINALIZE))(_writer);
            var seconds = (double)_frames / Fps;
            return hr >= 0 ? $"Saved {fileName} ({_frames} frames, {seconds:F1} s)" : $"Recording failed to finish (0x{hr:X8})";
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_writer != 0)
            {
                Release(_writer);
                _writer = 0;
                MFShutdown();
            }
        }
    }

    static nint Slot(nint com, int index) => (*(nint**)com)[index];

    static void Release(nint com)
    {
        if (com != 0)
            ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(com, RELEASE))(com);
    }

    static nint Type(Guid major, Guid sub)
    {
        Check(MFCreateMediaType(out var t), "MFCreateMediaType");
        SetGuid(t, MF_MT_MAJOR_TYPE, major);
        SetGuid(t, MF_MT_SUBTYPE, sub);
        return t;
    }

    static void Set32(nint attrs, Guid key, uint value) => Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(attrs, SET_UINT32))(attrs, &key, value), "SetUINT32");

    static void Set64(nint attrs, Guid key, ulong value) => Check(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Slot(attrs, SET_UINT64))(attrs, &key, value), "SetUINT64");

    static void SetGuid(nint attrs, Guid key, Guid value) => Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(attrs, SET_GUID))(attrs, &key, &value), "SetGUID");

    static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new COMException($"{what} failed (0x{hr:X8})", hr);
    }
}
