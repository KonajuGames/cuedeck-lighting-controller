using System.Buffers.Binary;
using System.IO.Compression;

namespace Cuedeck.UI;

/// <summary>
/// Reads PNG files into RGBA pixels: straight (not premultiplied) alpha, sRGB, eight bits a channel, rows from the
/// top. It takes the common kinds: greyscale, RGB, palette and with alpha, at 8 or 16 bits a channel (palette at 1
/// to 8 bits), with transparency from a tRNS chunk. Interlaced images aren't, as they're rare for icons.
/// </summary>
static class Png
{
    static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    const int GREY = 0, RGB = 2, PALETTE = 3, GREY_ALPHA = 4, RGBA = 6;

    public readonly record struct Image(int Width, int Height, byte[] Pixels);

    /// <summary>
    /// Throws <see cref="InvalidDataException"/> for anything that isn't a PNG it can read.
    /// </summary>
    public static Image Decode(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Decode(memory.ToArray());
    }

    public static Image Decode(ReadOnlySpan<byte> file)
    {
        if (file.Length < Signature.Length || !file[..Signature.Length].SequenceEqual(Signature))
            throw new InvalidDataException("Not a PNG file");

        int width = 0, height = 0, depth = 0, kind = -1;
        byte[] palette = [];
        byte[]? transparency = null;
        using var compressed = new MemoryStream();

        var at = Signature.Length;
        while (at + 8 <= file.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(file[at..]);
            var type = System.Text.Encoding.ASCII.GetString(file.Slice(at + 4, 4));
            if (length < 0 || at + 12 + length > file.Length)
                throw new InvalidDataException("PNG chunk runs past the end of the file");
            var data = file.Slice(at + 8, length);
            at += 12 + length;

            switch (type)
            {
                case "IHDR":
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(data);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                    depth = data[8];
                    kind = data[9];
                    if (data[12] != 0)
                        throw new InvalidDataException("Interlaced PNGs aren't supported");
                    break;
                case "PLTE":
                    palette = data.ToArray();
                    break;
                case "tRNS":
                    transparency = data.ToArray();
                    break;
                case "IDAT":
                    compressed.Write(data);
                    break;
                case "IEND":
                    at = file.Length;
                    break;
            }
        }

        var channels = kind switch
        {
            GREY or PALETTE => 1,
            GREY_ALPHA => 2,
            RGB => 3,
            RGBA => 4,
            _ => throw new InvalidDataException($"Unknown PNG colour type {kind}"),
        };
        var allowed = kind == PALETTE ? depth is 1 or 2 or 4 or 8 : kind == GREY ? depth is 1 or 2 or 4 or 8 or 16 : depth is 8 or 16;
        if (width <= 0 || height <= 0 || !allowed)
            throw new InvalidDataException($"Unsupported PNG: {width}x{height}, colour type {kind} at {depth} bits");

        // Bytes per whole pixel (at least one, for filtering), and per row.
        var bitsPerPixel = channels * depth;
        var pixelBytes = Math.Max(1, bitsPerPixel / 8);
        var rowBytes = (width * bitsPerPixel + 7) / 8;

        compressed.Position = 0;
        var raw = new byte[(rowBytes + 1) * height];
        using (var inflate = new ZLibStream(compressed, CompressionMode.Decompress))
            inflate.ReadExactly(raw);

        Unfilter(raw, rowBytes, pixelBytes, height);

        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var row = raw.AsSpan(y * (rowBytes + 1) + 1, rowBytes);
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                switch (kind)
                {
                    case PALETTE:
                    {
                        var index = Sample(row, x, depth);
                        if (index * 3 + 2 >= palette.Length)
                            throw new InvalidDataException("PNG palette index out of range");
                        (pixels[o], pixels[o + 1], pixels[o + 2]) = (palette[index * 3], palette[index * 3 + 1], palette[index * 3 + 2]);
                        pixels[o + 3] = transparency is not null && index < transparency.Length ? transparency[index] : (byte)255;
                        break;
                    }
                    case GREY:
                    {
                        var value = Sample(row, x, depth);
                        var max = (1 << Math.Min(depth, 8)) - 1;
                        var grey = (byte)(depth == 16 ? value : value * 255 / max);
                        pixels[o] = pixels[o + 1] = pixels[o + 2] = grey;
                        var transparent = transparency is { Length: >= 2 } && Sample16(row, x, depth) == BinaryPrimitives.ReadUInt16BigEndian(transparency);
                        pixels[o + 3] = transparent ? (byte)0 : (byte)255;
                        break;
                    }
                    default:
                    {
                        // Greyscale with alpha, RGB and RGBA, at 8 bits or the top byte of 16.
                        var step = depth / 8;
                        var source = x * channels * step;
                        if (kind == GREY_ALPHA)
                        {
                            pixels[o] = pixels[o + 1] = pixels[o + 2] = row[source];
                            pixels[o + 3] = row[source + step];
                        }
                        else
                        {
                            (pixels[o], pixels[o + 1], pixels[o + 2]) = (row[source], row[source + step], row[source + 2 * step]);
                            pixels[o + 3] = kind == RGBA ? row[source + 3 * step] : (byte)255;
                            if (kind == RGB && transparency is { Length: >= 6 } && depth == 8
                                && row[source] == transparency[1] && row[source + 1] == transparency[3] && row[source + 2] == transparency[5])
                                pixels[o + 3] = 0;
                        }
                        break;
                    }
                }
            }
        }
        return new Image(width, height, pixels);
    }

    /// <summary>
    /// The value of pixel <paramref name="x"/>'s one channel, at depths of 8 bits or fewer packed from the high bit,
    /// or the top byte at 16.
    /// </summary>
    static int Sample(ReadOnlySpan<byte> row, int x, int depth)
    {
        if (depth == 16)
            return row[x * 2];
        if (depth == 8)
            return row[x];
        var bit = x * depth;
        return (row[bit / 8] >> (8 - depth - bit % 8)) & ((1 << depth) - 1);
    }

    static int Sample16(ReadOnlySpan<byte> row, int x, int depth) =>
        depth == 16 ? BinaryPrimitives.ReadUInt16BigEndian(row[(x * 2)..]) : Sample(row, x, depth);

    /// <summary>
    /// Undoes each row's filter in place: every row starts with a byte saying how it was predicted from the pixel
    /// to the left, the one above, or both.
    /// </summary>
    static void Unfilter(byte[] raw, int rowBytes, int pixelBytes, int height)
    {
        for (var y = 0; y < height; y++)
        {
            var start = y * (rowBytes + 1);
            var filter = raw[start];
            var row = raw.AsSpan(start + 1, rowBytes);
            var above = y > 0 ? raw.AsSpan(start - rowBytes, rowBytes) : default;
            for (var i = 0; i < rowBytes; i++)
            {
                int left = i >= pixelBytes ? row[i - pixelBytes] : 0;
                int up = y > 0 ? above[i] : 0;
                int upLeft = y > 0 && i >= pixelBytes ? above[i - pixelBytes] : 0;
                row[i] += filter switch
                {
                    0 => 0,
                    1 => (byte)left,
                    2 => (byte)up,
                    3 => (byte)((left + up) / 2),
                    4 => (byte)Paeth(left, up, upLeft),
                    _ => throw new InvalidDataException($"Unknown PNG filter {filter}"),
                };
            }
        }
    }

    static int Paeth(int left, int up, int upLeft)
    {
        var estimate = left + up - upLeft;
        var (toLeft, toUp, toUpLeft) = (Math.Abs(estimate - left), Math.Abs(estimate - up), Math.Abs(estimate - upLeft));
        return toLeft <= toUp && toLeft <= toUpLeft ? left : toUp <= toUpLeft ? up : upLeft;
    }
    /// <summary>
    /// Writes RGBA pixels as a PNG (eight bits a channel, unfiltered).
    /// </summary>
    public static byte[] Encode(Image image)
    {
        using var file = new MemoryStream();
        file.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)image.Height);
        header[8] = 8;
        header[9] = RGBA;
        WriteChunk(file, "IHDR", header);

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var rowBytes = image.Width * 4;
            for (var y = 0; y < image.Height; y++)
            {
                deflate.WriteByte(0);
                deflate.Write(image.Pixels, y * rowBytes, rowBytes);
            }
        }
        WriteChunk(file, "IDAT", compressed.ToArray());
        WriteChunk(file, "IEND", []);
        return file.ToArray();
    }

    static void WriteChunk(Stream file, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length);
        file.Write(word);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        file.Write(typeBytes);
        file.Write(data);
        var crc = Crc32(Crc32(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(word, crc);
        file.Write(word);
    }

    static readonly uint[] CrcTable = [.. Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    })];

    static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    /// <summary>
    /// <paramref name="image"/> scaled to <paramref name="width"/> by <paramref name="height"/>, blending the four
    /// nearest pixels (bilinear).
    /// </summary>
    public static Image Resize(Image image, int width, int height)
    {
        var output = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Clamp((y + 0.5) * image.Height / height - 0.5, 0, image.Height - 1);
            var y0 = (int)sy;
            var y1 = Math.Min(y0 + 1, image.Height - 1);
            var fy = sy - y0;
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp((x + 0.5) * image.Width / width - 0.5, 0, image.Width - 1);
                var x0 = (int)sx;
                var x1 = Math.Min(x0 + 1, image.Width - 1);
                var fx = sx - x0;
                for (var c = 0; c < 4; c++)
                {
                    double P(int px, int py) => image.Pixels[(py * image.Width + px) * 4 + c];
                    var top = P(x0, y0) + (P(x1, y0) - P(x0, y0)) * fx;
                    var bottom = P(x0, y1) + (P(x1, y1) - P(x0, y1)) * fx;
                    output[(y * width + x) * 4 + c] = (byte)Math.Clamp(Math.Round(top + (bottom - top) * fy), 0, 255);
                }
            }
        }
        return new Image(width, height, output);
    }
}
