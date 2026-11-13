using System.Globalization;

namespace Cuedeck.Fixtures;

/// <summary>
/// How the profile editor shows a channel's range slots and a fixture's head positions as text, and reads them back,
/// as sLight's editor did.
/// </summary>
static class ProfileText
{
    /// <summary>
    /// The categories the editor offers, each with its name; "" lets the 3D view guess from the channels.
    /// </summary>
    public static readonly (string Key, string Name)[] Categories =
    [
        ("", "Auto (from channels)"),
        ("moving_head", "Moving head"),
        ("wash", "Wash"),
        ("par", "Par"),
        ("beam", "Beam"),
        ("strip", "Strip / bar"),
        ("blinder", "Blinder / strobe"),
        ("generic", "Generic"),
    ];

    /// <summary>
    /// Range slots from "0-9:Open, 10-19:Red:#f00": a span, then an optional label and colour. A part without a dash
    /// is skipped.
    /// </summary>
    public static List<RangeSlot> ParseRanges(string text)
    {
        var output = new List<RangeSlot>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim();
            if (p == "")
                continue;
            var bits = p.Split(':');
            var span = bits[0].Trim();
            var dash = span.IndexOf('-');
            if (dash == -1)
                continue;
            output.Add(
                new RangeSlot(
                    Common.Gd.ToInt(span[..dash].Trim()),
                    Common.Gd.ToInt(span[(dash + 1)..].Trim()),
                    bits.Length > 1 ? bits[1].Trim() : "",
                    bits.Length > 2 ? bits[2].Trim() : ""
                )
            );
        }
        return output;
    }

    /// <summary>
    /// Range slots as "0-9:Open, 10-19:Red:#f00".
    /// </summary>
    public static string FormatRanges(IEnumerable<RangeSlot> ranges) =>
        string.Join(
            ", ",
            ranges.Select(r =>
            {
                var s = $"{r.Lo}-{r.Hi}";
                if (r.Label != "" || r.Color != "")
                    s += ":" + r.Label;
                if (r.Color != "")
                    s += ":" + r.Color;
                return s;
            })
        );

    /// <summary>
    /// The edited slots, each given the gobo picture of the original slot with its label (as the text has no room
    /// for pictures), and its colour too if it has none.
    /// </summary>
    public static List<RangeSlot> CarryImages(IReadOnlyList<RangeSlot> edited, IReadOnlyList<RangeSlot> originals)
    {
        var output = new List<RangeSlot>(edited.Count);
        foreach (var slot in edited)
        {
            var carried = slot;
            if (slot.Image == "")
            {
                var want = slot.Label.Trim().ToLowerInvariant();
                if (originals.FirstOrDefault(o => o.Image != "" && o.Label.Trim().ToLowerInvariant() == want) is { } orig)
                    carried = slot with { Image = orig.Image, Color = slot.Color != "" ? slot.Color : orig.Color };
            }
            output.Add(carried);
        }
        return output;
    }

    /// <summary>
    /// A fixture's heads as text: one "x, y, z, rx, ry, rz" line each, leaving the rotation off when it's all zero.
    /// </summary>
    public static string FormatHeads(IEnumerable<double[]> heads) =>
        string.Join(
            "\n",
            heads.Select(h =>
            {
                var values = h.Length < 6 || (h[3] == 0 && h[4] == 0 && h[5] == 0) ? h.Take(3) : h;
                return string.Join(", ", values.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)));
            })
        );

    /// <summary>
    /// Heads from the editor's text: one line each of x, y, z (and optionally rotation x, y, z), split by commas or
    /// semicolons; blank lines skipped. Null and why, if a line can't be read.
    /// </summary>
    public static (List<double[]>? Heads, string Error) ParseHeads(string text)
    {
        var heads = new List<double[]>();
        var n = 0;
        foreach (var line in text.Split('\n'))
        {
            n++;
            if (line.Trim() == "")
                continue;
            var values = new List<double>();
            foreach (var part in line.Replace(';', ',').Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = part.Trim();
                if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v))
                    return (null, $"Head line {n}: \"{p}\" isn't a number.");
                values.Add(v);
            }
            if (values.Count != 3 && values.Count != 6)
                return (null, $"Head line {n} needs x, y, z (and optionally rot x, y, z) — it has {values.Count} value(s).");
            heads.Add(values.Count == 3 ? [values[0], values[1], values[2], 0, 0, 0] : [.. values]);
        }
        return (heads, "");
    }

    /// <summary>
    /// What to say once an edited profile's gone to the fixtures patched with it.
    /// </summary>
    public static string UpdateMessage(string name, int updated, int repatch)
    {
        var text = $"Updated profile '{name}'";
        if (updated > 0)
            text += $" — {updated} patched fixture{(updated == 1 ? "" : "s")} updated";
        if (repatch > 0)
            text += $"{(updated > 0 ? ";" : " —")} {repatch} need{(repatch == 1 ? "s" : "")} a re-patch for the channel count change (beam and pan/tilt apply now)";
        return text + ".";
    }
}
