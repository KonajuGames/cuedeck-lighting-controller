using System.Globalization;
using Brutal.Numerics;
using Cuedeck.Show;

namespace Cuedeck.UI.Console;

/// <summary>
/// The console's top bar: the grand master, run mode, tap tempo, Sending and Blackout All. The show's file commands,
/// adding and removing universes, and the views are in the menu bar above it.
/// </summary>
sealed class TopBar : Panel
{
    readonly LightingShow _show;
    readonly Slider _master;
    readonly Label _masterValue;
    readonly Dropdown<RunMode> _runMode;
    readonly Label _tempo;
    readonly Button _tempoStop;
    readonly Button _sending;
    readonly Flow _top;

    /// <summary>
    /// Whether frames go out on the wire; otherwise they're only worked out (for the 3D view). The show's, so
    /// controller feedback can follow it.
    /// </summary>
    public bool Sending
    {
        get => _show.Sending;
        set => _show.Sending = value;
    }

    public event Action? BlackoutAll;
    public event Action<string>? Status;

    public float Width
    {
        get => _top.Width;
        set => _top.Width = value;
    }

    public TopBar(Fonts fonts, ContextMenu menu, LightingShow show)
    {
        _show = show;
        Padding = 8;
        Spacing = 6;
        Background = Theme.Background;

        _top = Add(new Flow(8));
        _top.Add(new Label(fonts.Body, "Grand Master:") { Color = Theme.Muted });
        _master = _top.Add(new Slider { Min = 0, Max = 1, Length = 150, DefaultValue = 1, ShowFill = true, Value = 1 });
        _master.Changed += s => show.Dmx.Master = s.Value;
        _masterValue = _top.Add(new Label(fonts.Body, "100%") { Color = Theme.Text });
        _top.Add(new Spacer(8));
        _top.Add(new Label(fonts.Body, "Run Mode:") { Color = Theme.Muted });
        _runMode = _top.Add(
            new Dropdown<RunMode>(fonts.Body)
            {
                Menu = menu,
                Width = 150,
                Items = () => [RunMode.Cues, RunMode.SoundReactive],
                Text = m => m == RunMode.SoundReactive ? "Sound Reactive" : "Cue Mode",
            }
        );
        _runMode.Chosen += SetRunMode;
        var tap = _top.Add(new Button(fonts.Body, "Tap") { Tooltip = "Tap Tempo (T)" });
        tap.Clicked += _ => show.Tempo.Tap();
        _tempo = _top.Add(new Label(fonts.Body, "— BPM") { Color = Theme.Text });
        _tempoStop = _top.Add(new Button(fonts.Body, "×") { Padding = 4, Tooltip = "Stop the tempo clock", Visible = false });
        _tempoStop.Clicked += _ => show.Tempo.Stop();
        _top.Add(new Spacer(8));
        _sending = _top.Add(new Button(fonts.Body, "Sending") { Lit = true, Tooltip = "Put the frames on the wire" });
        _sending.Clicked += _ => Sending = !Sending;
        var blackout = _top.Add(new Button(fonts.Body, "Blackout All"));
        blackout.Clicked += _ => BlackoutAll?.Invoke();
    }

    /// <summary>
    /// Puts the show in Cue Mode or Sound Reactive, and says what that means on the status line.
    /// </summary>
    public void SetRunMode(RunMode mode)
    {
        _show.RunMode = mode;
        Status?.Invoke(
            mode == RunMode.SoundReactive ? "Sound Reactive — monitoring the audio input. Arm reactors in the Sound tab." : "Cue Mode — cue list drives playback."
        );
    }

    public void Update()
    {
        if (!_master.IsDragging)
            _master.Value = (float)_show.Dmx.Master;
        _masterValue.Text = $"{Math.Round(_show.Dmx.Master * 100).ToString(CultureInfo.InvariantCulture)}%";
        _runMode.Selected = _show.RunMode;
        _tempo.Text = _show.Tempo.Describe();
        _tempo.Color = Theme.Mix(Theme.Text, Theme.Accent, (float)_show.Tempo.BeatPulse);
        _tempoStop.Visible = _show.Tempo.Running;
        _sending.Lit = Sending;
        _sending.Text = Sending ? "Sending" : "Not sending";
    }

    public override void Draw(Batch batch)
    {
        base.Draw(batch);
        batch.AddRect(ScreenPosition + new float2(0, Size.Y - 1), new float2(Size.X, 1), Theme.PanelBorder);
    }
}
