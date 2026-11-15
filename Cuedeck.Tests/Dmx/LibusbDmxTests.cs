using Cuedeck.Dmx;

namespace Cuedeck.Tests.Dmx;

/// <summary>
/// libusb as a list of what was asked of it. A device with no name couldn't be opened (no libusb / WinUSB driver
/// bound to it).
/// </summary>
sealed class FakeLibusb : ILibusb
{
    public List<LibusbDevice> Attached { get; } = [];
    public List<string> Calls { get; } = [];
    public List<byte[]> Sent { get; } = [];

    /// <summary>
    /// What claiming an interface returns (0, or a libusb error).
    /// </summary>
    public int ClaimResult { get; set; }

    /// <summary>
    /// A libusb error every transfer returns, or 0 for them to work.
    /// </summary>
    public int TransferError { get; set; }

    public IReadOnlyList<LibusbDevice> Devices() => Attached;

    public nint Open(LibusbDevice device)
    {
        Calls.Add($"open {device.Bus}:{device.Address}");
        return device.Name is null ? 0 : 7;
    }

    public void Close(nint handle) => Calls.Add("close");

    public int SetConfiguration(nint handle, int configuration) => Log($"config {configuration}");

    public int ClaimInterface(nint handle, int number)
    {
        Calls.Add($"claim {number}");
        return ClaimResult;
    }

    public int ReleaseInterface(nint handle, int number) => Log($"release {number}");

    public int ControlTransfer(nint handle, byte requestType, byte request, ushort value, ushort index, ReadOnlySpan<byte> data, uint timeout)
    {
        Calls.Add($"control {requestType:X2} {request} {value:X4} {index} {data.Length}");
        Sent.Add(data.ToArray());
        return TransferError < 0 ? TransferError : data.Length;
    }

    public int BulkTransfer(nint handle, byte endpoint, ReadOnlySpan<byte> data, out int transferred, uint timeout)
    {
        Calls.Add($"bulk {endpoint:X2} {data.Length}");
        Sent.Add(data.ToArray());
        transferred = TransferError < 0 ? 0 : data.Length;
        return TransferError;
    }

    int Log(string call)
    {
        Calls.Add(call);
        return 0;
    }
}

/// <summary>
/// USB DMX through libusb (sLight's native <c>UsbDmxOutput</c>'s second backend): the anyma uDMX, sent a vendor
/// control transfer a frame, and FTDI interfaces with no D2XX driver, set up and given their break with FTDI's
/// vendor requests and sent their frames on the bulk endpoint. libusb's stood in for, so no hardware is needed; sLight
/// had no tests of its own for it.
/// </summary>
public sealed class LibusbDmxTests
{
    static readonly LibusbDevice Udmx = new(0x16C0, 0x05DC, 1, 5, "uDMX", "");
    static readonly LibusbDevice UdmxNoDriver = new(0x16C0, 0x05DC, 2, 3, null, "");
    static readonly LibusbDevice OtherFreeId = new(0x16C0, 0x05DC, 1, 6, "Some other gadget", "");
    static readonly LibusbDevice FtdiPro = new(0x0403, 0x6001, 3, 2, "DMX USB PRO", "EN9");
    static readonly LibusbDevice FtdiX = new(0x0403, 0x6015, 3, 1, "FT231X USB UART", "");
    static readonly LibusbDevice FtdiDual = new(0x0403, 0x6010, 3, 4, "Dual RS232", "DU1");

    static (UsbDmx Usb, FakeLibusb Lib) With(params LibusbDevice[] attached)
    {
        var lib = new FakeLibusb();
        lib.Attached.AddRange(attached);
        return (new UsbDmx(null, lib, stream: false), lib);
    }

    static byte[] Channels()
    {
        var frame = new byte[512];
        frame[0] = 255;
        frame[511] = 7;
        return frame;
    }

    [Fact]
    public void The_uDMX_and_FTDI_interfaces_found_through_libusb()
    {
        var (usb, _) = With(Udmx, OtherFreeId, UdmxNoDriver, FtdiPro, FtdiX, FtdiDual);
        using (usb)
        {
            Assert.True(usb.Available);
            Assert.Equal(
                [
                    // Named by bus and address where it has no serial number.
                    new UsbDevice("udmx:1:5", "uDMX", UsbDmxMode.Udmx, Libusb: true),
                    // (Another device on the uDMX's shared ID is left out.)
                    new UsbDevice("udmx:2:3", "uDMX? (needs a libusb / WinUSB driver)", UsbDmxMode.Udmx, Libusb: true),
                    new UsbDevice("EN9", "DMX USB PRO (libusb)", UsbDmxMode.EnttecPro, Libusb: true),
                    new UsbDevice("ftdi:3:1", "FT231X USB UART (libusb)", UsbDmxMode.OpenDmx, Libusb: true),
                    // (Multi-port FTDI chips go through D2XX only.)
                ],
                usb.ListDevices()
            );
        }
    }

    [Fact]
    public void With_D2XX_installed_its_FTDI_interfaces_arent_listed_through_libusb_too()
    {
        var ftdi = new FakeFtdi();
        ftdi.Attached.Add(("EN9", "DMX USB PRO"));
        var lib = new FakeLibusb();
        lib.Attached.AddRange([FtdiPro, Udmx]);
        using var usb = new UsbDmx(ftdi, lib, stream: false);
        Assert.Equal([new UsbDevice("EN9", "DMX USB PRO", UsbDmxMode.EnttecPro), new UsbDevice("udmx:1:5", "uDMX", UsbDmxMode.Udmx, Libusb: true)], usb.ListDevices());
        Assert.True(usb.Route("EN9", UsbDmxMode.Auto));
        Assert.Equal("open EN9", ftdi.Calls[0]); // through D2XX
        Assert.Empty(lib.Calls);
    }

    [Fact]
    public void A_uDMX_is_sent_a_control_transfer_a_frame()
    {
        var (usb, lib) = With(Udmx);
        using (usb)
        {
            Assert.True(usb.Route("udmx:1:5", UsbDmxMode.Auto));
            Assert.Equal(["open 1:5", "claim 0"], lib.Calls);
            Assert.Equal("open (uDMX) — udmx:1:5", usb.Status("udmx:1:5"));
            Assert.Equal(UsbDmxMode.Udmx, usb.Output("udmx:1:5")!.Mode);
            usb.Send("udmx:1:5", Channels());
            lib.Calls.Clear();
            Assert.True(usb.Output("udmx:1:5")!.WriteFrame());
            // Vendor, host to device: "set channel range", 512 channels from the first; no start code.
            Assert.Equal(["control 40 2 0200 0 512"], lib.Calls);
            Assert.Equal((255, 7), (lib.Sent[^1][0], lib.Sent[^1][511]));
            Assert.Equal("streaming", usb.Status("udmx:1:5"));
        }
    }

    [Fact]
    public void FTDI_through_libusb_as_Open_DMX_makes_its_own_break()
    {
        var (usb, lib) = With(FtdiX);
        using (usb)
        {
            Assert.True(usb.Route("ftdi:3:1", UsbDmxMode.Auto));
            Assert.Equal(
                [
                    "open 3:1",
                    "config 1",
                    "claim 0",
                    "control 40 0 0000 0 0", // reset
                    "control 40 2 0000 0 0", // no flow control
                    "control 40 3 000C 0 0", // 3 MHz / 12: 250 kbaud
                    "control 40 4 1008 0 0", // 8 bits, no parity, 2 stop bits
                    "control 40 9 0001 0 0", // latency 1 ms
                    "control 40 0 0001 0 0", // purge what's come in
                    "control 40 0 0002 0 0", // and what's going out
                ],
                lib.Calls
            );
            Assert.Equal("open (Open DMX, libusb) — ftdi:3:1", usb.Status("ftdi:3:1"));
            usb.Send("ftdi:3:1", Channels());
            lib.Calls.Clear();
            Assert.True(usb.Output("ftdi:3:1")!.WriteFrame());
            // The break on and off (the data bits with the break bit, then without), then the start code and channels.
            Assert.Equal(["control 40 4 5008 0 0", "control 40 4 1008 0 0", "bulk 02 513"], lib.Calls);
            Assert.Equal((0, 255, 7), (lib.Sent[^1][0], lib.Sent[^1][1], lib.Sent[^1][512]));
        }
    }

    [Fact]
    public void FTDI_through_libusb_as_a_Pro_is_sent_a_message()
    {
        var (usb, lib) = With(FtdiPro);
        using (usb)
        {
            Assert.True(usb.Route("EN9", UsbDmxMode.Auto));
            Assert.DoesNotContain("control 40 3 000C 0 0", lib.Calls); // its own chip does the timing
            Assert.Equal("open (Enttec Pro, libusb) — EN9", usb.Status("EN9"));
            usb.Send("EN9", Channels());
            lib.Calls.Clear();
            Assert.True(usb.Output("EN9")!.WriteFrame());
            Assert.Equal(["bulk 02 518"], lib.Calls);
            var m = lib.Sent[^1];
            Assert.Equal((0x7E, 6, 0x01, 0x02, 0, 255, 7, 0xE7), (m[0], m[1], m[2], m[3], m[4], m[5], m[516], m[517]));
        }
    }

    [Fact]
    public void Failures_and_letting_go()
    {
        var (usb, lib) = With(Udmx, UdmxNoDriver, FtdiX);
        using (usb)
        {
            // No driver bound, so it can't be opened.
            Assert.False(usb.Route("udmx:2:3", UsbDmxMode.Auto));
            Assert.Equal("could not open udmx:2:3", usb.Status("udmx:2:3"));

            // Opened, but another driver has it.
            lib.ClaimResult = -3; // LIBUSB_ERROR_ACCESS
            Assert.False(usb.Route("ftdi:3:1", UsbDmxMode.Auto));
            Assert.Equal("could not claim ftdi:3:1 (is a libusb / WinUSB driver bound to it?)", usb.Status("ftdi:3:1"));
            Assert.Equal("close", lib.Calls[^1]);

            // A uDMX chosen for an FTDI interface, and the other way round, aren't found as that.
            lib.ClaimResult = 0;
            Assert.False(usb.Route("ftdi:3:1", UsbDmxMode.Udmx));
            Assert.False(usb.Route("udmx:1:5", UsbDmxMode.OpenDmx));

            Assert.True(usb.Route("udmx:1:5", UsbDmxMode.Udmx));
            lib.TransferError = -4; // LIBUSB_ERROR_NO_DEVICE: unplugged
            Assert.False(usb.Output("udmx:1:5")!.WriteFrame());
            Assert.Equal("write failed — uDMX control transfer error -4", usb.Status("udmx:1:5"));
            lib.Calls.Clear();
            usb.Unroute("udmx:1:5");
            Assert.Equal(["release 0", "close"], lib.Calls);
        }
    }
}
