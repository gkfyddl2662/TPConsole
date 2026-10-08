using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace TPConsole.App;

/// <summary>
/// Auto-update from GitHub releases. CI publishes a release (tag vX.Y.Z with TPConsole-Setup-X.Y.Z.exe)
/// for every code change; this checks the latest one at start and hourly. Applying downloads the setup
/// and runs it with /silent: it waits for this process to exit, installs, and starts the app again.
/// </summary>
sealed class Updater : IDisposable
{
    public const string Repo = "gkfyddl2662/TPConsole";
    static readonly Version Full = typeof(Updater).Assembly.GetName().Version!;
    public static readonly Version Current = new(Full.Major, Full.Minor, Math.Max(Full.Build, 0));
    static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TPConsole");

    /// <summary>Running from the installed copy (a development build never updates itself).</summary>
    public static readonly bool Installed = Path.GetDirectoryName(Environment.ProcessPath)!
        .Equals(InstallDir, StringComparison.OrdinalIgnoreCase);

    static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
        DefaultRequestHeaders = { UserAgent = { new ProductInfoHeaderValue("TPConsole", Current.ToString()) } },
    };
    readonly System.Threading.Timer _poll;

    public (Version Version, string Url, string Name)? Available { get; private set; }
    /// <summary>Why the last check failed (offline, rate limit, ...); null when it worked.</summary>
    public string? Error { get; private set; }
    public event Action? Updated;

    public Updater() => _poll = new System.Threading.Timer(_ => _ = CheckAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromHours(1));

    public async Task CheckAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var res = await Http.SendAsync(req);
            res.EnsureSuccessStatusCode();
            var json = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
            var asset = json["assets"]!.AsArray().FirstOrDefault(a =>
                (string)a!["name"]! is var n && n.StartsWith("TPConsole-Setup-", StringComparison.Ordinal) && n.EndsWith(".exe"));
            Available = Version.TryParse(((string)json["tag_name"]!).TrimStart('v'), out var v) && v > Current && asset != null
                ? (v, (string)asset["browser_download_url"]!, (string)asset["name"]!)
                : null;
            Error = null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            Error = e.Message;
        }
        Updated?.Invoke();
    }

    /// <summary>A version whose silent install failed (written by the setup); not retried automatically.</summary>
    public bool AvailableFailedBefore
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TPConsole");
            return Available?.Version.ToString() == key?.GetValue("UpdateFailed") as string;
        }
    }

    /// <summary>Downloads <see cref="Available"/> and starts its silent install; the caller then quits.</summary>
    public async Task<bool> StartAsync(bool tray)
    {
        if (Available is not { } a) return false;
        var file = Path.Combine(Path.GetTempPath(), a.Name);
        using (var res = await Http.GetAsync(a.Url, HttpCompletionOption.ResponseHeadersRead))
        {
            res.EnsureSuccessStatusCode();
            await using var f = File.Create(file);
            await res.Content.CopyToAsync(f);
        }
        if (FileVersionInfo.GetVersionInfo(file).FileVersion != $"{a.Version}.0")
            throw new InvalidDataException($"Downloaded setup is not version {a.Version}");
        // The app runs elevated, so the setup (requireAdministrator) starts without a UAC prompt.
        Process.Start(new ProcessStartInfo(file, $"/silent /wait={Environment.ProcessId}" + (tray ? " /tray" : "")) { UseShellExecute = true });
        return true;
    }

    public void Dispose() => _poll.Dispose();
}
