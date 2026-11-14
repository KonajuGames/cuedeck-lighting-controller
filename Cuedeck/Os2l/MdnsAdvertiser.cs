using System.Runtime.InteropServices;

namespace Cuedeck.Os2l;

/// <summary>
/// How an mDNS advertisement stands.
/// </summary>
enum MdnsState
{
    Idle,
    Registering,
    Registered,
    Failed,
}

/// <summary>
/// Advertises one DNS-SD service over mDNS through the system's own responder, so DJ software on the network can find
/// the OS2L server. Registering finishes in the background: poll <see cref="State"/>.
/// </summary>
interface IMdnsAdvertiser
{
    /// <summary>
    /// Whether this system has a responder to register with.
    /// </summary>
    bool Available { get; }

    MdnsState State { get; }

    /// <summary>
    /// The name it was registered as: the one asked for, or the responder's rename of it ("Cuedeck (2)") when that's
    /// taken. "" until registered.
    /// </summary>
    string RegisteredName { get; }

    /// <summary>
    /// Why it failed, when <see cref="State"/> is <see cref="MdnsState.Failed"/>.
    /// </summary>
    string Error { get; }

    /// <summary>
    /// Starts registering <paramref name="instance"/> as a <paramref name="type"/> service (as <c>_os2l._tcp</c>) on
    /// this host's <paramref name="port"/>. Returns false, with <see cref="Error"/> set, if it couldn't even start.
    /// </summary>
    bool Start(string instance, string type, int port);

    /// <summary>
    /// Withdraws the advertisement (waiting a moment for the responder).
    /// </summary>
    void Stop();
}

/// <summary>
/// The Windows advertiser: dnsapi.dll's DNS-SD functions (Windows 10 1809 and later), looked up when it's made so an
/// older Windows only finds it unavailable. sLight's native <c>WindowsBackend</c>, ported: the registration's
/// callback comes on a system thread, and a responder that doesn't answer within <see cref="WAIT_MS"/> has its
/// registration abandoned (left for the late callback) rather than freed under it.
/// </summary>
sealed unsafe class WindowsMdnsAdvertiser : IMdnsAdvertiser
{
    const uint REQUEST_VERSION1 = 1; // DNS_QUERY_REQUEST_VERSION1
    const uint REQUEST_PENDING = 9506; // DNS_REQUEST_PENDING
    const int WAIT_MS = 3000;

    /// <summary>
    /// DNS_SERVICE_REGISTER_REQUEST.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct RegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public nint ServiceInstance;
        public nint CompletionCallback;
        public nint Context;
        public nint Credentials;
        public int UnicastEnabled;
    }

    /// <summary>
    /// One registration, shared with its callback (through a GC handle in the request's context).
    /// </summary>
    sealed class Reg
    {
        public RegisterRequest* Request;
        public nint Instance; // what was asked for
        public nint RegisteredInstance; // what the responder registered (from the callback)
        public WindowsMdnsAdvertiser? Owner; // not touched once abandoned
        public bool Deregistering;
        public bool Abandoned;
        public uint Status;
        public GCHandle Handle;
        public readonly ManualResetEventSlim Registered = new();
        public readonly ManualResetEventSlim Deregistered = new();
    }

    static readonly delegate* unmanaged[Stdcall]<char*, char*, uint*, void*, ushort, ushort, ushort, uint, char**, char**, nint> Construct;
    static readonly delegate* unmanaged[Stdcall]<nint, void> Free;
    static readonly delegate* unmanaged[Stdcall]<RegisterRequest*, void*, uint> Register;
    static readonly delegate* unmanaged[Stdcall]<RegisterRequest*, void*, uint> Deregister;

    static WindowsMdnsAdvertiser()
    {
        // dnsapi.dll stays loaded: an abandoned registration may still call back into it.
        if (!OperatingSystem.IsWindows() || !NativeLibrary.TryLoad("dnsapi.dll", out var lib))
            return;
        if (
            NativeLibrary.TryGetExport(lib, "DnsServiceConstructInstance", out var c)
            && NativeLibrary.TryGetExport(lib, "DnsServiceFreeInstance", out var f)
            && NativeLibrary.TryGetExport(lib, "DnsServiceRegister", out var r)
            && NativeLibrary.TryGetExport(lib, "DnsServiceDeRegister", out var d)
        )
        {
            Construct = (delegate* unmanaged[Stdcall]<char*, char*, uint*, void*, ushort, ushort, ushort, uint, char**, char**, nint>)c;
            Free = (delegate* unmanaged[Stdcall]<nint, void>)f;
            Register = (delegate* unmanaged[Stdcall]<RegisterRequest*, void*, uint>)r;
            Deregister = (delegate* unmanaged[Stdcall]<RegisterRequest*, void*, uint>)d;
        }
    }

    readonly Lock _lock = new();
    Reg? _reg;
    string _suffix = "";
    MdnsState _state;
    string _name = "";
    string _error = "";

    public bool Available => Construct != null;

    public MdnsState State
    {
        get
        {
            lock (_lock)
                return _state;
        }
    }

    public string RegisteredName
    {
        get
        {
            lock (_lock)
                return _name;
        }
    }

    public string Error
    {
        get
        {
            lock (_lock)
                return _error;
        }
    }

    void SetState(MdnsState state, string name = "", string error = "")
    {
        lock (_lock)
        {
            _state = state;
            _name = name;
            _error = error;
        }
    }

    public bool Start(string instance, string type, int port)
    {
        Stop();
        if (!Available)
        {
            SetState(MdnsState.Failed, error: "DNS-SD API not available (needs Windows 10 1809+)");
            return false;
        }
        _suffix = "." + type + ".local";
        var host = HostName() + ".local";
        var full = instance + _suffix;
        var reg = new Reg { Owner = this };
        fixed (char* pFull = full, pHost = host)
            // No addresses given: the responder answers with this host's own.
            reg.Instance = Construct(pFull, pHost, null, null, (ushort)port, 0, 0, 0, null, null);
        if (reg.Instance == 0)
        {
            SetState(MdnsState.Failed, error: "DnsServiceConstructInstance failed");
            Destroy(reg);
            return false;
        }
        reg.Handle = GCHandle.Alloc(reg);
        reg.Request = (RegisterRequest*)NativeMemory.AllocZeroed((nuint)sizeof(RegisterRequest));
        reg.Request->Version = REQUEST_VERSION1;
        reg.Request->InterfaceIndex = 0; // every interface
        reg.Request->ServiceInstance = reg.Instance;
        reg.Request->CompletionCallback = (nint)(delegate* unmanaged[Stdcall]<uint, nint, nint, void>)&OnComplete;
        reg.Request->Context = GCHandle.ToIntPtr(reg.Handle);

        SetState(MdnsState.Registering, instance);
        lock (_lock)
            _reg = reg; // before registering: the callback may come at once
        var r = Register(reg.Request, null);
        if (r != REQUEST_PENDING)
        {
            lock (_lock)
                _reg = null;
            SetState(MdnsState.Failed, error: $"DnsServiceRegister failed (error {r})");
            Destroy(reg);
            return false;
        }
        return true;
    }

    public void Stop()
    {
        Reg? reg;
        lock (_lock)
        {
            reg = _reg;
            _reg = null;
        }
        if (reg is null)
            return;
        // The register callback has to have come before deregistering (the request's reused for that).
        if (!reg.Registered.Wait(WAIT_MS))
        {
            Abandon(reg);
            return;
        }
        bool wasRegistered;
        lock (reg)
        {
            wasRegistered = reg.Status == 0;
            reg.Deregistering = true;
        }
        if (wasRegistered)
        {
            // Deregister what was actually registered: after a name conflict that's the renamed instance, and
            // deregistering the name asked for would withdraw whoever owns that and orphan ours.
            if (reg.RegisteredInstance != 0)
                reg.Request->ServiceInstance = reg.RegisteredInstance;
            var r = Deregister(reg.Request, null);
            if (r == REQUEST_PENDING && !reg.Deregistered.Wait(WAIT_MS))
            {
                Abandon(reg);
                return;
            }
        }
        Destroy(reg);
        SetState(MdnsState.Idle);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    static void OnComplete(uint status, nint context, nint result)
    {
        var reg = (Reg)GCHandle.FromIntPtr(context).Target!;
        // A DNS_SERVICE_INSTANCE's first field is its name.
        var resultName = result != 0 ? Marshal.PtrToStringUni(*(nint*)result) ?? "" : "";
        bool deregistering,
            abandoned,
            keep = false;
        lock (reg)
        {
            deregistering = reg.Deregistering;
            abandoned = reg.Abandoned;
            if (!deregistering)
            {
                reg.Status = status;
                // Kept for DnsServiceDeRegister.
                if (status == 0 && !abandoned && result != 0 && result != reg.Instance && reg.RegisteredInstance == 0)
                {
                    reg.RegisteredInstance = result;
                    keep = true;
                }
            }
        }
        if (!keep && result != 0 && result != reg.Instance && result != reg.RegisteredInstance)
            Free(result);
        if (abandoned)
            return; // its owner may be gone; reg is deliberately leaked
        if (deregistering)
        {
            reg.Deregistered.Set();
            return;
        }
        var self = reg.Owner!;
        bool current;
        lock (self._lock)
            current = self._reg == reg;
        if (current)
        {
            if (status == 0)
            {
                // "Cuedeck._os2l._tcp.local" -> "Cuedeck"
                if (resultName.Length > self._suffix.Length && resultName.EndsWith(self._suffix, StringComparison.OrdinalIgnoreCase))
                    resultName = resultName[..^self._suffix.Length];
                self.SetState(MdnsState.Registered, resultName);
            }
            else
                self.SetState(MdnsState.Failed, error: $"DnsServiceRegister failed (error {status})");
        }
        reg.Registered.Set();
    }

    /// <summary>
    /// The responder didn't answer in time: leave the registration for its late callback (which then only frees its
    /// own result) rather than free it under a pending call.
    /// </summary>
    void Abandon(Reg reg)
    {
        lock (reg)
        {
            reg.Abandoned = true;
            reg.Owner = null;
        }
        SetState(MdnsState.Idle);
    }

    static void Destroy(Reg reg)
    {
        if (reg.Instance != 0)
            Free(reg.Instance);
        if (reg.RegisteredInstance != 0)
            Free(reg.RegisteredInstance);
        if (reg.Request != null)
            NativeMemory.Free(reg.Request);
        if (reg.Handle.IsAllocated)
            reg.Handle.Free();
        reg.Registered.Dispose();
        reg.Deregistered.Dispose();
    }

    /// <summary>
    /// This computer's DNS host name (GetComputerNameEx's ComputerNameDnsHostname), or its NetBIOS name.
    /// </summary>
    static string HostName()
    {
        var name = System.Net.Dns.GetHostName();
        var dot = name.IndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }
}
