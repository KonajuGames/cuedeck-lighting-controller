using System.Text.Json.Nodes;

namespace Cuedeck.Tests;

/// <summary>
/// Bringing sLight's own files (Godot's user:// folder) into Cuedeck's app folder: its custom fixture profiles and
/// their models, the online library's cache, its settings and remembered folders, its preset, the show it kept
/// untitled, and the last show. Both apps write these files the same way, so they're copied; nothing of Cuedeck's is
/// overwritten, and sLight's folder is left as it was.
/// </summary>
public sealed class SlightImportTests : IDisposable
{
    readonly TempFolder _dir = new();
    readonly string _slight;
    readonly string _older;
    readonly string _cuedeck;

    public SlightImportTests()
    {
        _slight = Path.Combine(_dir.Path, "Godot", "app_userdata", "sLight");
        _older = Path.Combine(_dir.Path, "Godot", "app_userdata", "DMX Lighting Controller");
        _cuedeck = Path.Combine(_dir.Path, "Cuedeck");
        Directory.CreateDirectory(_slight);
        Directory.CreateDirectory(_cuedeck);
    }

    public void Dispose() => _dir.Dispose();

    static void Write(string folder, string name, string text)
    {
        var path = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    static JsonObject Read(string folder, string name) => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(folder, name)))!;

    SlightImportResult Import(params string[] from) => new SlightImport(_cuedeck).Import(from.Length > 0 ? from : [_slight]);

    [Fact]
    public void Its_folders_found_under_Godots_app_data_newest_name_first()
    {
        var appData = _dir.Path;
        Assert.Equal([_slight], SlightImport.Folders(appData));
        Directory.CreateDirectory(_older);
        Assert.Equal([_slight, _older], SlightImport.Folders(appData));
        Assert.Empty(SlightImport.Folders(Path.Combine(_dir.Path, "nowhere")));
    }

    [Fact]
    public void Profiles_their_models_and_the_online_cache_copied_keeping_Cuedecks_own()
    {
        Write(_slight, "fixture_profiles/my_par.json", "{\"id\":\"my_par\"}");
        Write(_slight, "fixture_profiles/shared.json", "{\"id\":\"shared\",\"from\":\"sLight\"}");
        Write(_cuedeck, "fixture_profiles/shared.json", "{\"id\":\"shared\",\"from\":\"Cuedeck\"}");
        Write(_slight, "fixture_models/my_spot/base.glb", "glb");
        Write(_slight, "fixture_models/my_spot/head.glb", "glb");
        Write(_slight, "fixture_online/index.json", "[]");
        Write(_slight, "fixture_online/_gdtf_download.gdtf", "half a download");

        var result = Import();

        Assert.Equal(1, result.Profiles);
        Assert.Equal("{\"id\":\"my_par\"}", File.ReadAllText(Path.Combine(_cuedeck, "fixture_profiles/my_par.json")));
        Assert.Equal("Cuedeck", Read(_cuedeck, "fixture_profiles/shared.json")["from"]!.GetValue<string>());
        Assert.Equal(1, result.Models);
        Assert.True(File.Exists(Path.Combine(_cuedeck, "fixture_models/my_spot/head.glb")));
        Assert.True(File.Exists(Path.Combine(_cuedeck, "fixture_online/index.json")));
        Assert.False(File.Exists(Path.Combine(_cuedeck, "fixture_online/_gdtf_download.gdtf")));
        // sLight's folder as it was.
        Assert.True(File.Exists(Path.Combine(_slight, "fixture_profiles/my_par.json")));
    }

    [Fact]
    public void Settings_remembered_folders_and_window_places_merged_key_by_key()
    {
        Write(_slight, "settings.json", "{\"dj_software\":\"cuepoint\",\"from_slight\":1.0}");
        Write(_cuedeck, "settings.json", "{\"dj_software\":\"virtualdj\"}");
        Write(_slight, "recent_dirs.json", "{\"show\":\"D:/Shows\",\"song\":\"F:/Music\",\"gltf_model\":\"user://models\"}");
        Write(_slight, "ui_state.json", "{\"viz_window\":{\"x\":10.0}}");
        Write(_slight, "dmx_preset.json", "{\"universes\":[]}");

        var result = Import();

        // Cuedeck's own choice kept; what it didn't have, added.
        var settings = Read(_cuedeck, "settings.json");
        Assert.Equal("virtualdj", settings["dj_software"]!.GetValue<string>());
        Assert.Equal(1.0, settings["from_slight"]!.GetValue<double>());
        var recent = Read(_cuedeck, "recent_dirs.json");
        Assert.Equal("D:/Shows", recent["show"]!.GetValue<string>());
        Assert.Equal("F:/Music", recent["song"]!.GetValue<string>());
        // A folder in sLight's own user:// is the same place in Cuedeck's.
        Assert.Equal(Path.Combine(_cuedeck, "models"), recent["gltf_model"]!.GetValue<string>());
        Assert.Equal(10.0, Read(_cuedeck, "ui_state.json")["viz_window"]!["x"]!.GetValue<double>());
        Assert.Equal(5, result.Settings);
        Assert.True(result.Preset);
        Assert.True(File.Exists(Path.Combine(_cuedeck, "dmx_preset.json")));
    }

    [Fact]
    public void The_show_sLight_kept_untitled_and_the_last_show()
    {
        Write(_slight, "dmx_show.json", "{\"version\":5.0}");
        Write(_slight, "dmx_show.timelines/abc.json", "{}");
        Write(_slight, "last_show.json", "{\"path\":\"user://dmx_show.json\"}");

        var result = Import();

        var show = Path.Combine(_cuedeck, "dmx_show.json");
        Assert.Equal(show, result.UntitledShow);
        Assert.True(File.Exists(show));
        Assert.True(File.Exists(Path.Combine(_cuedeck, "dmx_show.timelines/abc.json"))); // its songs' timelines beside it
        Assert.Equal(show, result.LastShow);
        Assert.Equal(show, Read(_cuedeck, "last_show.json")["path"]!.GetValue<string>());
    }

    [Fact]
    public void A_last_show_elsewhere_is_remembered_only_if_its_there_and_Cuedeck_has_none()
    {
        var elsewhere = Path.Combine(_dir.Path, "Shows", "Club.slight");
        Write(Path.GetDirectoryName(elsewhere)!, "Club.slight", "{}");
        Write(_slight, "last_show.json", $"{{\"path\":\"{elsewhere.Replace('\\', '/')}\"}}");
        Assert.Equal(elsewhere.Replace('\\', '/'), Import().LastShow);

        Write(_cuedeck, "last_show.json", "{\"path\":\"C:/Mine.slight\"}");
        Assert.Null(Import().LastShow);
        Assert.Equal("C:/Mine.slight", Read(_cuedeck, "last_show.json")["path"]!.GetValue<string>());

        File.Delete(Path.Combine(_cuedeck, "last_show.json"));
        Write(_slight, "last_show.json", "{\"path\":\"Z:/Gone.slight\"}");
        Assert.Null(Import().LastShow);
        Assert.False(File.Exists(Path.Combine(_cuedeck, "last_show.json")));
    }

    [Fact]
    public void Its_older_name_fills_in_what_the_newer_folder_lacks()
    {
        Write(_slight, "fixture_profiles/a.json", "{\"id\":\"a\",\"from\":\"new\"}");
        Write(_older, "fixture_profiles/a.json", "{\"id\":\"a\",\"from\":\"old\"}");
        Write(_older, "fixture_profiles/b.json", "{\"id\":\"b\"}");
        Write(_older, "dmx_preset.json", "{}");

        var result = Import(_slight, _older);

        Assert.Equal(2, result.Profiles);
        Assert.Equal("new", Read(_cuedeck, "fixture_profiles/a.json")["from"]!.GetValue<string>());
        Assert.True(result.Preset);
    }

    [Fact]
    public void What_was_sLights_alone_is_left_behind()
    {
        Write(_slight, "autosave.slight", "{}");
        Write(_slight, "session.json", "{}");
        Write(_slight, "render/shot_1.png", "png");
        Write(_slight, "logs/godot.log", "log");
        Write(_slight, "shader_cache/x", "x");

        var result = Import();

        Assert.False(result.Any);
        Assert.Equal("Nothing new to import from sLight.", result.ToString());
        Assert.Equal(["Cuedeck"], Directory.EnumerateFileSystemEntries(_dir.Path).Select(Path.GetFileName).Where(n => n != "Godot"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_cuedeck));
    }

    [Fact]
    public void Its_summary()
    {
        Write(_slight, "fixture_profiles/a.json", "{}");
        Write(_slight, "fixture_profiles/b.json", "{}");
        Write(_slight, "fixture_models/a/m.glb", "glb");
        Write(_slight, "settings.json", "{\"dj_software\":\"cuepoint\"}");
        Write(_slight, "dmx_show.json", "{}");
        Assert.Equal("Imported from sLight: 2 fixture profiles, 1 fixture model, 1 setting and its untitled show (dmx_show.json).", Import().ToString());
    }
}
