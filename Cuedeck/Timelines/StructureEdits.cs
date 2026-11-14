using Cuedeck.Audio;

namespace Cuedeck.Timelines;

/// <summary>
/// Corrections to a song's analysed structure, as the structure bar makes them: moving the border between two
/// sections, splitting one, deleting one (a neighbour grows to fill its place) and relabelling one. They change the
/// analysis's sections in place; the caller then tells the timelines (<see cref="SongTimelines.StructureEdited"/>).
/// sLight's <c>StructureBar</c>'s edits.
/// </summary>
static class StructureEdits
{
    /// <summary>
    /// The kinds of section, in the bar's menu's order.
    /// </summary>
    public static readonly string[] Labels = ["Intro", "Verse", "Chorus", "Bridge", "Build", "Drop", "Outro"];

    /// <summary>
    /// Where a dragged border or a split lands: the nearest bar (the default, as a border almost always falls on
    /// one), the nearest beat, or exactly there. Unsnapped without the grid.
    /// </summary>
    public static double SnapTime(SongAnalysis a, double t, SnapMode snap) =>
        snap switch
        {
            SnapMode.Bar => SongTimelines.Nearest(a.Downbeats, t),
            SnapMode.Beat => SongTimelines.Nearest(a.BeatTimes, t),
            _ => t,
        };

    /// <summary>
    /// The border between sections <paramref name="i"/> and <paramref name="i"/> + 1 dragged to
    /// <paramref name="t"/>: snapped, and kept between the first's start and the second's end.
    /// </summary>
    public static void MoveBorder(SongAnalysis a, int i, double t, SnapMode snap)
    {
        var secs = a.Sections;
        if (i < 0 || i + 1 >= secs.Count)
            return;
        double lo = secs[i].Start, hi = secs[i + 1].End;
        var at = Math.Clamp(SnapTime(a, Math.Clamp(t, lo, hi), snap), lo, hi);
        secs[i] = secs[i] with { End = at };
        secs[i + 1] = secs[i + 1] with { Start = at };
    }

    /// <summary>
    /// A border dropped (between sections <paramref name="i"/> and <paramref name="i"/> + 1): either side left with
    /// no length is deleted, its neighbour already covering its span.
    /// </summary>
    public static void DropBorder(SongAnalysis a, int i)
    {
        var secs = a.Sections;
        foreach (var k in new[] { i + 1, i })
        {
            if (k >= 0 && k < secs.Count && secs.Count > 1 && secs[k].End - secs[k].Start < 0.02)
            {
                secs.RemoveAt(k);
                return;
            }
        }
    }

    /// <summary>
    /// Splits section <paramref name="idx"/> at <paramref name="t"/> (snapped) into two of its kind. Returns whether
    /// it did (not too near either end).
    /// </summary>
    public static bool Split(SongAnalysis a, int idx, double t, SnapMode snap)
    {
        var secs = a.Sections;
        if (idx < 0 || idx >= secs.Count)
            return false;
        var seg = secs[idx];
        var at = SnapTime(a, Math.Clamp(t, seg.Start, seg.End), snap);
        if (at - seg.Start < 0.02 || seg.End - at < 0.02)
            return false;
        secs[idx] = seg with { End = at };
        secs.Insert(idx + 1, seg with { Start = at });
        return true;
    }

    /// <summary>
    /// Deletes section <paramref name="idx"/>, the one before it (or after, for the first) growing to fill its place.
    /// The last section stays. Returns whether it did.
    /// </summary>
    public static bool Delete(SongAnalysis a, int idx)
    {
        var secs = a.Sections;
        if (secs.Count <= 1 || idx < 0 || idx >= secs.Count)
            return false;
        if (idx > 0)
            secs[idx - 1] = secs[idx - 1] with { End = secs[idx].End };
        else
            secs[idx + 1] = secs[idx + 1] with { Start = secs[idx].Start };
        secs.RemoveAt(idx);
        return true;
    }

    public static bool Relabel(SongAnalysis a, int idx, string label)
    {
        if (idx < 0 || idx >= a.Sections.Count || a.Sections[idx].Label == label)
            return false;
        a.Sections[idx] = a.Sections[idx] with { Label = label };
        return true;
    }
}
