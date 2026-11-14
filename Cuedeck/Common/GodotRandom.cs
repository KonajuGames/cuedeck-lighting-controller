namespace Cuedeck.Common;

/// <summary>
/// Godot's <c>RandomNumberGenerator</c> (PCG32, with Godot's own <c>randf</c>), so a seed gives the same numbers in
/// order as it gave sLight: the timeline generator's shows depend on it.
/// </summary>
sealed class GodotRandom
{
    /// <summary>
    /// PCG's default increment, which Godot seeds with.
    /// </summary>
    const ulong DEFAULT_INC = 1442695040888963407UL;

    ulong _state;
    readonly ulong _inc;

    /// <summary>
    /// <c>rng.seed = seed</c>: pcg32_srandom_r.
    /// </summary>
    public GodotRandom(ulong seed)
    {
        _state = 0;
        _inc = (DEFAULT_INC << 1) | 1;
        Next();
        _state += seed;
        Next();
    }

    /// <summary>
    /// Godot's <c>hash()</c> of a string: djb2 over its code points, as a positive int.
    /// </summary>
    public static ulong Hash(string s)
    {
        uint h = 5381;
        foreach (var rune in s.EnumerateRunes())
            h = (h << 5) + h + (uint)rune.Value;
        return h;
    }

    /// <summary>
    /// pcg32_random_r.
    /// </summary>
    public uint Next()
    {
        var old = _state;
        _state = unchecked(old * 6364136223846793005UL + _inc);
        var xorshifted = (uint)(((old >> 18) ^ old) >> 27);
        var rot = (int)(old >> 59);
        return (xorshifted >> rot) | (xorshifted << (-rot & 31));
    }

    /// <summary>
    /// <c>randf()</c>: 0 to 1, as Godot works it out from two draws.
    /// </summary>
    public float Randf()
    {
        var protoExpOffset = Next();
        if (protoExpOffset == 0)
            return 0;
        return MathF.ScaleB((float)(Next() | 0x80000001u), -32 - System.Numerics.BitOperations.LeadingZeroCount(protoExpOffset));
    }

    /// <summary>
    /// <c>randf_range(from, to)</c>, in 32-bit floats as Godot does it.
    /// </summary>
    public float RandfRange(float from, float to) => Randf() * (to - from) + from;

    /// <summary>
    /// <c>randi_range(from, to)</c>: both ends included, without bias (pcg32_boundedrand_r).
    /// </summary>
    public int RandiRange(int from, int to)
    {
        if (from == to)
            return from;
        var bound = (uint)Math.Abs(from - to) + 1u;
        var threshold = unchecked(0u - bound) % bound;
        while (true)
        {
            var r = Next();
            if (r >= threshold)
                return (int)(r % bound) + Math.Min(from, to);
        }
    }
}
