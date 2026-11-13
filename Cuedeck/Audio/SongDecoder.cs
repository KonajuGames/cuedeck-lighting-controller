using System.Buffers.Binary;
using System.Text;
using Brutal.SdlApi.Mixer;

namespace Cuedeck.Audio;

/// <summary>
/// Decodes a song file whole, to interleaved float samples: WAV here (sLight's own reader, ported), everything else
/// through <see cref="AudioDecoder"/> (SDL_mixer for MP3, OGG and FLAC; Media Foundation for AAC in .m4a and .mp4 on
/// Windows).
/// </summary>
static class SongDecoder
{
    static readonly object InitLock = new();
    static bool _mixerReady;

    /// <summary>
    /// Decodes <paramref name="path"/>. Throws <see cref="InvalidDataException"/> (or SDL_mixer's and Media
    /// Foundation's own) when it can't.
    /// </summary>
    public static PcmAudio Decode(string path)
    {
        if (Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            return DecodeWav(File.ReadAllBytes(path));
        EnsureMixer();
        return AudioDecoder.Decode(path).Audio;
    }

    /// <summary>
    /// SDL_mixer wants starting before its decoders work; once, for the app.
    /// </summary>
    static void EnsureMixer()
    {
        lock (InitLock)
        {
            if (_mixerReady)
                return;
            SdlMixer.Init();
            _mixerReady = true;
        }
    }

    /// <summary>
    /// A RIFF WAV's samples: PCM at 8, 16, 24 or 32 bits, or IEEE float at 32 or 64, any number of channels,
    /// WAVE_FORMAT_EXTENSIBLE too. sLight's <c>dec_wav.c</c>.
    /// </summary>
    public static PcmAudio DecodeWav(ReadOnlySpan<byte> d)
    {
        if (d.Length < 44 || Encoding.ASCII.GetString(d[..4]) != "RIFF" || Encoding.ASCII.GetString(d.Slice(8, 4)) != "WAVE")
            throw new InvalidDataException("not a WAV file");
        int format = 0, channels = 0, bits = 0, rate = 0;
        int dataAt = -1, dataLength = 0;
        var pos = 12;
        while (pos + 8 <= d.Length)
        {
            var id = Encoding.ASCII.GetString(d.Slice(pos, 4));
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(d[(pos + 4)..]), (uint)(d.Length - pos - 8));
            var body = d.Slice(pos + 8, size);
            if (id == "fmt " && size >= 16)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
                if (format == 0xFFFE && size >= 26)
                    format = BinaryPrimitives.ReadUInt16LittleEndian(body[24..]); // extensible: the subformat's first two bytes
            }
            else if (id == "data")
            {
                dataAt = pos + 8;
                dataLength = size;
            }
            pos += 8 + size + (size & 1); // chunks are word-aligned
        }
        if (dataAt < 0 || channels == 0 || rate == 0 || bits == 0)
            throw new InvalidDataException("a WAV without its format or data");
        if (format is not (1 or 3) || bits / 8 < 1)
            throw new InvalidDataException("a WAV that isn't PCM or float");
        var bytes = bits / 8;
        var frames = dataLength / (bytes * channels);
        if (frames < 1)
            throw new InvalidDataException("a WAV with no samples");
        var samples = new float[frames * channels];
        var data = d.Slice(dataAt, dataLength);
        for (var i = 0; i < samples.Length; i++)
        {
            var s = data.Slice(i * bytes, bytes);
            samples[i] = (format, bits) switch
            {
                (3, 64) => (float)BinaryPrimitives.ReadDoubleLittleEndian(s),
                (3, _) => BinaryPrimitives.ReadSingleLittleEndian(s),
                (_, 8) => (s[0] - 128) * (1f / 128f),
                (_, 16) => BinaryPrimitives.ReadInt16LittleEndian(s) * (1f / 32768f),
                (_, 24) => (s[0] << 8 | s[1] << 16 | s[2] << 24) >> 8 is var v ? v * (1f / 8388608f) : 0,
                (_, 32) => BinaryPrimitives.ReadInt32LittleEndian(s) * (1f / 2147483648f),
                _ => throw new InvalidDataException($"a {bits}-bit WAV"),
            };
        }
        return new PcmAudio(samples, channels, rate);
    }
}
