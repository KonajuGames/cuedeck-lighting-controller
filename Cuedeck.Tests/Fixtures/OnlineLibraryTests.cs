using System.IO.Compression;
using System.Net;
using System.Text;
using Cuedeck.Fixtures;

namespace Cuedeck.Tests.Fixtures;

/// <summary>
/// The library's online sources, against a stand-in for GitHub and GDTF-Share (the tests don't touch the network).
/// </summary>
public class OnlineLibraryTests
{
    static readonly FixtureLibrary Bundled = new(FixtureLibrary.DefaultFolder);

    const string Tree = "https://api.github.com/repos/OpenLightingProject/open-fixture-library/git/trees/master?recursive=1";
    const string Raw = "https://raw.githubusercontent.com/OpenLightingProject/open-fixture-library/master/fixtures/";
    const string Login = "https://gdtf-share.com/apis/public/login.php";
    const string List = "https://gdtf-share.com/apis/public/getList.php";
    const string Download = "https://gdtf-share.com/apis/public/downloadFile.php";

    static FakeWeb OflWeb()
    {
        var web = new FakeWeb();
        web.Json(
            Tree,
            """
            { "tree": [
              { "path": "fixtures/manufacturers.json" },
              { "path": "fixtures/cameo/auro-beam-150.json" },
              { "path": "fixtures/acme/led-par-9.json" },
              { "path": "fixtures/acme/led-par-10.json" },
              { "path": "fixtures/acme/old-name.json" },
              { "path": "README.md" }
            ] }
            """
        );
        web.Json(Raw + "manufacturers.json", """{ "$schema": "x", "cameo": { "name": "Cameo" }, "acme": { "name": "ACME Lighting" } }""");
        web.Json(Raw + "acme/old-name.json", """{ "redirectTo": "acme/led-par-9", "reason": "FixtureRenamed" }""");
        web.Json(
            Raw + "acme/led-par-9.json",
            """{ "name": "LED Par 9", "availableChannels": { "Red": {}, "Green": {}, "Blue": {} }, "modes": [ { "name": "3ch", "channels": ["Red", "Green", "Blue"] } ] }"""
        );
        return web;
    }

    [Fact]
    public async Task The_ofl_is_listed_from_github()
    {
        using var dir = new TempFolder();
        var online = new OnlineLibrary(Bundled, dir.Path, OflWeb());
        Assert.True(await online.RefreshOfl());
        Assert.Equal(4, online.OflCount);
        Assert.Equal(["ACME Lighting", "Cameo"], online.OflManufacturers);
        Assert.Equal(["Led Par 9", "Led Par 10", "Old Name", "Auro Beam 150"], online.SearchOfl().Select(e => e.Model));
        var beam = Assert.Single(online.SearchOfl("auro 150"));
        Assert.Equal(("cameo-auro-beam-150", "Cameo", true), (beam.Id, beam.Maker, beam.Bundled));
        Assert.Equal(3, online.SearchOfl(maker: "ACME Lighting").Count);
    }

    [Fact]
    public async Task An_ofl_fixture_is_downloaded_once_and_kept()
    {
        using var dir = new TempFolder();
        var web = OflWeb();
        var online = new OnlineLibrary(Bundled, dir.Path, web);
        await online.RefreshOfl();
        Assert.Null(online.Resolve("acme-old-name"));

        var p = await online.Download("acme-old-name"); // renamed: the redirect's followed
        Assert.Equal(("acme-old-name", "ACME Lighting", "LED Par 9", 3), (p!.Id, p.Manufacturer, p.Model, p.ChannelCount(0)));
        Assert.Equal(1, online.CachedOflCount);

        // Kept: no more requests, even after a restart.
        var sent = web.Sent.Count;
        Assert.Same(p, await online.Download("acme-old-name"));
        var later = new OnlineLibrary(Bundled, dir.Path, web);
        Assert.Equal("LED Par 9", later.Resolve("acme-old-name")!.Model);
        Assert.Equal(sent, web.Sent.Count);

        online.ClearOflCache();
        Assert.Equal(0, online.CachedOflCount);
        Assert.Null(online.Resolve("acme-old-name"));
    }

    [Fact]
    public async Task Failures_say_why()
    {
        using var dir = new TempFolder();
        var online = new OnlineLibrary(Bundled, dir.Path, new FakeWeb());
        Assert.False(await online.RefreshOfl());
        Assert.Equal("couldn't reach GitHub (HTTP 404)", online.OflError);

        var web = OflWeb();
        online = new OnlineLibrary(Bundled, dir.Path, web);
        await online.RefreshOfl();
        Assert.Null(await online.Download("cameo-auro-beam-150"));
        Assert.Equal("download failed (HTTP 404)", online.OflError);
    }

    static byte[] Gdtf()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        using (var s = zip.CreateEntry("description.xml").Open())
            s.Write(
                Encoding.UTF8.GetBytes(
                    """<GDTF><FixtureType Name="W" LongName="Wash 7" Manufacturer="Acme"><DMXModes><DMXMode Name="1ch"><DMXChannels><DMXChannel Offset="1"><LogicalChannel Attribute="Dimmer"/></DMXChannel></DMXChannels></DMXMode></DMXModes></FixtureType></GDTF>"""
                )
            );
        return memory.ToArray();
    }

    static FakeWeb GdtfWeb()
    {
        var web = new FakeWeb();
        web.Json(Login, """{ "result": true }""", HttpStatusCode.OK, "PHPSESSID=abc123; path=/; HttpOnly", "lang=en; path=/");
        web.Json(
            List,
            """{ "result": true, "list": [ { "rid": 150162.0, "manufacturer": "Acme", "fixture": "Wash 7", "version": "1.1", "creator": "Someone", "modes": [ { "name": "1ch", "dmxfootprint": 1.0 } ] }, { "manufacturer": "No Rid" } ] }"""
        );
        web.Pages[Download + "?rid=150162"] = (HttpStatusCode.OK, Gdtf(), []);
        return web;
    }

    [Fact]
    public async Task Gdtf_share_needs_a_login_and_keeps_it_in_memory_only()
    {
        using var dir = new TempFolder();
        var web = GdtfWeb();
        var online = new OnlineLibrary(Bundled, dir.Path, web);
        Assert.False(await online.RefreshGdtf());
        Assert.Equal("not logged in", online.GdtfError);

        Assert.True(await online.GdtfLogin("lamp-person", "s3cret-pass"));
        Assert.Equal(("lamp-person", true), (online.GdtfUser, online.GdtfLoggedIn));
        Assert.Contains("\"password\":\"s3cret-pass\"", web.Sent[^1].Body);

        Assert.True(await online.RefreshGdtf());
        var e = Assert.Single(online.SearchGdtf("wash"));
        Assert.Equal(("gdtf-acme-wash-7-150162", "150162", "1.1", true), (e.Id, e.Rid, e.Version, e.Gdtf));
        Assert.Equal([("1ch", 1)], e.Modes);
        Assert.Equal(["Someone"], e.Authors);

        var p = await online.Download(e.Id);
        Assert.Equal(("Acme", "Wash 7", Roles.Dimmer), (p!.Manufacturer, p.Model, p.Channels(0)[0].Role));
        Assert.Equal(1, online.CachedGdtfCount);
        Assert.Single(Directory.GetFiles(dir.Path)); // the download itself is gone

        // The session cookie went back with each request after the login, and the password with none of them.
        Assert.All(web.Sent.Skip(1), s => Assert.Equal("PHPSESSID=abc123; lang=en", s.Cookie));
        Assert.All(web.Sent.Skip(1), s => Assert.DoesNotContain("s3cret", s.Body));
        // Nothing about the account is on disk.
        foreach (var file in Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("s3cret", text);
            Assert.DoesNotContain("abc123", text);
            Assert.DoesNotContain("lamp-person", text);
        }

        online.GdtfLogout();
        Assert.False(online.GdtfLoggedIn);
        Assert.Equal(0, online.GdtfCount);
        Assert.Equal(1, online.CachedGdtfCount); // downloads stay
    }

    [Fact]
    public async Task A_failed_login_says_why()
    {
        using var dir = new TempFolder();
        var web = new FakeWeb();
        web.Json(Login, """{ "result": false, "error": "Wrong user or password" }""");
        var online = new OnlineLibrary(Bundled, dir.Path, web);
        Assert.False(await online.GdtfLogin("x", "y"));
        Assert.Equal("Wrong user or password", online.GdtfError);
        Assert.False(online.GdtfLoggedIn);

        web.Pages.Remove(Login);
        Assert.False(await online.GdtfLogin("x", "y"));
        Assert.Equal("login failed (HTTP 404)", online.GdtfError);
    }

    [Fact]
    public async Task An_expired_session_logs_out()
    {
        using var dir = new TempFolder();
        var web = GdtfWeb();
        var online = new OnlineLibrary(Bundled, dir.Path, web);
        await online.GdtfLogin("x", "y");
        web.Json(List, "{}", HttpStatusCode.Unauthorized);
        Assert.False(await online.RefreshGdtf());
        Assert.False(online.GdtfLoggedIn);
        Assert.Equal("couldn't reach GDTF-Share (HTTP 401)", online.GdtfError);
    }

    [Fact]
    public void Ids_match_the_bundled_librarys()
    {
        Assert.Equal("cameo-auro-beam-150", OnlineLibrary.Sanitise("cameo-auro-beam-150"));
        Assert.Equal("a-b-c", OnlineLibrary.Sanitise("-A  b/C-"));
    }
}
