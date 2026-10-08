using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TPConsole.Core;

/// <summary>
/// HID control interface of the TOPPING E2x2 OTG (VID_152A PID_8756 MI_04).
/// Several processes may open it at once (Control Center included); every open handle
/// receives its own copy of the device's input reports.
/// </summary>
public sealed class E2x2Device : IDisposable
{
    const string HardwareId = "vid_152a&pid_8756";
    const string Interface = "mi_04";

    readonly FileStream _stream;
    readonly Lock _writeLock = new();

    public string Path { get; }

    E2x2Device(string path, FileStream stream) { Path = path; _stream = stream; }

    public static E2x2Device Open()
    {
        Native.HidD_GetHidGuid(out var hid);
        var path = Native.EnumerateInterfacePaths(hid).FirstOrDefault(p =>
                       p.Contains(HardwareId, StringComparison.OrdinalIgnoreCase) && p.Contains(Interface, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("E2x2 OTG HID interface not found");
        var h = Native.CreateFile(path, Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, 0, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, 0);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateFile {path}");
        // The device streams meters nonstop; the default 32-report queue overflows (and drops the
        // replies we care about) whenever we're busy writing.
        Native.HidD_SetNumInputBuffers(h, 512);
        return new E2x2Device(path, new FileStream(h, FileAccess.ReadWrite, 0, isAsync: true));
    }

    public void Write(Frame frame)
    {
        var report = frame.ToReport();
        lock (_writeLock) _stream.Write(report);
    }

    /// <summary>Streams every valid frame the device sends (mostly level meters) until cancelled.</summary>
    public async IAsyncEnumerable<Frame> ReadFramesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var buf = new byte[Frame.ReportLength];
        while (true)
        {
            int n = await _stream.ReadAsync(buf, ct);
            if (n == 0) yield break;
            if (Frame.Parse(buf.AsSpan(0, n)) is { } f) yield return f;
        }
    }

    public void Dispose() => _stream.Dispose();
}

static partial class Native
{
    public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    const uint DIGCF_PRESENT = 2, DIGCF_DEVICEINTERFACE = 0x10;

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("hid")]
    public static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid")]
    public static extern bool HidD_SetNumInputBuffers(SafeFileHandle h, uint count);

    [DllImport("setupapi", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetClassDevsW")]
    static extern nint SetupDiGetClassDevs(ref Guid cls, nint enumerator, nint hwnd, uint flags);

    [DllImport("setupapi", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(nint set, nint devInfo, ref Guid cls, uint index, ref DeviceInterfaceData data);

    [DllImport("setupapi", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
    static extern bool SetupDiGetDeviceInterfaceDetail(nint set, ref DeviceInterfaceData data, nint detail, int size, out int required, nint devInfo);

    [DllImport("setupapi")]
    static extern bool SetupDiDestroyDeviceInfoList(nint set);

    [StructLayout(LayoutKind.Sequential)]
    struct DeviceInterfaceData
    {
        public int Size;
        public Guid ClassGuid;
        public int Flags;
        public nint Reserved;
    }

    public static List<string> EnumerateInterfacePaths(Guid guid)
    {
        var set = SetupDiGetClassDevs(ref guid, 0, 0, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == -1) throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs");
        var paths = new List<string>();
        try
        {
            var data = new DeviceInterfaceData { Size = Marshal.SizeOf<DeviceInterfaceData>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, 0, ref guid, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref data, 0, 0, out int size, 0);
                var detail = Marshal.AllocHGlobal(size);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize is 8 on x64; the path follows the DWORD.
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, 0))
                        paths.Add(Marshal.PtrToStringUni(detail + 4)!);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return paths;
    }
}
