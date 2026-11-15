using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cuedeck.Dmx;

/// <summary>
/// What kind of USB DMX interface: as sLight's modes (a show keeps the number). A uDMX is reached through libusb; one
/// named for an interface D2XX found is sent as Open DMX.
/// </summary>
enum UsbDmxMode
{
    Auto = 0,
    OpenDmx = 1,
    EnttecPro = 2,
    Udmx = 3,
}

/// <summary>
/// A USB DMX interface found: its serial number (what a universe is routed by; for one without, where it's plugged in,
/// as <c>udmx:</c> or <c>ftdi:</c> and its bus and address), what it calls itself, the kind it looks like, and whether
/// it's reached through libusb rather than D2XX.
/// </summary>
sealed record UsbDevice(string Serial, string Description, UsbDmxMode GuessedMode, bool Libusb = false);

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
/// Universes sent to USB DMX interfaces, each streamed from a thread of its own at a steady rate; the console hands
/// each its latest frame every tick. Through FTDI's D2XX driver: Enttec's Open DMX USB and DMX USB Pro, DMXking's, and
/// other FTDI-based ones. Through libusb: the anyma uDMX, and those FTDI ones where D2XX isn't installed (a libusb or
/// WinUSB driver bound to them instead). Without either library, <see cref="Available"/> is false and everything here
/// does nothing. sLight's <c>UsbDmx</c> bridge and its native <c>UsbDmxOutput</c>.
/// </summary>
sealed class UsbDmx(IFtdi? ftdi, ILibusb? libusb = null, bool stream = true) : IDisposable
{
    public static readonly string[] ModeNames = ["Auto", "Open DMX", "Enttec Pro", "uDMX"];

    /// <summary>
    /// The anyma uDMX's ID: the shared "free" vendor and product, so it's told apart by its name.
    /// </summary>
    const ushort UDMX_VENDOR = 0x16C0,
        UDMX_PRODUCT = 0x05DC;

    const ushort FTDI_VENDOR = 0x0403;

    readonly Dictionary<string, UsbDmxOutput> _outputs = [];
    readonly Dictionary<string, string> _failures = [];

    public bool Available => ftdi is not null || libusb is not null;

    /// <summary>
    /// The single-port FTDI chips libusb may drive (FT232AM/BM/R, FT-X); the others go through D2XX.
    /// </summary>
    static bool IsFtdiDmx(LibusbDevice d) => d.Vendor == FTDI_VENDOR && d.Product is 0x6001 or 0x6015;

    static bool IsUdmx(LibusbDevice d) => d.Vendor == UDMX_VENDOR && d.Product == UDMX_PRODUCT;

    /// <summary>
    /// What a libusb device is routed by: its serial number, or where it's plugged in.
    /// </summary>
    static string IdOf(LibusbDevice d, bool udmx) => d.Serial != "" ? d.Serial : $"{(udmx ? "udmx" : "ftdi")}:{d.Bus}:{d.Address}";

    public List<UsbDevice> ListDevices()
    {
        var found = new List<UsbDevice>();
        if (ftdi is not null)
            found.AddRange(ftdi.Devices().Where(d => d.Serial != "").Select(d => new UsbDevice(d.Serial, d.Description, Guess(d.Description))));
        if (libusb is null)
            return found;
        foreach (var d in libusb.Devices())
        {
            if (IsUdmx(d))
            {
                // Another device on the shared ID, if it could be asked its name.
                if (d.Name is { } name && !name.Contains("UDMX", StringComparison.OrdinalIgnoreCase))
                    continue;
                var description = d.Name is null ? "uDMX? (needs a libusb / WinUSB driver)" : d.Name == "" ? "uDMX" : d.Name;
                found.Add(new UsbDevice(IdOf(d, true), description, UsbDmxMode.Udmx, Libusb: true));
            }
            else if (ftdi is null && IsFtdiDmx(d))
            {
                var name = string.IsNullOrEmpty(d.Name) ? "FTDI" : d.Name;
                var guess = name.Contains("PRO", StringComparison.OrdinalIgnoreCase) ? UsbDmxMode.EnttecPro : UsbDmxMode.OpenDmx;
                found.Add(new UsbDevice(IdOf(d, false), name + " (libusb)", guess, Libusb: true));
            }
        }
        return found;
    }

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
        if (!Available || serial == "")
            return false;
        Unroute(serial);
        _failures.Remove(serial);
        var device = ListDevices().FirstOrDefault(d => d.Serial == serial);
        if (mode == UsbDmxMode.Auto)
            mode = device?.GuessedMode ?? UsbDmxMode.OpenDmx;
        if (device is { Libusb: true })
            return mode == UsbDmxMode.Udmx ? RouteUdmx(serial) : RouteLibusbFtdi(serial, mode);
        if (ftdi is null)
        {
            _failures[serial] = "could not open " + serial;
            return false;
        }
        if (mode == UsbDmxMode.Udmx)
            mode = UsbDmxMode.OpenDmx;
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
        return Start(new D2xxLink(ftdi, handle, mode), mode, serial, mode == UsbDmxMode.EnttecPro ? "Enttec Pro" : "Open DMX");
    }

    bool Start(IUsbLink link, UsbDmxMode mode, string serial, string kind)
    {
        var output = new UsbDmxOutput(link, mode, serial, kind);
        _outputs[serial] = output;
        if (stream)
            output.Start();
        return true;
    }

    /// <summary>
    /// Opens the libusb device that's a uDMX (or an FTDI interface, by <paramref name="udmx"/>) routed by
    /// <paramref name="serial"/>; 0 (with why kept for <see cref="Status"/>) if it can't.
    /// </summary>
    nint OpenLibusb(string serial, bool udmx)
    {
        var device = libusb!.Devices().FirstOrDefault(d => (udmx ? IsUdmx(d) : IsFtdiDmx(d)) && IdOf(d, udmx) == serial);
        var handle = device is null ? 0 : libusb.Open(device);
        if (handle == 0)
            _failures[serial] = "could not open " + serial;
        return handle;
    }

    bool RouteUdmx(string serial)
    {
        var handle = OpenLibusb(serial, udmx: true);
        if (handle == 0)
            return false;
        libusb!.ClaimInterface(handle, 0); // as sLight: it only uses the control endpoint, so this may fail harmlessly
        return Start(new UdmxLink(libusb, handle), UsbDmxMode.Udmx, serial, "uDMX");
    }

    bool RouteLibusbFtdi(string serial, UsbDmxMode mode)
    {
        var handle = OpenLibusb(serial, udmx: false);
        if (handle == 0)
            return false;
        var lib = libusb!;
        lib.SetConfiguration(handle, 1);
        if (lib.ClaimInterface(handle, 0) != 0)
        {
            lib.Close(handle);
            _failures[serial] = $"could not claim {serial} (is a libusb / WinUSB driver bound to it?)";
            return false;
        }
        mode = mode == UsbDmxMode.EnttecPro ? UsbDmxMode.EnttecPro : UsbDmxMode.OpenDmx;
        // FTDI's vendor requests: reset, no flow control, (Open DMX's 250 kbaud 8N2), a 1 ms latency, then both
        // buffers purged.
        void Request(byte request, ushort value) => lib.ControlTransfer(handle, 0x40, request, value, 0, [], 100);
        Request(LibusbFtdiLink.RESET, 0);
        Request(LibusbFtdiLink.SET_FLOW_CONTROL, 0);
        if (mode == UsbDmxMode.OpenDmx)
        {
            Request(LibusbFtdiLink.SET_BAUD_RATE, 12); // 3 MHz / 12
            Request(LibusbFtdiLink.SET_DATA, LibusbFtdiLink.DATA_8N2);
        }
        Request(LibusbFtdiLink.SET_LATENCY, 1);
        Request(LibusbFtdiLink.RESET, 1);
        Request(LibusbFtdiLink.RESET, 2);
        return Start(new LibusbFtdiLink(lib, handle, mode), mode, serial, mode == UsbDmxMode.EnttecPro ? "Enttec Pro, libusb" : "Open DMX, libusb");
    }

    public void Unroute(string serial)
    {
        if (_outputs.Remove(serial, out var o))
            o.Dispose();
    }

    public bool IsSending(string serial) => _outputs.ContainsKey(serial);

    public string Status(string serial) =>
        !Available ? "USB DMX needs FTDI's D2XX driver or libusb"
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
        (libusb as IDisposable)?.Dispose();
    }
}

/// <summary>
/// An open interface as the streaming thread sees it: one frame sent its way, and let go of.
/// </summary>
interface IUsbLink
{
    /// <summary>
    /// Sends the start code and 512 channels; returns why it failed, or null.
    /// </summary>
    string? Write(ReadOnlySpan<byte> frame513);

    void Close();
}

/// <summary>
/// An FTDI interface through D2XX: the Pro's framed message, or Open DMX's break, mark and frame.
/// </summary>
sealed class D2xxLink(IFtdi ftdi, nint handle, UsbDmxMode mode) : IUsbLink
{
    public string? Write(ReadOnlySpan<byte> frame)
    {
        if (mode == UsbDmxMode.EnttecPro)
            return Check(ftdi.Write(handle, UsbDmxOutput.Packet(frame), out var wrote), wrote, 518);
        // The break (at least 88 µs), the mark after it, then the frame.
        var st = ftdi.SetBreak(handle, true);
        if (st != 0)
            return $"write failed — SetBreakOn status {st}";
        UsbDmxOutput.SleepMicro(120);
        st = ftdi.SetBreak(handle, false);
        if (st != 0)
            return $"write failed — SetBreakOff status {st}";
        UsbDmxOutput.SleepMicro(12);
        return Check(ftdi.Write(handle, frame, out var w), w, 513);
    }

    static string? Check(uint status, uint wrote, int length) => status != 0 || wrote != length ? $"write failed — FT_Write status {status}, wrote {wrote} of {length}" : null;

    public void Close() => ftdi.Close(handle);
}

/// <summary>
/// The anyma uDMX: its "set channel range" vendor request with all 512 channels, a frame at a time (its firmware does
/// the DMX timing; the control endpoint's slow, so a full universe goes at 20 to 25 frames a second).
/// </summary>
sealed class UdmxLink(ILibusb lib, nint handle) : IUsbLink
{
    const byte SET_CHANNEL_RANGE = 2;

    public string? Write(ReadOnlySpan<byte> frame)
    {
        var r = lib.ControlTransfer(handle, 0x40, SET_CHANNEL_RANGE, 512, 0, frame[1..], 250);
        return r < 0 ? $"write failed — uDMX control transfer error {r}" : null;
    }

    public void Close()
    {
        lib.ReleaseInterface(handle, 0);
        lib.Close(handle);
    }
}

/// <summary>
/// An FTDI interface through libusb, D2XX's work done by hand: the break as a "set data" vendor request with its break
/// bit, and the frame (or the Pro's message) on bulk endpoint 2.
/// </summary>
sealed class LibusbFtdiLink(ILibusb lib, nint handle, UsbDmxMode mode) : IUsbLink
{
    public const byte RESET = 0,
        SET_FLOW_CONTROL = 2,
        SET_BAUD_RATE = 3,
        SET_DATA = 4,
        SET_LATENCY = 9;

    /// <summary>
    /// 8 data bits, no parity, 2 stop bits; and the same with the break on.
    /// </summary>
    public const ushort DATA_8N2 = 0x1008,
        DATA_8N2_BREAK = 0x5008;

    const byte ENDPOINT_OUT = 0x02;

    public string? Write(ReadOnlySpan<byte> frame)
    {
        if (mode == UsbDmxMode.EnttecPro)
            return Bulk(UsbDmxOutput.Packet(frame));
        var r = lib.ControlTransfer(handle, 0x40, SET_DATA, DATA_8N2_BREAK, 0, [], 100);
        if (r < 0)
            return $"write failed — break on: control transfer error {r}";
        UsbDmxOutput.SleepMicro(120);
        r = lib.ControlTransfer(handle, 0x40, SET_DATA, DATA_8N2, 0, [], 100);
        if (r < 0)
            return $"write failed — break off: control transfer error {r}";
        UsbDmxOutput.SleepMicro(12);
        return Bulk(frame);
    }

    string? Bulk(ReadOnlySpan<byte> data)
    {
        var r = lib.BulkTransfer(handle, ENDPOINT_OUT, data, out var sent, 250);
        return r != 0 || sent != data.Length ? $"write failed — bulk transfer error {r}, sent {sent} of {data.Length}" : null;
    }

    public void Close()
    {
        lib.ReleaseInterface(handle, 0);
        lib.Close(handle);
    }
}

/// <summary>
/// One open interface: its latest frame, streamed by a thread of its own at <see cref="FPS"/> frames a second through
/// its link. Open DMX's frames get their break and mark from there, then aren't sent again until the last has had time
/// to go out (23 ms at 250 kbaud), so a hiccup can't cut one short; the Pro is sent "Send DMX Packet" messages and the
/// uDMX its channels, and each does its own timing.
/// </summary>
sealed class UsbDmxOutput(IUsbLink link, UsbDmxMode mode, string serial, string kind) : IDisposable
{
    public const int FPS = 40;

    readonly byte[] _frame = new byte[512];
    readonly Lock _lock = new();
    Thread? _thread;
    volatile bool _running;
    bool _linkOk = true;
    string _status = $"open ({kind}) — {serial}";

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

    /// <summary>
    /// The streaming thread: a frame every 1/<see cref="FPS"/> seconds until disposed, then the link closed, here, so
    /// it's never closed under a write still going.
    /// </summary>
    void Stream()
    {
        // Windows sleeps in 15.6 ms steps unless asked for finer ones, which would make 40 frames a second about 32.
        using var fine = TimerResolution.Fine();
        try
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
        finally
        {
            link.Close();
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
        var failed = link.Write(frame);
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

    internal static void SleepMicro(int us)
    {
        var until = Stopwatch.GetTimestamp() + us * Stopwatch.Frequency / 1_000_000;
        while (Stopwatch.GetTimestamp() < until)
            Thread.SpinWait(10);
    }

    /// <summary>
    /// Stops sending. A streaming thread closes the link itself as it stops (waited for here for a moment; a write
    /// stuck in the driver means later); without one, it's closed now.
    /// </summary>
    public void Dispose()
    {
        _running = false;
        if (_thread is null)
            link.Close();
        else
            _thread.Join(500);
    }
}

/// <summary>
/// Windows' timer resolution, asked to be 1 ms while something needs sleeps that short (USB DMX's frame timing).
/// </summary>
static partial class TimerResolution
{
    public static IDisposable Fine() => OperatingSystem.IsWindows() && timeBeginPeriod(1) == 0 ? new Release() : new Nothing();

    sealed class Release : IDisposable
    {
        int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                timeEndPeriod(1);
        }
    }

    sealed class Nothing : IDisposable
    {
        public void Dispose() { }
    }

    [System.Runtime.InteropServices.LibraryImport("winmm.dll")]
    private static partial uint timeBeginPeriod(uint period);

    [System.Runtime.InteropServices.LibraryImport("winmm.dll")]
    private static partial uint timeEndPeriod(uint period);
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
