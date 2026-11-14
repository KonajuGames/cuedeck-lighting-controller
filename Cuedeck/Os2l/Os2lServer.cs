using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Cuedeck.Common;

namespace Cuedeck.Os2l;

/// <summary>
/// An OS2L (Open Sound to Light) server: the lighting end of the link that DJ software (VirtualDJ, Cuepoint) connects
/// to. OS2L is JSON objects over TCP, back to back with nothing between them. The DJ software sends <c>beat</c> (every
/// beat: its number, the tempo, its strength), <c>btn</c> (a button mapped to the lighting software) and <c>cmd</c> (a
/// numbered command with a value); the server can send <c>feedback</c> back to light a DJ-side button.
/// <para>
/// VirtualDJ also lets the server subscribe to VDJscript queries ("deck 1 get_bpm", "deck 2 get_filepath"…), which
/// it then reports whenever they change; those raise <see cref="ValueChanged"/> and are kept in <see cref="Values"/>.
/// <see cref="Subscriptions"/> go to every client as it connects. DJ software finds the server by mDNS
/// (<c>_os2l._tcp</c>), advertised through the system's own responder (<see cref="IMdnsAdvertiser"/>); without one,
/// point it at the server directly (VirtualDJ's <c>os2lDirectIp</c>). Call <see cref="Poll"/> often (each tick) to
/// take connections and messages. sLight's <c>Os2lServer</c>.
/// </para>
/// </summary>
sealed class Os2lServer : IDisposable
{
    public const int DEFAULT_PORT = 9960;
    public const string SERVICE_TYPE = "_os2l._tcp";

    /// <summary>
    /// A client that sends this much without completing a message is dropped rather than buffered forever.
    /// </summary>
    public const int MAX_BUFFER = 1 << 20;

    sealed class Client(Socket socket, string name)
    {
        public Socket Socket { get; } = socket;
        public string Name { get; } = name;
        public byte[] Buffer { get; set; } = [];
    }

    TcpListener? _listener;
    readonly List<Client> _clients = [];
    IMdnsAdvertiser? _mdns;
    MdnsState _mdnsState = MdnsState.Idle;

    public event Action<string>? ClientConnected;
    public event Action<string>? ClientDisconnected;

    /// <summary>
    /// A beat of the playing deck: whether it doesn't carry on the rhythm before (a tempo change, a seek, playback
    /// starting; VirtualDJ sets it on many beats in a song, so it doesn't mean a new song), its number from the
    /// song's first beat (-1 just before it), the deck's tempo, and its strength 0 to 1.
    /// </summary>
    public event Action<bool, int, double, double>? Beat;

    public event Action<string, string, bool>? Button;
    public event Action<int, double>? Command;

    /// <summary>
    /// A subscribed query reported a value (only one that differs from what's kept).
    /// </summary>
    public event Action<string, JsonNode?>? ValueChanged;

    /// <summary>
    /// Every message received, after the events above.
    /// </summary>
    public event Action<JsonObject, string>? Message;

    public event Action<bool, string>? StateChanged;

    /// <summary>
    /// The mDNS advertisement came up, failed or was withdrawn.
    /// </summary>
    public event Action<bool, string>? AdvertisingChanged;

    /// <summary>
    /// VDJscript queries subscribed to as each client connects (VirtualDJ's; other OS2L clients ignore them). Empty
    /// for none.
    /// </summary>
    public List<string> Subscriptions { get; set; } = DefaultSubscriptions(2);

    /// <summary>
    /// How often VirtualDJ should report subscribed values, milliseconds.
    /// </summary>
    public int SubscriptionFrequencyMs { get; set; } = 100;

    /// <summary>
    /// Advertise over mDNS when it starts (never for a loopback-only listener, which nothing else could reach).
    /// </summary>
    public bool Advertise { get; set; } = true;

    /// <summary>
    /// Advertise a loopback-only listener too: for tests, which then needn't listen where the firewall would ask.
    /// </summary>
    internal bool AdvertiseLoopback { get; set; }

    /// <summary>
    /// The name DJ software shows; the responder may add a number if it's taken.
    /// </summary>
    public string ServiceName { get; set; } = "Cuedeck";

    /// <summary>
    /// Makes the advertiser (the system's DNS-SD on Windows; null where there's none).
    /// </summary>
    public Func<IMdnsAdvertiser?> MakeAdvertiser { get; set; } = () => OperatingSystem.IsWindows() ? new WindowsMdnsAdvertiser() : null;

    /// <summary>
    /// The last value reported for each subscribed query.
    /// </summary>
    public Dictionary<string, JsonNode?> Values { get; } = [];

    /// <summary>
    /// The last beat, and when it came (<see cref="Environment.TickCount64"/>); null before any.
    /// </summary>
    public (bool Change, int Pos, double Bpm, double Strength, long Time)? LastBeat { get; private set; }

    public bool IsListening => _listener is not null;
    public int Port { get; private set; }
    public int ClientCount => _clients.Count;

    /// <summary>
    /// The queries subscribed to by default for <paramref name="decks"/> decks: the crossfader and how many decks the
    /// skin shows, then each deck's file, tempo, beat position, first beat (milliseconds), play, level, elapsed time
    /// (milliseconds), loop and loop length.
    /// </summary>
    public static List<string> DefaultSubscriptions(int decks)
    {
        var output = new List<string> { "crossfader", "get_decks" };
        for (var i = 1; i <= decks; i++)
            foreach (var q in new[] { "get_filepath", "get_bpm", "get_beatpos", "get_firstbeat", "play", "level", "get_time elapsed absolute", "loop", "get_loop" })
                output.Add($"deck {i} {q}");
        return output;
    }

    /// <summary>
    /// Starts listening on <paramref name="port"/> (0 for any free one; see <see cref="Port"/>), on every interface
    /// or just <paramref name="bindAddress"/>. Returns "" or what went wrong.
    /// </summary>
    public string Start(int port = DEFAULT_PORT, string bindAddress = "*")
    {
        Stop();
        var address = bindAddress == "*" ? IPAddress.Any : IPAddress.Parse(bindAddress == "localhost" ? "127.0.0.1" : bindAddress);
        try
        {
            var listener = new TcpListener(address, port);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            _listener = listener;
        }
        catch (SocketException e)
        {
            StateChanged?.Invoke(false, $"port :{port} unavailable ({e.Message.TrimEnd('.')})");
            return e.Message;
        }
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        StateChanged?.Invoke(true, $"listening on :{Port}");
        if (Advertise && (!IPAddress.IsLoopback(address) || AdvertiseLoopback))
            StartAdvertising();
        return "";
    }

    public void Stop()
    {
        StopAdvertising();
        foreach (var c in _clients.ToList())
        {
            c.Socket.Dispose();
            ClientDisconnected?.Invoke(c.Name);
        }
        _clients.Clear();
        if (_listener is not null)
        {
            _listener.Stop();
            _listener = null;
            StateChanged?.Invoke(false, "off");
        }
        Port = 0;
    }

    public void Dispose() => Stop();

    /// <summary>
    /// A subscribed query's kept value, as <c>DeckValue(1, "get_bpm")</c>; null if none's come.
    /// </summary>
    public JsonNode? DeckValue(int deck, string query, JsonNode? fallback = null) => Values.GetValueOrDefault($"deck {deck} {query}", fallback);

    /// <summary>
    /// Takes new connections and handles everything received.
    /// </summary>
    public void Poll()
    {
        if (_listener is null)
            return;
        PollAdvertising();
        while (_listener.Pending())
        {
            var socket = _listener.AcceptSocket();
            socket.NoDelay = true;
            socket.Blocking = false;
            var ep = (IPEndPoint)socket.RemoteEndPoint!;
            var c = new Client(socket, $"{ep.Address}:{ep.Port}");
            _clients.Add(c);
            ClientConnected?.Invoke(c.Name);
            if (Subscriptions.Count > 0)
                SendTo(c, Subscribe(Subscriptions));
        }
        foreach (var c in _clients.ToList())
        {
            if (!Read(c))
            {
                Drop(c);
                continue;
            }
            Drain(c);
            if (c.Buffer.Length > MAX_BUFFER)
            {
                System.Diagnostics.Trace.TraceWarning($"OS2L: {c.Name} sent {c.Buffer.Length} bytes without a complete message; dropping");
                Drop(c);
            }
        }
    }

    /// <summary>
    /// Reads what's waiting. Returns false once the client's gone.
    /// </summary>
    static bool Read(Client c)
    {
        try
        {
            // Readable with nothing to read: the other end closed.
            if (c.Socket.Poll(0, SelectMode.SelectRead) && c.Socket.Available == 0)
                return false;
            var n = c.Socket.Available;
            if (n == 0)
                return true;
            var got = new byte[n];
            var read = c.Socket.Receive(got);
            c.Buffer = [.. c.Buffer, .. got.AsSpan(0, read)];
            return true;
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock)
        {
            return true;
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    void Drop(Client c)
    {
        c.Socket.Dispose();
        _clients.Remove(c);
        ClientDisconnected?.Invoke(c.Name);
    }

    void Drain(Client c)
    {
        var (messages, remainder) = SplitMessages(c.Buffer);
        c.Buffer = remainder;
        foreach (var text in messages)
        {
            if (Json.Parse(text) is JsonObject msg)
                HandleMessage(msg, c.Name);
            else
                System.Diagnostics.Trace.TraceWarning($"OS2L: unparseable message from {c.Name}: {text[..Math.Min(text.Length, 200)]}");
        }
    }

    /// <summary>
    /// Raises the events for one message.
    /// </summary>
    public void HandleMessage(JsonObject msg, string peer = "")
    {
        switch (msg.Str("evt"))
        {
            case "beat":
                LastBeat = (Truthy(msg["change"]), (int)msg.Num("pos"), msg.Num("bpm"), msg.Num("strength"), Environment.TickCount64);
                var b = LastBeat.Value;
                Beat?.Invoke(b.Change, b.Pos, b.Bpm, b.Strength);
                break;
            case "btn":
                Button?.Invoke(Json.AsString(msg["name"]), Json.AsString(msg["page"]), Truthy(msg["state"]));
                break;
            case "cmd":
                Command?.Invoke((int)msg.Num("id"), msg.Num("param"));
                break;
        }
        // VirtualDJ's replies to subscribe carry the query and its value.
        if (msg.ContainsKey("trigger") && msg.ContainsKey("value"))
        {
            var t = Json.AsString(msg["trigger"]);
            var v = msg["value"]?.DeepClone();
            if (!Values.TryGetValue(t, out var was) || !JsonNode.DeepEquals(was, v))
            {
                Values[t] = v;
                ValueChanged?.Invoke(t, v);
            }
        }
        Message?.Invoke(msg, peer);
    }

    /// <summary>
    /// Godot's truthiness of an OS2L on/off: true, a number not 0, or "on", "true", "yes", "1".
    /// </summary>
    static bool Truthy(JsonNode? v) =>
        v is JsonValue j
        && j.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.Number => j.GetValue<double>() != 0,
            System.Text.Json.JsonValueKind.String => j.GetValue<string>().ToLowerInvariant() is "on" or "true" or "yes" or "1",
            _ => false,
        };

    /// <summary>
    /// Splits a byte stream of back-to-back JSON objects into the complete ones' texts, and the rest (an incomplete
    /// object to keep for next time). Braces in strings, and escaped quotes, are handled; whatever's between objects
    /// (whitespace, newlines, NULs) is skipped. On bytes, so a UTF-8 character split between reads is fine.
    /// </summary>
    public static (List<string> Messages, byte[] Remainder) SplitMessages(ReadOnlySpan<byte> buf)
    {
        var messages = new List<string>();
        int depth = 0, start = -1, consumed = 0;
        bool inString = false, escaped = false;
        for (var i = 0; i < buf.Length; i++)
        {
            var b = buf[i];
            if (inString)
            {
                if (escaped)
                    escaped = false;
                else if (b == '\\')
                    escaped = true;
                else if (b == '"')
                    inString = false;
            }
            else if (b == '"')
            {
                if (depth > 0)
                    inString = true;
            }
            else if (b == '{')
            {
                if (depth == 0)
                    start = i;
                depth++;
            }
            else if (b == '}')
            {
                if (depth > 0 && --depth == 0)
                {
                    messages.Add(Encoding.UTF8.GetString(buf[start..(i + 1)]));
                    consumed = i + 1;
                    start = -1;
                }
            }
            else if (depth == 0)
                consumed = i + 1; // junk between objects
        }
        return (messages, buf[consumed..].ToArray());
    }

    // --------------------------------------------------------------- sending --

    /// <summary>
    /// Lights (or unlights) a DJ-side button mapped to <paramref name="name"/> on <paramref name="page"/>.
    /// </summary>
    public void SendFeedback(string name, bool on, string page = "")
    {
        var msg = new JsonObject
        {
            ["evt"] = "feedback",
            ["name"] = name,
            ["state"] = on ? "on" : "off",
        };
        if (page != "")
            msg["page"] = page;
        Send(msg);
    }

    /// <summary>
    /// Subscribes every client to more queries (kept for clients that connect later too).
    /// </summary>
    public void SubscribeTo(IEnumerable<string> triggers)
    {
        var list = triggers.ToList();
        foreach (var t in list)
            if (!Subscriptions.Contains(t))
                Subscriptions.Add(t);
        Send(Subscribe(list));
    }

    JsonObject Subscribe(IEnumerable<string> triggers) =>
        new()
        {
            ["evt"] = "subscribe",
            ["trigger"] = new JsonArray([.. triggers.Select(t => (JsonNode)t)]),
            ["frequency"] = SubscriptionFrequencyMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

    /// <summary>
    /// Sends a message to every client.
    /// </summary>
    public void Send(JsonObject msg)
    {
        foreach (var c in _clients.ToList())
            SendTo(c, msg);
    }

    static void SendTo(Client c, JsonObject msg)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
            var sent = 0;
            while (sent < bytes.Length)
                sent += c.Socket.Send(bytes, sent, bytes.Length - sent, SocketFlags.None);
        }
        catch (SocketException) { } // a client gone: noticed on the next poll
    }

    // ----------------------------------------------------------- advertising --

    public bool IsAdvertised => _mdnsState == MdnsState.Registered;

    /// <summary>
    /// How the advertising stands, as "advertised as Cuedeck (_os2l._tcp)" or why not.
    /// </summary>
    public string AdvertisingStatus { get; private set; } = "not advertised";

    void StartAdvertising()
    {
        var mdns = MakeAdvertiser();
        if (mdns is null || !mdns.Available)
        {
            SetMdns(MdnsState.Idle, "not advertised: no DNS-SD service on this system — set VirtualDJ's os2lDirectIp");
            return;
        }
        if (!mdns.Start(ServiceName, SERVICE_TYPE, Port))
        {
            SetMdns(MdnsState.Failed, "advertising failed: " + mdns.Error);
            return;
        }
        _mdns = mdns;
        _mdnsState = MdnsState.Idle;
        PollAdvertising();
    }

    void PollAdvertising()
    {
        if (_mdns is null)
            return;
        var st = _mdns.State;
        if (st == _mdnsState)
            return;
        if (st == MdnsState.Registered)
            SetMdns(st, $"advertised as {_mdns.RegisteredName} ({SERVICE_TYPE})");
        else if (st == MdnsState.Failed)
            SetMdns(st, "advertising failed: " + _mdns.Error);
        else
            _mdnsState = st;
    }

    void StopAdvertising()
    {
        if (_mdns is null)
            return;
        _mdns.Stop();
        _mdns = null;
        SetMdns(MdnsState.Idle, "not advertised");
    }

    void SetMdns(MdnsState st, string status)
    {
        _mdnsState = st;
        AdvertisingStatus = status;
        AdvertisingChanged?.Invoke(st == MdnsState.Registered, status);
    }
}
