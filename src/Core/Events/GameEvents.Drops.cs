using Il2Cpp;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

internal static partial class GameEvents
{
    // ---- Drops ----

    private const float FilterWaitSeconds = 2f;
    private const int MaxKeys = 10000;

    private static Job? s_dropScan;
    private static bool s_dropsBaseline = true;
    private static readonly HashSet<uint> s_seenDrops = new();

    // New drops waiting for the loot filter to judge them: the game labels an item a moment after it appears.
    private sealed record PendingDrop(GroundItemVisuals Visual, ItemDropped Drop, float SeenAt);
    private static readonly List<PendingDrop> s_pendingDrops = new();

    // Items already reported this load, by key, oldest first for the cap: an item picked up and dropped again (here or in
    // another zone) keeps its packed data, so it isn't a new drop.
    private static readonly HashSet<string> s_droppedKeys = new();
    private static readonly Queue<string> s_droppedOrder = new();

    private static void UpdateDrops(Feature feature, bool? online)
    {
        bool active = Wanted<ItemDropped>(feature, s_drops, online);
        if (active == s_drops.Active) return;
        s_drops.Active = active;
        s_dropsBaseline = true;
        s_seenDrops.Clear();
        s_pendingDrops.Clear();
        s_dropScan?.Cancel();
        s_dropScan = active ? Scheduler.Every(feature, "drops scan", DropScanSeconds, ScanDrops, startNow: true) : null;
    }

    // The game lists the item visuals on the ground itself: no scene search.
    private static void ScanDrops()
    {
        if (!Player.Exists) return;
        var all = GroundItemVisuals.all;
        if (all == null) return;
        bool baseline = s_dropsBaseline;
        s_dropsBaseline = false;
        for (int i = 0; i < all.Count; i++)
        {
            var visual = all[i];
            if (!Game.Alive(visual)) continue;
            var item = visual.itemData;
            if (item == null || !s_seenDrops.Add(visual.id) || baseline) continue;
            string? key = ItemKey(item);
            if (key != null && !RememberKey(key)) continue;
            var entry = GameData.Item(item);
            s_pendingDrops.Add(new PendingDrop(visual, new ItemDropped(entry.Name ?? NullIfEmpty(item.FullName), entry.BaseName, item.rarity, RarityName(item), visual.id, Key: key), Clock.Now));
        }
        RaiseJudged();
    }

    // Each waiting drop is raised once its label has the filter's result, or after FilterWaitSeconds without one.
    private static void RaiseJudged()
    {
        if (s_pendingDrops.Count == 0) return;
        bool canRead = s_feature?.Has(DropFilter) == true;
        bool filterOn = canRead && FilterActive();
        for (int i = 0; i < s_pendingDrops.Count; i++)
        {
            var pending = s_pendingDrops[i];
            bool late = Clock.Now - pending.SeenAt >= FilterWaitSeconds;
            ItemDropped drop;
            if (!filterOn)
            {
                drop = pending.Drop;
            }
            else if (Judged(pending.Visual) is { } judged)
            {
                drop = pending.Drop with { Shown = judged.Shown, Rule = judged.Rule };
            }
            else if (late || !Game.Alive(pending.Visual))
            {
                drop = pending.Drop;
            }
            else
            {
                continue;
            }
            s_pendingDrops.RemoveAt(i--);
            Bus.Raise(drop);
        }
    }

    // A 64-bit FNV-1a hash of the item's packed data, as 16 hex digits; null when it can't be read.
    private static string? ItemKey(ItemDataUnpacked item)
    {
        if (s_feature?.Has(DropKey) != true) return null;
        try
        {
            var bytes = item.id;
            if (bytes == null || bytes.Length == 0) return null;
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash *= 1099511628211UL;
            }
            return hash.ToString("x16");
        }
        catch (Exception e)
        {
            Log.WarnThrottled("events:dropkey", $"Drop key read failed: {e.Message}", 60);
            return null;
        }
    }

    // False when the key was reported before.
    private static bool RememberKey(string key)
    {
        if (!s_droppedKeys.Add(key)) return false;
        s_droppedOrder.Enqueue(key);
        if (s_droppedOrder.Count > MaxKeys) s_droppedKeys.Remove(s_droppedOrder.Dequeue());
        return true;
    }

    private static bool FilterActive()
    {
        var manager = Il2CppItemFiltering.ItemFilterManager.Instance;
        return Game.Alive(manager) && manager!.Filter != null && !manager.ManualFilterDisable;
    }

    // The label's result once the filter has run on it: a rule number above 0 on a label the game has listed.
    private static (bool Shown, int Rule)? Judged(GroundItemVisuals visual)
    {
        if (!Game.Alive(visual)) return null;
        var label = visual.label;
        if (!Game.Alive(label) || !label!.isAddedToAll || label.CurrentRuleNumber <= 0) return null;
        return (label.lastFilterResult != Il2CppItemFiltering.Rule.RuleOutcome.HIDE, label.CurrentRuleNumber);
    }

    // The rarity byte is the affix count below exalted, so the name comes from the game's own checks.
    private static string RarityName(ItemDataUnpacked item) =>
        item.isLegendary() ? "Legendary" : item.isSet() ? "Set" : item.isUnique() ? "Unique" : item.isExaltedItem() ? "Exalted"
            : item.isRare() ? "Rare" : item.isMagic() ? "Magic" : "Normal";

    private static string? NullIfEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;
}
