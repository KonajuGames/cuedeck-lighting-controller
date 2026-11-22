using Brutal.Numerics;
using Cuedeck.Fixtures;
using Cuedeck.Show;
using Cuedeck.UI;
using Cuedeck.UI.Console;

namespace Cuedeck.Tests.UI;

/// <summary>
/// Text cut short to fit, with an ellipsis: a label's, and a fixture row's model, which gives way to the category and
/// buttons on its right rather than pushing them on. Measured with a built atlas, so text is as wide as it's drawn
/// (the other UI tests' fonts measure everything as nothing).
/// </summary>
public sealed class TruncationTests
{
    static readonly FontAtlas Atlas = new();
    static readonly Images Pictures = Images.Load(Atlas);
    static readonly Fonts Measured = MakeFonts();

    static Fonts MakeFonts()
    {
        var regular = FontAtlas.FindSystemFont(bold: false) ?? FontAtlas.FindSystemFont()!;
        var bold = FontAtlas.FindSystemFont(bold: true) ?? regular;
        var fonts = new Fonts(Atlas.Add(regular, 12), Atlas.Add(regular, 14), Atlas.Add(bold, 14), Atlas.Add(bold, 18), Atlas.Add(bold, 26));
        Atlas.Build();
        return fonts;
    }

    const string LONG = "Moving Head Beam Spot Wash Hybrid with a Very Long Model Name Indeed, Mark Two";

    [Fact]
    public void A_label_too_long_for_its_width_ends_in_an_ellipsis()
    {
        var label = new Label(Measured.Body, LONG) { MaxWidth = 150 };
        label.Measure();
        Assert.True(label.Truncated);
        Assert.EndsWith("…", label.Shown);
        Assert.StartsWith("Moving Head", label.Shown);
        Assert.True(label.Size.X <= 150);
        Assert.True(Measured.Body.Measure(label.Shown + "x").X > 150 - Measured.Body.Measure("…").X); // as much as fits

        // One that fits is left alone; given more room, a cut one's shown whole again.
        label.MaxWidth = 2000;
        label.Measure();
        Assert.False(label.Truncated);
        Assert.Equal(LONG, label.Shown);

        // Too narrow even for the ellipsis: nothing.
        label.MaxWidth = 2;
        label.Measure();
        Assert.Equal("", label.Shown);

        // Each line of several is cut on its own; wrapping still wraps rather than cutting.
        var lines = new Label(Measured.Body, "Short\n" + LONG) { MaxWidth = 150 };
        lines.Measure();
        Assert.StartsWith("Short\nMoving Head", lines.Shown);
        Assert.EndsWith("…", lines.Shown);
        var wrapped = new Label(Measured.Body, LONG) { MaxWidth = 150, Wrap = true };
        wrapped.Measure();
        Assert.False(wrapped.Truncated);
        Assert.Contains('\n', wrapped.Shown);
    }

    [Fact]
    public void Text_fitted_to_a_width_ends_in_an_ellipsis_where_its_cut()
    {
        var font = Measured.Body;
        Assert.Equal("Short", font.Fit("Short", 200));
        var cut = font.Fit(LONG, 120);
        Assert.EndsWith(Font.ELLIPSIS, cut);
        Assert.True(font.Measure(cut).X <= 120);
        Assert.DoesNotContain(" …", cut); // no space left before it
        Assert.Equal("", font.Fit(LONG, 1));
        Assert.Equal("Do 🎵…", font.Fit("Do 🎵🎵🎵🎵🎵🎵🎵🎵🎵🎵🎵🎵", font.Measure("Do 🎵…").X)); // never half a character
    }

    [Fact]
    public void A_fixture_rows_long_model_is_cut_short_rather_than_pushing_its_buttons_off()
    {
        using var show = new LightingShow();
        var fixture = show.Patch.Add(0, Profiles.Simple("long", LONG, ("Dimmer", "DIMMER")), 0, name: "Hybrid");
        var dialogs = MainScreenTests.LaidOut(new float2(1280, 720)).Screen.Dialogs;
        var row = new FixtureRow(Measured, Pictures.Fixture, new ContextMenu(Measured.Body), dialogs, show, fixture) { Width = 760 };
        row.Measure();
        row.Arrange(float2.Zero);

        Assert.Equal(760, row.Size.X); // no wider than it's given
        var remove = All(row).OfType<Button>().Single(b => b.Tooltip == "Remove from the patch");
        Assert.Equal(row.Size.X - 9, remove.ScreenPosition.X + remove.Size.X); // inside its padding and border
        var model = All(row).OfType<Label>().Single(l => l.Text == LONG);
        Assert.True(model.Truncated);
        Assert.EndsWith("…", model.Shown);
        var category = All(row).OfType<Dropdown<string>>().Single();
        Assert.True(model.ScreenPosition.X + model.Size.X <= category.ScreenPosition.X); // clear of the controls

        // Wide enough, it's shown whole.
        row.Width = 1600;
        row.Measure();
        Assert.False(model.Truncated);
    }

    static IEnumerable<Element> All(Element e) =>
        e is Panel p ? p.Children.SelectMany(All).Prepend(e) : [e];
}
