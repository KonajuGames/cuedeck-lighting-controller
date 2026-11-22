using Brutal.Numerics;
using Cuedeck.Common;
using Cuedeck.Perform;
using Cuedeck.Show;

namespace Cuedeck.UI.DjPerform;

/// <summary>
/// DJ Perform Mode's view: the OS2L connection with the DJ software and, for each deck, the song on it, whether it's
/// playing, where it is and whether its lighting timelines are loaded (and so playing into the lights); above them the
/// live flashes. The work is the <see cref="DjPerformer"/>'s, which runs while the view shows; this just shows it, each
/// frame. sLight's <c>DjPerformPanel</c>.
/// </summary>
sealed class DjPerformView : Region
{
    static float4 Live => AppTheme.OnPage(Colors.Hex(0x8CD98C));

    /// <summary>
    /// A lit flash button's colour (Colour's is its own).
    /// </summary>
    static readonly float4[] FlashOn = [Colors.FromSrgb(0.85f, 0.3f, 0.2f), Colors.FromSrgb(0.8f, 0.85f, 1.0f), Colors.FromSrgb(0.95f, 0.95f, 0.95f), Colors.FromSrgb(1, 0, 0)];

    /// <summary>
    /// The keys that hold each live flash, in <see cref="FlashKind"/> order.
    /// </summary>
    public static readonly string[] FlashKeys = ["B", "S", "W", "C"];

    readonly ScrollingPage _page;
    readonly Panel _content;
    readonly Label _connection;
    readonly Label _hint;
    readonly LiveFlash _flash;
    readonly Button[] _flashButtons = new Button[4];
    readonly Swatch _colour;
    readonly Checkbox _latch;

    /// <summary>
    /// A deck's row: its state, song, position and timelines.
    /// </summary>
    public sealed record DeckRow(Label State, Label Song, Label Position, Label Timelines);

    public DjPerformView(Fonts fonts, DjPerformer performer, LiveFlash flash, ColourPicker picker)
    {
        Performer = performer;
        _flash = flash;
        _content = new Panel { Spacing = 12 };
        _page = Add(new ScrollingPage(_content, 12));
        _content.Add(new Label(fonts.Heading, "DJ Perform Mode") { Color = Theme.Text });
        _connection = _content.Add(new Label(fonts.Body, "") { Color = Theme.Text, Wrap = true });

        // The live flashes: a big button each (held, or toggled with Latch), the Colour flash's colour, and Latch.
        var bar = _content.Add(new Row(8));
        bar.Add(new Label(fonts.Body, "Live:") { Color = Theme.Muted });
        for (var kind = 0; kind < 4; kind++)
        {
            var k = (FlashKind)kind;
            var name = LiveFlash.Names[kind];
            var b = bar.Add(
                new Button(fonts.Bold, $"{name} ({FlashKeys[kind]})")
                {
                    Padding = 13, // 44 tall, the label in the middle
                    MinSize = new float2(120, 0),
                    Tooltip = $"Hold for {name.ToLowerInvariant()} over everything playing (or press {FlashKeys[kind]}). With Latch on, click to turn it on and again to turn it off.",
                }
            );
            b.PressStarted += _ => flash.Press(k);
            b.PressEnded += _ => flash.Release(k);
            _flashButtons[kind] = b;
        }
        _colour = bar.Add(new Swatch { Tooltip = "The Colour flash's colour" });
        _colour.Clicked += _ => picker.Show(_colour.ScreenPosition + new float2(0, _colour.Size.Y + 4), flash.Colour, c => flash.Colour = c);
        _latch = bar.Add(new Checkbox(fonts.Body, "Latch") { Tooltip = "Flashes stay on when clicked, until clicked again. Turning Latch off lets go of them all." });
        _latch.Changed += c =>
        {
            flash.Latch = c.Checked;
            if (!c.Checked)
                flash.Clear();
        };

        // The decks.
        var grid = _content.Add(new Panel { Spacing = 8 });
        grid.Add(GridRow([.. new[] { "Deck", "State", "Song", "Position", "Timelines" }.Select(h => new Label(fonts.Body, h) { Color = Theme.Muted })]));
        for (var i = 0; i < DjPerformer.DECKS; i++)
        {
            var row = new DeckRow(new Label(fonts.Body, ""), new Label(fonts.Body, ""), new Label(fonts.Body, ""), new Label(fonts.Body, ""));
            Rows.Add(row);
            grid.Add(GridRow(new Label(fonts.Body, (i + 1).ToString()) { Color = Theme.Text }, row.State, row.Song, row.Position, row.Timelines));
        }

        _hint = _content.Add(new Label(fonts.Body, "") { Color = Theme.Muted, Wrap = true });
    }

    static readonly float[] ColumnWidths = [60, 150, 320, 90];

    static Row GridRow(params Label[] cells)
    {
        var row = new Row(24);
        for (var i = 0; i < cells.Length; i++)
        {
            if (i < ColumnWidths.Length)
            {
                cells[i].MaxWidth = ColumnWidths[i] - 8;
                row.Add(new FixedWidth(cells[i], ColumnWidths[i]));
            }
            else
                row.Add(cells[i]);
        }
        return row;
    }

    public DjPerformer Performer { get; }

    /// <summary>
    /// The DJ software's name, as the Options dialog has it.
    /// </summary>
    public Func<string> DjSoftwareName { get; set; } = () => "VirtualDJ";

    public List<DeckRow> Rows { get; } = [];
    public string ConnectionText => _connection.Text;
    public IReadOnlyList<Button> FlashButtons => _flashButtons;
    public Checkbox LatchBox => _latch;

    /// <summary>
    /// B, S, W and C hold the live flashes while the view shows (latched, a press toggles one); a key coming up lets go
    /// in any view, so one held across a view change isn't left on. Returns whether it was one of them.
    /// </summary>
    public bool FlashKey(Key key, bool down, Modifiers mods)
    {
        var kind = key switch
        {
            Key.B => FlashKind.Blackout,
            Key.S => FlashKind.Strobe,
            Key.W => FlashKind.White,
            Key.C => FlashKind.Colour,
            _ => (FlashKind)(-1),
        };
        if (kind < 0 || mods != Modifiers.None || down && !Visible)
            return false;
        if (down)
            _flash.Press(kind);
        else
            _flash.Release(kind);
        return true;
    }

    /// <summary>
    /// Shows the performer's state.
    /// </summary>
    public void Update()
    {
        var dj = DjSoftwareName();
        if (!Performer.IsActive)
            _connection.Text = "OS2L: off";
        else
        {
            var parts = new List<string> { "OS2L: " + Performer.Status };
            if (Performer.AdvertisingStatus != "")
                parts.Add(Performer.AdvertisingStatus);
            var n = Performer.ClientCount;
            parts.Add(n == 1 ? dj + " connected" : n > 1 ? $"{n} clients connected" : "waiting for " + dj);
            _connection.Text = string.Join("  ·  ", parts);
        }
        for (var i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i];
            var d = Performer.Deck(i + 1);
            if (d.Hidden || d.Path == "")
            {
                row.State.Text = d.Hidden ? "not in the skin" : "empty";
                row.Song.Text = row.Position.Text = row.Timelines.Text = "";
                row.Song.Tooltip = null;
                row.State.Color = Theme.Muted;
                continue;
            }
            var silent = d.Level is { } level && level <= DjPerformer.SILENT_LEVEL;
            row.State.Text = d.Playing ? "playing" + (silent ? " (muted)" : "") : "paused";
            row.State.Color = row.Timelines.Color = d.Live ? Live : Theme.Text;
            row.Song.Text = Path.GetFileNameWithoutExtension(Gd.GetFile(d.Path));
            row.Song.Color = Theme.Text;
            row.Song.Tooltip = d.Path;
            row.Position.Text = Clock(Performer.Position(d));
            row.Position.Color = Theme.Text;
            row.Timelines.Text = TimelinesText(d);
        }
        // The flashes on lit; the colour and Latch as the show has them.
        for (var kind = 0; kind < 4; kind++)
        {
            var b = _flashButtons[kind];
            b.Lit = _flash.IsOn((FlashKind)kind);
            var c = kind == (int)FlashKind.Colour ? Colors.FromSrgb(_flash.Colour.R, _flash.Colour.G, _flash.Colour.B) : FlashOn[kind];
            b.LitBackground = c;
            b.TextColor = b.Lit && Luminance(c) > 0.6f ? Colors.Hex(0x000000) : Theme.Text;
        }
        _colour.Colour = Colors.FromSrgb(_flash.Colour.R, _flash.Colour.G, _flash.Colour.B);
        _latch.Checked = _flash.Latch;
        _hint.Text =
            $"{dj} and Cuepoint find Cuedeck over the network (OS2L, advertised as \"Cuedeck\"), or point VirtualDJ's os2lDirectIp option at this machine. "
            + "A deck's lighting timelines (from DJ Edit Mode, saved with the show) play into the lights while it's playing with its volume up; "
            + "decks playing together are mixed: colour, dimmer and strobe highest-takes-precedence, pan and tilt averaged. "
            + "A song without timelines gets temporary ones: on the beat at once, then a show generated from the song once it's analysed "
            + "(saved with the show, marked orange in DJ Edit Mode's song list until you edit them). "
            + "The Live buttons (or B, S, W and C held down, or MIDI and OSC pads) flash over everything playing while held; with Latch on they stay on until pressed again.";
    }

    /// <summary>
    /// A colour's luminance as Godot works it out, from its sRGB components (the linear ones Cuedeck draws with put
    /// back).
    /// </summary>
    static float Luminance(float4 linear)
    {
        static float S(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - 0.055f;
        return 0.2126f * S(linear.X) + 0.7152f * S(linear.Y) + 0.0722f * S(linear.Z);
    }

    /// <summary>
    /// What the Timelines column says for a deck.
    /// </summary>
    public static string TimelinesText(Deck d)
    {
        switch (d.LoadState)
        {
            case LoadState.Loading:
                return d.Temporary ? "generating…" : "loading…";
            case LoadState.Loaded:
                var n = d.Tracks.Count;
                var text = (d.Live ? "playing" : "ready") + $" ({n} track{(n == 1 ? "" : "s")}";
                if (d.Analysing)
                    return text + $", temporary: on the beat only while the song's analysed — {Gd.RoundToInt(d.Progress * 100)}%)";
                text += d.Temporary ? ", temporary)" : ")";
                return text + (d.Note != "" ? " — " + d.Note : "");
            case LoadState.NoTimelines:
                return "none for this song" + (d.Note != "" ? $" (couldn't generate: {d.Note})" : "");
            case LoadState.NoFolder:
                return "show not saved (no timelines folder)";
        }
        return "";
    }

    static string Clock(double t)
    {
        var s = (int)Math.Max(t, 0);
        return $"{s / 60}:{s % 60:00}";
    }

    protected override void MeasureContent()
    {
        var w = Available.X - 24 - 12;
        _connection.MaxWidth = _hint.MaxWidth = Math.Max(w, 200);
        foreach (var r in Rows)
            r.Timelines.MaxWidth = Math.Max(w - ColumnWidths.Sum() - 4 * 24, 150);
        _page.Available = Available;
        _page.Measure();
    }

    /// <summary>
    /// The Colour flash's colour, to click for the colour picker.
    /// </summary>
    sealed class Swatch : Element
    {
        public float4 Colour { get; set; } = Colors.Hex(0xFF0000);

        public override bool IsInteractive => true;

        public override float2 Measure() => Size = new float2(44, 44);

        public override void Draw(Batch batch)
        {
            batch.AddRect(ScreenPosition, Size, Colour);
            batch.AddBorder(ScreenPosition, Size, IsHovered ? 2 : 1, IsHovered ? Theme.Focus : Theme.PanelBorder);
        }
    }
}
