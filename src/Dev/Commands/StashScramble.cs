using EpochAssist.Core;
using EpochAssist.Features;
using UnityEngine;

namespace EpochAssist.Dev;

// Makes work for the Stash page: every empty tab gets a made-up name, a random colour, no rules and a random folder, and
// every folder a made-up name and colour. Tabs holding items and whatever the page keeps are left alone. Sent through the
// page's own step calls under Apply's guards: the game's stash window open, its message limit per type, nothing while two folders or tabs share an id or
// the game's folder window is closing, and it stops when the character unloads.
internal static class StashScramble
{
    private static readonly List<Stash.Step> s_queue = new();
    private static int s_total;

    public static bool Running => s_queue.Count > 0;
    public static string Status => Running ? $"Scrambling: {s_total - s_queue.Count} of {s_total}" : string.Empty;

    /// <summary>Queues the scramble; the reason it can't, or null.</summary>
    public static string? Start()
    {
        if (Running) return "a scramble is running";
        if (Stash.StepsBusy || StashReset.Running) return "something else is running";
        var snap = Stash.Read();
        if (snap == null) return "no stash";
        if (snap.SharedIds.Count > 0) return Stash.SharedIdsText(snap);
        var choices = Stash.CurrentChoices;
        var random = new System.Random();
        int n = 0;
        foreach (var f in snap.Folders.Where(f => choices.OfFolder(snap, f) != Stash.TabChoice.Keep))
        {
            var plan = new Stash.PlanFolder { ExistingId = f.Id, Name = $"Mixed {++n}", ColourId = random.Next(0, 18) };
            s_queue.Add(new Stash.Step { Kind = Stash.StepKind.ConfigureFolder, Id = f.Id, Folder = plan, Says = $"Folder {f.Name} → {plan.Name}" });
        }
        n = 0;
        // Into folders the page may change, so a kept folder gains no tab.
        var into = snap.Folders.Where(f => choices.OfFolder(snap, f) != Stash.TabChoice.Keep).ToList();
        foreach (var t in snap.Tabs.Where(t => t.ItemCount == 0 && choices.Of(t) != Stash.TabChoice.Keep && into.Any(f => f.Id == t.FolderId)))
        {
            var folder = into[random.Next(into.Count)];
            var target = new Stash.Target
            {
                Tab = new Stash.PresetTab { Key = "scramble", Name = $"Shuffled {++n}", ColourId = random.Next(0, 18) },
                Rules = Stash.TabRules.Empty,
                Folder = new Stash.PlanFolder { ExistingId = folder.Id, Name = folder.Name },
            };
            s_queue.Add(new Stash.Step { Kind = Stash.StepKind.ConfigureTab, Id = t.Id, Target = target, Says = $"{t.Name} → {target.Tab.Name}" });
        }
        s_total = s_queue.Count;
        Log.Info($"Stash dev scramble: {s_total} changes queued");
        return null;
    }

    public static void Stop() => s_queue.Clear();

    public static void Frame()
    {
        if (!Running) return;
        // The stash is read only when a change has room for its message type to go.
        if (!s_queue.Any(Stash.CanSendNow)) return;
        var snap = Stash.Read();
        if (snap == null || !GameState.InGame)
        {
            Log.Warn("Stash dev scramble stopped: no stash");
            Stop();
            return;
        }
        if (snap.SharedIds.Count > 0)
        {
            Log.Warn("Stash dev scramble stopped: " + Stash.SharedIdsText(snap));
            Stop();
            return;
        }
        if (Stash.ClosingFolderWindow || !Stash.StashWindowOpen) return;
        try
        {
            // Whichever change has room for its message type goes next, so folders and tabs go out side by side.
            for (int i = 0; i < s_queue.Count; i++)
                if (Stash.SendNow(s_queue[i], snap))
                {
                    s_queue.RemoveAt(i);
                    break;
                }
        }
        catch (Exception e)
        {
            Log.Warn($"Stash dev scramble stopped: {e.Message}");
            Stop();
        }
        if (!Running) Log.Info("Stash dev scramble: done");
    }
}
