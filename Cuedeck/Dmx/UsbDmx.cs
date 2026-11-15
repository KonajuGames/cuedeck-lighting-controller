using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cuedeck.Dmx;

/// <summary>
/// What kind of USB DMX interface: as sLight's modes (a show keeps the number). uDMX isn't supported here (it needs
/// libusb), so it's sent as Open DMX if it names an FTDI interface.
/// </summary>
enum UsbDmxMode
{
    Auto = 0,
    OpenDmx = 1,
    EnttecPro = 2,
    Udmx = 3,
}

/// <summary>
/// A USB DMX interface found: its serial number (what a universe is routed by), what it calls itself, and the kind
/// it looks like.
/// </summary>
sealed record UsbDevice(string Serial, string Description, UsbDmxMode GuessedMode);

/// <summary>
/// The FTDI D2XX calls USB DMX uses, each returning D2XX's FT_STATUS (0 for OK): stood in for by the tests.
/// </summary>
interface IFtdi
{
    IReadOnlyList<(string Serial, string Description)> Devices();

    /// <summary>
    /// Opens the interface with that serial number; 0 if it can't.
    /// </summary>
    nint Open(string serial);

    void Close(nint handle);
    uint ResetDevice(nint handle);
    uint SetUsbParameters(nint handle, uint inSize, uint outSize);
    uint SetLatencyTimer(nint handle, byte ms);
    uint SetFlowControl(nint handle, ushort flow);
    uint SetBaudRate(nint handle, uint baud);
    uint SetDataCharacteristics(nint handle, byte bits, byte stopBits, byte parity);
    uint ClrRts(nint handle);
    uint SetBreak(nint handle, bool on);
    uint Purge(nint handle, uint mask);
    uint Write(nint handle, ReadOnlySpan<byte> data, out uint written);
}

/// <summary>
/// Universes sent to USB DMX interfaces through FTDI's D2XX driver (Enttec's Open DMX USB and DMX USB Pro, DMXking's,
/// and other FTDI-based ones), each streamed from a thread of its own at a steady rate; the console hands each its
/// latest frame every tick. Without the driver, <see cref="Available"/> is false and everything here does nothing.
/// sLight's <c>UsbDmx</c> bridge and its native <c>UsbDmxOutput</c>, for D2XX.
/// </summary>
sealed class UsbDmx(IFtdi? ftdi, bool stream = true) : IDisposable
{
    public static readonly string[] ModeNames = ["Auto", "Open DMX", "Enttec Pro"];

    readonly Dictionary<string, UsbDmxOutput> _outputs = [];
    readonly Dictionary<string, string> _failures = [];

    public bool Available => ftdi is not null;

    public List<UsbDevice> ListDevices() =>
        ftdi is null ? [] : [.. ftdi.Devices().Where(d => d.Serial != "").Select(d => new UsbDevice(d.Serial, d.Description, Guess(d.Description)))];

    /// <summary>
    /// The kind an interface looks like from what it calls itself: a Pro (or DMXking's, which speak its language), else
    /// Open DMX.
    /// </summary>
    public static UsbDmxMode Guess(string description)
    {
        var d = description.ToUpperInvariant();
        return d.Contains("PRO") || d.Contains("DMXKING") || d.Contains("ULTRADMX") ? UsbDmxMode.EnttecPro : UsbDmxMode.OpenDmx;
    }

    public bool Route(string serial, int mode) => Route(serial, (UsbDmxMode)mode);

    /// <summary>
    /// Opens (or opens again) interface <paramref name="serial"/> as <paramref name="mode"/> (Auto: as it looks) and
    /// starts streaming to it. Returns whether it is.
    /// </summary>
    public bool Route(string serial, UsbDmxMode mode)
    {
        if (ftdi is null || serial == "")
            return false;
        Unroute(serial);
        _failures.Remove(serial);
        if (mode is UsbDmxMode.Auto or UsbDmxMode.Udmx)
            mode = mode == UsbDmxMode.Auto ? ListDevices().FirstOrDefault(d => d.Serial == serial)?.GuessedMode ?? UsbDmxMode.OpenDmx : UsbDmxMode.OpenDmx;
        var handle = ftdi.Open(serial);
        if (handle == 0)
        {
            _failures[serial] = "could not open " + serial;
            return false;
        }
        ftdi.ResetDevice(handle);
        ftdi.SetUsbParameters(handle, 4096, 4096);
        ftdi.SetLatencyTimer(handle, 1);
        ftdi.SetFlowControl(handle, 0);
        if (mode == UsbDmxMode.OpenDmx)
        {
            // DMX's 250 kbaud, 8 bits, no parity, 2 stop bits; the break made by hand each frame.
            ftdi.SetBaudRate(handle, 250000);
            ftdi.SetDataCharacteristics(handle, 8, 2, 0);
            ftdi.ClrRts(handle);
            ftdi.SetBreak(handle, false);
        }
        ftdi.Purge(handle, 3);
        var output = new UsbDmxOutput(ftdi, handle, mode, serial);
        _outputs[serial] = output;
        if (stream)
            output.Start();
        return true;
    }

    public void Unroute(string serial)
    {
        if (_outputs.Remove(serial, out var o))
            o.Dispose();
    }

    public bool IsSending(string serial) => _outputs.ContainsKey(serial);

    public string Status(string serial) =>
        ftdi is null ? "USB DMX needs FTDI's D2XX driver"
        : _outputs.TryGetValue(serial, out var o) ? o.Status
        : _failures.GetValueOrDefault(serial, "not open");

    /// <summary>
    /// Hands interface <paramref name="serial"/> its universe's 512 channels (every tick).
    /// </summary>
    public void Send(string serial, byte[] dmx512)
    {
        if (_outputs.TryGetValue(serial, out var o))
            o.SetFrame(dmx512);
    }

    internal UsbDmxOutput? Output(string serial) => _outputs.GetValueOrDefault(serial);

    public void Dispose()
    {
        foreach (var o in _outputs.Values)
            o.Dispose();
        _outputs.Clear();
    }
}

/// <summary>
/// One open interface: its latest frame, streamed by a thread of its own at <see cref="FPS"/> frames a second. Open
/// DMX's frames get their break and mark from here, then aren't sent again until the last has had time to go out
/// (23 ms at 250 kbaud), so a hiccup can't cut one short; the Pro is sent "Send DMX Packet" messages and does its own
/// timing.
/// </summary>
sealed class UsbDmxOutput(IFtdi ftdi, nint handle, UsbDmxMode mode, string serial) : IDisposable
{
    public const int FPS = 40;

    readonly byte[] _frame = new byte[512];
    readonly Lock _lock = new();
    Thread? _thread;
    volatile bool _running;
    bool _linkOk = true;
    string _status = $"open ({(mode == UsbDmxMode.EnttecPro ? "Enttec Pro" : "Open DMX")}) — {serial}";

    public UsbDmxMode Mode { get; } = mode;

    public string Status
    {
        get
        {
            lock (_lock)
                return _status;
        }
    }

    /// <summary>
    /// A copy of the frame it's sending.
    /// </summary>
    public byte[] Frame
    {
        get
        {
            lock (_lock)
                return (byte[])_frame.Clone();
        }
    }

    public void SetFrame(ReadOnlySpan<byte> dmx)
    {
        lock (_lock)
            dmx[..Math.Min(dmx.Length, 512)].CopyTo(_frame);
    }

    public void Start()
    {
        _running = true;
        _thread = new Thread(Stream) { IsBackground = true, Name = "USB DMX " + serial };
        _thread.Start();
    }

    void Stream()
    {
        var clock = Stopwatch.StartNew();
        var next = 0.0;
        while (_running)
        {
            if (!WriteFrame())
            {
                Thread.Sleep(250);
                continue;
            }
            var written = clock.Elapsed.TotalSeconds;
            next += 1.0 / FPS;
            if (Mode == UsbDmxMode.OpenDmx)
                next = Math.Max(next, written + 0.023); // the last frame's still going out
            else if (next < written)
                next = written; // fell behind: don't spiral
            var wait = next - clock.Elapsed.TotalSeconds;
            if (wait > 0)
                Thread.Sleep(TimeSpan.FromSeconds(wait));
        }
    }

    /// <summary>
    /// Sends the latest frame once. Returns false (with why in <see cref="Status"/>) if the interface wouldn't take it.
    /// </summary>
    internal bool WriteFrame()
    {
        Span<byte> frame = stackalloc byte[513]; // the start code, then the channels
        lock (_lock)
            _frame.CopyTo(frame[1..]);
        string? failed;
        if (Mode == UsbDmxMode.EnttecPro)
            failed = Check(ftdi.Write(handle, Packet(frame), out var wrote), wrote, 518);
        else
        {
            // The break (at least 88 µs), the mark after it, then the frame.
            var st = ftdi.SetBreak(handle, true);
            failed = st != 0 ? $"write failed — SetBreakOn status {st}" : null;
            if (failed is null)
            {
                SleepMicro(120);
                st = ftdi.SetBreak(handle, false);
                failed = st != 0 ? $"write failed — SetBreakOff status {st}" : null;
            }
            if (failed is null)
            {
                SleepMicro(12);
                failed = Check(ftdi.Write(handle, frame, out var wrote), wrote, 513);
            }
        }
        lock (_lock)
        {
            if (failed is not null)
            {
                _linkOk = false;
                _status = failed;
                return false;
            }
            if (!_linkOk || !_status.StartsWith("streaming", StringComparison.Ordinal))
                _status = "streaming";
            _linkOk = true;
        }
        return true;
    }

    static string? Check(uint status, uint wrote, int length) => status != 0 || wrote != length ? $"write failed — FT_Write status {status}, wrote {wrote} of {length}" : null;

    /// <summary>
    /// The Pro's "Send DMX Packet" (label 6): 0x7E, 6, the length low and high, the start code and channels, 0xE7.
    /// </summary>
    public static byte[] Packet(ReadOnlySpan<byte> frame513)
    {
        var m = new byte[4 + frame513.Length + 1];
        (m[0], m[1], m[2], m[3]) = (0x7E, 6, (byte)(frame513.Length & 0xFF), (byte)(frame513.Length >> 8));
        frame513.CopyTo(m.AsSpan(4));
        m[^1] = 0xE7;
        return m;
    }

    static void SleepMicro(int us)
    {
        var until = Stopwatch.GetTimestamp() + us * Stopwatch.Frequency / 1_000_000;
        while (Stopwatch.GetTimestamp() < until)
            Thread.SpinWait(10);
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(500);
        ftdi.Close(handle);
    }
}

/// <summary>
/// FTDI's D2XX driver (ftd2xx.dll), found when it's made; null where it isn't installed.
/// </summary>
sealed unsafe class Ftd2xx : IFtdi
{
    [StructLayout(LayoutKind.Sequential)]
    struct DeviceNode
    {
        public uint Flags,
            Type,
            Id,
            LocId;
        public fixed byte SerialNumber[16];
        public fixed byte Description[64];
        public nint Handle;
    }

    readonly delegate* unmanaged[Stdcall]<uint*, uint> _createList;
    readonly delegate* unmanaged[Stdcall]<DeviceNode*, uint*, uint> _getList;
    readonly delegate* unmanaged[Stdcall]<byte*, uint, nint*, uint> _openEx;
    readonly delegate* unmanaged[Stdcall]<nint, uint> _close,
        _reset,
        _clrRts,
        _breakOn,
        _breakOff;
    readonly delegate* unmanaged[Stdcall]<nint, uint, uint, uint> _usbParams;
    readonly delegate* unmanaged[Stdcall]<nint, byte, uint> _latency;
    readonly delegate* unmanaged[Stdcall]<nint, ushort, byte, byte, uint> _flow;
    readonly delegate* unmanaged[Stdcall]<nint, uint, uint> _baud,
        _purge;
    readonly delegate* unmanaged[Stdcall]<nint, byte, byte, byte, uint> _data;
    readonly delegate* unmanaged[Stdcall]<nint, byte*, uint, uint*, uint> _write;

    Ftd2xx(nint lib)
    {
        nint E(string name) => NativeLibrary.GetExport(lib, name);
        _createList = (delegate* unmanaged[Stdcall]<uint*, uint>)E("FT_CreateDeviceInfoList");
        _getList = (delegate* unmanaged[Stdcall]<DeviceNode*, uint*, uint>)E("FT_GetDeviceInfoList");
        _openEx = (delegate* unmanaged[Stdcall]<byte*, uint, nint*, uint>)E("FT_OpenEx");
        _close = (delegate* unmanaged[Stdcall]<nint, uint>)E("FT_Close");
        _reset = (delegate* unmanaged[Stdcall]<nint, uint>)E("FT_ResetDevice");
        _clrRts = (delegate* unmanaged[Stdcall]<nint, uint>)E("FT_ClrRts");
        _breakOn = (delegate* unmanaged[Stdcall]<nint, uint>)E("FT_SetBreakOn");
        _breakOff = (delegate* unmanaged[Stdcall]<nint, uint>)E("FT_SetBreakOff");
        _usbParams = (delegate* unmanaged[Stdcall]<nint, uint, uint, uint>)E("FT_SetUSBParameters");
        _latency = (delegate* unmanaged[Stdcall]<nint, byte, uint>)E("FT_SetLatencyTimer");
        _flow = (delegate* unmanaged[Stdcall]<nint, ushort, byte, byte, uint>)E("FT_SetFlowControl");
        _baud = (delegate* unmanaged[Stdcall]<nint, uint, uint>)E("FT_SetBaudRate");
        _purge = (delegate* unmanaged[Stdcall]<nint, uint, uint>)E("FT_Purge");
        _data = (delegate* unmanaged[Stdcall]<nint, byte, byte, byte, uint>)E("FT_SetDataCharacteristics");
        _write = (delegate* unmanaged[Stdcall]<nint, byte*, uint, uint*, uint>)E("FT_Write");
    }

    /// <summary>
    /// The driver, or null if it isn't installed (or isn't whole).
    /// </summary>
    public static Ftd2xx? Load()
    {
        foreach (var name in new[] { "ftd2xx.dll", "ftd2xx64.dll" })
            if (NativeLibrary.TryLoad(name, out var lib))
                try
                {
                    return new Ftd2xx(lib);
                }
                catch (EntryPointNotFoundException)
                {
                    NativeLibrary.Free(lib);
                }
        return null;
    }

    public IReadOnlyList<(string Serial, string Description)> Devices()
    {
        uint n = 0;
        if (_createList(&n) != 0 || n == 0)
            return [];
        var nodes = new DeviceNode[n];
        fixed (DeviceNode* p = nodes)
            if (_getList(p, &n) != 0)
                return [];
        var output = new List<(string, string)>();
        for (var i = 0; i < n; i++)
            fixed (DeviceNode* node = &nodes[i])
                output.Add((Marshal.PtrToStringAnsi((nint)node->SerialNumber) ?? "", Marshal.PtrToStringAnsi((nint)node->Description) ?? ""));
        return output;
    }

    public nint Open(string serial)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(serial + "\0");
        nint handle = 0;
        fixed (byte* s = bytes)
            return _openEx(s, 1 /* FT_OPEN_BY_SERIAL_NUMBER */, &handle) == 0 ? handle : 0;
    }

    public void Close(nint handle) => _close(handle);

    public uint ResetDevice(nint h) => _reset(h);

    public uint SetUsbParameters(nint h, uint inSize, uint outSize) => _usbParams(h, inSize, outSize);

    public uint SetLatencyTimer(nint h, byte ms) => _latency(h, ms);

    public uint SetFlowControl(nint h, ushort flow) => _flow(h, flow, 0, 0);

    public uint SetBaudRate(nint h, uint baud) => _baud(h, baud);

    public uint SetDataCharacteristics(nint h, byte bits, byte stopBits, byte parity) => _data(h, bits, stopBits, parity);

    public uint ClrRts(nint h) => _clrRts(h);

    public uint SetBreak(nint h, bool on) => on ? _breakOn(h) : _breakOff(h);

    public uint Purge(nint h, uint mask) => _purge(h, mask);

    public uint Write(nint h, ReadOnlySpan<byte> data, out uint written)
    {
        uint w = 0;
        uint st;
        fixed (byte* p = data)
            st = _write(h, p, (uint)data.Length, &w);
        written = w;
        return st;
    }
}
