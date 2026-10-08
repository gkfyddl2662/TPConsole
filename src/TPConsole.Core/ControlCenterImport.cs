using System.Text.Json.Nodes;

namespace TPConsole.Core;

/// <summary>Reads a Control Center 1.0.4.0 E2x2 OTG workspace (E2X2_OTG\*.TPwork, JSON).</summary>
public static class ControlCenterImport
{
    public static string DefaultFolder =>
        @"C:\Program Files\TOPPING Pro\TOPPING Professional Control Center\E2X2_OTG";

    public static Mixer Load(string path)
    {
        var j = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var m = new Mixer
        {
            PhoneGainHigh1 = Bool(j, "Gain1_Enable"),
            PhoneGainHigh2 = Bool(j, "Gain2_Enable"),
            Out12 = Output(j["OUT12"]!),
            MobileOut = Output(j["OUT34"]!),
            Spdif = Output(j["OUT56"]!),
            Loopback12 = Output(j["Loopback12"]!),
            Loopback34 = Output(j["Loopback34"]!),
            Loopback56 = Output(j["Loopback56"]!),
        };
        for (int i = 0; i < 4; i++)
        {
            var n = j[$"IN{i + 1}"]!;
            m.Inputs[i] = new InputChannel
            {
                GainDb = Int(n, "IN_volume"),
                Phantom48V = Bool(n, "P48VEnable"),
                Instrument = Bool(n, "INSTEnable"),
                Monitor = Bool(n, "MONIEnable"),
                Mute = Bool(n, "MUTEEnable"),
                Solo = Bool(n, "SOLOEnable"),
                Invert = Bool(n, "ANTIEnable"),
            };
        }
        for (int x = 0; x < 4; x++) m.Mixes[x] = Mix(j[$"MixSave{(char)('A' + x)}"]!);
        return m;
    }

    // Sliders: 0 = -inf, otherwise dB = position - 90 (outputs top out at 90, mixer faders at 102).
    static int? Db(JsonNode n, string key) => Int(n, key) is 0 ? null : Int(n, key) - 90;

    static Output Output(JsonNode n) => new()
    {
        Source = (Source)Int(n, "ChannelOUT_mode"),
        LevelDbL = Db(n, "OUT_volume_L"),
        LevelDbR = Db(n, "OUT_volume_R"),
        Link = Bool(n, "LinkEnable"),
        MuteL = Bool(n, "MUTEEnable_L"),
        MuteR = Bool(n, "MUTEEnable_R"),
        Invert = Bool(n, "ANTIEnable"),
        Headphone = Bool(n, "HPEnable"),
        Line = Bool(n, "LINEOUTEnable"),
        Aux = Bool(n, "AUXEnable"),
    };

    static Mix Mix(JsonNode n)
    {
        var mix = new Mix();
        for (int k = 0; k < 6; k++)
        {
            var p = Group(n, $"MixParmeter_{k + 1}");
            mix.Link[k] = Bool(p, "LinkEnable");
            foreach (var (side, idx) in new[] { ("L", 2 * k), ("R", 2 * k + 1) })
            {
                // CC's Mix_channel is the share sent to the channel's own side (100 = hard to its side).
                int share = Int(p, $"Mix_channel_{side}");
                mix.Channel[idx] = new MixChannel
                {
                    LevelDb = Db(p, $"Mix_volume_{side}"),
                    Pan = side == "L" ? 100 - share : share,
                    Mute = Bool(p, $"MUTEEnable_{side}"),
                    Solo = Bool(p, $"SOLOEnable_{side}"),
                    Invert = Bool(p, $"ANTIEnable_{side}"),
                };
            }
        }
        return mix;
    }

    // Mixer entries are stored flat ("MixParmeter_1.LinkEnable"); gather one group into an object.
    static JsonObject Group(JsonNode n, string prefix)
    {
        var o = new JsonObject();
        foreach (var (k, v) in n.AsObject())
            if (k.StartsWith(prefix + ".")) o[k[(prefix.Length + 1)..]] = v?.DeepClone();
        return o;
    }

    static bool Bool(JsonNode n, string key) => n[key]?.GetValue<bool>() ?? false;
    static int Int(JsonNode n, string key) => n[key]?.GetValue<int>() ?? 0;
}
