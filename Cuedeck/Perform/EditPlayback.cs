using Cuedeck.Dmx;
using Cuedeck.Show;
using Cuedeck.Timelines;

namespace Cuedeck.Perform;

/// <summary>
/// DJ Edit Mode's open song played into the rig: its timelines (edits included, as they stand) at the playhead, the
/// way DJ Perform Mode would play them, as a layer of the DMX output (added when it's made). While the song plays, and
/// while it's paused with its timeline showing (holding the look at the playhead, following it as it's moved); nothing
/// in the other views (the Positions editor included) unless it's playing, and nothing while DJ Perform Mode is on, as
/// the live decks own the rig then. Never the highlighted fixtures (the Positions editor's): they keep their white and
/// aim. The screen says how the song stands through the functions. sLight's <c>_edit_playback_layer</c>.
/// </summary>
sealed class EditPlayback : IDisposable
{
    readonly DmxOutput _dmx;
    readonly Patch _patch;
    readonly PositionLibrary _positions;
    readonly SongTimelines _timelines;
    readonly LayerProvider _layer;

    public EditPlayback(DmxOutput dmx, Patch patch, PositionLibrary positions, SongTimelines timelines)
    {
        (_dmx, _patch, _positions, _timelines) = (dmx, patch, positions, timelines);
        _layer = Layer;
        dmx.LayerProviders.Add(_layer);
    }

    /// <summary>
    /// Whether the open song's loaded to play.
    /// </summary>
    public Func<bool> CanPlay { get; set; } = () => false;

    public Func<bool> IsPlaying { get; set; } = () => false;

    /// <summary>
    /// Whether DJ Edit Mode is showing the song's timeline.
    /// </summary>
    public Func<bool> IsShown { get; set; } = () => false;

    /// <summary>
    /// The playhead, in seconds.
    /// </summary>
    public Func<double> PlaybackPosition { get; set; } = () => 0;

    /// <summary>
    /// DJ Perform Mode, which has the rig while it's on.
    /// </summary>
    public DjPerformer? Performer { get; set; }

    public IReadOnlyList<Dictionary<int, int>> Layer(int n)
    {
        if (!CanPlay() || !(IsPlaying() || IsShown()) || Performer?.IsActive == true)
            return SongOutput.Empty(n);
        var fixtures = _patch.All.Where(f => !_patch.IsHighlighted(f)).Select(PerformFixture.Of).ToList();
        return SongOutput.Of(_timelines.LiveTracks(), PlaybackPosition(), fixtures, DjPerformer.Bases(_dmx, n), n, _positions);
    }

    public void Dispose() => _dmx.LayerProviders.Remove(_layer);
}
