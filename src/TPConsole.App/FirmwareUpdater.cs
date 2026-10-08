using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace TPConsole.App;

/// <summary>
/// Firmware update, following Control Center's procedure through the
/// installed Thesycon API DLL, which is loaded only for this. Unlike Control Center, any failure stops.
/// NEVER run this during development; only on an explicit user action after the warning.
/// </summary>
public static class FirmwareUpdater
{
    const string ApiDll = @"C:\Program Files\TOPPING Pro\USB Audio Device Driver\x64\ToppingProUsbAudioapi_x64.dll";
    const string Product = "E2x2 OTG";

    public enum State { Idle, Initializing, EnteringDfuMode, InProgress, EnteringAppMode, Finished, Failed }

    /// <summary>Downloads the official zip and returns the extracted E2x2 OTG .bin.</summary>
    public static async Task<string> DownloadAsync(string url)
    {
        var dir = Path.Combine(Path.GetTempPath(), "TPConsole-firmware");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, "firmware.zip");
        using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(2) })
        await using (var f = File.Create(zip))
            await (await http.GetStreamAsync(url)).CopyToAsync(f);
        ZipFile.ExtractToDirectory(zip, dir);
        return Directory.GetFiles(dir, "*.bin", SearchOption.AllDirectories).Order().FirstOrDefault()
               ?? throw new InvalidOperationException("No .bin in the firmware package");
    }

    /// <summary>Flashes the image. progress: (state, percent). Throws with the reason on any failure.</summary>
    public static unsafe void Flash(string binPath, Action<State, int> progress)
    {
        // Extra check Control Center doesn't do: only E2x2 OTG images.
        if (!Path.GetFileName(binPath).StartsWith("E2x2_OTG", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{Path.GetFileName(binPath)} is not an E2x2 OTG firmware file");

        var lib = NativeLibrary.Load(ApiDll);
        try
        {
            var enumerate = (delegate* unmanaged<uint>)NativeLibrary.GetExport(lib, "TUSBAUDIO_EnumerateDevices");
            var count = (delegate* unmanaged<uint>)NativeLibrary.GetExport(lib, "TUSBAUDIO_GetDeviceCount");
            var open = (delegate* unmanaged<uint, uint*, uint>)NativeLibrary.GetExport(lib, "TUSBAUDIO_OpenDeviceByIndex");
            var props = (delegate* unmanaged<uint, byte*, uint>)NativeLibrary.GetExport(lib, "TUSBAUDIO_GetDeviceProperties");
            var close = (delegate* unmanaged<uint, uint>)NativeLibrary.GetExport(lib, "TUSBAUDIO_CloseDevice");
            var load = (delegate* unmanaged<char*, uint>)NativeLibrary.GetExport(lib, "TUSBAUDIO_LoadFirmwareImageFromFile");
            var size = (delegate* unmanaged<uint*, uint, uint>)NativeLibrary.GetExport(lib, "TUSBAUDIO_GetFirmwareImageSize");
            var start = (delegate* unmanaged<uint, uint, uint, uint>)NativeLibrary.GetExport(lib, "TUSBAUDIO_StartDfuDownload");
            var status = (delegate* unmanaged<int*, uint*, uint*, uint*, uint>)NativeLibrary.GetExport(lib, "TUSBAUDIO_GetDfuStatus");
            var end = (delegate* unmanaged<uint>)NativeLibrary.GetExport(lib, "TUSBAUDIO_EndDfuProc");
            var unload = (delegate* unmanaged<uint>)NativeLibrary.GetExport(lib, "TUSBAUDIO_UnloadFirmwareImage");
            void Ok(uint st, string what) { if (st != 0) throw new InvalidOperationException($"{what} failed (0x{st:X8})"); }

            // 1. Find the E2x2 OTG among the driver's devices.
            Ok(enumerate(), "EnumerateDevices");
            uint n = count(), index = uint.MaxValue;
            var buf = new byte[0x2000];
            for (uint i = 0; i < n && index == uint.MaxValue; i++)
            {
                uint h;
                if (open(i, &h) != 0) continue;
                fixed (byte* p = buf) props(h, p);
                close(h);
                if (Encoding.Unicode.GetString(buf).Contains(Product)) index = i;
            }
            if (index == uint.MaxValue) throw new InvalidOperationException("E2x2 OTG not found");

            // 2. Load the image and start.
            fixed (char* path = binPath + "\0") Ok(load(path), "LoadFirmwareImageFromFile");
            try
            {
                uint bytes;
                Ok(size(&bytes, 0), "GetFirmwareImageSize");
                if (bytes == 0) throw new InvalidOperationException("Empty firmware image");
                Ok(start(index, 0, 0), "StartDfuDownload");

                // 3. Poll until finished or failed (Control Center: 200 ms, no timeout; we give up after 10 min).
                var deadline = DateTime.UtcNow.AddMinutes(10);
                while (true)
                {
                    int state; uint cur, total, completion;
                    Ok(status(&state, &cur, &total, &completion), "GetDfuStatus");
                    int pct = total == 0 ? 0 : (int)(cur * 100L / total);
                    progress((State)state, pct);
                    if (state == (int)State.Finished) break;
                    if (state == (int)State.Failed) throw new InvalidOperationException($"Device reported failure (0x{completion:X8})");
                    if (DateTime.UtcNow > deadline) throw new TimeoutException("No progress for 10 minutes");
                    Thread.Sleep(200);
                }
            }
            finally
            {
                end();
                unload();
            }
        }
        finally
        {
            NativeLibrary.Free(lib);
        }
    }

}
