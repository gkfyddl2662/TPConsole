using TPConsole.Core;

namespace TPConsole.Tests;

// Vectors come from Control Center 1.0.4.0 (captured 2026-10-08).
public class ProtocolTests
{
    static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    [Theory]
    [InlineData("00 22 33 20 01 01 31 01 ff ff fe e5 13 fa 66 77 00", 0x31, 0x01, unchecked((int)0xFFFFFEE5))]
    [InlineData("00 22 33 20 01 01 5a 02 ff ff fd b3 06 36 66 77 00", 0x5A, 0x02, unchecked((int)0xFFFFFDB3))]
    [InlineData("00 22 33 20 01 01 32 01 ff ff fe 3d 7a fa 66 77 00", 0x32, 0x01, unchecked((int)0xFFFFFE3D))]
    public void ParsesDeviceFrameWithCrc(string report, byte addr, byte sub, int value)
    {
        Assert.Equal(new Frame(addr, sub, value), Frame.Parse(Hex(report)));
    }

    [Fact]
    public void RejectsBadCrc()
    {
        Assert.Null(Frame.Parse(Hex("00 22 33 20 01 01 31 01 ff ff fe e5 13 fb 66 77 00")));
    }

    [Fact]
    public void BuildsReportLikeControlCenter()
    {
        Assert.Equal(Hex("00 22 33 20 01 01 37 01 00 00 00 01 00 00 66 77 00"), new Frame(0x37, 0x01, 1).ToReport());
    }

    [Theory]
    [InlineData(-3, 0x016A77E6)]
    [InlineData(-6, 0x01009B96)]
    [InlineData(-9, 0x00B5AA0C)]
    [InlineData(-12, 0x00809BD8)]
    [InlineData(-15, 0x005B0C45)]
    public void LevelMatchesControlCenter(int db, int expected)
    {
        Assert.Equal(expected, Levels.Level(db));
    }

    [Fact]
    public void AllInputGainStepsMatchCapture()
    {
        int[] captured =
        [
            0x02000000, 0x023e7928, 0x028491d0, 0x02d3382c, 0x032b771c, 0x038e7a98, 0x03fd92f8, 0x047a39a8,
            0x050615e8, 0x05a30318, 0x06531618, 0x0718a508, 0x07f64f18, 0x08ef0520, 0x0a061410, 0x0b3f3000,
            0x0c9e8060, 0x0e28aec0, 0x0fe2f5e0, 0x11d33460, 0x14000000,
        ];
        for (int db = 0; db <= 20; db++) Assert.Equal(captured[db], Levels.Level(db));
    }

    [Theory]
    [InlineData(0, 100, 0x01FFFFE0)]
    [InlineData(0, 50, 0x00FFFFF0)]
    [InlineData(0, 0, 0)]
    public void MixerSendMatchesControlCenter(int db, int pan, int expected)
    {
        Assert.Equal(expected, Levels.MixerSend(db, pan));
    }

    [Fact]
    public void MuteAndInvert()
    {
        Assert.Equal(0, Levels.Level(null));
        Assert.Equal(-0x02000000, Levels.Level(0, invert: true));
    }
}
