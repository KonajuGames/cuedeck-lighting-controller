using Cuedeck.UI;
using Cuedeck.UI.Console;

namespace Cuedeck.Tests.UI;

/// <summary>
/// Importing from sLight through the screen: from the Options dialog, and offered at Cuedeck's first start when
/// sLight's folder's found.
/// </summary>
public sealed class SlightImportUiTests : ConsoleHarness
{
    readonly string _slight;
    readonly string _cuedeck;
    int _imported;

    public SlightImportUiTests()
    {
        _slight = Dir.File("sLight");
        _cuedeck = Dir.File("Cuedeck");
        Directory.CreateDirectory(Path.Combine(_slight, "fixture_profiles"));
        File.WriteAllText(Path.Combine(_slight, "fixture_profiles", "my_par.json"), "{\"id\":\"my_par\"}");
        Shell.SlightImportFolder = _cuedeck;
        Shell.SlightImported += () => _imported++;
    }

    [Fact]
    public void From_the_Options_dialog()
    {
        Shell.SlightFolders = [_slight];
        Ui.Elements.OfType<OptionsDialog>().Single().Show();
        Frame();
        Click(ButtonCalled("Import from sLight"));
        Assert.True(File.Exists(Path.Combine(_cuedeck, "fixture_profiles", "my_par.json")));
        Assert.Equal(1, _imported); // the app's stores read again
        Assert.Equal("Imported from sLight: 1 fixture profile.", Screen.StatusText);
        Assert.False(Ui.Elements.OfType<OptionsDialog>().Single().Visible); // closed, so the line's seen
    }

    [Fact]
    public void Not_offered_in_Options_without_sLight()
    {
        Shell.SlightFolders = [];
        Ui.Elements.OfType<OptionsDialog>().Single().Show();
        Frame();
        Assert.DoesNotContain(All(Find<OptionsDialog>(_ => true)).OfType<Button>(), b => b.Text == "Import from sLight");
    }

    [Fact]
    public void Offered_at_the_first_start_reopening_sLights_last_show()
    {
        File.WriteAllText(Path.Combine(_slight, "dmx_show.json"), File.ReadAllText(SaveAShow()));
        File.WriteAllText(Path.Combine(_slight, "last_show.json"), "{\"path\":\"user://dmx_show.json\"}");
        Shell.SlightFolders = [_slight];
        Shell.OfferSlightImport();
        Frame();
        var confirm = Ui.Elements.OfType<ConfirmDialog>().Single();
        Assert.True(confirm.Visible);
        Click(ButtonCalled("Import"));
        Assert.Equal(1, _imported);
        Assert.Equal(Path.Combine(_cuedeck, "dmx_show.json"), Shell.ShowPath);
        Assert.Single(Show.Patch.Universes[0].Fixtures);
        Assert.StartsWith("Imported from sLight: 1 fixture profile and its untitled show (dmx_show.json). Opened dmx_show.json.", Screen.StatusText);
    }

    [Fact]
    public void Not_now_leaves_it()
    {
        Shell.SlightFolders = [_slight];
        Shell.OfferSlightImport();
        Frame();
        Click(ButtonCalled("Not now"));
        Assert.False(Directory.Exists(_cuedeck));
        Assert.Equal(0, _imported);
    }

    [Fact]
    public void Not_offered_without_sLight()
    {
        Shell.SlightFolders = [];
        Shell.OfferSlightImport();
        Frame();
        Assert.False(Ui.Elements.OfType<ConfirmDialog>().Single().Visible);
    }

    /// <summary>
    /// A show with one fixture, saved; returns its file.
    /// </summary>
    string SaveAShow()
    {
        using var show = new Cuedeck.Show.LightingShow();
        show.Patch.Add(0, Store.Profiles[0], 1);
        var path = Dir.File("one.slight");
        File.WriteAllText(path, Cuedeck.Common.Json.Stringify(Cuedeck.Show.ShowFile.ToJson(show)));
        return path;
    }
}
