using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TPConsole.Core;

public sealed record AsioInfo(uint Rate, uint BufferSize, uint InputLatency, uint OutputLatency, uint[] BufferSizes, bool SafeMode);

/// <summary>USB stream health counters from the driver.</summary>
public sealed record StreamStats(string Name, uint Completed, uint Late, uint UsbErrors, uint PacketErrors, uint Stalls);
public sealed record DriverStats(uint EngineUnderruns, StreamStats[] Streams)
{
    /// <summary>Times audio had to be filled with silence (late USB submissions + engine underruns).</summary>
    public long Dropouts => EngineUnderruns + Streams.Sum(s => (long)s.Late);
    public long UsbErrors => Streams.Sum(s => (long)s.UsbErrors + s.PacketErrors);
}

public sealed record DriverEvent(uint Category, uint Id, byte[] Data);

/// <summary>
/// Direct IOCTL access to the TOPPING (Thesycon-based) USB audio kernel driver, without the
/// vendor API DLL.
/// Only codes on the allow-list can be sent; firmware/DFU and vendor-out requests are never on it.
/// </summary>
public sealed class DriverClient : IDisposable
{
    public static readonly Guid InterfaceGuid = new("E97E1508-2A66-4DF8-B7A7-32D6E5FCBA46");

    public const uint IoctlVersion = 0x80882004;
    public const uint IoctlDeviceProperties = 0x808820C4;
    public const uint IoctlSupportedRates = 0x80882100;
    public const uint IoctlCurrentRate = 0x80882104;
    public const uint IoctlSetRate = 0x80882108;
    public const uint IoctlDeviceStatistics = 0x80882044;
    public const uint IoctlSetAsioBuffer = 0x80882184;
    public const uint IoctlRegisterNotification = 0x808820D8;
    public const uint IoctlReadNotification = 0x808820DC;
    public const uint IoctlChannelIds = 0x80882214;
    public const uint IoctlAsioActiveInstance = 0x8088219C;
    public const uint IoctlAsioInstanceDetails = 0x80882180;
    // Peak meters: driver-side metering only, no effect on audio (Control Center never uses them).
    public const uint IoctlMeterCoefficient = 0x80882218;
    public const uint IoctlMeterEnable = 0x8088221C;
    public const uint IoctlMeterDisable = 0x80882220;
    public const uint IoctlMeterRead = 0x80882224;

    // Read-only requests verified in the driver's dispatch code.
    static readonly HashSet<uint> Allowed =
    [
        IoctlVersion, IoctlDeviceProperties, IoctlSupportedRates, IoctlCurrentRate, IoctlChannelIds,
        IoctlAsioActiveInstance, IoctlAsioInstanceDetails, IoctlSetRate,
        IoctlDeviceStatistics, IoctlSetAsioBuffer, IoctlRegisterNotification, IoctlReadNotification,
        IoctlMeterCoefficient, IoctlMeterEnable, IoctlMeterDisable, IoctlMeterRead,
    ];

    readonly SafeFileHandle _h;
    public string Path { get; }

    DriverClient(string path, SafeFileHandle h) { Path = path; _h = h; }

    public static DriverClient Open()
    {
        var path = Native.EnumerateInterfacePaths(InterfaceGuid).FirstOrDefault()
                   ?? throw new InvalidOperationException("TOPPING driver interface not found");
        var h = Native.CreateFile(path, Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, 0, Native.OPEN_EXISTING, 0, 0);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateFile {path}");
        return new DriverClient(path, h);
    }

    /// <summary>Sends an allow-listed IOCTL. Returns the output bytes, or throws with the driver's status.</summary>
    public byte[] Query(uint code, byte[]? input = null, int outSize = 0x1000)
    {
        if (!Allowed.Contains(code)) throw new InvalidOperationException($"IOCTL 0x{code:X8} is not allow-listed");
        var output = new byte[outSize];
        input ??= [];
        if (!Native.DeviceIoControl(_h, code, input, input.Length, output, output.Length, out int returned, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"IOCTL 0x{code:X8}");
        return output[..returned];
    }

    /// <summary>Driver API version (major, minor); the layouts we use are from 5.74.</summary>
    public (uint Major, uint Minor) Version()
    {
        var b = Query(IoctlVersion, outSize: 0x18);
        return (BitConverter.ToUInt32(b, 0), BitConverter.ToUInt32(b, 4));
    }

    public uint CurrentSampleRate() => BitConverter.ToUInt32(Query(IoctlCurrentRate, outSize: 4));

    /// <summary>Sample rates the device supports (count, then up to 32 rates).</summary>
    public uint[] SupportedSampleRates()
    {
        var b = Query(IoctlSupportedRates, outSize: 0x84);
        uint n = Math.Min(BitConverter.ToUInt32(b, 0), 32);
        return Enumerable.Range(0, (int)n).Select(i => BitConverter.ToUInt32(b, 4 + 4 * i)).ToArray();
    }

    /// <summary>Changes the sample rate. Streaming restarts (audible dropout); refused while ASIO owns the rate.</summary>
    public void SetSampleRate(uint hz) => Query(IoctlSetRate, BitConverter.GetBytes(hz), 0);

    /// <summary>ASIO instance currently attached to the device, or null when no ASIO host is open.</summary>
    public int? ActiveAsioInstance()
    {
        int id = BitConverter.ToInt32(Query(IoctlAsioActiveInstance, outSize: 4));
        return id == -1 ? null : id;
    }

    /// <summary>
    /// ASIO instance details (0xC4 bytes). Observed with Studio Pro at 48 kHz /
    /// 32 samples: +4 rate, +0x14/+0x18 input/output latency (104/168), +0x1C buffer size (32),
    /// +0x20 count and +0x24 list of selectable buffer sizes (8..2048).
    /// </summary>
    public AsioInfo AsioDetails(int instance)
    {
        var b = Query(IoctlAsioInstanceDetails, BitConverter.GetBytes(instance), 0xC4);
        uint U(int o) => BitConverter.ToUInt32(b, o);
        var sizes = Enumerable.Range(0, (int)Math.Min(U(0x20), 32)).Select(i => U(0x24 + 4 * i)).ToArray();
        return new AsioInfo(U(4), U(0x1C), U(0x14), U(0x18), sizes, (U(0x10) & 0x10000) != 0);
    }

    /// <summary>16-byte channel ids. Direction: 0 = recording (10 ch on the E2x2), 1 = playback (8 ch).</summary>
    public byte[][] ChannelIds(uint direction) => Query(IoctlChannelIds, BitConverter.GetBytes(direction), 64 * 16).Chunk(16).ToArray();

    // Meter request entries are {channel id (16), flags (4)}; flag bit0 resets the max-hold on read.
    static byte[] MeterEntries(byte[][] ids, uint flags) => [.. ids.SelectMany(id => id.Concat(BitConverter.GetBytes(flags)))];

    /// <summary>
    /// Starts driver peak metering for the channels. The release coefficient is driver-global;
    /// 0 is what the vendor DLL sends for its default release time. We smooth in the UI anyway.
    /// </summary>
    public void EnablePeakMeters(byte[][] ids)
    {
        Query(IoctlMeterCoefficient, BitConverter.GetBytes(0u), 0);
        Query(IoctlMeterEnable, MeterEntries(ids, 0), 0);
    }

    public void DisablePeakMeters(byte[][] ids) => Query(IoctlMeterDisable, MeterEntries(ids, 0), 0);

    /// <summary>Linear peak (0..1, |sample| / 2^31) per channel since the last read.</summary>
    public float[] ReadPeakMeters(byte[][] ids)
    {
        var b = Query(IoctlMeterRead, MeterEntries(ids, 1), ids.Length * 8);
        return Enumerable.Range(0, ids.Length).Select(i => BitConverter.ToSingle(b, i * 8 + 4)).ToArray();
    }

    /// <summary>
    /// Changes the ASIO buffer size / safe mode. The size must be one of AsioInfo.BufferSizes.
    /// The driver restarts streaming (short dropout) and tells the ASIO host to reset.
    /// </summary>
    public void SetAsioBuffer(int instance, uint rate, uint size, bool safeMode) =>
        Query(IoctlSetAsioBuffer, [.. BitConverter.GetBytes(instance), .. BitConverter.GetBytes(rate), .. BitConverter.GetBytes(size),
            .. BitConverter.GetBytes(safeMode ? 0x10000u : 0u)], 0);

    /// <summary>Per-device stream statistics. reset = clear this device's counters after reading.</summary>
    // The statistics block differs between driver releases (exact size required):
    // 5.74: 0x1BA40, underruns at 0x1B0, streams at 0x1B8, 0xB8 each.
    // 6.16: 0x1BA58, everything from the underrun counter on is 0xC later, and each stream entry has one
    //       more field before the counters (0xBC each). Checked against a live 6.16 dump (stream names IN/OUT).
    sealed record StatsLayout(int Size, int Header, int Stride, int Field);
    static readonly StatsLayout Stats574 = new(0x1BA40, 0, 0xB8, 0), Stats616 = new(0x1BA58, 0xC, 0xBC, 4);

    public DriverStats Statistics(bool reset = false)
    {
        var layout = Version().Major >= 6 ? Stats616 : Stats574;
        var b = Query(IoctlDeviceStatistics, BitConverter.GetBytes(reset ? 1u : 0u), layout.Size);
        uint U(int o) => BitConverter.ToUInt32(b, o);
        int h = layout.Header, f = layout.Field;
        int n = (int)Math.Min(U(0x1B4 + h), 3);
        var streams = Enumerable.Range(0, n).Select(i =>
        {
            int o = 0x1B8 + h + i * layout.Stride;
            var name = System.Text.Encoding.Unicode.GetString(b, o, 16).TrimEnd(' ');
            return new StreamStats(name, U(o + 0x28 + f), U(o + 0x74 + f), U(o + 0x78 + f), U(o + 0x84 + f), U(o + 0x64 + f));
        }).ToArray();
        return new DriverStats(U(0x1B0 + h), streams);
    }

    /// <summary>Registers for driver events; the event is signalled when messages are queued (per handle).</summary>
    public void RegisterNotifications(uint mask, SafeHandle autoResetEvent) =>
        Query(IoctlRegisterNotification, [.. BitConverter.GetBytes(mask), .. BitConverter.GetBytes((ulong)autoResetEvent.DangerousGetHandle())], 0);

    /// <summary>Next queued driver event, or null when the queue is empty.</summary>
    public DriverEvent? ReadNotification()
    {
        try
        {
            var b = Query(IoctlReadNotification, outSize: 0x200);
            if (b.Length < 12) return null;
            uint len = Math.Min(BitConverter.ToUInt32(b, 8), (uint)b.Length - 12);
            return new DriverEvent(BitConverter.ToUInt32(b, 0), BitConverter.ToUInt32(b, 4), b[12..(int)(12 + len)]);
        }
        catch (Win32Exception) { return null; } // empty queue
    }

    public void Dispose() => _h.Dispose();
}

static partial class Native
{
    [DllImport("kernel32", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int returned, nint overlapped);
}
