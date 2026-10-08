using System.Buffers.Binary;

namespace TPConsole.Core;

/// <summary>
/// One parameter frame. Same layout both ways (17-byte HID report incl. report id 0):
/// <c>00 | 22 33 | 20 01 01 | AA SS | V3 V2 V1 V0 | C1 C0 | 66 77 | 00</c>.
/// </summary>
public readonly record struct Frame(byte Addr, byte Sub, int Value)
{
    public const int ReportLength = 17;

    public Param Param => new(Addr, Sub);

    /// <summary>PC -> device report. Control Center leaves the checksum at 0 and the device accepts it.</summary>
    public byte[] ToReport()
    {
        var r = new byte[ReportLength];
        r[1] = 0x22; r[2] = 0x33;
        r[3] = 0x20; r[4] = 0x01; r[5] = 0x01;
        r[6] = Addr; r[7] = Sub;
        BinaryPrimitives.WriteInt32BigEndian(r.AsSpan(8), Value);
        r[14] = 0x66; r[15] = 0x77;
        return r;
    }

    /// <summary>Parses a device -> PC report (with leading report id). Returns null for anything that isn't a frame.</summary>
    public static Frame? Parse(ReadOnlySpan<byte> report, bool verifyCrc = true)
    {
        if (report.Length < 16 || report[0] != 0) return null;
        var f = report[1..];
        if (f[0] != 0x22 || f[1] != 0x33 || f[13] != 0x66 || f[14] != 0x77) return null;
        if (verifyCrc && Crc16Modbus(f[2..11]) != (f[11] << 8 | f[12])) return null;
        if (f[5] == 0 && f[6] == 0) return null;
        return new Frame(f[5], f[6], BinaryPrimitives.ReadInt32BigEndian(f[7..]));
    }

    /// <summary>Device frames carry CRC-16/MODBUS over <c>20 01 01 AA SS V3..V0</c>, high byte first.</summary>
    public static int Crc16Modbus(ReadOnlySpan<byte> data)
    {
        int crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
        }
        return crc;
    }

    public override string ToString() => $"{Param} = 0x{Value:X8}";
}

/// <summary>Parameter address: <c>AA.SS</c> in hex, e.g. 37.01 = OUT 1+2 headphone enable.</summary>
public readonly record struct Param(byte Addr, byte Sub)
{
    public override string ToString() => $"{Addr:X2}.{Sub:X2}";
}
