using System.Runtime.InteropServices;

namespace Cuedeck.Dmx;

/// <summary>
/// A USB device libusb can see: its vendor and product IDs, where it's plugged in, its product name (null if it
/// couldn't be opened to ask: no libusb or WinUSB driver bound to it) and serial number ("" for none).
/// </summary>
sealed record LibusbDevice(ushort Vendor, ushort Product, byte Bus, byte Address, string? Name, string Serial);

/// <summary>
/// The libusb calls USB DMX uses, each returning libusb's result (0 or more for OK, a negative error): stood in for by
/// the tests.
/// </summary>
interface ILibusb
{
    /// <summary>
    /// The devices that could be DMX interfaces (FTDI's and the uDMX's vendors).
    /// </summary>
    IReadOnlyList<LibusbDevice> Devices();

    /// <summary>
    /// Opens the device; 0 if it can't.
    /// </summary>
    nint Open(LibusbDevice device);

    void Close(nint handle);
    int SetConfiguration(nint handle, int configuration);
    int ClaimInterface(nint handle, int number);
    int ReleaseInterface(nint handle, int number);

    /// <summary>
    /// A control transfer sending <paramref name="data"/>; returns how many bytes went, or an error.
    /// </summary>
    int ControlTransfer(nint handle, byte requestType, byte request, ushort value, ushort index, ReadOnlySpan<byte> data, uint timeout);

    int BulkTransfer(nint handle, byte endpoint, ReadOnlySpan<byte> data, out int transferred, uint timeout);
}

/// <summary>
/// libusb 1.0 (libusb-1.0.dll), found when it's made; null where it isn't installed. sLight's <c>libusb_dyn</c>.
/// </summary>
sealed unsafe class Libusb : ILibusb, IDisposable
{
    /// <summary>
    /// libusb's device descriptor: the standard USB one, 18 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct DeviceDescriptor
    {
        public byte Length,
            DescriptorType;
        public ushort Usb;
        public byte DeviceClass,
            DeviceSubClass,
            DeviceProtocol,
            MaxPacketSize0;
        public ushort Vendor,
            Product,
            Device;
        public byte Manufacturer,
            ProductName,
            SerialNumber,
            Configurations;
    }

    readonly nint _lib;
    nint _context;

    // LIBUSB_CALL is WINAPI on Windows.
    readonly delegate* unmanaged[Stdcall]<nint*, int> _init;
    readonly delegate* unmanaged[Stdcall]<nint, void> _exit;
    readonly delegate* unmanaged[Stdcall]<nint, nint**, nint> _getDeviceList;
    readonly delegate* unmanaged[Stdcall]<nint*, int, void> _freeDeviceList;
    readonly delegate* unmanaged[Stdcall]<nint, DeviceDescriptor*, int> _getDescriptor;
    readonly delegate* unmanaged[Stdcall]<nint, byte> _busNumber,
        _deviceAddress;
    readonly delegate* unmanaged[Stdcall]<nint, nint*, int> _open;
    readonly delegate* unmanaged[Stdcall]<nint, void> _close;
    readonly delegate* unmanaged[Stdcall]<nint, byte, byte*, int, int> _getString;
    readonly delegate* unmanaged[Stdcall]<nint, int, int> _setConfiguration,
        _claim,
        _release;
    readonly delegate* unmanaged[Stdcall]<nint, byte, byte, ushort, ushort, byte*, ushort, uint, int> _control;
    readonly delegate* unmanaged[Stdcall]<nint, byte, byte*, int, int*, uint, int> _bulk;

    Libusb(nint lib)
    {
        _lib = lib;
        nint E(string name) => NativeLibrary.GetExport(lib, "libusb_" + name);
        _init = (delegate* unmanaged[Stdcall]<nint*, int>)E("init");
        _exit = (delegate* unmanaged[Stdcall]<nint, void>)E("exit");
        _getDeviceList = (delegate* unmanaged[Stdcall]<nint, nint**, nint>)E("get_device_list");
        _freeDeviceList = (delegate* unmanaged[Stdcall]<nint*, int, void>)E("free_device_list");
        _getDescriptor = (delegate* unmanaged[Stdcall]<nint, DeviceDescriptor*, int>)E("get_device_descriptor");
        _busNumber = (delegate* unmanaged[Stdcall]<nint, byte>)E("get_bus_number");
        _deviceAddress = (delegate* unmanaged[Stdcall]<nint, byte>)E("get_device_address");
        _open = (delegate* unmanaged[Stdcall]<nint, nint*, int>)E("open");
        _close = (delegate* unmanaged[Stdcall]<nint, void>)E("close");
        _getString = (delegate* unmanaged[Stdcall]<nint, byte, byte*, int, int>)E("get_string_descriptor_ascii");
        _setConfiguration = (delegate* unmanaged[Stdcall]<nint, int, int>)E("set_configuration");
        _claim = (delegate* unmanaged[Stdcall]<nint, int, int>)E("claim_interface");
        _release = (delegate* unmanaged[Stdcall]<nint, int, int>)E("release_interface");
        _control = (delegate* unmanaged[Stdcall]<nint, byte, byte, ushort, ushort, byte*, ushort, uint, int>)E("control_transfer");
        _bulk = (delegate* unmanaged[Stdcall]<nint, byte, byte*, int, int*, uint, int>)E("bulk_transfer");
    }

    /// <summary>
    /// libusb, started, or null if it isn't installed (or isn't whole, or won't start).
    /// </summary>
    public static Libusb? Load()
    {
        foreach (var name in new[] { "libusb-1.0.dll", "libusb-1.0" })
            if (NativeLibrary.TryLoad(name, out var lib))
                try
                {
                    var usb = new Libusb(lib);
                    nint context = 0;
                    if (usb._init(&context) != 0)
                    {
                        NativeLibrary.Free(lib);
                        return null;
                    }
                    usb._context = context;
                    return usb;
                }
                catch (EntryPointNotFoundException)
                {
                    NativeLibrary.Free(lib);
                }
        return null;
    }

    /// <summary>
    /// Calls <paramref name="each"/> with every device that could be a DMX interface and its descriptor, until it
    /// returns true.
    /// </summary>
    void Each(Func<nint, DeviceDescriptor, bool> each)
    {
        nint* list = null;
        var n = _getDeviceList(_context, &list);
        try
        {
            for (nint i = 0; i < n; i++)
            {
                DeviceDescriptor d;
                if (_getDescriptor(list[i], &d) == 0 && d.Vendor is 0x0403 or 0x16C0 && each(list[i], d))
                    return;
            }
        }
        finally
        {
            if (list != null)
                _freeDeviceList(list, 1);
        }
    }

    string String(nint handle, byte index)
    {
        if (index == 0)
            return "";
        var buffer = stackalloc byte[256];
        var n = _getString(handle, index, buffer, 256);
        return n > 0 ? Marshal.PtrToStringAnsi((nint)buffer, n) : "";
    }

    public IReadOnlyList<LibusbDevice> Devices()
    {
        var found = new List<LibusbDevice>();
        Each(
            (device, d) =>
            {
                nint handle = 0;
                string? name = null;
                var serial = "";
                if (_open(device, &handle) == 0 && handle != 0)
                {
                    name = String(handle, d.ProductName);
                    serial = String(handle, d.SerialNumber);
                    _close(handle);
                }
                found.Add(new LibusbDevice(d.Vendor, d.Product, _busNumber(device), _deviceAddress(device), name, serial));
                return false;
            }
        );
        return found;
    }

    public nint Open(LibusbDevice device)
    {
        nint opened = 0;
        Each(
            (dev, d) =>
            {
                if (d.Vendor != device.Vendor || d.Product != device.Product || _busNumber(dev) != device.Bus || _deviceAddress(dev) != device.Address)
                    return false;
                nint handle = 0;
                if (_open(dev, &handle) == 0)
                    opened = handle;
                return true;
            }
        );
        return opened;
    }

    public void Close(nint handle) => _close(handle);

    public int SetConfiguration(nint handle, int configuration) => _setConfiguration(handle, configuration);

    public int ClaimInterface(nint handle, int number) => _claim(handle, number);

    public int ReleaseInterface(nint handle, int number) => _release(handle, number);

    public int ControlTransfer(nint handle, byte requestType, byte request, ushort value, ushort index, ReadOnlySpan<byte> data, uint timeout)
    {
        fixed (byte* p = data)
            return _control(handle, requestType, request, value, index, p, (ushort)data.Length, timeout);
    }

    public int BulkTransfer(nint handle, byte endpoint, ReadOnlySpan<byte> data, out int transferred, uint timeout)
    {
        int sent = 0;
        int r;
        // libusb doesn't write to an OUT transfer's buffer, whatever its signature says.
        fixed (byte* p = data)
            r = _bulk(handle, endpoint, p, data.Length, &sent, timeout);
        transferred = sent;
        return r;
    }

    public void Dispose()
    {
        if (_context == 0)
            return;
        _exit(_context);
        _context = 0;
        NativeLibrary.Free(_lib);
    }
}
