using System.Net.Sockets;
using Cuedeck.Common;

namespace Cuedeck.Dmx;

/// <summary>
/// Where a universe's frames go.
/// </summary>
enum OutputMode
{
    ArtNet,
    Sacn,
    Usb,
}

/// <summary>
/// A fixture's virtual dimmer: a level that scales its colour channels in the output, for a fixture without a DIMMER
/// of its own.
/// </summary>
sealed class VirtualDimmer(IReadOnlyList<int> channels, double level)
{
    /// <summary>
    /// The universe channels it scales (0-based).
    /// </summary>
    public IReadOnlyList<int> Channels { get; set; } = channels;

    /// <summary>
    /// 0 to 1.
    /// </summary>
    public double Level { get; set; } = level;
}

/// <summary>
/// One universe: its 512 channels as the operator set them (<see cref="Data"/>), the frame worked out from them and
/// every layer over them (<see cref="Output"/>), and where it's sent.
/// </summary>
sealed class DmxUniverse : IDisposable
{
    public const int Size = 512;

    UdpClient? _udp;
    int _sequence;
    int _sacnSequence;

    public string TargetIp { get; private set; } = "127.0.0.1";
    public int TargetPort { get; private set; } = ArtNetPacket.Port;

    /// <summary>
    /// The universe number written into the packet (Art-Net 0 to 32767, sACN 1 to 63999). Separate from the slot's
    /// place in the app: several slots can even send the same number to different addresses.
    /// </summary>
    public int Number { get; set; }

    public OutputMode Mode { get; private set; } = OutputMode.ArtNet;

    /// <summary>
    /// Whether its socket is open, so frames can go out.
    /// </summary>
    public bool Connected { get; private set; }

    /// <summary>
    /// The USB DMX interface's serial number, for <see cref="OutputMode.Usb"/>.
    /// </summary>
    public string UsbSerial { get; set; } = "";

    /// <summary>
    /// The USB DMX interface's type, for <see cref="OutputMode.Usb"/>.
    /// </summary>
    public int UsbMode { get; set; }

    /// <summary>
    /// The E1.31 priority, 0 to 200.
    /// </summary>
    public int SacnPriority { get; set; } = 100;

    /// <summary>
    /// An sACN receiver to send to directly; "" to multicast to the universe's group.
    /// </summary>
    public string SacnUnicastIp { get; set; } = "";

    /// <summary>
    /// The channels as set by the fixture controls and cue fades.
    /// </summary>
    public byte[] Data { get; set; } = new byte[Size];

    /// <summary>
    /// The last frame worked out: <see cref="Data"/> with every layer, virtual dimmer and the grand master applied.
    /// What's sent, and what the 3D view shows.
    /// </summary>
    public byte[] Output { get; private set; } = new byte[Size];

    /// <summary>
    /// The virtual dimmers of fixtures without a DIMMER channel, by the fixture's id.
    /// </summary>
    public Dictionary<int, VirtualDimmer> VirtualDimmers { get; } = [];

    /// <summary>
    /// Points the universe's Art-Net output at <paramref name="ip"/>, reopening its socket.
    /// </summary>
    public void SetTarget(string ip, int port = ArtNetPacket.Port)
    {
        TargetIp = ip;
        TargetPort = port;
        OpenSocket();
    }

    /// <summary>
    /// Switches where the universe goes and reopens its socket to match. For sACN, set <see cref="Number"/> and
    /// <see cref="SacnUnicastIp"/> first.
    /// </summary>
    public void SetOutputMode(OutputMode mode)
    {
        Mode = mode;
        OpenSocket();
    }

    void OpenSocket()
    {
        _udp?.Dispose();
        _udp = null;
        Connected = false;
        if (Mode == OutputMode.Usb)
            return; // the USB interface's own driver has it
        var ip = TargetIp;
        var port = TargetPort;
        if (Mode == OutputMode.Sacn)
        {
            ip = SacnUnicastIp != "" ? SacnUnicastIp : Sacn.MulticastIp(Math.Max(Number, 1));
            port = Sacn.Port;
        }
        try
        {
            _udp = new UdpClient();
            _udp.Connect(ip, port);
            Connected = true;
        }
        catch (Exception e) when (e is SocketException or ArgumentException)
        {
            _udp?.Dispose();
            _udp = null;
        }
    }

    /// <summary>
    /// Sets channel <paramref name="channel"/> (0 to 511) to <paramref name="value"/>, clamped to 0 to 255.
    /// </summary>
    public void SetChannel(int channel, int value)
    {
        if (channel is >= 0 and < Size)
            Data[channel] = (byte)Math.Clamp(value, 0, 255);
    }

    public int GetChannel(int channel) => channel is >= 0 and < Size ? Data[channel] : 0;

    public void SetAll(int value) => Array.Fill(Data, (byte)Math.Clamp(value, 0, 255));

    /// <summary>
    /// Gives fixture <paramref name="key"/> a virtual dimmer over <paramref name="channels"/>, keeping its level if it
    /// had one; no channels removes it.
    /// </summary>
    public void SetVirtualDimmer(int key, IReadOnlyList<int> channels)
    {
        if (channels.Count == 0)
        {
            VirtualDimmers.Remove(key);
            return;
        }
        VirtualDimmers[key] = new VirtualDimmer(channels, VirtualDimmerLevel(key));
    }

    public void RemoveVirtualDimmer(int key) => VirtualDimmers.Remove(key);

    public bool HasVirtualDimmer(int key) => VirtualDimmers.ContainsKey(key);

    public void SetVirtualDimmerLevel(int key, double level)
    {
        if (VirtualDimmers.TryGetValue(key, out var vd))
            vd.Level = Math.Clamp(level, 0, 1);
    }

    /// <summary>
    /// A virtual dimmer's level, or 1 (open) if there's none.
    /// </summary>
    public double VirtualDimmerLevel(int key) => VirtualDimmers.TryGetValue(key, out var vd) ? vd.Level : 1;

    /// <summary>
    /// Works out <see cref="Output"/> from <see cref="Data"/>: the effect and chase <paramref name="overrides"/>
    /// replace channels, then the virtual dimmers and <paramref name="scales"/> (group masters) scale them, then the
    /// <paramref name="top"/> layer (the live flashes) replaces channels again, and last the grand master
    /// <paramref name="master"/> scales the lot. <see cref="Data"/> is left as it was, and nothing is sent.
    /// </summary>
    public void ComputeOutput(
        double master = 1,
        IReadOnlyDictionary<int, int>? overrides = null,
        IReadOnlyDictionary<int, int>? top = null,
        IReadOnlyDictionary<int, double>? scales = null
    )
    {
        var data = (byte[])Data.Clone();
        if (overrides is not null)
            foreach (var (c, v) in overrides)
                if (c is >= 0 and < Size)
                    data[c] = (byte)Math.Clamp(v, 0, 255);
        foreach (var vd in VirtualDimmers.Values)
        {
            if (vd.Level >= 1)
                continue;
            foreach (var c in vd.Channels)
                if (c is >= 0 and < Size)
                    data[c] = (byte)Gd.RoundToInt(data[c] * vd.Level);
        }
        if (scales is not null)
            foreach (var (c, factor) in scales)
                if (c is >= 0 and < Size)
                    data[c] = (byte)Gd.RoundToInt(data[c] * Math.Clamp(factor, 0, 1));
        if (top is not null)
            foreach (var (c, v) in top)
                if (c is >= 0 and < Size)
                    data[c] = (byte)Math.Clamp(v, 0, 255);
        if (master < 1)
            for (var i = 0; i < data.Length; i++)
                data[i] = (byte)(int)(data[i] * master);
        Output = data;
    }

    /// <summary>
    /// Sends <see cref="Output"/> as one ArtDMX packet. Call 30 to 40 times a second: receivers expect a steady
    /// stream, as real DMX512 is.
    /// </summary>
    public void Transmit()
    {
        if (!Connected || _udp is null)
            return;
        // 1 to 255, skipping 0, which would turn sequencing off.
        _sequence = (_sequence % 255) + 1;
        Send(ArtNetPacket.Dmx(Number, Output, _sequence));
    }

    /// <summary>
    /// Sends <see cref="Output"/> as one E1.31 Data packet, from source <paramref name="cid"/>.
    /// </summary>
    public void TransmitSacn(ReadOnlySpan<byte> cid)
    {
        if (!Connected || _udp is null)
            return;
        _sacnSequence = (_sacnSequence + 1) & 0xFF;
        Send(Sacn.DataPacket(Math.Max(Number, 1), Output, cid, "Cuedeck", SacnPriority, _sacnSequence));
    }

    void Send(byte[] packet)
    {
        try
        {
            _udp?.Send(packet, packet.Length);
        }
        catch (SocketException)
        {
            // Nothing listening, or the network's gone: the next frame tries again.
        }
    }

    public void Dispose()
    {
        _udp?.Dispose();
        _udp = null;
        Connected = false;
    }
}
