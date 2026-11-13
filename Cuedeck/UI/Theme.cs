using Brutal.Numerics;

namespace Cuedeck.UI;

/// <summary>
/// The app's colours, in one place so the screen hangs together: near-black panels, muted text for labels and orange
/// for the app's own accents.
/// </summary>
static class Theme
{
    public static readonly float4 Background = Colors.Hex(0x121318);
    public static readonly float4 TitleBar = Colors.Hex(0x0B0C0F);
    public static readonly float4 Panel = Colors.Hex(0x1B1C22);
    public static readonly float4 PanelRaised = Colors.Hex(0x23242C);
    public static readonly float4 PanelBorder = Colors.Hex(0x2E3039);
    public static readonly float4 Well = Colors.Hex(0x0E0F12);

    public static readonly float4 Text = Colors.Hex(0xE4E5EC);
    public static readonly float4 Muted = Colors.Hex(0x8C8E9C);
    public static readonly float4 Faint = Colors.Hex(0x5A5C68);

    public static readonly float4 Accent = Colors.Hex(0xEB7828);
    public static readonly float4 Focus = Colors.Hex(0x7AB8FF);

    /// <summary>
    /// Something live and well: output being sent, DJ software connected.
    /// </summary>
    public static readonly float4 Connected = Colors.Hex(0x3CC25A);

    /// <summary>
    /// Something wrong that needs seeing to.
    /// </summary>
    public static readonly float4 Warning = Colors.Hex(0xFF4D3A);

    /// <summary>
    /// <paramref name="color"/> with its alpha multiplied by <paramref name="alpha"/>.
    /// </summary>
    public static float4 Fade(float4 color, float alpha) => color * new float4(1, 1, 1, alpha);

    /// <summary>
    /// Mixes from <paramref name="from"/> to <paramref name="to"/> by <paramref name="t"/>, from 0 to 1.
    /// </summary>
    public static float4 Mix(float4 from, float4 to, float t) => from + (to - from) * Math.Clamp(t, 0, 1);

    /// <summary>
    /// A colour from a hue, saturation and value, each from 0 to 1, worked out in sRGB (as picked) and made linear
    /// for the swapchain.
    /// </summary>
    public static float4 Hsv(float hue, float saturation, float value)
    {
        float Channel(float n)
        {
            var k = (n + hue * 6) % 6;
            return value - value * saturation * MathF.Max(0, MathF.Min(MathF.Min(k, 4 - k), 1));
        }
        float Linear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        return new float4(Linear(Channel(5)), Linear(Channel(3)), Linear(Channel(1)), 1);
    }
}
