namespace Cuedeck.Timelines;

/// <summary>
/// One selected area: a time span across some rows, each named by <see cref="TimelineSelection.RowId"/>.
/// </summary>
sealed record SelectionBlock(IReadOnlyList<string> Rows, double T0, double T1)
{
    /// <summary>
    /// A block over <paramref name="rows"/>, its times in order.
    /// </summary>
    public static SelectionBlock Of(IReadOnlyList<string> rows, double t0, double t1) => new(rows, Math.Min(t0, t1), Math.Max(t0, t1));
}

/// <summary>
/// The selected areas of a song's timelines: blocks, each a time span across a set of rows. A row's named by its
/// track's key ("master", a fixture's uid, "&lt;uid&gt;/h&lt;n&gt;") and the row, so a block can cover rows of several
/// tracks and still means the same rows after the patch changes. sLight's <c>TimelineSelection</c>.
/// </summary>
sealed class TimelineSelection
{
    List<SelectionBlock> _blocks = [];

    // Each row's spans, worked out from the blocks the first time they're asked for after they change (asked per row,
    // per track, on every redraw and pointer move).
    Dictionary<string, List<(double T0, double T1)>>? _byRow;

    public event Action? Changed;

    public IReadOnlyList<SelectionBlock> Blocks => _blocks;

    public static string RowId(string trackKey, TimelineRow row) => $"{trackKey}|{(int)row}";

    public bool IsEmpty => _blocks.Count == 0;

    public void Clear()
    {
        if (_blocks.Count == 0)
            return;
        _blocks = [];
        _byRow = null;
        Changed?.Invoke();
    }

    public void SetBlocks(IEnumerable<SelectionBlock> blocks)
    {
        _blocks = [.. blocks];
        _byRow = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// The selected time spans on one row.
    /// </summary>
    public List<(double T0, double T1)> SpansFor(string trackKey, TimelineRow row)
    {
        if (_byRow is null)
        {
            _byRow = [];
            foreach (var b in _blocks)
            {
                foreach (var id in b.Rows)
                {
                    if (!_byRow.TryGetValue(id, out var spans))
                        _byRow[id] = spans = [];
                    spans.Add((b.T0, b.T1));
                }
            }
        }
        return _byRow.TryGetValue(RowId(trackKey, row), out var found) ? [.. found] : [];
    }

    /// <summary>
    /// Whether <paramref name="t"/> on a row is selected.
    /// </summary>
    public bool Has(string trackKey, TimelineRow row, double t) => SpansFor(trackKey, row).Any(s => t >= s.T0 && t <= s.T1);
}
