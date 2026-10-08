using TPConsole.Core;

const string Usage = """
usage: tpconsole <command>
  export-cc TPWORK       print a Control Center workspace as a TPConsole profile (JSON)
  driver-info            read-only queries to the USB audio driver (rate, channels, profiles)
  driver-meters          driver peak meters (read-only)
  driver-stats           driver stream statistics (read-only, no reset)
  engine-info            device versions/settings as the app engine sees them (read-only)
  listen [--meters]      print frames the device sends; meters hidden by default
""";

switch (args)
{
    case ["export-cc", var tpwork]:
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
            new Profile { Mixer = ControlCenterImport.Load(tpwork) }, Engine.Json));
        return 0;
    case ["driver-info"]:
    {
        // Read-only queries only (DriverClient enforces an allow-list).
        using var drv = DriverClient.Open();
        Console.WriteLine($"interface {drv.Path}");
        void Try(string name, Func<object> f)
        {
            try { Console.WriteLine($"{name}: {f()}"); }
            catch (Exception e) { Console.WriteLine($"{name}: ERROR {e.Message}"); }
        }
        string Hex(byte[] b) => $"{b.Length} bytes: {Convert.ToHexString(b.AsSpan(0, Math.Min(b.Length, 512)))}";
        string Text(byte[] b) => new string(System.Text.Encoding.Unicode.GetString(b).Select(c => c < ' ' ? '|' : c).ToArray());
        Try("version", () => drv.Version());
        Try("sample rate", () => drv.CurrentSampleRate());
        Try("asio", () => drv.ActiveAsioInstance() is int i && drv.AsioDetails(i) is var a ? $"instance {i}, {a} sizes [{string.Join(",", a.BufferSizes)}]" : "none");
        Try("supported rates", () => string.Join(", ", drv.SupportedSampleRates()));
        Try("device properties", () => Text(drv.Query(DriverClient.IoctlDeviceProperties, outSize: 0x410)));
        for (uint t = 0; t <= 5; t++)
        {
            var tt = t;
            Try($"channel ids type {t}", () => Hex(drv.Query(DriverClient.IoctlChannelIds, BitConverter.GetBytes(tt), 64 * 16)));
        }
        return 0;
    }
    case ["driver-meters"]:
    {
        // Driver-side peak meters for the 8 playback channels (incl. ASIO streams), 5 s.
        using var drv = DriverClient.Open();
        var ids = drv.ChannelIds(1);
        drv.EnablePeakMeters(ids);
        try
        {
            for (int t = 0; t < 10; t++)
            {
                Thread.Sleep(500);
                var p = drv.ReadPeakMeters(ids);
                Console.WriteLine(string.Join("  ", p.Select((v, i) => $"PB{i + 1} {(v <= 0 ? "-inf" : (20 * Math.Log10(v)).ToString("0.0")),6}")));
            }
        }
        finally { drv.DisablePeakMeters(ids); }
        return 0;
    }
    case ["driver-stats"]:
    {
        // Read-only (reset = false).
        using var drv = DriverClient.Open();
        var st = drv.Statistics();
        Console.WriteLine($"dropouts {st.Dropouts}, usb errors {st.UsbErrors}, engine underruns {st.EngineUnderruns}");
        foreach (var x in st.Streams) Console.WriteLine($"  {x}");
        return 0;
    }
    case ["engine-info"]:
    {
        // Runs the app engine briefly (re-sends the saved profile, same values) and prints what the device reported.
        // Read-only: never stores to the device's flash or rewrites the app's profile on exit.
        using var engine = new Engine(readOnly: true);
        for (int i = 0; i < 40 && engine.Device.SoftwareVersion is null; i++) Thread.Sleep(100);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(engine.Device, Engine.Json));
        return 0;
    }
    case ["listen", .. var rest]:
    {
        bool meters = rest.Contains("--meters");
        using var dev = E2x2Device.Open();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.WriteLine($"listening on {dev.Path} (Ctrl+C to stop)");
        var last = new Dictionary<Param, int>();
        try
        {
            await foreach (var f in dev.ReadFramesAsync(cts.Token))
            {
                bool isMeter = Meters.IsMeter(f.Param);
                if (isMeter && !meters) continue;
                // Meters stream constantly; only print changes.
                if (last.TryGetValue(f.Param, out var prev) && prev == f.Value) continue;
                last[f.Param] = f.Value;
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {f}{(isMeter ? $"  ({f.Value / 10.0} dB)" : "")}");
            }
        }
        catch (OperationCanceledException) { }
        return 0;
    }
    default:
        Console.Error.WriteLine(Usage);
        return 1;
}
