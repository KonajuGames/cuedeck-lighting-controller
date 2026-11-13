using Brutal.Numerics;
using Cuedeck.Show;

namespace Cuedeck.UI.Console;

/// <summary>
/// A colour for each fixture model in the patch, so its fixtures' rows and their channels in the DMX Output grid
/// match: sLight's dark teal, rust, plum and the rest, in the order the models first appear in the patch (repeating
/// past ten models).
/// </summary>
static class ModelColours
{
    static readonly float4[] Palette =
    [
        Colors.FromSrgb(0.12f, 0.32f, 0.33f), // teal
        Colors.FromSrgb(0.42f, 0.22f, 0.13f), // rust
        Colors.FromSrgb(0.33f, 0.20f, 0.38f), // plum
        Colors.FromSrgb(0.27f, 0.33f, 0.13f), // olive
        Colors.FromSrgb(0.42f, 0.15f, 0.24f), // wine
        Colors.FromSrgb(0.12f, 0.31f, 0.19f), // forest
        Colors.FromSrgb(0.40f, 0.31f, 0.10f), // ochre
        Colors.FromSrgb(0.22f, 0.20f, 0.42f), // indigo
        Colors.FromSrgb(0.30f, 0.24f, 0.19f), // brown
        Colors.FromSrgb(0.20f, 0.27f, 0.36f), // slate
    ];

    /// <summary>
    /// Each model in <paramref name="patch"/>, by its full name, to its colour.
    /// </summary>
    public static Dictionary<string, float4> Of(Patch patch)
    {
        var output = new Dictionary<string, float4>();
        foreach (var f in patch.All)
            output.TryAdd(f.Profile.FullName, Palette[output.Count % Palette.Length]);
        return output;
    }

    /// <summary>
    /// Each universe's channels to the colour of the fixture on them (a later fixture wins a channel two share).
    /// </summary>
    public static List<Dictionary<int, float4>> Channels(Patch patch)
    {
        var colours = Of(patch);
        var output = patch.Universes.Select(_ => new Dictionary<int, float4>()).ToList();
        foreach (var f in patch.All)
            for (var k = 0; k < f.Channels.Count; k++)
                output[f.Universe][f.Start + k] = colours[f.Profile.FullName];
        return output;
    }
}
