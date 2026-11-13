using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Cuedeck.Audio;

/// <summary>
/// Listens to a Windows audio input (a microphone or line-in) through WASAPI, in shared mode, keeping the latest
/// samples for <see cref="SoundEngine"/>. Nothing's played back: the input is only heard, never monitored.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
sealed class WasapiInput : IAudioCapture
{
    readonly CaptureBuffer _buffer = new();
    WasapiRecorder? _capture;
    float[] _l = [];
    float[] _r = [];

    public int SampleRate { get; private set; } = 48000;

    public string Error { get; private set; } = "";

    public IReadOnlyList<string> Devices()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            return [.. devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Select(d => d.FriendlyName)];
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return [];
        }
    }

    public bool Start(string device)
    {
        Stop();
        try
        {
            MMDevice? chosen = null;
            using var devices = new MMDeviceEnumerator();
            if (device != "")
                chosen = devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).FirstOrDefault(d => d.FriendlyName == device);
            chosen ??= devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            var capture = new WasapiRecorderBuilder().WithDevice(chosen).WithSharedMode().Build();
            capture.DataAvailable += (buffer, flags, _, _) =>
            {
                if ((flags & AudioClientBufferFlags.Silent) != 0)
                    buffer = new byte[buffer.Length]; // the device says it's silent: whatever's there isn't sound
                Heard(capture.WaveFormat, buffer);
            };
            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is { } x)
                    Error = x.Message;
            };
            _buffer.Clear();
            capture.StartRecording();
            SampleRate = capture.WaveFormat.SampleRate;
            _capture = capture;
            Error = "";
            return true;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            Error = e.Message;
            return false;
        }
    }

    /// <summary>
    /// Some samples, in the input's own format, split into left and right (mono heard on both).
    /// </summary>
    void Heard(WaveFormat format, ReadOnlySpan<byte> bytes)
    {
        var count = bytes.Length;
        var channels = Math.Max(1, format.Channels);
        var bytesPerSample = format.BitsPerSample / 8;
        var frames = count / (bytesPerSample * channels);
        if (_l.Length < frames)
        {
            _l = new float[frames];
            _r = new float[frames];
        }
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || format is WaveFormatExtensible ext && ext.SubFormat == FloatSubtype;
        for (var i = 0; i < frames; i++)
        {
            _l[i] = Sample(bytes, (i * channels) * bytesPerSample, isFloat, bytesPerSample);
            _r[i] = Sample(bytes, (i * channels + Math.Min(1, channels - 1)) * bytesPerSample, isFloat, bytesPerSample);
        }
        _buffer.Write(_l.AsSpan(0, frames), _r.AsSpan(0, frames));
    }

    /// <summary>
    /// KSDATAFORMAT_SUBTYPE_IEEE_FLOAT: a WAVE_FORMAT_EXTENSIBLE's 32-bit float samples.
    /// </summary>
    static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    static float Sample(ReadOnlySpan<byte> bytes, int at, bool isFloat, int size) =>
        (isFloat, size) switch
        {
            (true, 4) => BitConverter.ToSingle(bytes[at..]),
            (_, 2) => BitConverter.ToInt16(bytes[at..]) / 32768f,
            (_, 3) => (bytes[at] << 8 | bytes[at + 1] << 16 | bytes[at + 2] << 24) / 2147483648f,
            (false, 4) => BitConverter.ToInt32(bytes[at..]) / 2147483648f,
            _ => 0,
        };

    public void Latest(Span<float> left, Span<float> right) => _buffer.Latest(left, right);

    public void Stop()
    {
        if (_capture is null)
            return;
        try
        {
            _capture.StopRecording();
        }
        catch (InvalidOperationException) { }
        _capture.Dispose();
        _capture = null;
    }

    public void Dispose() => Stop();
}
