using System.IO.Compression;
using System.Net;
using System.Text;
using Cuedeck.Fixtures;
using Cuedeck.Input;
using Cuedeck.UI;
using Cuedeck.UI.Console;

namespace Cuedeck.Tests.UI;

/// <summary>
/// The Patch tab's profile buttons (New, Import, Edit, Delete), the profile editor, and the fixture library's online
/// sources, clicked through.
/// </summary>
public sealed class ProfilesUiTests : ConsoleHarness
{
    ProfileEditor Editor => Ui.Elements.OfType<ProfileEditor>().Single();

    LibraryDialog Library => Ui.Elements.OfType<LibraryDialog>().Single();

    Dropdown<FixtureProfile> ProfileChoice => Find<Dropdown<FixtureProfile>>(_ => true);

    /// <summary>
    /// Replaces what's in a text box, by clicking it, selecting all and typing.
    /// </summary>
    void Retype(TextBox box, string text)
    {
        Click(box);
        box.SelectAll();
        Ui.KeyDown(Key.Delete, Modifiers.None);
        Type(text);
    }

    TextBox BoxAfter(Element panel, string label)
    {
        var row = All(panel).OfType<Row>().First(r => r.Children.OfType<Label>().Any(l => l.Text == label));
        var at = row.Children.IndexOf(row.Children.OfType<Label>().First(l => l.Text == label));
        return row.Children.Skip(at).OfType<TextBox>().First();
    }

    [Fact]
    public void A_new_profile_is_made_saved_and_chosen()
    {
        Click(ButtonCalled("New…"));
        Assert.True(Editor.Visible);
        Retype(BoxAfter(Editor, "Manufacturer:"), "Acme");
        Retype(BoxAfter(Editor, "Model:"), "Duo Par");

        // The first channel's row, and a second.
        var first = Assert.Single(Editor.Rows);
        Retype(All(first).OfType<TextBox>().First(), "Intensity");
        Choose(All(first).OfType<Dropdown<string>>().Single(), "DIMMER");
        Click(ButtonCalled("Add Channel"));
        var second = Editor.Rows.Last();
        Choose(All(second).OfType<Dropdown<string>>().Single(), "STROBE");
        Retype(All(second).OfType<TextBox>().Last(), "0-9:Open, 10-255:Strobe");

        Click(ButtonCalled("Save Profile"));
        Assert.False(Editor.Visible);
        var p = Store.Profiles[^1];
        Assert.Equal(("acme_duo_par", "Acme", "Duo Par"), (p.Id, p.Manufacturer, p.Model));
        Assert.Equal([("Intensity", Roles.Dimmer), ("Ch 2", Roles.Strobe)], p.Channels(0).Select(c => (c.Name, c.Role)));
        Assert.Equal(["Open", "Strobe"], p.Channels(0)[1].Ranges.Select(r => r.Label));
        Assert.True(File.Exists(Store.FilePath("acme_duo_par")));
        Assert.Same(p, ProfileChoice.Selected);
        Assert.Equal("Saved profile 'Duo Par' (1 mode(s)).", Screen.StatusText);
    }

    [Fact]
    public void Modes_are_added_copied_and_checked_on_save()
    {
        Click(ButtonCalled("New…"));
        Retype(All(Editor.Rows.Single()).OfType<TextBox>().First(), "Dim");
        Click(ButtonCalled("Duplicate Mode"));
        Assert.Equal("Default (copy)", BoxAfter(Editor, "Mode:").Text);
        Assert.Equal("Dim", All(Editor.Rows.Single()).OfType<TextBox>().First().Text);
        Click(ButtonCalled("Add Mode"));
        Assert.Equal("Mode 3", BoxAfter(Editor, "Mode:").Text);
        Click(All(Editor.Rows.Single()).OfType<Button>().First(b => b.Text == "×")); // no channels left
        Click(ButtonCalled("Save Profile"));
        Assert.True(Editor.Visible);
        Assert.Contains(All(Editor).OfType<Label>(), l => l.Text == "Mode 'Mode 3' has no channels.");

        Click(ButtonCalled("Delete Mode"));
        Retype(BoxAfter(Editor, "Heads:"), "0, 0, 0 / 1, x");
        Click(ButtonCalled("Save Profile"));
        Assert.Contains(All(Editor).OfType<Label>(), l => l.Text == "Head line 2: \"x\" isn't a number.");
        Retype(BoxAfter(Editor, "Heads:"), "-0.1, 0, 0 / 0.1, 0, 0");
        Click(ButtonCalled("Save Profile"));
        var p = Store.Profiles[^1];
        Assert.Equal(["Default", "Default (copy)"], p.ModeNames);
        Assert.Equal([-0.1, 0.1], p.Physical.Heads.Select(h => h[0]));
    }

    [Fact]
    public void Channels_move_and_copy()
    {
        Click(ButtonCalled("New…"));
        Retype(All(Editor.Rows.Single()).OfType<TextBox>().First(), "A");
        Click(All(Editor.Rows.Single()).OfType<Button>().First(b => b.Text == "Copy"));
        Retype(All(Editor.Rows.Last()).OfType<TextBox>().First(), "B");
        Click(All(Editor.Rows.Last()).OfType<Button>().First(b => b.Text == "▲"));
        Assert.Equal(["B", "A"], Editor.Rows.Select(r => r.ChannelName));
    }

    [Fact]
    public void Editing_a_profile_updates_its_patched_fixtures()
    {
        var mine = Profiles.DimmedRgb();
        mine.Id = "mine";
        Store.Save(mine);
        Frame();
        Choose(ProfileChoice, mine.FullName);
        Click(ButtonCalled("Add Fixture"));
        var fixture = Assert.Single(Show.Patch.All);
        Show.Dmx.Universes[0].SetChannel(0, 77);

        Click(ButtonCalled("Edit…"));
        Assert.Equal("Edit Fixture Profile", All(Editor).OfType<Label>().First().Text);
        Retype(All(Editor.Rows.First()).OfType<TextBox>().First(), "Master");
        Click(ButtonCalled("Save Changes"));

        Assert.Equal("Master", fixture.Profile.Channels(0)[0].Name);
        Assert.Equal(77, Show.Dmx.Universes[0].GetChannel(0));
        Assert.Equal("Updated profile 'Dimmer RGB par' — 1 patched fixture updated.", Screen.StatusText);
        Assert.Equal("Master", Store.Profiles.Single(p => p.Id == "mine").Channels(0)[0].Name);
        Assert.Same(Store.Profiles.Single(p => p.Id == "mine"), ProfileChoice.Selected);
        // The fixture's row was built again from its new profile.
        Assert.Same(fixture.Profile, Find<FixtureRow>(_ => true).Fixture.Profile);
    }

    [Fact]
    public void A_built_in_is_kept_a_custom_one_deleted()
    {
        Click(ButtonCalled("Delete…"));
        Assert.EndsWith("is a built-in profile and can't be deleted.", Screen.StatusText);

        var mine = Profiles.Rgb();
        mine.Id = "mine";
        Store.Save(mine);
        Frame();
        Choose(ProfileChoice, "RGB");
        Click(ButtonCalled("Delete…"));
        Click(ButtonCalled("Delete"));
        Assert.Equal("Deleted profile 'RGB'.", Screen.StatusText);
        Assert.DoesNotContain(Store.Profiles, p => p.Id == "mine");
        Assert.Equal(Store.Profiles[0], ProfileChoice.Selected);
    }

    [Fact]
    public void A_fixture_file_is_imported_and_chosen()
    {
        var file = Dir.File("par.json");
        File.WriteAllText(file, """{ "name": "Par X", "availableChannels": { "Dimmer": {} }, "modes": [ { "name": "1ch", "channels": ["Dimmer"] } ] }""");
        Screen.Console.Patch.AskForFixtureFile = () => file;
        Click(ButtonCalled("Import…"));
        Assert.Equal("Imported 'Par X' — 1 mode(s), 1 ch.", Screen.StatusText);
        Assert.Equal("par_x", ProfileChoice.Selected.Id);

        Screen.Console.Patch.AskForFixtureFile = () => null; // cancelled
        Click(ButtonCalled("Import…"));
        Assert.Equal("Imported 'Par X' — 1 mode(s), 1 ch.", Screen.StatusText);
    }

    // ------------------------------------------------------- online library --

    const string Tree = "https://api.github.com/repos/OpenLightingProject/open-fixture-library/git/trees/master?recursive=1";
    const string Raw = "https://raw.githubusercontent.com/OpenLightingProject/open-fixture-library/master/fixtures/";

    [Fact]
    public void An_ofl_fixture_is_found_online_and_patched()
    {
        Web.Json(Tree, """{ "tree": [ { "path": "fixtures/acme/beam-1.json" }, { "path": "fixtures/acme/wash-2.json" } ] }""");
        Web.Json(Raw + "manufacturers.json", """{ "acme": { "name": "Acme" } }""");
        Web.Json(Raw + "acme/wash-2.json", """{ "name": "Wash 2", "availableChannels": { "Dimmer": {} }, "modes": [ { "name": "1ch", "channels": ["Dimmer"] } ] }""");

        Click(ButtonCalled("Library…"));
        Choose(Find<Dropdown<LibrarySource>>(_ => true), "OFL online");
        WaitFor(() => Library.Results.Items.Count == 2);
        Assert.Contains(All(Library).OfType<Label>(), l => l.Text == "2 of 2 OFL fixtures · 0 cached");

        Click(Find<TextBox>(b => b.Placeholder.StartsWith("Search", StringComparison.Ordinal)));
        Type("wash");
        Assert.Equal("Wash 2", Assert.Single(Library.Results.Items).Model);
        ClickAt(Library.Results.ScreenPosition + new Brutal.Numerics.float2(40, Library.Results.RowHeight / 2));
        WaitFor(() => ButtonCalled("Add to Patch").Enabled);
        Assert.Contains(All(Library).OfType<Label>(), l => l.Text == "fetched from Open Fixture Library");
        Click(ButtonCalled("Add to Patch"));
        Assert.Equal("acme-wash-2", ProfileChoice.Selected.Id);
    }

    [Fact]
    public void Ofl_online_says_when_github_cant_be_reached()
    {
        Click(ButtonCalled("Library…"));
        Choose(Find<Dropdown<LibrarySource>>(_ => true), "OFL online");
        WaitFor(() => Library.Results.EmptyText.StartsWith("Couldn't", StringComparison.Ordinal));
        Assert.Equal("Couldn't reach OFL: couldn't reach GitHub (HTTP 404)", Library.Results.EmptyText);
    }

    [Fact]
    public void Gdtf_share_asks_to_log_in_first()
    {
        const string login = "https://gdtf-share.com/apis/public/login.php";
        Web.Json(login, """{ "result": false, "error": "Wrong user or password" }""");
        Click(ButtonCalled("Library…"));
        Choose(Find<Dropdown<LibrarySource>>(_ => true), "GDTF-Share");
        var user = Find<TextBox>(b => b.Placeholder == "Username");
        var password = Find<TextBox>(b => b.Placeholder == "Password");
        Assert.True(password.Secret);

        Click(user);
        Type("someone");
        Click(password);
        Type("hunter2");
        Click(ButtonCalled("Log In"));
        WaitFor(() => All(Library).OfType<Label>().Any(l => l.Text == "Wrong user or password"));
        Assert.Equal("", password.Text); // not kept in the box either

        // Logged in, the list's fetched.
        Web.Json(login, """{ "result": true }""", HttpStatusCode.OK, "s=1");
        Web.Json("https://gdtf-share.com/apis/public/getList.php", """{ "list": [ { "rid": 7, "manufacturer": "Acme", "fixture": "Spot 9", "version": "1.2" } ] }""");
        Click(password);
        Type("hunter3");
        Ui.KeyDown(Key.Enter, Modifiers.None);
        WaitFor(() => Library.Results.Items.Count == 1);
        Assert.DoesNotContain(user, All(Library));
        Assert.Contains(All(Library).OfType<Label>(), l => l.Text == "1 of 1 GDTF-Share fixtures · 0 cached · logged in as someone");

        // Escape on the login box goes back to the bundled library.
        Choose(Find<Dropdown<LibrarySource>>(_ => true), "Bundled library");
        Assert.Equal(638, Library.Results.Items.Count);
    }

    [Fact]
    public void Cancelling_the_login_goes_back_to_the_bundled_library()
    {
        Click(ButtonCalled("Library…"));
        Choose(Find<Dropdown<LibrarySource>>(_ => true), "GDTF-Share");
        Ui.KeyDown(Key.Escape, Modifiers.None);
        Frame();
        Assert.True(Library.Visible);
        Assert.Equal(LibrarySource.Bundled, Library.Source);
        Assert.Equal(638, Library.Results.Items.Count);
    }
}
