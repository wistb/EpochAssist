using EpochAssist.Core;
using Il2Cpp;

namespace EpochAssist.Features;

internal static partial class Stash
{
    /// <summary>One item in the stash: its tab, the game's entry for it (the item's element in the window points at the
    /// same one), the tab the game would file it into once the preview is made (by name; empty when no tab takes it), and
    /// whether that is another tab.</summary>
    internal sealed record ItemPlace(int TabId, IntPtr Entry, string Into, bool Elsewhere);

    // A tab that files items once the preview is made: a planned tab (with the stash tab that fills it, if any) or one of
    // the player's kept tabs with rules of its own, in the order the game tries them.
    private sealed record Filer(string Name, int? TabId, StashPriority Game);

    private sealed class BelongsRun
    {
        public ulong Fingerprint;
        public Preview? Preview;
        public List<Filer> Filers = new();
        public List<(int TabId, IntPtr Entry, ItemDataUnpacked Data)> Items = new();
        public List<ItemPlace> Done = new();
        public int Level;
    }

    private static BelongsRun? s_belongs;

    /// <summary>Items are being checked: the frame's budget allows the slice.</summary>
    public static bool Belonging => s_belongs != null;
    private static (ulong Fingerprint, Preview Preview, List<ItemPlace> Items, Dictionary<int, int> Elsewhere)? s_belonged;

    // Time a frame may spend testing items: the game's own rule test costs microseconds an item and tab, and a full stash
    // holds thousands of items.
    private const double BelongsMsPerFrame = 1.0;

    /// <summary>Per tab, how many of its items the game would file into another tab once the preview is made; null while
    /// they are still being worked out.</summary>
    public static IReadOnlyDictionary<int, int>? ElsewhereCounts =>
        s_belonged is { } b && s_snap != null && s_preview != null && b.Fingerprint == s_snap.Fingerprint && b.Preview == s_preview ? b.Elsewhere : null;

    /// <summary>Every stash item with the tab it would go to, for the current read and preview; null while being worked out.</summary>
    public static IReadOnlyList<ItemPlace>? ItemPlaces =>
        s_belonged is { } b && s_snap != null && s_preview != null && b.Fingerprint == s_snap.Fingerprint && b.Preview == s_preview ? b.Items : null;

    // Each frame the page shows: starts the work for a new read or preview, then tests items for a slice of time.
    private static void BelongsFrame()
    {
        var snap = s_snap;
        var preview = s_preview;
        if (snap == null || preview == null || !Has(TestGroup) || !StashSettings.AnyItemHints) return;
        if (s_belonged is { } done && done.Fingerprint == snap.Fingerprint && done.Preview == preview) return;
        if (s_belongs is not { } run || run.Fingerprint != snap.Fingerprint || run.Preview != preview)
        {
            s_belongs = run = StartBelongs(snap, preview);
            if (run == null) return;
        }
        try
        {
            long until = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(BelongsMsPerFrame * System.Diagnostics.Stopwatch.Frequency / 1000d);
            while (run.Done.Count < run.Items.Count)
            {
                var (tabId, entry, data) = run.Items[run.Done.Count];
                var into = run.Filers.FirstOrDefault(f => f.Game.Match(data, run.Level));
                run.Done.Add(new ItemPlace(tabId, entry, into?.Name ?? string.Empty, into != null && into.TabId != tabId));
                if (System.Diagnostics.Stopwatch.GetTimestamp() > until) return;
            }
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("stash:belongs", $"Stash: item check failed: {e.Message}", 30);
            s_belongs = null;
            return;
        }
        var elsewhere = run.Done.Where(i => i.Elsewhere).GroupBy(i => i.TabId).ToDictionary(g => g.Key, g => g.Count());
        s_belonged = (run.Fingerprint, preview, run.Done, elsewhere);
        s_belongs = null;
        InvalidateRows();
    }

    private static BelongsRun? StartBelongs(StashSnapshot snap, Preview preview)
    {
        try
        {
            var run = new BelongsRun { Fingerprint = snap.Fingerprint, Preview = preview, Level = CharacterLevel() };
            var targeted = new HashSet<int>(preview.Targets.Where(x => x.TabId != null).Select(x => x.TabId!.Value));
            foreach (var f in preview.Folders.OrderBy(f => f.Index))
            {
                foreach (var x in preview.Targets.Where(x => x.Folder == f).OrderBy(x => x.Index))
                    if (Rules.ToGame(x.Rules) is { } game) run.Filers.Add(new Filer(x.Tab.Name, x.TabId, game));
                if (f.ExistingId is not { } id) continue;
                foreach (var t in snap.TabsIn(id))
                    if (!targeted.Contains(t.Id) && !t.Rules.IsEmpty && Rules.ToGame(t.Rules) is { } own) run.Filers.Add(new Filer(t.Name, t.Id, own));
            }
            var holder = ItemContainersManager.Instance?.stash;
            var all = holder?.stashItemContainers;
            if (all == null || all.Length == 0) return run;
            int index = holder!._CurrentStashIndex_k__BackingField;
            var containers = (index >= 0 && index < all.Length ? all[index] : all[0])?.containers;
            // Container n holds the items of the tab with id n; only trusted when there is one for each tab.
            if (containers == null || containers.Count != snap.TabCount) return run;
            for (int i = 0; i < containers.Count; i++)
            {
                if (snap.Tab(i) == null) continue;
                var content = containers[i]?.content;
                if (content == null) continue;
                for (int k = 0; k < content.Count; k++)
                    if (content[k] is { } e && e.data?.getAsUnpacked() is { } data)
                        run.Items.Add((i, e.Pointer, data));
            }
            return run;
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("stash:belongs", $"Stash: item check couldn't start: {e.Message}", 30);
            return null;
        }
    }
}
