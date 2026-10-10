using System.Runtime.InteropServices;

namespace AmllTtmlTool.WinUI.Services;

// Minimal WASAPI latency access: stream latency, endpoint buffer size,
// and mix rate off the default render device. OS interfaces only, no new
// dependency. Used for latency compensation numbers only; the position
// truth itself stays provider-integrated (see PositionClock). NOTE: this
// probe client is never started, so it must never be used for IAudioClock
// position/QPC reads — a never-started stream's clock returns undefined
// values (observed poisoning snapshots with ~27ms steps).
internal static class NativeAudioClock
{
    private const uint CLSCTX_ALL = 0x17;

    private enum EDataFlow
    {
        Render = 0,
    }

    private enum ERole
    {
        Multimedia = 1,
    }

    // The coclass is activated by CLSID (canonical pattern): asking for
    // `new` on a ComImport coclass can resolve to another assembly's
    // same-CLSID wrapper (observed: NAudio's), so never name a coclass here.
    private static readonly Guid CLSID_MMDeviceEnumerator =
        new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    private static IMMDeviceEnumeratorRaw CreateEnumerator()
    {
        var unk = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator));
        if (unk is null) throw new InvalidOperationException("MMDeviceEnumerator activation returned null");
        return (IMMDeviceEnumeratorRaw)unk;
    }

    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [System.Runtime.InteropServices.Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    private interface IMMDeviceEnumeratorRaw
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [System.Runtime.InteropServices.Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    private interface IMMDeviceRaw
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [System.Runtime.InteropServices.Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    private interface IAudioClientRaw
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDurationHns, long periodicityHns, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint numBufferFrames);
        [PreserveSig] int GetStreamLatency(out long latencyHns);
        [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriodHns, out long minimumPeriodHns);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid interfaceId, out IntPtr service);
    }

    // NOTE on refcounting: each out-IntPtr carries exactly one COM
    // reference, which Marshal.GetObjectForIUnknown wraps WITHOUT adding
    // another — the RCW owns that single reference, and
    // Marshal.ReleaseComObject hands it back. Releasing the raw pointer on
    // top would free the object while the RCW still uses it. CoTaskMem
    // from GetMixFormat is freed plain.
    // Step-tagged failure detail for the harness (which step + HRESULT).
    // Empty when the last call succeeded.
    public static string LastErrorDetail = "";

    private static void Fail(string step, int hr)
    {
        LastErrorDetail = $"{step} hr=0x{hr:X8}";
    }

    private static void Check(string step, int hr)
    {
        if (hr < 0)
        {
            Fail(step, hr);
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    public static bool TryReadLatency(out double streamLatencyMs, out uint bufferFrames, out int mixRate)
    {
        streamLatencyMs = 0;
        bufferFrames = 0;
        mixRate = 0;
        object? deviceObj = null;
        object? clientObj = null;
        IntPtr mixPtr = IntPtr.Zero;
        try
        {
            var enumerator = CreateEnumerator();
            Check("GetDefaultAudioEndpoint", enumerator.GetDefaultAudioEndpoint(
                (int)EDataFlow.Render, (int)ERole.Multimedia, out var devicePtr));
            deviceObj = Marshal.GetObjectForIUnknown(devicePtr);
            var device = (IMMDeviceRaw)deviceObj;
            Check("Activate", device.Activate(ref UnsafeIidAudioClient(), (int)CLSCTX_ALL, IntPtr.Zero, out var clientPtr));
            clientObj = Marshal.GetObjectForIUnknown(clientPtr);
            var client = (IAudioClientRaw)clientObj;
            Check("GetMixFormat", client.GetMixFormat(out mixPtr));
            mixRate = Marshal.ReadInt32(mixPtr, 4); // nSamplesPerSec at offset 4
            // Stream latency and buffer size need an initialized client: spin
            // up a short-lived shared-mode stream (never started, no sound)
            // purely to query them.
            Check("Initialize", client.Initialize(0, 0, 1_000_000, 0, mixPtr, IntPtr.Zero));
            Check("GetStreamLatency", client.GetStreamLatency(out var latencyHns));
            streamLatencyMs = latencyHns / 10000.0;
            // An uninitialized client would report E_NOT_VALID_STATE here.
            if (client.GetBufferSize(out var frames) >= 0) bufferFrames = frames;
            LastErrorDetail = "";
            return true;
        }
        catch (Exception ex)
        {
            LastErrorDetail = $"managed:{ex.GetType().Name}:{ex.Message} hr=0x{ex.HResult:X8}";
            return false;
        }
        finally
        {
            if (mixPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(mixPtr);
            if (clientObj is not null) Marshal.ReleaseComObject(clientObj);
            if (deviceObj is not null) Marshal.ReleaseComObject(deviceObj);
        }
    }

    private static Guid _iidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");

    private static ref Guid UnsafeIidAudioClient() => ref _iidAudioClient;
}
