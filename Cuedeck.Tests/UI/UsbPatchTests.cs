using Cuedeck.Dmx;
using Cuedeck.Tests.Dmx;
using Cuedeck.UI;

namespace Cuedeck.Tests.UI;

/// <summary>
/// Sending a universe to a USB DMX interface from the Patch tab (sLight's universe panel's USB row), with the driver
/// stood in for.
/// </summary>
public sealed class UsbPatchTests : ConsoleHarness
{
    readonly FakeFtdi _ftdi = new();
    readonly UsbDmx _usb;

    public UsbPatchTests()
    {
        _ftdi.Attached.Add(("EN123", "DMX USB PRO"));
        _usb = new UsbDmx(_ftdi, stream: false);
        Show.Dmx.RouteUsb = _usb.Route;
        Show.Dmx.SendUsb = _usb.Send;
        Show.Dmx.ReleaseUsb = _usb.Unroute;
        Screen.Console.Patch.Usb = _usb;
        Frame();
    }

    public override void Dispose()
    {
        _usb.Dispose();
        base.Dispose();
    }

    [Fact]
    public void A_universe_sent_to_USB()
    {
        Click(ButtonCalled("Universe 1"));
        Choose(Find<Dropdown<OutputMode>>(_ => true), "USB");
        // The interface found and chosen, its kind guessed, and the universe streaming to it.
        Assert.Equal(OutputMode.Usb, Show.Patch.Universes[0].Output);
        Assert.Equal("EN123", Show.Patch.Universes[0].UsbSerial);
        Assert.True(_usb.IsSending("EN123"));
        Assert.Equal("Universe 1 now sends to USB DMX EN123.", Screen.StatusText);
        Assert.Contains(All(Screen.Console).OfType<Label>(), l => l.Text.StartsWith("→ USB DMX EN123  [open (Enttec Pro)"));
        Assert.Contains(All(Screen.Console).OfType<Button>(), b => b.Text == "Rescan");

        // As Open DMX instead.
        Choose(Find<Dropdown<int>>(d => d.Selected == 0), "Open DMX");
        Assert.Equal((int)UsbDmxMode.OpenDmx, Show.Patch.Universes[0].UsbMode);
        Assert.Equal(UsbDmxMode.OpenDmx, _usb.Output("EN123")!.Mode);

        // Back to Art-Net: the interface let go, its row gone.
        Choose(Find<Dropdown<OutputMode>>(_ => true), "Art-Net");
        Assert.False(_usb.IsSending("EN123"));
        Assert.DoesNotContain(All(Screen.Console).OfType<Button>(), b => b.Text == "Rescan");
    }

    [Fact]
    public void Without_the_driver_USB_isnt_offered()
    {
        Screen.Console.Patch.Usb = new UsbDmx(null);
        Click(ButtonCalled("Universe 1"));
        Click(Find<Dropdown<OutputMode>>(_ => true));
        Assert.DoesNotContain(All(Ui.Elements.OfType<ContextMenu>().Single()).OfType<Button>(), b => b.Text == "USB");
    }
}
