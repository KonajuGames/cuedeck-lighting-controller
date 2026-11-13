using System.Globalization;
using Brutal.Numerics;
using Cuedeck.Show;

namespace Cuedeck.UI.Console;

/// <summary>
/// One fader's strip: its name, the fader and its level, what it's on, and the MIDI control it's mapped to.
/// </summary>
sealed class FaderStrip : Panel
{
    const float WIDTH = 120;

    readonly LightingShow _show;
    readonly Slider _slider;
    readonly Label _level;
    readonly Dropdown<FaderKind> _kind;
    readonly Dropdown<(string Label, string Target)> _target;
    readonly Button _learn;
    readonly Button _forget;

    public Fader Fader { get; }

    public FaderStrip(Fonts fonts, ContextMenu menu, LightingShow show, Fader fader)
    {
        _show = show;
        Fader = fader;
        Padding = 6;
        Spacing = 6;
        Background = Theme.PanelRaised;
        BorderColor = Theme.PanelBorder;
        BorderWidth = 1;
        MinSize = new float2(WIDTH + 12, 0);

        var top = Add(new Row(4));
        var name = top.Add(new TextBox(fonts.Small) { Width = WIDTH - 30, Text = fader.Name });
        name.TextChanged += t => fader.Name = t.Text;
        var remove = top.Add(new Button(fonts.Body, "×") { Padding = 3, MinSize = new float2(24, 0), Tooltip = "Remove this fader" });
        remove.Clicked += _ => show.Faders.Remove(show.Faders.Faders.IndexOf(fader));

        _slider = Add(new Slider { Orientation = Orientation.Vertical, Min = 0, Max = 1, Length = 150, Thickness = 28, ShowFill = true, DefaultValue = 0 });
        _slider.Changed += s => show.Faders.SetLevel(show.Faders.Faders.IndexOf(fader), s.Value);
        _level = Add(new Label(fonts.Body) { Color = Theme.Text });

        _kind = Add(
            new Dropdown<FaderKind>(fonts.Small)
            {
                Menu = menu,
                Width = WIDTH,
                Items = () => Enum.GetValues<FaderKind>(),
                Text = k => FaderBank.Kinds[(int)k],
            }
        );
        _kind.Chosen += k =>
        {
            var first = Targets(k).FirstOrDefault();
            show.Faders.SetTarget(show.Faders.Faders.IndexOf(fader), k, first.Target ?? "");
        };
        _target = Add(
            new Dropdown<(string Label, string Target)>(fonts.Small)
            {
                Menu = menu,
                Width = WIDTH,
                Items = () => Targets(fader.Kind),
                Text = t => t.Label,
            }
        );
        _target.Chosen += t => show.Faders.SetTarget(show.Faders.Faders.IndexOf(fader), fader.Kind, t.Target);

        _learn = Add(
            new Button(fonts.Small, "Learn")
            {
                Padding = 3,
                Tooltip = "MIDI Learn: click, then move a fader or knob on your controller (a CC or a pitch bend), or press a pad or key (a note: on is full, off is 0). Right-click to forget the mapping.",
            }
        );
        _learn.Clicked += _ =>
        {
            var i = show.Faders.Faders.IndexOf(fader);
            show.Faders.Learn(show.Faders.IsLearning(i) ? -1 : i);
        };
        _learn.SecondaryClicked += _ => fader.ForgetMidi();
        _forget = Add(new Button(fonts.Small, "Forget MIDI") { Padding = 3, Tooltip = "Forget its MIDI mapping" });
        _forget.Clicked += _ => fader.ForgetMidi();
    }

    /// <summary>
    /// What a fader of <paramref name="kind"/> can be on: what to call it, and its target.
    /// </summary>
    List<(string Label, string Target)> Targets(FaderKind kind) =>
        kind switch
        {
            FaderKind.Cue => [.. _show.Cues.Cues.Select((c, i) => ($"Cue {i + 1}", (i + 1).ToString(CultureInfo.InvariantCulture)))],
            FaderKind.Chase => [.. _show.Effects.Chases.Select(c => (c.Name, c.Name))],
            FaderKind.Group => [.. _show.Groups.Select(g => (g.Name, g.Name))],
            _ => [.. _show.Effects.Effects.Select(e => (e.Name, e.Name))],
        };

    public void Update()
    {
        if (!_slider.IsDragging)
            _slider.Value = (float)Fader.Level;
        _level.Text = $"{Math.Round(Fader.Level * 100).ToString(CultureInfo.InvariantCulture)}%";
        _kind.Selected = Fader.Kind;
        var targets = Targets(Fader.Kind);
        var found = targets.FirstOrDefault(t => t.Target.Equals(Fader.Target.Trim(), StringComparison.OrdinalIgnoreCase));
        // A target gone from the show (or not made yet) is kept, marked missing.
        _target.Selected = found.Label is not null ? found
            : Fader.Target != "" ? ($"{Fader.Target} (missing)", Fader.Target)
            : (targets.Count > 0 ? "(pick…)" : "(none to pick)", "");
        var learning = _show.Faders.IsLearning(_show.Faders.Faders.IndexOf(Fader));
        _learn.Text = learning ? "Move it…" : Fader.IsMapped ? Fader.MidiText : "Learn";
        _learn.Lit = learning;
        _forget.Visible = Fader.IsMapped;
    }
}

/// <summary>
/// The Faders tab: a strip per playback fader, side by side like a desk's and wrapping onto more rows.
/// </summary>
sealed class FadersTab : Panel, IWidthAware
{
    readonly Fonts _fonts;
    readonly ContextMenu _menu;
    readonly LightingShow _show;
    readonly Flow _strips;
    readonly Button _add;
    readonly Label _hint;
    List<Fader> _shown = [];

    public FadersTab(Fonts fonts, ContextMenu menu, LightingShow show)
    {
        _fonts = fonts;
        _menu = menu;
        _show = show;
        Spacing = 8;
        Add(new Label(fonts.Heading, "Playback Faders") { Color = Theme.Text });
        _add = Add(new Button(fonts.Body, "Add Fader"));
        _add.Clicked += _ =>
        {
            // A new fader starts on cue 1, if there is one.
            if (show.Faders.Add() is not null && show.Cues.Cues.Count > 0)
                show.Faders.SetTarget(show.Faders.Faders.Count - 1, FaderKind.Cue, "1");
        };
        _strips = Add(new Flow(6) { LineSpacing = 10 });
        _hint = Add(
            new Label(
                fonts.Small,
                "A Cue fader brings that cue's look up as a submaster (its intensity scaled by the fader, highest wins). "
                    + "A Chase fader runs the chase while it's up, at the fader's intensity. "
                    + "A Group fader is a group master: it scales the group's intensity down from full. "
                    + "An Effect fader runs the effect while it's up, faded by the fader (a movement's swing shrinks). "
                    + "Learn, then move a fader or knob on a MIDI controller (a CC or a pitch bend) or press a pad or key (a note: on full, off 0), to control it from there."
            )
            {
                Color = Theme.Faint,
                Wrap = true,
            }
        );
    }

    public float Width { get; set; } = 600;

    public void Update()
    {
        if (!_shown.SequenceEqual(_show.Faders.Faders))
        {
            _shown = [.. _show.Faders.Faders];
            _strips.Children.Clear();
            foreach (var f in _shown)
                _strips.Add(new FaderStrip(_fonts, _menu, _show, f));
        }
        foreach (var strip in _strips.Children.OfType<FaderStrip>())
            strip.Update();
        _add.Enabled = _show.Faders.Faders.Count < FaderBank.MaxFaders;
    }

    public override float2 Measure()
    {
        _strips.Width = Width;
        _hint.MaxWidth = Width;
        return base.Measure();
    }
}
