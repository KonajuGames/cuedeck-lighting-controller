using Cuedeck.Dj;

namespace Cuedeck.Tests.Dj;

/// <summary>
/// VirtualDJ's databases and lists: virtualdj_database_test.gd and virtualdj_list_test.gd (their synthetic parts; the
/// ones that read this machine's own VirtualDJ files aren't ported, as tests don't depend on what's installed).
/// </summary>
public sealed class VirtualDjTests : IDisposable
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

    const string DbA = """
        <?xml version="1.0" encoding="UTF-8"?>
        <VirtualDJ_Database Version="8.5">
         <Song FilePath="D:\Music\Band\Album\01 Track.m4a" FileSize="6495416">
          <Tags Author="Band &amp; Friends" Title="Track" Genre="Pop" Album="Album" TrackNumber="1/14" Year="2019" Bpm="0.434783" Key="C#m" Flag="1" />
          <Infos SongLength="181.043107" LastModified="1697984254" FirstSeen="1697983832" Bitrate="261" Cover="2" />
          <Scan Version="801" Bpm="0.434785" Phase="0.905646" AltBpm="0.652177" Volume="1.276375" Key="C#m" Flag="32768" />
          <Poi Pos="0.905646" Type="beatgrid" />
          <Poi Type="automix" Point="realStart" />
          <Poi Pos="178.811066" Type="automix" Point="realEnd" />
          <Poi Name="Break 2" Pos="169.827846" Type="remix" />
          <Poi Name="Break 1" Pos="53.261497" Type="remix" />
          <Poi Pos="47.160" Num="2" Type="cue" />
          <Poi Pos="12.5" Num="1" Name="Intro" Type="cue" />
          <Comment>Great opener</Comment>
         </Song>
         <Song FilePath="D:\thriller.mp4" FileSize="7369550" Flag="64">
          <Tags Title="Thriller" Flag="1" />
         </Song>
         <Song FilePath="D:\Music\untagged.mp3" FileSize="1" />
        </VirtualDJ_Database>
        """;

    /// <summary>
    /// A second database (as if from Documents, on C:) listing one of the same D: songs with different data, and one
    /// of its own.
    /// </summary>
    const string DbB = """
        <?xml version="1.0" encoding="UTF-8"?>
        <VirtualDJ_Database Version="8.5">
         <Song FilePath="d:/music/band/album/01 track.m4a" FileSize="6495416">
          <Tags Title="Stale copy" Bpm="0.5" />
         </Song>
         <Song FilePath="C:\Users\me\Sampler\Siren.vdjsample" FileSize="331329">
          <Tags Bpm="0.542857" Flag="1" />
         </Song>
        </VirtualDJ_Database>
        """;

    [Fact]
    public void A_database_is_read()
    {
        var db = new VirtualDjDatabase();
        Assert.True(db.LoadFile(Write("a.xml", DbA)));
        Assert.Equal(3, db.SongCount);
        Assert.Empty(db.Errors);

        var s = db.FindSong("D:/Music/Band/Album/01 Track.m4a")!;
        Assert.Same(s, db.FindSong(@"D:\Music\Band\Album\01 Track.m4a"));
        Assert.Same(s, db.FindSong("d:/music/band/album/01 track.m4a")); // Windows: any case
        Assert.Equal("Band & Friends", s.Artist);
        Assert.Equal(("Track", "Pop", "2019", "1/14"), (s.Title, s.Genre, s.Year, s.TrackNumber));
        Assert.Equal(138, s.Bpm, 0.01); // 0.434783 seconds a beat
        Assert.Equal(138, s.ScanBpm, 0.01);
        Assert.Equal(92, s.ScanAltBpm, 0.01);
        Assert.Equal(("C#m", "C#m"), (s.Key, s.ScanKey));
        Assert.Equal(181.043107, s.Length, 4);
        Assert.Equal((261, 1697983832L), (s.Bitrate, s.FirstSeen));
        Assert.True(s.Scanned);
        Assert.Equal(1.276375, s.Volume, 5);
        Assert.Equal(0.905646, s.FirstBeat, 5); // from the beat grid
        Assert.Equal(0, s.Automix["realStart"]); // Pos left out reads as 0
        Assert.Equal(178.811066, s.Automix["realEnd"], 4);
        Assert.Equal(["Break 1", "Break 2"], s.RemixPoints.Select(p => p.Name)); // by position
        Assert.Equal([(1, "Intro"), (2, "")], s.Cues.Select(c => (c.Number, c.Name))); // by number
        Assert.Equal("Great opener", s.Comment);
        Assert.False(s.IsVideo);

        var v = db.FindSong("D:/thriller.mp4")!;
        Assert.True(v.IsVideo);
        Assert.Equal((0.0, false), (v.Bpm, v.Scanned));
        Assert.Equal("untagged", db.FindSong("D:/Music/untagged.mp3")!.DisplayName); // a self-closing <Song/>
    }

    [Fact]
    public void Two_databases_merge()
    {
        var db = new VirtualDjDatabase();
        // The C: (Documents) one first, as LoadAll does. The temp folder's on C:, so neither is "on D:": the first wins.
        var b = Write("b.xml", DbB);
        var a = Write("a.xml", DbA);
        db.LoadFile(b);
        db.LoadFile(a);
        Assert.Equal(2, db.Sources.Count);
        Assert.Equal(4, db.SongCount);
        if (char.ToLowerInvariant(b[0]) == 'c')
            Assert.Equal("Stale copy", db.FindSong("D:/Music/Band/Album/01 Track.m4a")!.Title);

        // The real rule: a database on the song's own drive beats one elsewhere.
        var d2 = new VirtualDjDatabase();
        d2.Add(new DjSong { Path = "D:/x.mp3", Source = "C:/Users/me/Documents/VirtualDJ/database.xml", Title = "from C" });
        d2.Add(new DjSong { Path = "D:/x.mp3", Source = "D:/VirtualDJ/database.xml", Title = "from D" });
        Assert.Equal("from D", d2.FindSong("D:/x.mp3")!.Title);
        d2.Add(new DjSong { Path = "D:/x.mp3", Source = "E:/VirtualDJ/database.xml", Title = "from E" });
        Assert.Equal("from D", d2.FindSong("D:/x.mp3")!.Title);
    }

    [Fact]
    public void Bad_databases_are_rejected()
    {
        var db = new VirtualDjDatabase();
        Assert.False(db.LoadFile(_dir.File("missing.xml")));
        Assert.False(db.LoadFile(Write("other.xml", """<?xml version="1.0"?><Other/>""")));
        Assert.Equal(2, db.Errors.Count);
    }

    [Theory]
    [InlineData("C", 0, "major")]
    [InlineData("C#m", 1, "minor")]
    [InlineData("Ebm", 3, "minor")]
    [InlineData("G#", 8, "major")]
    [InlineData("Bb", 10, "major")]
    [InlineData("Cb", 11, "major")]
    [InlineData("Am", 9, "minor")]
    public void Keys_are_read(string key, int root, string mode) => Assert.Equal((root, mode), VirtualDjDatabase.ParseKey(key));

    [Fact]
    public void Unreadable_keys_are_none()
    {
        Assert.Null(VirtualDjDatabase.ParseKey(""));
        Assert.Null(VirtualDjDatabase.ParseKey("8A")); // Camelot
    }

    [Fact]
    public void A_beat_grid_is_read_and_expanded()
    {
        var (mode, segs) = VirtualDjDatabase.ParseBeatGrid("1[10:4,0.5][12:2][13:1,0.25][13.25:0,0.6,0.61]")!.Value;
        Assert.Equal(1, mode);
        Assert.Equal(
            [new GridSegment(10, 4, 0.5), new GridSegment(12, 2, 0.5), new GridSegment(13, 1, 0.25), new GridSegment(13.25, 0, 0.6)],
            segs
        );
        foreach (var bad in new[] { "", "1", "[1:2,0.5]", "x[1:2,0.5]", "1[1:2]", "1[1:2,0.5", "1[a:2,0.5]", "1[1:2,0]" })
            Assert.Null(VirtualDjDatabase.ParseBeatGrid(bad));

        var s = new DjSong { Length = 15, GridSegments = segs };
        // 10, 10.5, 11, 11.5 | 12, 12.5 | 13 | 13.25, 13.85, 14.45 (and then 15.05, past the end)
        Assert.Equal([10.0, 10.5, 11, 11.5, 12, 12.5, 13, 13.25, 13.85, 14.45], s.BeatTimes().Select(b => Math.Round(b, 4)));
        Assert.Equal(5, s.BeatTimes(12).Count);

        var c = new DjSong { Length = 3, ScanBpm = 120, Phase = 1.25 };
        Assert.Equal([0.25f, 0.75f, 1.25f, 1.75f, 2.25f, 2.75f], c.BeatTimes()); // a steady tempo locked to the phase
        Assert.Empty(new DjSong().BeatTimes());

        var db = new VirtualDjDatabase();
        db.LoadFile(
            Write(
                "grid.xml",
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <VirtualDJ_Database Version="8.5">
                 <Song FilePath="D:\mix.m4a"><Infos SongLength="15" />
                  <Scan Version="801" Bpm="0.5" Phase="10" Rigid="0.51" BeatGrid="1[10:4,0.5][12:2][13:1,0.25][13.25:0,0.6,0.61]" />
                 </Song>
                </VirtualDJ_Database>
                """
            )
        );
        var m = db.FindSong("D:/mix.m4a")!;
        Assert.True(m.HasBeatGrid && m.GridMode == 1 && m.GridSegments.Count == 4);
        Assert.Equal(0.51, m.Rigid, 6);
        Assert.Equal(10, m.BeatTimes().Count);
    }

    /// <summary>
    /// Tempo and first beat, pinned to what a live VirtualDJ reported over OS2L for the same tracks.
    /// </summary>
    [Fact]
    public void Tempo_and_first_beat_match_a_live_virtualdj()
    {
        // The scan wins over the tag: a track tagged 94 played at the scanned 124.99.
        var t = new DjSong { Bpm = 94, ScanBpm = 124.994 };
        Assert.Equal(124.994, t.BestBpm);
        t.ScanBpm = 0;
        Assert.Equal(94, t.BestBpm);
        t.Phase = 0.269955;
        Assert.Equal((0.269955, 94.0), (t.FirstBeat, t.BpmAt(100)));

        // A slice of a real variable-tempo grid; VirtualDJ reported these tempos on these beats.
        var g = new DjSong
        {
            Phase = 45.560112, // a beat far into the track
            GridSegments = VirtualDjDatabase
                .ParseBeatGrid("9644[34.4:1,0.42639][34.82:1,0.41389][35.228:1,0.404][35.6:1,0.408][36:1,0.412][36.44:3,0.43674][37.75:0,0.43674]")!
                .Value.Segments,
        };
        Assert.Equal(34.4, g.FirstBeat);
        var want = new Dictionary<double, double> { [34.4] = 140.72, [34.82] = 144.97, [35.228] = 148.51, [35.6] = 147.06, [36.0] = 145.63, [36.44] = 137.38 };
        foreach (var (at, bpm) in want)
            Assert.Equal(bpm, g.BpmAt(at), 0.01);
        Assert.Equal(140.72, g.BpmAt(34.6), 0.01); // inside a run
        Assert.Equal(144.97, g.BpmAt(34.79), 0.01); // just before a rounded start counts as inside it
        Assert.Equal(140.72, g.BpmAt(10), 0.01);
        Assert.Equal(137.38, g.BpmAt(500), 0.01);

        // Where bars count from in a beat list.
        var c = new DjSong { ScanBpm = 60 / 0.483878, Phase = -0.042766, Length = 10 }; // first beat just before 0 s
        var cb = c.BeatTimes();
        Assert.Equal(0.441112, cb[0], 0.001);
        Assert.Equal(-1, c.FirstBeatIndex(cb));
        c.Phase = 2 * 0.483878 + 0.1; // the third listed
        Assert.Equal(2, c.FirstBeatIndex(c.BeatTimes()));
        c.Phase = -4 * 0.483878 - 0.042766; // a whole bar before 0 s
        Assert.Equal(-5, c.FirstBeatIndex(c.BeatTimes()));
        Assert.Equal(0, g.FirstBeatIndex(g.BeatTimes(40)));
        Assert.Equal(0, c.FirstBeatIndex([]));
    }

    // -------------------------------------------------------------- lists --

    const string List = """
        <?xml version="1.0" encoding="UTF-8"?>
        <VirtualFolder ordered="yes" singleDrive="yes">
        	<song path="D:\Music\Smart E’s\01. Sesame&apos;s Treet.mp3" size="4392884" songlength="213.8" bpm="72.447" key="C" artist="Smart E&apos;s" title="Sesame&apos;s Treet (edit)" remix="edit" idx="2" />
        	<song path="D:\Music\Happy.m4a" size="8306944" songlength="233.3" bpm="79.998" key="Fm" artist="Pharrell Williams" title="Happy (from &quot;Despicable Me 2&quot;)" idx="0" />
        	<song path="D:\Music\Majestic &amp; Boney M_\01 Rasputin.m4a" size="7084518" songlength="186.2" bpm="127.999" key="B" title="Rasputin" idx="1" />
        	<song path="D:\Music\no-idx.mp3" songlength="10" />
        </VirtualFolder>
        """;

    [Fact]
    public void A_list_is_read_in_its_order()
    {
        var errors = new List<string>();
        var list = DjList.LoadFile(Write("lists/Disco 26.vdjfolder", List), errors)!;
        Assert.Empty(errors);
        Assert.Equal("Disco 26", list.Name);
        Assert.Equal(("yes", "yes"), (list.Options["ordered"], list.Options["singleDrive"]));
        // By idx; one without keeps its place in the file.
        Assert.Equal(["Happy (from \"Despicable Me 2\")", "Rasputin", "Sesame's Treet (edit)", ""], list.Entries.Select(e => e.Title));
        var e = list.Entries[2];
        Assert.Equal("Smart E's", e.Artist);
        Assert.Equal("D:/Music/Smart E’s/01. Sesame's Treet.mp3", e.Path);
        Assert.Equal((72.447, "C", "edit"), (e.Bpm, e.Key, e.Remix));
        Assert.Equal((213.8, 4392884L), (e.Length, e.FileSize));
        Assert.Equal("D:/Music/Majestic & Boney M_/01 Rasputin.m4a", list.Entries[1].Path);
        Assert.Equal("Rasputin", list.Entries[1].DisplayName);
        Assert.Equal("no-idx", list.Entries[3].DisplayName);
        Assert.Equal(213.8 + 233.3 + 186.2 + 10, list.TotalLength, 3);

        var db = new VirtualDjDatabase();
        db.LoadFile(Write("db.xml", """<?xml version="1.0" encoding="UTF-8"?><VirtualDJ_Database Version="8.5"><Song FilePath="D:\Music\Happy.m4a"><Tags Title="Happy" Bpm="0.75" /></Song></VirtualDJ_Database>"""));
        var songs = list.Resolve(db.FindSong);
        Assert.Equal(4, songs.Count);
        Assert.Equal("Happy", songs[0]!.Title);
        Assert.Null(songs[1]);
    }

    [Fact]
    public void A_mylists_folder_is_read_with_its_subfolders_and_order()
    {
        Write("MyLists/A list.vdjfolder", List);
        Write("MyLists/B list.vdjfolder", List);
        Write("MyLists/notes.txt", "not a list");
        Write("MyLists/Gigs.subfolders/2026.vdjfolder", List);
        Write("MyLists/order", "B list\r\n\r\nA list\r\n");
        var lists = new List<DjList>();
        var errors = new List<string>();
        DjList.LoadDir(_dir.File("MyLists"), "", lists, errors);
        Assert.Empty(errors);
        Assert.Equal(3, lists.Count);
        Assert.Equal("Gigs", lists.Single(l => l.Name == "2026").Group);
        Assert.Equal(["B list", "A list"], DjList.ReadOrder(_dir.File("MyLists/order")));
    }

    [Fact]
    public void Bad_lists_are_none()
    {
        var errors = new List<string>();
        Assert.Null(DjList.LoadFile(_dir.File("missing.vdjfolder"), errors));
        Assert.Null(DjList.LoadFile(Write("other.vdjfolder", """<?xml version="1.0"?><Other/>"""), errors));
        Assert.Equal(2, errors.Count);
        Assert.Empty(DjList.LoadFile(Write("empty.vdjfolder", """<?xml version="1.0"?><VirtualFolder />"""), errors)!.Entries);
    }
}
