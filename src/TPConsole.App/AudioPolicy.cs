using System.Runtime.InteropServices;

namespace TPConsole.App;

/// <summary>
/// Per-app default output device ("App volume and device preferences" in Windows Settings).
/// Uses the undocumented WinRT factory Windows.Media.Internal.AudioPolicyConfig, called through its
/// vtable (the same interface EarTrumpet uses). Windows applies the change to running streams.
/// </summary>
public static unsafe class AudioPolicy
{
    // IAudioPolicyConfigFactory IID on Windows 10 21H2+ / 11 (the only Windows this personal app runs on).
    static readonly Guid Iid = new("ab3d4648-e242-459f-b02f-541c70306324");
    // Vtable: IInspectable (6) + 19 unused methods, then Set/Get/ClearAll.
    const int SlotSet = 25, SlotGet = 26;

    const string RenderInterface = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    const string Prefix = @"\\?\SWD#MMDEVAPI#";
    const int Console = 0, Multimedia = 1, Render = 0;

    static nint _factory;

    static nint Factory()
    {
        if (_factory != 0) return _factory;
        const string cls = "Windows.Media.Internal.AudioPolicyConfig";
        WindowsCreateString(cls, cls.Length, out var hs);
        try
        {
            var iid = Iid;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hs, ref iid, out _factory));
        }
        finally { WindowsDeleteString(hs); }
        return _factory;
    }

    /// <summary>Endpoint id (as from MMDevice.ID) the app is pinned to, or null if it follows the Windows default.</summary>
    public static string? Get(uint pid)
    {
        var f = Factory();
        var get = (delegate* unmanaged[Stdcall]<nint, uint, int, int, nint*, int>)(*(nint**)f)[SlotGet];
        nint hs;
        if (get(f, pid, Render, Multimedia, &hs) < 0 || hs == 0) return null;
        try
        {
            var raw = new string(WindowsGetStringRawBuffer(hs, out _));
            // "\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{guid}#{interface}" -> "{0.0.0.00000000}.{guid}"
            if (!raw.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return raw.Length == 0 ? null : raw;
            var id = raw[Prefix.Length..];
            int hash = id.IndexOf('#');
            return hash < 0 ? id : id[..hash];
        }
        finally { WindowsDeleteString(hs); }
    }

    /// <summary>Pins the app (all its streams) to an output endpoint; null = follow the Windows default.</summary>
    public static void Set(uint pid, string? endpointId)
    {
        var f = Factory();
        var set = (delegate* unmanaged[Stdcall]<nint, uint, int, int, nint, int>)(*(nint**)f)[SlotSet];
        nint hs = 0;
        if (endpointId is not null)
        {
            var full = Prefix + endpointId + RenderInterface;
            Marshal.ThrowExceptionForHR(WindowsCreateString(full, full.Length, out hs));
        }
        try
        {
            Marshal.ThrowExceptionForHR(set(f, pid, Render, Multimedia, hs));
            Marshal.ThrowExceptionForHR(set(f, pid, Render, Console, hs));
        }
        finally { if (hs != 0) WindowsDeleteString(hs); }
    }

    [DllImport("combase", PreserveSig = true)]
    static extern int RoGetActivationFactory(nint classId, ref Guid iid, out nint factory);
    [DllImport("combase", CharSet = CharSet.Unicode)]
    static extern int WindowsCreateString(string s, int length, out nint hstring);
    [DllImport("combase")]
    static extern int WindowsDeleteString(nint hstring);
    [DllImport("combase")]
    static extern char* WindowsGetStringRawBuffer(nint hstring, out uint length);
}
