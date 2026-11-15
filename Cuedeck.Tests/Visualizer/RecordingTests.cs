using Cuedeck.Rendering;
using Cuedeck.Visualizer;

namespace Cuedeck.Tests.Visualizer;

/// <summary>
/// The visualizer's recording without the GPU: pacing its frames, the pictures and sound turned into what an MP4
/// wants, and Windows' writer making one (its software encoder: no GPU or sound card needed).
/// </summary>
public sealed class RecordingTests : IDisposable
{
    readonly TempFolder _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Frames_are_paced_at_30_a_second()
    {
        var pacer = new FramePacer();
        Assert.Equal(0, pacer.Due(0.02));
        Assert.Equal(1, pacer.Due(0.02)); // 40 ms: one due
        Assert.Equal(2, pacer.Due(1.0 / 15));
        Assert.Equal(FramePacer.MAX_CATCH_UP, pacer.Due(1.0)); // a stall: three now...
        Assert.Equal(FramePacer.MAX_CATCH_UP, pacer.Due(0)); // ...and the rest after, a few at a time
    }

    [Fact]
    public void Pictures_and_sound_as_an_MP4_wants_them()
    {
        Assert.Equal((1904, 1072), Pictures.VideoSize(1915, 1080));
        // RGBA to BGRA, cut to size.
        byte[] rgba = [1, 2, 3, 99, 4, 5, 6, 99, 7, 8, 9, 99, 10, 11, 12, 99];
        Assert.Equal(new byte[] { 3, 2, 1, 255, 6, 5, 4, 255 }, Pictures.ToBgra(rgba, 2, 2, 2, 1));
        Assert.Equal(new short[] { 0, short.MaxValue, -short.MaxValue, short.MaxValue }, Pictures.ToPcm16([0f, 1f, -1f, 2f]));
        Assert.Equal("shot_2026-11-15_21-04-05.png", Pictures.Stamped("shot", "png", new DateTime(2026, 11, 15, 21, 4, 5)));
    }

    [Fact]
    public void An_MP4_with_its_sound()
    {
        var path = _dir.File("rec.mp4");
        var (w, error) = Mp4Writer.Start(path, 64, 48, 30, 2000, (48000, 2));
        Assert.True(w is not null, error);
        using (w)
        {
            var frame = new byte[64 * 48 * 4];
            for (var i = 0; i < 15; i++)
            {
                Array.Fill(frame, (byte)(i * 16));
                w.PushFrame(frame);
                w.PushAudio(new short[1600 * 2]); // a thirtieth of a second
            }
            Assert.Equal(15, w.FrameCount);
            Assert.Equal(0.5, w.AudioSeconds, 6);
            Assert.StartsWith("Saved rec.mp4 (15 frames, 0.5 s)", w.Finish("rec.mp4"));
        }
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 1000);
        Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(bytes, 4, 4)); // an MP4
    }
}
