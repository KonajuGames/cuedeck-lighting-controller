using Brutal.Numerics;

namespace Cuedeck.UI;

/// <summary>
/// Cuedeck's own colours, on top of the ones the Cue apps share (<see cref="Theme"/>): the Sound tab's, the song
/// timeline's and DJ Edit Mode's.
/// </summary>
static class AppTheme
{
    /// <summary>
    /// <paramref name="color"/>, deepened on a light look, for a bright colour (a status's, a missing song's) that
    /// would wash out on white. The timeline, waveforms, meters and visualizer stay dark in every look, so their
    /// colours are left as they are.
    /// </summary>
    public static float4 OnPage(float4 color) => Theme.IsLight ? Theme.Mix(color, Colors.Hex(0x000000), 0.4f) : color;

    /// <summary>
    /// The Sound tab's meter: a bar for each band, on its background (sLight's colours).
    /// </summary>
    public static readonly float4 SoundBass = Colors.Hex(0xF2594D);
    public static readonly float4 SoundMid = Colors.Hex(0x66D973);
    public static readonly float4 SoundTreble = Colors.Hex(0x66A6F2);
    public static readonly float4 SoundLevel = Colors.Hex(0xE6D959);
    public static readonly float4 SoundMeter = Colors.Hex(0x17171C);

    /// <summary>
    /// The spectrogram's heat scale, from silence (a near-black blue) through purple, red and orange to loud (near
    /// white), as sLight's.
    /// </summary>
    public static readonly float4[] Heat =
    [
        Colors.Hex(0x050512),
        Colors.Hex(0x290D61),
        Colors.Hex(0x730F85),
        Colors.Hex(0xD12966),
        Colors.Hex(0xFA7A26),
        Colors.Hex(0xFFDB52),
        Colors.Hex(0xFFFFF5),
    ];

    // Song timeline (DJ Edit Mode): the waveform, structure bar, bar ruler and tracks, in sLight's colours.
    public static readonly float4 TimelinePlayhead = Colors.Hex(0xFF3838);
    public static readonly float4 TimelineBeatLine = Colors.Hex(0xFFFFFF, 0.16f);
    public static readonly float4 TimelineBarLine = Colors.Hex(0xFFFFFF, 0.5f);
    public static readonly float4 WaveBass = Colors.Hex(0xFF9E33);
    public static readonly float4 WaveMid = Colors.Hex(0x59CC6B);
    public static readonly float4 WaveAir = Colors.Hex(0x66B8FF);
    public static readonly float4 WaveNeutral = Colors.Hex(0x8C8C99);
    public static readonly float4 WaveEmpty = Colors.Hex(0x1A1A21);
    public static readonly float4 WaveProgress = Colors.Hex(0x334C73);
    public static readonly float4 WaveHint = Colors.Hex(0x80808C);
    public static readonly float4 WaveDownbeat = Colors.Hex(0xFFFFFF, 0.22f);
    public static readonly float4 SectionUnknown = Colors.Hex(0x4C4C59);
    public static readonly float4 StructureEmpty = Colors.Hex(0x1F1F24);
    public static readonly float4 SectionBorder = Colors.Hex(0x000000, 0.35f);
    public static readonly float4 SectionBorderHot = Colors.Hex(0xFFFFFF, 0.9f);
    public static readonly float4 SectionText = Colors.Hex(0xFFFFFF, 0.9f);
    public static readonly float4 StructureTick = Colors.Hex(0xFFFFFF, 0.25f);
    public static readonly float4 RulerBackground = Colors.Hex(0x212126);
    public static readonly float4 RulerTick = Colors.Hex(0xFFFFFF, 0.35f);
    public static readonly float4 RulerPhraseTick = Colors.Hex(0xFFFFFF, 0.8f);
    public static readonly float4 RulerBeatTick = Colors.Hex(0xFFFFFF, 0.22f);
    public static readonly float4 RulerText = Colors.Hex(0xC7C7D1);
    public static readonly float4 ViewBoxDim = Colors.Hex(0x000000, 0.35f);
    public static readonly float4 ViewBoxFill = Colors.Hex(0xFFFFFF, 0.1f);
    public static readonly float4 ViewBoxEdge = Colors.Hex(0xFFFFFF, 0.85f);
    public static readonly float4 TrackRowA = Colors.Hex(0x1A1A1F);
    public static readonly float4 TrackRowB = Colors.Hex(0x1F1F24);
    public static readonly float4 TrackSeparator = Colors.Hex(0x000000, 0.6f);
    public static readonly float4 TrackRowName = Colors.Hex(0xFFFFFF, 0.28f);
    public static readonly float4 TrackLine = Colors.Hex(0xFFFFFF, 0.9f);
    public static readonly float4 TrackGhostLine = Colors.Hex(0xFFFFFF, 0.55f);
    public static readonly float4 TrackGhostDim = Colors.Hex(0x000000, 0.35f);
    public static readonly float4 TrackStrobe = Colors.Hex(0xFFFFFF, 0.9f);
    public static readonly float4 TrackSelectFill = Colors.Hex(0x5999FF, 0.28f);
    public static readonly float4 TrackSelectEdge = Colors.Hex(0x8CC7FF, 0.9f);
    public static readonly float4 TrackSilenced = Colors.Hex(0x0D0D12, 0.55f);
    public static readonly float4 TrackGridBar = Colors.Hex(0xFFFFFF, 0.22f);
    public static readonly float4 TrackGridBeat = Colors.Hex(0xFFFFFF, 0.07f);
    public static readonly float4 TrackPositionGone = Colors.Hex(0x8C8C99);
    public static readonly float4 TrackMovement = Colors.Hex(0xBFD9FF, 0.9f);
    public static readonly float4 PlateFill = Colors.Hex(0x33333D);
    public static readonly float4 PlateBorder = Colors.Hex(0x000000, 0.6f);
    public static readonly float4 PlateText = Colors.Hex(0xD9D9E6);
    public static readonly float4 PlateButtonOff = Colors.Hex(0x000000, 0.35f);
    public static readonly float4 PlateMuteOn = Colors.Hex(0xF27333);
    public static readonly float4 PlateSoloOn = Colors.Hex(0xF2D940);
    public static readonly float4 PlateButtonTextOn = Colors.Hex(0x14141A);

    // DJ Edit Mode's music browser: a missing file's row, the "has timelines" mark (orange for temporary ones), and a
    // song's regeneration's progress behind its row.
    public static readonly float4 MusicMissing = Colors.Hex(0xFF8080);
    public static readonly float4 TimelineMark = Colors.Hex(0x73B8F2);
    public static readonly float4 TemporaryMark = Colors.Hex(0xFA9E40);
    public static readonly float4 RegenerateProgress = Colors.Hex(0x73B8F2, 0.28f);

    /// <summary>
    /// Each kind of section's colour in sRGB (the structure bar lightens it by the section's energy), as sLight's.
    /// </summary>
    public static readonly Dictionary<string, Common.Rgb> SectionColours = new()
    {
        ["Intro"] = new(0.20f, 0.35f, 0.75f),
        ["Verse"] = new(0.20f, 0.55f, 0.55f),
        ["Chorus"] = new(0.85f, 0.45f, 0.15f),
        ["Bridge"] = new(0.55f, 0.25f, 0.70f),
        ["Build"] = new(0.55f, 0.55f, 0.58f),
        ["Drop"] = new(0.80f, 0.20f, 0.20f),
        ["Outro"] = new(0.22f, 0.28f, 0.55f),
    };
}
