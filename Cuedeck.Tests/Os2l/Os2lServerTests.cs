using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Dj;
using Cuedeck.Os2l;

namespace Cuedeck.Tests.Os2l;

/// <summary>
/// The OS2L server (sLight's tests/os2l_server_test.gd): the message splitter directly, then the server driven over
/// loopback TCP by a fake DJ-software client on a free port. Advertising uses a fake responder, so nothing goes on the
/// network.
/// </summary>
public sealed class Os2lServerTests
{
    static (List<string> Messages, byte[] Remainder) Split(string text) => Os2lServer.SplitMessages(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Splitting_messages()
    {
        var r = Split("""{"evt":"beat","pos":1}{"evt":"beat","pos":2}""");
        Assert.Equal(2, r.Messages.Count);
        Assert.Empty(r.Remainder);

        r = Split("""{"evt":"beat","pos":1}{"evt":"be""");
        Assert.Single(r.Messages);
        Assert.Equal("""{"evt":"be""", Encoding.UTF8.GetString(r.Remainder));

        // Whitespace and NULs between objects are skipped.
        r = Os2lServer.SplitMessages([.. Encoding.UTF8.GetBytes("\r\n {\"a\":1}\n"), 0, .. Encoding.UTF8.GetBytes("{\"b\":2} ")]);
        Assert.Equal(["""{"a":1}""", """{"b":2}"""], r.Messages);
        Assert.Empty(r.Remainder);

        // Braces and escaped quotes inside strings.
        r = Split("""{"trigger":"deck 1 get_text '{x}'","value":"a \"}\" b"}""");
        Assert.Single(r.Messages);
        Assert.Empty(r.Remainder);
        Assert.Equal("a \"}\" b", Json.Parse(r.Messages[0]).Str("value"));

        Assert.Single(Split("""{"v":{"nested":{"deep":1}}}""").Messages);

        // A multi-byte UTF-8 character split between two reads.
        var whole = Encoding.UTF8.GetBytes("""{"value":"D:/Music/Smart E’s/Tiësto.mp3"}""");
        var cut = Array.IndexOf(whole, (byte)0xE2) + 1; // inside the 3-byte ’
        var r1 = Os2lServer.SplitMessages(whole.AsSpan(0, cut));
        var r2 = Os2lServer.SplitMessages([.. r1.Remainder, .. whole.AsSpan(cut)]);
        Assert.Empty(r1.Messages);
        Assert.Single(r2.Messages);
        Assert.Equal("D:/Music/Smart E’s/Tiësto.mp3", Json.Parse(r2.Messages[0]).Str("value"));
    }

    static JsonObject Msg(string json) => (JsonObject)Json.Parse(json)!;

    [Fact]
    public void Messages_raise_their_events()
    {
        using var s = new Os2lServer();
        (bool, int, double, double)? beat = null;
        (string, string, bool)? btn = null;
        (int, double)? cmd = null;
        (string, JsonNode?)? val = null;
        int valN = 0,
            msgN = 0;
        s.Beat += (c, p, b, st) => beat = (c, p, b, st);
        s.Button += (n, p, on) => btn = (n, p, on);
        s.Command += (id, p) => cmd = (id, p);
        s.ValueChanged += (t, v) =>
        {
            val = (t, v);
            valN++;
        };
        s.Message += (_, _) => msgN++;

        s.HandleMessage(Msg("""{"evt": "beat", "change": true, "pos": 33.0, "bpm": 127.98, "strength": 0.8}"""));
        Assert.Equal((true, 33, 127.98, 0.8), beat);
        Assert.Equal(33, s.LastBeat!.Value.Pos);
        s.HandleMessage(Msg("""{"evt": "btn", "name": "Strobe", "page": "FX", "state": "on"}"""));
        Assert.Equal(("Strobe", "FX", true), btn);
        s.HandleMessage(Msg("""{"evt": "btn", "name": "Strobe", "state": "off"}"""));
        Assert.Equal(("Strobe", "", false), btn);
        s.HandleMessage(Msg("""{"evt": "cmd", "id": 4.0, "param": 0.5}"""));
        Assert.Equal((4, 0.5), cmd);
        s.HandleMessage(Msg("""{"evt": "subscribed", "trigger": "deck 1 get_bpm", "value": 128.0}"""));
        Assert.Equal("deck 1 get_bpm", val!.Value.Item1);
        Assert.Equal(128.0, Json.AsDouble(val.Value.Item2));
        Assert.Equal(128.0, Json.AsDouble(s.DeckValue(1, "get_bpm")));
        s.HandleMessage(Msg("""{"evt": "subscribed", "trigger": "deck 1 get_bpm", "value": 128.0}"""));
        Assert.Equal(1, valN); // unchanged: not raised again
        s.HandleMessage(Msg("""{"evt": "something-new", "x": 1}"""));
        Assert.Equal(7, msgN);
        Assert.Equal(-1, Json.AsInt(s.DeckValue(2, "get_bpm", -1)));
    }

    /// <summary>
    /// Messages exactly as a live VirtualDJ sent them (one read's worth, back to back), so the format's pinned to real
    /// traffic.
    /// </summary>
    const string CAPTURED =
        """{"evt":"subscribed","trigger":"deck 1 get_filepath","value":"D:\\Music\\Imagine Dragons\\Evolve\\04 Believer.m4a"}"""
        + """{"evt":"subscribed","trigger":"deck 1 get_bpm","value":124.994011}"""
        + """{"evt":"subscribed","trigger":"deck 1 get_firstbeat","value":269.955017}"""
        + """{"evt":"subscribed","trigger":"deck 1 play","value":"on"}"""
        + """{"evt":"subscribed","trigger":"deck 1 get_time elapsed absolute","value":374.0}"""
        + """{"evt":"subscribed","trigger":"deck 1 get_beatpos","value":0.217015}"""
        + """{"bpm":124.99,"change":true,"evt":"beat","pos":0.0,"strength":0.8}"""
        + """{"bpm":124.99,"change":false,"evt":"beat","pos":1.0,"strength":0.0}"""
        + """{"evt":"subscribed","trigger":"crossfader","value":0.506849}""";

    [Fact]
    public void Captured_VirtualDJ_traffic()
    {
        var split = Split(CAPTURED);
        Assert.Equal(9, split.Messages.Count);
        Assert.Empty(split.Remainder);
        using var s = new Os2lServer();
        var beats = new List<(bool, int, double, double)>();
        s.Beat += (c, p, b, st) => beats.Add((c, p, b, st));
        foreach (var text in split.Messages)
            s.HandleMessage(Msg(text));
        Assert.Equal(@"D:\Music\Imagine Dragons\Evolve\04 Believer.m4a", Json.AsString(s.DeckValue(1, "get_filepath")));
        Assert.Equal(124.994011, Json.AsDouble(s.DeckValue(1, "get_bpm")), 6);
        Assert.Equal(269.955017, Json.AsDouble(s.DeckValue(1, "get_firstbeat")), 6);
        Assert.Equal("on", Json.AsString(s.DeckValue(1, "play")));
        Assert.Equal([(true, 0, 124.99, 0.8), (false, 1, 124.99, 0.0)], beats);
        Assert.Equal(0.506849, Json.AsDouble(s.Values["crossfader"]), 6);

        // The path VirtualDJ reports finds the song in a database.
        var db = new VirtualDjDatabase();
        var song = new DjSong { Path = "D:/Music/Imagine Dragons/Evolve/04 Believer.m4a", Source = "D:/VirtualDJ/database.xml" };
        db.Add(song);
        Assert.Same(song, db.FindSong(Json.AsString(s.DeckValue(1, "get_filepath"))));
    }

    /// <summary>
    /// Polls the server until <paramref name="done"/> (or the time's up).
    /// </summary>
    static bool Pump(Os2lServer s, Func<bool> done, int timeoutMs = 3000)
    {
        var t = Stopwatch.StartNew();
        while (t.ElapsedMilliseconds < timeoutMs)
        {
            s.Poll();
            if (done())
                return true;
            Thread.Sleep(5);
        }
        return false;
    }

    static void ReadAll(Socket client, List<byte> into)
    {
        var n = client.Available;
        if (n == 0)
            return;
        var buf = new byte[n];
        var got = client.Receive(buf);
        into.AddRange(buf.AsSpan(0, got));
    }

    [Fact]
    public void Over_TCP()
    {
        using var s = new Os2lServer { Subscriptions = Os2lServer.DefaultSubscriptions(2) };
        var events = new List<string>();
        s.ClientConnected += p => events.Add("connected " + p);
        s.ClientDisconnected += p => events.Add("disconnected " + p);
        var beats = new List<int>();
        s.Beat += (_, pos, _, _) => beats.Add(pos);

        Assert.Equal("", s.Start(0, "127.0.0.1"));
        Assert.True(s.Port > 0);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect("127.0.0.1", s.Port);
        Assert.True(Pump(s, () => s.ClientCount == 1));
        Assert.Single(events);
        Assert.StartsWith("connected 127.0.0.1:", events[0]);

        // It subscribes as soon as the client connects.
        var subBytes = new List<byte>();
        Pump(s, () =>
        {
            ReadAll(client, subBytes);
            return Os2lServer.SplitMessages([.. subBytes]).Messages.Count > 0;
        });
        var sub = Json.Parse(Os2lServer.SplitMessages([.. subBytes]).Messages[0]);
        Assert.Equal("subscribe", sub.Str("evt"));
        Assert.Equal("100", sub.Str("frequency"));
        var triggers = sub.Arr("trigger").Select(t => Json.AsString(t)).ToList();
        Assert.Equal(20, triggers.Count);
        Assert.Contains("get_decks", triggers);
        Assert.Contains("deck 2 get_filepath", triggers);

        // A message split across two writes, then two in one write.
        client.Send(Encoding.UTF8.GetBytes("""{"evt":"beat","change":false,"pos":1,"bp"""));
        Pump(s, () => beats.Count > 0, 200);
        Assert.Empty(beats);
        client.Send(Encoding.UTF8.GetBytes("""m":120.0,"strength":50}{"evt":"beat","change":false,"pos":2,"bpm":120.0,"strength":50}"""));
        Assert.True(Pump(s, () => beats.Count == 2));
        Assert.Equal([1, 2], beats);

        client.Send(Encoding.UTF8.GetBytes("""{"evt":"subscribed","trigger":"deck 1 get_filepath","value":"D:\\Music\\Tiësto.mp3"}"""));
        Assert.True(Pump(s, () => s.DeckValue(1, "get_filepath") is not null));
        Assert.Equal(@"D:\Music\Tiësto.mp3", Json.AsString(s.DeckValue(1, "get_filepath")));

        // Server to client.
        s.SendFeedback("Strobe", true, "FX");
        var fbBytes = new List<byte>();
        Pump(s, () =>
        {
            ReadAll(client, fbBytes);
            return Os2lServer.SplitMessages([.. fbBytes]).Messages.Count > 0;
        });
        var fbs = Os2lServer.SplitMessages([.. fbBytes]).Messages;
        Assert.Single(fbs);
        Assert.True(Json.SameTree(Json.Parse("""{"evt": "feedback", "name": "Strobe", "state": "on", "page": "FX"}"""), Json.Parse(fbs[0])));

        client.Shutdown(SocketShutdown.Both);
        client.Close();
        Assert.True(Pump(s, () => s.ClientCount == 0));
        Assert.Equal(2, events.Count);
        Assert.StartsWith("disconnected", events[1]);

        // A second server can't take the same port.
        using (var s2 = new Os2lServer())
            Assert.NotEqual("", s2.Start(s.Port, "127.0.0.1"));
        s.Stop();
        Assert.False(s.IsListening);
        Assert.False(s.IsAdvertised);
        Assert.Equal("not advertised", s.AdvertisingStatus); // a loopback-only listener isn't advertised
    }

    [Fact]
    public void Messages_arriving_a_byte_at_a_time_come_out_whole_and_once()
    {
        var splitter = new Os2lSplitter();
        var text = """ {"a":"x}y","b":{"c":1}}{"d":2} {"e""";
        var got = new List<string>();
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            splitter.Feed([b]);
            got.AddRange(splitter.Take());
        }
        Assert.Equal(["""{"a":"x}y","b":{"c":1}}""", """{"d":2}"""], got);
        Assert.Equal("""{"e""", Encoding.UTF8.GetString(splitter.Pending));
    }

    [Fact]
    public void Sending_more_than_the_socket_takes_keeps_every_message_whole()
    {
        using var s = new Os2lServer { Subscriptions = [] };
        s.Start(0, "127.0.0.1");
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ReceiveBufferSize = 4096 };
        client.Connect("127.0.0.1", s.Port);
        Assert.True(Pump(s, () => s.ClientCount == 1));

        // Far more than the socket buffers hold, sent while the client isn't reading.
        const int COUNT = 400;
        var name = new string('n', 1500);
        for (var i = 0; i < COUNT; i++)
            s.SendFeedback($"{name}{i}", true);

        var bytes = new List<byte>();
        var splitter = new Os2lSplitter();
        var messages = new List<string>();
        Assert.True(
            Pump(
                s,
                () =>
                {
                    var before = bytes.Count;
                    ReadAll(client, bytes);
                    splitter.Feed(bytes.ToArray().AsSpan(before));
                    messages.AddRange(splitter.Take());
                    return messages.Count == COUNT;
                },
                10000
            )
        );
        Assert.Equal(Enumerable.Range(0, COUNT).Select(i => $"{name}{i}"), messages.Select(m => Json.Parse(m).Str("name")));
    }

    [Fact]
    public void A_client_that_never_finishes_a_message_is_dropped()
    {
        using var s = new Os2lServer { Subscriptions = [] };
        s.Start(0, "127.0.0.1");
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect("127.0.0.1", s.Port);
        Assert.True(Pump(s, () => s.ClientCount == 1));
        client.Send(Encoding.UTF8.GetBytes("{\"value\":\"" + new string('x', Os2lServer.MAX_BUFFER + 10)));
        Assert.True(Pump(s, () => s.ClientCount == 0));
    }

    sealed class FakeAdvertiser(bool available = true) : IMdnsAdvertiser
    {
        public bool Available => available;
        public MdnsState State { get; set; }
        public string RegisteredName { get; set; } = "";
        public string Error { get; set; } = "";
        public (string Instance, string Type, int Port)? Started;
        public bool Stopped;

        public bool Start(string instance, string type, int port)
        {
            Started = (instance, type, port);
            State = MdnsState.Registering;
            return true;
        }

        public void Stop()
        {
            Stopped = true;
            State = MdnsState.Idle;
        }
    }

    [Fact]
    public void Advertising()
    {
        var mdns = new FakeAdvertiser();
        using var s = new Os2lServer
        {
            Subscriptions = [],
            ServiceName = "Cuedeck test",
            MakeAdvertiser = () => mdns,
            AdvertiseLoopback = true, // listening on every interface would bring up the firewall's prompt
        };
        var changes = new List<(bool, string)>();
        s.AdvertisingChanged += (on, detail) => changes.Add((on, detail));
        Assert.Equal("", s.Start(0, "127.0.0.1"));
        Assert.Equal(("Cuedeck test", "_os2l._tcp", s.Port), mdns.Started);
        s.Poll();
        Assert.False(s.IsAdvertised);

        // The responder renamed it on a conflict.
        mdns.RegisteredName = "Cuedeck test (2)";
        mdns.State = MdnsState.Registered;
        s.Poll();
        Assert.True(s.IsAdvertised);
        Assert.Equal("advertised as Cuedeck test (2) (_os2l._tcp)", s.AdvertisingStatus);

        s.Stop();
        Assert.True(mdns.Stopped);
        Assert.False(s.IsAdvertised);
        Assert.Equal([(true, "advertised as Cuedeck test (2) (_os2l._tcp)"), (false, "not advertised")], changes);
    }

    [Fact]
    public void Advertising_without_a_responder_points_at_os2lDirectIp()
    {
        using var s = new Os2lServer { Subscriptions = [], MakeAdvertiser = () => new FakeAdvertiser(available: false), AdvertiseLoopback = true };
        s.Start(0, "127.0.0.1");
        Assert.False(s.IsAdvertised);
        Assert.Contains("os2lDirectIp", s.AdvertisingStatus);

        var failing = new FakeAdvertiser();
        s.MakeAdvertiser = () => failing;
        s.Start(0, "127.0.0.1");
        failing.Error = "DnsServiceRegister failed (error 5)";
        failing.State = MdnsState.Failed;
        s.Poll();
        Assert.False(s.IsAdvertised);
        Assert.Equal("advertising failed: DnsServiceRegister failed (error 5)", s.AdvertisingStatus);
    }
}
