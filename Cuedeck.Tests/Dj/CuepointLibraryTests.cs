using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Dj;

namespace Cuedeck.Tests.Dj;

/// <summary>
/// Cuepoint's library files and virtual folders: cuepoint_library_test.gd (its synthetic parts; the settings part is
/// in OptionsTests, and the one that reads this machine's own Cuepoint library isn't ported).
/// </summary>
public sealed class CuepointLibraryTests : IDisposable
{
    readonly TempFolder _dir = new();

    public void Dispose() => _dir.Dispose();

    string Write(string name, string text)
    {
        var path = _dir.File(name).Replace('\\', '/');
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    const string Library = """
        {
          "version": 1,
          "tracks": {
            "Music\\5 Seconds of Summer\\01 Teeth.m4a": {
              "title": "Teeth", "artist": "5 Seconds of Summer", "album": "Teeth - Single",
              "size": 8723391, "modified": "2019-10-02T13:16:10Z", "analysisVersion": 4,
              "bpm": 139, "duration": 204.939, "grid": [ { "time": 0.3414, "bpm": 139 } ],
              "downbeat": 0, "loudness": -6.68, "peak": 0.953057, "key": "Eb minor"
            },
            "C:\\Music\\broken.mp3": {
              "size": 1234, "modified": "2021-03-04T05:06:07.1234567Z", "analysisVersion": 4,
              "bpm": 0, "duration": 0, "downbeat": 0, "loudness": 0, "peak": 0,
              "error": "Couldn't decode broken.mp3."
            },
            "C:\\Music\\tagged but broken.mp3": {
              "title": "Tagged", "size": 1, "bpm": 0, "duration": 0, "error": "Couldn't decode it."
            },
            "Music\\Happy.m4a": {
              "size": 100, "modified": "2019-10-02T13:16:10Z", "analysisVersion": 4,
              "bpm": 120, "duration": 100, "grid": [ { "time": 1.0, "bpm": 120 } ],
              "downbeat": 2, "loudness": -20, "peak": 0.5, "key": "F major", "someNewField": 1
            }
          },
          "hotcues": {
            "music\\happy.M4A": [12.346, null, null, 100.0, null, null, null, null],
            "Music\\Cues only.mp3": [5.0]
          },
          "grids": {
            "Music\\Fixed.m4a": {
              "markers": [ { "time": 0.5, "bpm": 120 }, { "time": 50.5, "bpm": 100 } ],
              "downbeat": 3, "pins": [ 0.5, 50.5 ]
            }
          }
        }
        """;

    [Fact]
    public void A_library_file_is_read()
    {
        var lib = new CuepointLibrary();
        var p = Write("Cuepoint/library.json", Library);
        Assert.Null(lib.LoadFile(p, "F:/"));
        Assert.Equal(5, lib.SongCount); // an untagged failure left out; cues-only and grid-only kept

        var teeth = lib.FindSong(@"F:\Music\5 Seconds of Summer\01 Teeth.m4a")!;
        Assert.Equal("F:/Music/5 Seconds of Summer/01 Teeth.m4a", teeth.Path);
        Assert.Equal((139.0, "Ebm"), (teeth.BestBpm, teeth.BestKey));
        Assert.Equal((204.939, 8723391L), (teeth.Length, teeth.FileSize));
        Assert.Equal(p, teeth.Source);
        Assert.Equal(("Teeth", "5 Seconds of Summer", "Teeth - Single"), (teeth.Title, teeth.Artist, teeth.Album));
        Assert.Equal("5 Seconds of Summer - Teeth", teeth.DisplayName);

        Assert.Null(lib.FindSong("C:/Music/broken.mp3")); // a failed analysis with no tags isn't a song
        var tagged = lib.FindSong("C:/Music/tagged but broken.mp3")!;
        Assert.True(tagged.Title == "Tagged" && !tagged.HasBeatGrid);

        var happy = lib.FindSong("f:/MUSIC/happy.m4a")!; // any case
        Assert.Equal([(1, 12.346), (4, 100.0)], happy.Cues.Select(c => (c.Number, c.Position))); // cues merged in, from 1
        Assert.Equal("F", happy.BestKey);

        var cuesOnly = lib.FindSong("F:/Music/Cues only.mp3")!;
        Assert.True(!cuesOnly.HasBeatGrid && cuesOnly.BestBpm == 0 && cuesOnly.Title == "");
        Assert.Equal(("", ""), (happy.Title, happy.Artist)); // written before tags were kept

        // Full paths only, with no drive root.
        var sys = new CuepointLibrary();
        sys.LoadFile(p);
        Assert.Equal(1, sys.SongCount);
        Assert.NotNull(sys.FindSong("C:/Music/tagged but broken.mp3"));

        // A later file's entry replaces the earlier one.
        lib.LoadFile(Write("later.json", """{"version":1,"tracks":{"F:\\Music\\Happy.m4a":{"bpm":90,"duration":10,"grid":[{"time":0,"bpm":90}],"key":"A minor"}},"hotcues":{}}"""));
        Assert.Equal(90, lib.FindSong("F:/Music/Happy.m4a")!.BestBpm);
    }

    /// <summary>
    /// The beats that start a bar, as the DJ Edit timeline counts them.
    /// </summary>
    static List<double> BarStarts(DjSong s, List<float> beats)
    {
        var phase = s.FirstBeatIndex(beats);
        return [.. beats.Where((_, i) => Gd.PosMod(i - phase, 4) == 0).Select(b => Math.Round(b, 3))];
    }

    static JsonNode J(string json) => Json.Parse(json)!;

    [Fact]
    public void Grids_lead_in_and_count_bars_from_the_downbeat()
    {
        // One marker at 1.0 s, 120 BPM: two beats lead in from 0 s, downbeat 2.
        var s = CuepointLibrary.MakeSong("x", J("""{"bpm":120,"duration":100,"grid":[{"time":1.0,"bpm":120}],"downbeat":2,"key":"C major"}"""), null, null)!;
        Assert.True(s.HasBeatGrid);
        Assert.Equal(2, s.GridSegments.Count);
        Assert.Equal((0.0, 2), (Math.Round(s.GridSegments[0].Start, 6), s.GridSegments[0].Beats));
        var beats = s.BeatTimes(10);
        Assert.Equal(21, beats.Count);
        Assert.Equal((0.0, 10.0), (Math.Round(beats[0], 6), Math.Round(beats[^1], 6)));
        Assert.Equal([0.0, 2, 4], BarStarts(s, beats).Take(3)); // the marker's beat 0, so beat 2 at 2.0 s and beat -2 at 0 s

        // A hand-fixed grid replaces the analysed one; a tempo change mid-song.
        var f = CuepointLibrary.MakeSong(
            "y",
            J("""{"bpm":130,"duration":200,"grid":[{"time":0.1,"bpm":130}],"downbeat":0,"key":"C major"}"""),
            null,
            J("""{"markers":[{"time":0.5,"bpm":120},{"time":50.5,"bpm":100}],"downbeat":3}""")
        )!;
        Assert.Equal([1, 100, 0], f.GridSegments.Select(g => g.Beats));
        Assert.Equal((120.0, 100.0), (Math.Round(f.BpmAt(10), 6), Math.Round(f.BpmAt(60), 6)));
        Assert.Equal(100, f.BestBpm); // 0 to 50.5 s at 120, 50.5 to 200 s at 100: the median over time is 100
        Assert.Equal([0.0, 2], BarStarts(f, f.BeatTimes(10)).Take(2));

        var bad = CuepointLibrary.MakeSong("z", J("""{"bpm":130,"duration":10,"grid":[{"time":0.0,"bpm":130}],"key":"C major"}"""), null, J("""{"markers":[{"time":5.0,"bpm":120},{"time":1.0,"bpm":120}]}"""))!;
        Assert.Equal(130, bad.BestBpm); // out of order: the analysis stands
        Assert.Null(CuepointLibrary.MakeSong("w", J("""{"bpm":120,"duration":10,"grid":[],"key":"C major"}"""), null, null));
        Assert.Equal(120, CuepointLibrary.MainTempo([(0, 120), (60, 100)], 100)); // the longer span
    }

    [Fact]
    public void Keys_and_gain()
    {
        Assert.Equal((1, "minor"), CuepointLibrary.ParseKey("C# minor"));
        Assert.Equal((10, "major"), CuepointLibrary.ParseKey("Bb major"));
        Assert.Null(CuepointLibrary.ParseKey("Db major"));
        Assert.Null(CuepointLibrary.ParseKey("8A"));
        Assert.Equal((3, "minor"), VirtualDjDatabase.ParseKey(CuepointLibrary.ShortKey(CuepointLibrary.ParseKey("Eb minor")!.Value)));
        Assert.Equal(1, CuepointLibrary.AutoGain(-14, 0.5), 6);
        Assert.Equal(Math.Pow(10, -6.0 / 20), CuepointLibrary.AutoGain(-8, 1), 6);
        Assert.Equal(1 / 0.794, CuepointLibrary.AutoGain(-20, 0.794), 3); // a boost no more than the peak allows
    }

    [Fact]
    public void A_virtual_folder_is_read()
    {
        var dir = _dir.File("Cuepoint/VirtualFolders").Replace('\\', '/');
        var p = Write("Cuepoint/VirtualFolders/Warm up.m3u8", "\uFEFF#EXTM3U\r\nC:\\Music\\First.mp3\r\n#EXTINF:123,Comment\n\n  ..\\..\\Music\\Second.m4a  \nc:\\music\\FIRST.mp3\nmissing.mp3\n");
        var errors = new List<string>();
        var list = CuepointLibrary.LoadM3u8(p, errors)!;
        Assert.Empty(errors);
        Assert.Equal("Warm up", list.Name);
        Assert.Equal(
            ["C:/Music/First.mp3", Gd.SimplifyPath(_dir.Path) + "/Music/Second.m4a", Gd.SimplifyPath(dir) + "/missing.mp3"],
            list.Entries.Select(e => e.Path)
        ); // comments, blanks and a repeat (in any case) left out; relative to the folder; a missing song kept
        Assert.Equal("Second", list.Entries[1].DisplayName);
        Assert.Null(CuepointLibrary.LoadM3u8(_dir.File("none.m3u8"), errors));
        Assert.Single(errors);
    }

    [Fact]
    public void Bad_library_files()
    {
        var lib = new CuepointLibrary();
        Assert.Equal("not a Cuepoint library (version 2)", lib.LoadFile(Write("v2.json", """{"version":2,"tracks":{}}""")));
        Assert.Equal("not valid JSON", lib.LoadFile(Write("bad.json", "{not json")));
        Assert.Equal("not found", lib.LoadFile(_dir.File("missing.json")));
        Assert.Equal(3, lib.Errors.Count);
        Assert.Null(lib.LoadFile(Write("empty.json", """{"version":1,"tracks":{},"hotcues":{}}""")));
        Assert.Equal(0, lib.SongCount);
    }

    [Fact]
    public void Paths_are_simplified_as_godot_did()
    {
        Assert.Equal("C:/a/c", Gd.SimplifyPath("C:\\a\\.\\b\\..\\c"));
        Assert.Equal("/x/y", Gd.SimplifyPath("/x//y/"));
        Assert.Equal("F:/Music/x.mp3", CuepointLibrary.ResolveKey("Music\\x.mp3", "F:/"));
        Assert.Equal("", CuepointLibrary.ResolveKey("Music\\x.mp3", ""));
    }
}
