namespace TPConsole.Core;

// User-facing model of the whole device. Everything the device can't do itself (mute, solo,
// phase invert, link, pan) lives here and is folded into wire values by ToFrames().
// Levels are in dB; null means "-inf" (slider at the bottom).

public enum Source
{
    In1 = 1, MobileIn = 2, In2 = 3, In4 = 4, In12 = 5, In34 = 6,
    Playback12 = 7, Playback34 = 8, Playback56 = 9, Playback78 = 10,
    MixA = 11, MixB = 12, MixC = 13, MixD = 14,
}

public sealed class InputChannel
{
    public int GainDb { get; set; }          // 0..+20, digital
    public bool Phantom48V { get; set; }
    public bool Instrument { get; set; }
    public bool Monitor { get; set; }        // MON button: direct input monitoring
    public bool Mute { get; set; }
    public bool Solo { get; set; }
    public bool Invert { get; set; }
}

public sealed class Output
{
    public Source Source { get; set; } = Source.MixA;
    public int? LevelDbL { get; set; } = 0;  // -89..0
    public int? LevelDbR { get; set; } = 0;
    public bool Link { get; set; } = true;
    public bool MuteL { get; set; }
    public bool MuteR { get; set; }
    public bool Invert { get; set; }
    // Jacks; only meaningful for OUT 1+2 (all three) and Mobile OUT (headphone, line).
    public bool Headphone { get; set; }
    public bool Line { get; set; }
    public bool Aux { get; set; }
}

public sealed class MixChannel
{
    public int? LevelDb { get; set; } = 0;   // -89..+12
    public int Pan { get; set; }             // 0 = hard left, 50 = centre, 100 = hard right (linear)
    public bool Mute { get; set; }
    public bool Solo { get; set; }
    public bool Invert { get; set; }
}

/// <summary>One of MIX A..D. 12 mono channels: IN1, IN2, Mobile IN L/R, then playback pairs 1/2..7/8 as L/R.</summary>
public sealed class Mix
{
    public const int Channels = 12;

    public MixChannel[] Channel { get; set; } = Enumerable.Range(0, Channels)
        .Select(i => new MixChannel { Pan = i % 2 == 0 ? 0 : 100 }).ToArray();
    /// <summary>Stereo link per channel pair (6 pairs).</summary>
    public bool[] Link { get; set; } = [false, true, true, true, true, true];
}

public sealed class Mixer
{
    // Inputs in device order 21..24: IN 1, Mobile IN L, IN 2, Mobile IN R.
    public InputChannel[] Inputs { get; set; } = [new(), new(), new(), new()];
    public bool PhoneGainHigh1 { get; set; }  // Phone Out 1: false = 0 dBu, true = +17 dBu
    public bool PhoneGainHigh2 { get; set; }

    public Output Out12 { get; set; } = new() { Headphone = true };
    public Output MobileOut { get; set; } = new() { Source = Source.Playback12 };
    public Output Loopback12 { get; set; } = new();
    public Output Loopback34 { get; set; } = new();
    public Output Loopback56 { get; set; } = new();
    public Output Spdif { get; set; } = new() { Source = Source.Playback12 };

    public Mix[] Mixes { get; set; } = [new(), new(), new(), new()];

    /// <summary>All wire values, in Control Center's startup-sync order.</summary>
    public IEnumerable<Frame> ToFrames()
    {
        Frame F(byte a, byte s, int v) => new(a, s, v);
        static int B(bool b) => b ? 1 : 0;

        yield return F(0x37, 0x01, B(Out12.Headphone));
        yield return F(0x37, 0x03, B(Out12.Line));
        yield return F(0x37, 0x05, B(Out12.Aux));
        yield return F(0x37, 0x02, B(MobileOut.Headphone));
        yield return F(0x37, 0x04, B(MobileOut.Line));
        // Control Center mirrors the OUT 1+2 AUX flag here; kept as-is until tested.
        yield return F(0x37, 0x06, B(Out12.Aux));
        yield return F(0x35, 0x02, B(PhoneGainHigh1));
        yield return F(0x36, 0x02, B(PhoneGainHigh2));

        yield return F(0x35, 0x01, (int)Out12.Source);
        yield return F(0x36, 0x01, (int)MobileOut.Source);
        yield return F(0x57, 0x01, (int)Loopback12.Source);
        yield return F(0x58, 0x01, (int)Loopback34.Source);
        yield return F(0x59, 0x01, (int)Loopback56.Source);
        yield return F(0x5C, 0x01, (int)Spdif.Source);

        // Control Center ignores mute on OUT 1+2; we honour it everywhere.
        foreach (var (o, a) in new[] { (Out12, (byte)0x31), (MobileOut, (byte)0x33), (Loopback12, (byte)0x51), (Loopback34, (byte)0x53), (Loopback56, (byte)0x55), (Spdif, (byte)0x5A) })
        {
            yield return F(a, 0x03, o.MuteL ? 0 : Levels.Level(o.LevelDbL, o.Invert));
            yield return F((byte)(a + 1), 0x03, (o.Link ? o.MuteL : o.MuteR) ? 0 : Levels.Level(o.Link ? o.LevelDbL : o.LevelDbR, o.Invert));
        }

        for (int m = 0; m < Mixes.Length; m++)
        {
            var mix = Mixes[m];
            bool anySolo = mix.Channel.Any(c => c.Solo);
            byte busL = (byte)(0x61 + 2 * m), busR = (byte)(busL + 1);
            for (int i = 0; i < Mix.Channels; i++)
            {
                var c = mix.Channel[i];
                int state = c.Mute || (anySolo && !c.Solo) ? 0 : c.Invert ? -1 : 1;
                int toL = state == 0 ? 0 : Levels.MixerSend(c.LevelDb, 100 - c.Pan, c.Invert);
                int toR = state == 0 ? 0 : Levels.MixerSend(c.LevelDb, c.Pan, c.Invert);
                byte sub = (byte)(i + 1);
                yield return F(busL, sub, toL);
                yield return F(busR, sub, toR);
            }
        }

        bool anyInputSolo = Inputs.Any(i => i.Solo);
        for (int i = 0; i < 4; i++)
        {
            var c = Inputs[i];
            byte a = (byte)(0x21 + i);
            yield return F(a, 0x01, B(c.Monitor));
            yield return F(a, 0x02, B(c.Phantom48V));
            yield return F(a, 0x03, B(c.Instrument));
        }
        for (int i = 0; i < 4; i++)
        {
            var c = Inputs[i];
            bool silent = c.Mute || (anyInputSolo && !c.Solo);
            yield return F((byte)(0x21 + i), 0x05, silent ? 0 : Levels.Level(c.GainDb, c.Invert));
        }
    }
}
