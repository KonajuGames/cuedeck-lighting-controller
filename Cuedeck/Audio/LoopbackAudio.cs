using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Cuedeck.Audio;

/// <summary>
/// What the computer's playing, captured as it's heard (WASAPI loopback on the default output): the visualizer
/// recording's sound, the music in DJ Edit Mode or the DJ software's in DJ Perform Mode. Calls back on its own
/// thread with interleaved float samples in the output's own rate and channels. sLight tapped its own mix instead.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
sealed class LoopbackAudio : IDisposable
{
    readonly WasapiRecorder _capture;

    LoopbackAudio(WasapiRecorder capture) => _capture = capture;

    /// <summary>
    /// Starts capturing, or null (nothing to capture from).
    /// </summary>
    public static (IDisposable Capture, int Rate, int Channels)? Start(Action<float[]> heard)
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            var output = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var capture = new WasapiRecorderBuilder().WithDevice(output).WithLoopbackCapture().WithSharedMode().Build();
            var format = capture.WaveFormat;
            var channels = Math.Max(1, format.Channels);
            var size = format.BitsPerSample / 8;
            var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible ext && ext.SubFormat == FloatSubtype;
            capture.DataAvailable += (buffer, flags, _, _) =>
            {
                var count = buffer.Length / size;
                var samples = new float[count - count % channels];
                var silent = (flags & AudioClientBufferFlags.Silent) != 0;
                if (!silent)
                    for (var i = 0; i < samples.Length; i++)
                        samples[i] = (isFloat, size) switch
                        {
                            (true, 4) => BitConverter.ToSingle(buffer[(i * 4)..]),
                            (_, 2) => BitConverter.ToInt16(buffer[(i * 2)..]) / 32768f,
                            (false, 4) => BitConverter.ToInt32(buffer[(i * 4)..]) / 2147483648f,
                            _ => 0,
                        };
                heard(samples);
            };
            capture.StartRecording();
            return (new LoopbackAudio(capture), format.SampleRate, channels);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// KSDATAFORMAT_SUBTYPE_IEEE_FLOAT: a WAVE_FORMAT_EXTENSIBLE's 32-bit float samples.
    /// </summary>
    static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    public void Dispose()
    {
        try
        {
            _capture.StopRecording();
        }
        catch (InvalidOperationException) { }
        _capture.Dispose();
    }
}
