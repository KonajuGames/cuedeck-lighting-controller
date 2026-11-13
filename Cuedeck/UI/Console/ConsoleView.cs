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

    readonly TabView _left;
    readonly TabView _right;

    public PatchTab Patch { get; }

    public ConsoleView(Fonts fonts, ContextMenu menu, LightingShow show, ProfileStore profiles)
    {
        _left = Add(new TabView(fonts.Body));
        _left.AddTab("Cues", new Placeholder(fonts.Body, "The cue list comes next."));
        _left.AddTab("Chases", new Placeholder(fonts.Body, "Chases are coming."));
        _left.AddTab("Effects", new Placeholder(fonts.Body, "Effects are coming."));
        _left.AddTab("Sound", new Placeholder(fonts.Body, "The Sound tab comes with live audio (phase 3)."));
        _left.AddTab("Groups", new Placeholder(fonts.Body, "Groups are coming."));

        _right = Add(new TabView(fonts.Body));
        Patch = new PatchTab(fonts, menu, show, profiles);
        _right.AddTab("Patch", Patch);
        _right.AddTab("3D Visualizer", new Placeholder(fonts.Body, "The 3D Visualizer comes in phase 6."));
        _right.AddTab("DMX Output", new Placeholder(fonts.Body, "DMX Output is coming."));
        _right.AddTab("Faders", new Placeholder(fonts.Body, "Faders are coming."));
        _right.AddTab("Palettes", new Placeholder(fonts.Body, "Palettes are coming."));
    }

    public void Update()
    {
        if (_right.SelectedPage == Patch)
            Patch.Update();
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
