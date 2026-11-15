using Cuedeck.Dmx;
using Cuedeck.Show;

namespace Cuedeck.Tests.Dmx;

/// <summary>
/// D2XX as a list of what was asked of it.
/// </summary>
sealed class FakeFtdi : IFtdi
{
    public List<(string Serial, string Description)> Attached { get; } = [];
    public List<string> Calls { get; } = [];
    public List<byte[]> Writes { get; } = [];
    public uint WriteStatus { get; set; }

    /// <summary>
    /// Whether opening fails even for an interface that's listed (held by another program, say).
    /// </summary>
    public bool OpenFails { get; set; }

    public IReadOnlyList<(string Serial, string Description)> Devices() => Attached;

    public nint Open(string serial)
    {
        Calls.Add("open " + serial);
        return !OpenFails && Attached.Any(d => d.Serial == serial) ? 42 : 0;
    }

    public void Close(nint handle) => Calls.Add("close");

    public uint ResetDevice(nint h) => Log("reset");

    public uint SetUsbParameters(nint h, uint inSize, uint outSize) => Log($"usb {inSize} {outSize}");

    public uint SetLatencyTimer(nint h, byte ms) => Log($"latency {ms}");

    public uint SetFlowControl(nint h, ushort flow) => Log($"flow {flow}");

    public uint SetBaudRate(nint h, uint baud) => Log($"baud {baud}");

    public uint SetDataCharacteristics(nint h, byte bits, byte stops, byte parity) => Log($"data {bits} {stops} {parity}");

    public uint ClrRts(nint h) => Log("clrrts");

    public uint SetBreak(nint h, bool on) => Log(on ? "break on" : "break off");

    public uint Purge(nint h, uint mask) => Log($"purge {mask}");

    public uint Write(nint h, ReadOnlySpan<byte> data, out uint written)
    {
        Calls.Add($"write {data.Length}");
        Writes.Add(data.ToArray());
        written = WriteStatus == 0 ? (uint)data.Length : 0;
        return WriteStatus;
    }

    uint Log(string call)
    {
        Calls.Add(call);
        return 0;
    }
}

/// <summary>
/// USB DMX through FTDI's D2XX (sLight's native <c>UsbDmxOutput</c> and its <c>UsbDmx</c> bridge): finding the
/// interfaces, setting them up, and each frame's bytes and timing signals, Open DMX's (the computer makes the break)
/// and the Enttec Pro's (a framed message). The driver's stood in for, so no hardware is needed; sLight had no tests
/// of its own for it.
/// </summary>
public sealed class UsbDmxTests
{
    static (UsbDmx Usb, FakeFtdi Ftdi) WithInterfaces(params (string, string)[] attached)
    {
        var ftdi = new FakeFtdi();
        ftdi.Attached.AddRange(attached);
        return (new UsbDmx(ftdi, stream: false), ftdi);
    }

    [Fact]
    public void Without_the_driver_nothing_happens()
    {
        using var usb = new UsbDmx(null);
        Assert.False(usb.Available);
        Assert.Empty(usb.ListDevices());
        Assert.False(usb.Route("A1", UsbDmxMode.OpenDmx));
        Assert.Equal("USB DMX needs FTDI's D2XX driver or libusb", usb.Status("A1"));
        usb.Send("A1", new byte[512]); // harmless
    }

    [Fact]
    public void Interfaces_found_and_their_kind_guessed()
    {
        var (usb, _) = WithInterfaces(("EN123", "DMX USB PRO"), ("OD456", "FT232R USB UART"), ("DK789", "DMXking ultraDMX Micro"), ("", "No serial"));
        using (usb)
        {
            Assert.True(usb.Available);
            Assert.Equal(
                [new UsbDevice("EN123", "DMX USB PRO", UsbDmxMode.EnttecPro), new UsbDevice("OD456", "FT232R USB UART", UsbDmxMode.OpenDmx), new UsbDevice("DK789", "DMXking ultraDMX Micro", UsbDmxMode.EnttecPro)],
                usb.ListDevices()
            ); // one without a serial number can't be opened again by it: left out
        }
    }

    [Fact]
    public void Open_DMX_makes_its_own_break()
    {
        var (usb, ftdi) = WithInterfaces(("OD456", "FT232R USB UART"));
        using (usb)
        {
            Assert.True(usb.Route("OD456", UsbDmxMode.Auto)); // guessed: Open DMX
            Assert.Equal(["open OD456", "reset", "usb 4096 4096", "latency 1", "flow 0", "baud 250000", "data 8 2 0", "clrrts", "break off", "purge 3"], ftdi.Calls);
            Assert.Equal("open (Open DMX) — OD456", usb.Status("OD456"));
            var frame = new byte[512];
            frame[0] = 255;
            frame[511] = 7;
            usb.Send("OD456", frame);
            ftdi.Calls.Clear();
            Assert.True(usb.Output("OD456")!.WriteFrame());
            // The break, the mark after it, then the start code and 512 channels.
            Assert.Equal(["break on", "break off", "write 513"], ftdi.Calls);
            var sent = ftdi.Writes[^1];
            Assert.Equal((0, 255, 7), (sent[0], sent[1], sent[512]));
            Assert.Equal("streaming", usb.Status("OD456"));
        }
    }

    [Fact]
    public void The_Enttec_Pro_is_sent_a_message()
    {
        var (usb, ftdi) = WithInterfaces(("EN123", "DMX USB PRO"));
        using (usb)
        {
            Assert.True(usb.Route("EN123", UsbDmxMode.Auto));
            Assert.DoesNotContain("baud 250000", ftdi.Calls); // its own chip does the timing
            usb.Send("EN123", [.. Enumerable.Range(0, 512).Select(i => (byte)i)]);
            ftdi.Calls.Clear();
            Assert.True(usb.Output("EN123")!.WriteFrame());
            Assert.Equal(["write 518"], ftdi.Calls);
            var m = ftdi.Writes[^1];
            // "Send DMX Packet": 0x7E, label 6, the length (513) low then high, the start code and channels, 0xE7.
            Assert.Equal((0x7E, 6, 0x01, 0x02, 0, 0, 1, 0xE7), (m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[517]));
            Assert.Equal(255, m[4 + 256]); // channel 256's 255
        }
    }

    [Fact]
    public void Failures_and_letting_go()
    {
        var (usb, ftdi) = WithInterfaces(("OD456", "FT232R USB UART"));
        using (usb)
        {
            Assert.False(usb.Route("GONE", UsbDmxMode.OpenDmx));
            Assert.Equal("could not open GONE", usb.Status("GONE"));
            Assert.True(usb.Route("OD456", UsbDmxMode.OpenDmx));
            ftdi.WriteStatus = 4; // FT_IO_ERROR: unplugged, say
            Assert.False(usb.Output("OD456")!.WriteFrame());
            Assert.Equal("write failed — FT_Write status 4, wrote 0 of 513", usb.Status("OD456"));
            Assert.True(usb.IsSending("OD456"));
            usb.Unroute("OD456");
            Assert.False(usb.IsSending("OD456"));
            Assert.Equal("close", ftdi.Calls[^1]);
            Assert.Equal("not open", usb.Status("OD456"));
        }
    }

    [Fact]
    public void A_universe_set_to_USB_is_routed_there()
    {
        var (usb, _) = WithInterfaces(("EN123", "DMX USB PRO"));
        using (usb)
        using (var show = new LightingShow())
        {
            show.Dmx.RouteUsb = usb.Route;
            show.Dmx.SendUsb = usb.Send;
            show.Dmx.ReleaseUsb = usb.Unroute;
            var p = show.Patch.Universes[0];
            p.Output = OutputMode.Usb;
            p.UsbSerial = "EN123";
            p.UsbMode = (int)UsbDmxMode.EnttecPro;
            show.Patch.ApplyConnection(0);
            Assert.True(usb.IsSending("EN123"));
            show.Dmx.Universes[0].SetChannel(0, 200);
            show.Tick(0.04, transmit: true);
            Assert.Equal(200, usb.Output("EN123")!.Frame[0]); // its frame handed over each tick
            p.Output = OutputMode.ArtNet;
            show.Patch.ApplyConnection(0);
            Assert.False(usb.IsSending("EN123")); // let go
        }
    }

    [Fact]
    public void A_universe_whose_interface_cant_be_opened_sends_Art_Net_instead()
    {
        var (usb, ftdi) = WithInterfaces(("EN123", "DMX USB PRO"));
        using (usb)
        using (var show = new LightingShow())
        {
            show.Dmx.RouteUsb = usb.Route;
            var p = show.Patch.Universes[0];
            (p.Output, p.UsbSerial) = (OutputMode.Usb, "GONE");
            Assert.False(show.Patch.ApplyConnection(0));
            // As sLight: Art-Net, and no interface named (so it's saved that way).
            Assert.Equal((OutputMode.ArtNet, ""), (p.Output, p.UsbSerial));
            Assert.Equal(OutputMode.ArtNet, show.Dmx.Universes[0].Mode);

            // A show saved with an interface that isn't plugged in this time.
            (p.Output, p.UsbSerial) = (OutputMode.Usb, "EN123");
            Assert.True(show.Patch.ApplyConnection(0));
            var saved = show.Patch.PatchToJson(0);
            ftdi.Attached.Clear();
            usb.Unroute("EN123");
            show.Patch.ApplyPatch(0, saved, usbAvailable: true);
            Assert.Equal(OutputMode.ArtNet, show.Patch.Universes[0].Output);
        }
    }

    /// <summary>
    /// A link whose writes hang until let go, as a wedged driver's can.
    /// </summary>
    sealed class StuckLink : IUsbLink
    {
        public readonly ManualResetEventSlim Writing = new();
        public readonly ManualResetEventSlim LetGo = new();
        public volatile bool Closed;
        public volatile bool WroteAfterClose;

        public string? Write(ReadOnlySpan<byte> frame513)
        {
            if (Closed)
                WroteAfterClose = true;
            Writing.Set();
            LetGo.Wait();
            return null;
        }

        public void Close() => Closed = true;
    }

    [Fact]
    public void A_link_isnt_closed_under_a_write_still_going()
    {
        var link = new StuckLink();
        var output = new UsbDmxOutput(link, UsbDmxMode.EnttecPro, "EN1", "test");
        output.Start();
        Assert.True(link.Writing.Wait(2000));
        output.Dispose(); // gives up waiting for the write
        Assert.False(link.Closed);
        link.LetGo.Set(); // the write comes back: the thread stops and closes the link itself
        Assert.True(SpinWait.SpinUntil(() => link.Closed, 2000));
        Assert.False(link.WroteAfterClose);

        // Not streaming, it closes straight away.
        var idle = new StuckLink();
        new UsbDmxOutput(idle, UsbDmxMode.EnttecPro, "EN2", "test").Dispose();
        Assert.True(idle.Closed);
    }
}
