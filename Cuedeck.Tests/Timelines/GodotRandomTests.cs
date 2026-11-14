using System.Globalization;
using Cuedeck.Common;

namespace Cuedeck.Tests.Timelines;

/// <summary>
/// Godot's string hash and random number generator, pinned to what Godot 4.7 printed for the same seeds, so the
/// timeline generator makes sLight's shows.
/// </summary>
public class GodotRandomTests
{
    [Theory]
    [InlineData("song.mp3|0", 2023361094UL)]
    [InlineData("another.mp3|3", 1942121315UL)]
    [InlineData("x|0|roles", 2496484010UL)]
    [InlineData("Ä€😀|1", 807123522UL)]
    public void Strings_hash_as_in_godot(string s, ulong want) => Assert.Equal(want, GodotRandom.Hash(s));

    [Fact]
    public void A_seed_gives_godots_numbers()
    {
        var rng = new GodotRandom(GodotRandom.Hash("song.mp3|0"));
        var floats = Enumerable.Range(0, 6).Select(_ => rng.Randf().ToString("F9", CultureInfo.InvariantCulture));
        Assert.Equal(["0.727450728", "0.923840106", "0.867379785", "0.792109907", "0.052923944", "0.852238595"], floats);
        Assert.Equal("0.844563067", rng.RandfRange(0.75f, 0.95f).ToString("F9", CultureInfo.InvariantCulture));
        Assert.Equal((5, 0, 3), (rng.RandiRange(0, 7), rng.RandiRange(0, 2), rng.RandiRange(3, 3)));
    }
}
