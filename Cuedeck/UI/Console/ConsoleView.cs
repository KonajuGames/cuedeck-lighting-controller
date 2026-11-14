using Brutal.Numerics;
using Cuedeck.Fixtures;
using Cuedeck.Show;

namespace Cuedeck.UI.Console;

/// <summary>
/// The console: the playback tabs (Cues, Chases, Effects, Sound, Groups) on the left, and the rig's tabs (Patch, 3D
/// Visualizer, DMX Output, Faders, Palettes) filling the rest.
/// </summary>
sealed class ConsoleView : Region
{
    const float GAP = 8;
    const float LEFT_WIDTH = 380;

    readonly Dialogs _dialogs;
    readonly TabView _left;
    readonly TabView _right;

    public PatchTab Patch { get; }
    public CuesTab Cues { get; }
    public ChasesTab Chases { get; }
    public EffectsTab Effects { get; }
    public SoundTab Sound { get; }
    public GroupsTab Groups { get; }
    public DmxOutputTab DmxOutput { get; }
    public FadersTab Faders { get; }
    public PalettesTab Palettes { get; }

    /// <summary>
    /// Something a tab wants said on the status line.
    /// </summary>
    public event Action<string>? Status;

    /// <summary>
    /// The 3D Visualizer tab.
    /// </summary>
    public Visualizer.VisualizerView Visualizer { get; }

    public ConsoleView(Fonts fonts, ContextMenu menu, Dialogs dialogs, LightingShow show, ProfileStore profiles)
    {
        _dialogs = dialogs;
        _left = Add(new TabView(fonts.Body));
        Cues = new CuesTab(fonts, show);
        _left.AddTab("Cues", Cues);
        Chases = new ChasesTab(fonts, menu, show);
        _left.AddTab("Chases", new ScrollingPage(Chases));
        Effects = new EffectsTab(fonts, menu, show);
        _left.AddTab("Effects", new ScrollingPage(Effects));
        Sound = new SoundTab(fonts, menu, show);
        _left.AddTab("Sound", new ScrollingPage(Sound));
        Groups = new GroupsTab(fonts, show);
        _left.AddTab("Groups", new ScrollingPage(Groups));

        foreach (var say in new Action<Action<string>>[] { a => Cues.Status += a, a => Chases.Status += a, a => Effects.Status += a, a => Sound.Status += a, a => Groups.Status += a })
            say(m => Status?.Invoke(m));

        _right = Add(new TabView(fonts.Body));
        Patch = new PatchTab(fonts, menu, dialogs, show, profiles);
        _right.AddTab("Patch", Patch);
        Visualizer = new Visualizer.VisualizerView(fonts, menu, dialogs.Prompt, show);
        Visualizer.IsShown = () => Visible && _right.SelectedPage == Visualizer;
        _right.AddTab("3D Visualizer", Visualizer);
        DmxOutput = new DmxOutputTab(fonts, show);
        _right.AddTab("DMX Output", DmxOutput);
        Faders = new FadersTab(fonts, menu, show);
        _right.AddTab("Faders", new ScrollingPage(Faders));
        Palettes = new PalettesTab(fonts, menu, dialogs, show);
        _right.AddTab("Palettes", new ScrollingPage(Palettes));
        Palettes.Status += m => Status?.Invoke(m);
    }

    public void Update()
    {
        _dialogs.Library.Update();
        _dialogs.Triggers.Update();
        if (_left.SelectedPage == Cues)
            Cues.Update();
        if (_left.SelectedPage is ScrollingPage { Content: ChasesTab chases })
            chases.Update();
        if (_left.SelectedPage is ScrollingPage { Content: EffectsTab effects })
            effects.Update();
        if (_left.SelectedPage is ScrollingPage { Content: SoundTab sound })
            sound.Update();
        if (_left.SelectedPage is ScrollingPage { Content: GroupsTab groups })
            groups.Update();
        if (_right.SelectedPage == Visualizer)
            Visualizer.Update();
        if (_right.SelectedPage == Patch)
            Patch.Update();
        if (_right.SelectedPage == DmxOutput)
            DmxOutput.Update();
        if (_right.SelectedPage is ScrollingPage { Content: FadersTab faders })
            faders.Update();
        if (_right.SelectedPage is ScrollingPage { Content: PalettesTab palettes })
            palettes.Update();
    }

    protected override void MeasureContent()
    {
        var height = Available.Y - 2 * GAP;
        _left.Available = new float2(LEFT_WIDTH, height);
        _right.Available = new float2(MathF.Max(200, Available.X - LEFT_WIDTH - 3 * GAP), height);
        _left.Measure();
        _right.Measure();
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        _left.Arrange(position + new float2(GAP, GAP));
        _right.Arrange(position + new float2(2 * GAP + LEFT_WIDTH, GAP));
    }
}
