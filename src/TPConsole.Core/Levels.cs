namespace TPConsole.Core;

/// <summary>
/// dB -> wire value, bit-exact with Control Center 1.0.4.0.
/// Linear gain scaled by 2^25 (0x02000000 = 0 dB), computed through a float step.
/// </summary>
public static class Levels
{
    // CC uses a table of round(10^(dB/20) * 1e6) times 0.33554432 (= 2^25 / 1e8), stored as float.
    static float Gain(int db) => (float)(Math.Round(Math.Pow(10, db / 20.0) * 1e6, MidpointRounding.AwayFromZero) * 0.33554432);

    /// <summary>Output / loopback / S/PDIF level and input digital gain. Null dB = mute (0).</summary>
    public static int Level(int? db, bool invert = false)
    {
        if (db is null) return 0;
        int v = (int)(Gain(db.Value) * 100f);
        return invert ? -v : v;
    }

    /// <summary>
    /// Mixer send for one input channel to one side of a bus. Pan is linear 0..100 (50 = centre = -6 dB).
    /// For an odd sub (left input) CC sends pan to the L bus and 100-pan to the R bus; even subs the reverse.
    /// </summary>
    public static int MixerSend(int? db, int pan, bool invert = false)
    {
        if (db is null) return 0;
        if (pan is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(pan));
        int v = (int)Gain(db.Value) * pan;
        return invert ? -v : v;
    }
}
