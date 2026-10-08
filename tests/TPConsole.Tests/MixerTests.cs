using TPConsole.Core;

namespace TPConsole.Tests;

/// <summary>Runs only where the private notes (private/, not in the public repo) are checked out.</summary>
sealed class PrivateFixturesFactAttribute : FactAttribute
{
    public PrivateFixturesFactAttribute()
    {
        if (!File.Exists(MixerTests.RepoFile("private/fixtures/cc_20261008_0052.TPwork")))
            Skip = "Needs private/ fixtures (not in the public repo)";
    }
}

public class MixerTests
{
    internal static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "TPConsole.slnx"))) dir = dir.Parent!;
        return Path.Combine(dir.FullName, rel);
    }

    [PrivateFixturesFact]
    public void ControlCenterWorkspaceProducesCapturedStartupSync()
    {
        // The workspace was saved by Control Center at 00:52, the same startup whose sync is in this log.
        var mixer = ControlCenterImport.Load(RepoFile("private/fixtures/cc_20261008_0052.TPwork"));
        var captured = File.ReadLines(RepoFile("private/capture/log_20261008_005146.txt"))
            .Where(l => l.Contains("HID_WRITE")).Take(139).Skip(1)
            .Select(l => Frame.Parse(Convert.FromHexString(l[(l.IndexOf('[') + 1)..l.IndexOf(']')].Replace(" ", "")), verifyCrc: false))
            .ToList();
        var ours = mixer.ToFrames().ToList();
        Assert.Equal(captured.Count, ours.Count);
        var diffs = captured.Zip(ours).Where(p => p.First != p.Second).Select(p => $"cc {p.First} / ours {p.Second}");
        Assert.Empty(diffs);
    }
}
