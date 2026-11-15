namespace Cuedeck.Tests;

/// <summary>
/// Each file dialog's own last folder, kept between runs (sLight's <c>RecentDirs</c>): the show folder, the fixture
/// imports', the glTF models' and the MVR rigs' don't have to share one "last folder". sLight had no tests of it.
/// </summary>
public sealed class RecentFoldersTests : IDisposable
{
    readonly TempFolder _dir = new();

    public void Dispose() => _dir.Dispose();

    string Folder(string name) => Directory.CreateDirectory(_dir.File(name)).FullName;

    [Fact]
    public void Each_dialog_remembers_its_own_folder_between_runs()
    {
        var (shows, rigs) = (Folder("shows"), Folder("rigs"));
        var recent = new RecentFolders(_dir.File("recent_dirs.json"));
        Assert.Null(recent.For("show"));
        recent.Remember("show", Path.Combine(shows, "club.slight"));
        recent.Remember("mvr", Path.Combine(rigs, "rig.mvr"));
        Assert.Equal(shows, recent.For("show"));

        var again = new RecentFolders(_dir.File("recent_dirs.json"));
        Assert.Equal(shows, again.For("show"));
        Assert.Equal(rigs, again.For("mvr"));
        Assert.Null(again.For("gltf_model"));
    }

    [Fact]
    public void A_folder_thats_gone_is_forgotten()
    {
        var gone = Folder("gone");
        var recent = new RecentFolders(_dir.File("recent_dirs.json"));
        recent.Remember("show", Path.Combine(gone, "a.slight"));
        Directory.Delete(gone);
        Assert.Null(recent.For("show")); // the dialog opens where Windows likes instead
    }

    [Fact]
    public void Asking_starts_in_the_remembered_folder_and_remembers_the_answer()
    {
        var (a, b) = (Folder("a"), Folder("b"));
        var recent = new RecentFolders(null); // kept for this run only
        recent.Remember("fixture_import", Path.Combine(a, "x.gdtf"));
        string? startedIn = null;
        Assert.Equal(Path.Combine(b, "y.gdtf"), recent.Ask("fixture_import", folder => (startedIn = folder) is { } ? Path.Combine(b, "y.gdtf") : null));
        Assert.Equal(a, startedIn);
        Assert.Equal(b, recent.For("fixture_import"));

        // Cancelled: nothing changes.
        Assert.Null(recent.Ask("fixture_import", _ => null));
        Assert.Equal(b, recent.For("fixture_import"));

        // A dialog that's not asked before starts where it's told to.
        recent.Ask("show", folder => startedIn = folder, fallback: b);
        Assert.Equal(b, startedIn);
    }

    [Fact]
    public void A_damaged_file_starts_afresh()
    {
        File.WriteAllText(_dir.File("recent_dirs.json"), "{ not json");
        var recent = new RecentFolders(_dir.File("recent_dirs.json"));
        Assert.Null(recent.For("show"));
        recent.Remember("show", Path.Combine(Folder("s"), "a.slight"));
        Assert.NotNull(new RecentFolders(_dir.File("recent_dirs.json")).For("show"));
    }
}
