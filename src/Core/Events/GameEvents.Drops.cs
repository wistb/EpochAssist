using Il2Cpp;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

internal static partial class GameEvents
{
    // ---- Drops ----

    private static Job? s_dropScan;
    private static bool s_dropsBaseline = true;
    private static readonly HashSet<uint> s_seenDrops = new();

    private static void UpdateDrops(Feature feature, bool? online)
    {
        bool active = Wanted<ItemDropped>(feature, s_drops, online);
        if (active == s_drops.Active) return;
        s_drops.Active = active;
        s_dropsBaseline = true;
        s_seenDrops.Clear();
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
            var entry = GameData.Item(item);
            Bus.Raise(new ItemDropped(entry.Name ?? NullIfEmpty(item.FullName), entry.BaseName, item.rarity, RarityName(item), visual.id));
        }
    }

    // The rarity byte is the affix count below exalted, so the name comes from the game's own checks.
    private static string RarityName(ItemDataUnpacked item) =>
        item.isLegendary() ? "Legendary" : item.isSet() ? "Set" : item.isUnique() ? "Unique" : item.isExaltedItem() ? "Exalted"
            : item.isRare() ? "Rare" : item.isMagic() ? "Magic" : "Normal";

    private static string? NullIfEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;
}
