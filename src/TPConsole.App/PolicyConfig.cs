using System.Runtime.InteropServices;

namespace TPConsole.App;

/// <summary>
/// Sets the Windows default playback/recording device through the undocumented IPolicyConfig
/// (what the Sound control panel uses). Call from an MTA thread.
/// </summary>
public static class PolicyConfig
{
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient;

    // Vtable order matters; only SetDefaultEndpoint is used, the rest are placeholders.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    }

    const int Console = 0, Multimedia = 1, Communications = 2;

    /// <summary>Makes the endpoint the default device, or the default communications device.</summary>
    public static void SetDefault(string endpointId, bool communications)
    {
        var pc = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            foreach (var role in communications ? [Communications] : new[] { Console, Multimedia })
                Marshal.ThrowExceptionForHR(pc.SetDefaultEndpoint(endpointId, role));
        }
        finally { Marshal.ReleaseComObject(pc); }
    }
}
