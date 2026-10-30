using EpochAssist.Core;
using EpochAssist.Features;
using UnityEngine;

namespace EpochAssist.Dev;

// The dev window's Stash section, while the Stash page shows (or a reset runs): the stash read, a reset to each saved
// snapshot and back in as this character, the stash saved as the start snapshot, and a scramble that makes work for the
// page.
internal static class StashDevSection
{
    private static string s_message = string.Empty;
    private static string[] s_snapshots = Array.Empty<string>();
    private static float s_listedAt = -10f;

    public static void Register() => DevWindow.Add(new DevWindow.Section("Stash", () => Stash.PageShown || StashReset.Running, Draw));

    private static void Draw()
    {
        var snap = Stash.Snapshot;
        DevWindow.Text(snap == null ? "no stash read" : $"{snap.Source} copy · {snap.Folders.Count} folders · {snap.TabCount} tabs · {snap.Gold:N0} gold");
        if (Time.unscaledTime > s_listedAt + 2f)
        {
            s_listedAt = Time.unscaledTime;
            s_snapshots = Directory.Exists(StashReset.Snapshots)
                ? Directory.GetDirectories(StashReset.Snapshots).Select(Path.GetFileName).OfType<string>().OrderBy(n => n == StashReset.DefaultSnapshot ? 0 : 1).ThenBy(n => n).ToArray()
                : Array.Empty<string>();
        }
        string? blocked = Stash.Snapshot is not { ClientCopy: false } ? "saved copy only" : StashReset.Running ? "running" : null;
        foreach (string name in s_snapshots)
            if (DevWindow.Button($"Reset to {name}", blocked))
                s_message = StashReset.Start(name) is { } why ? "error " + why : string.Empty;
        if (s_snapshots.Length == 0) DevWindow.Text("no snapshots: save one");
        if (DevWindow.Button($"Save as {StashReset.DefaultSnapshot}", blocked))
        {
            s_message = StashReset.Save(StashReset.DefaultSnapshot);
            s_listedAt = -10f;
        }
        string? busy = StashScramble.Running ? "running" : Stash.StepsBusy ? "steps running" : !Stash.StashWindowOpen ? "open your stash" : null;
        if (DevWindow.Button("Scramble (names, colours, folders)", busy))
            s_message = StashScramble.Start() is { } why ? "error " + why : string.Empty;
        DevWindow.Text(StashScramble.Running ? StashScramble.Status : StashReset.Running ? StashReset.Status : StashReset.Result ?? s_message);
    }
}
