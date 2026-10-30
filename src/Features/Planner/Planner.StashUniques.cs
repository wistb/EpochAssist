using EpochAssist.Core;
using Il2Cpp;

namespace EpochAssist.Features;

internal static partial class Planner
{
    private static (int Generation, List<ushort> Ids)? s_libraryUniques;

    /// <summary>Every unique the library's builds plan, over all their stages (slots, idols, weaver items), in build order;
    /// uniques this game version doesn't have are left out. Kept until the library changes.</summary>
    internal static IReadOnlyList<ushort> LibraryUniques()
    {
        int generation = Library.Generation;
        if (s_libraryUniques is { } cached && cached.Generation == generation) return cached.Ids;
        var ids = new List<ushort>();
        var seen = new HashSet<ushort>();
        foreach (var build in Library.Builds)
            for (int stage = 0; stage < build.Stages.Count; stage++)
            {
                var gear = GearOf(build, stage);
                foreach (var item in gear.Slots.Values.Concat(gear.Idols).Concat(gear.WeaverItems.Values))
                    if (item.Unique != 0 && !item.Unknown.Contains("unique") && seen.Add(item.Unique)) ids.Add(item.Unique);
            }
        s_libraryUniques = (generation, ids);
        return ids;
    }

    /// <summary>Changes whenever the library does, for callers that cache what they made from it.</summary>
    internal static int LibraryGeneration => Library.Generation;

    // A unique as the game would drop it, rolls at their best, for its tooltip; null for one this game version can't make.
    private static readonly Dictionary<ushort, PlannedItem?> s_uniqueItems = new();

    /// <summary>
    /// The pointer rests on a unique's name on another menu page: the game's tooltip for it, rolls at their best, beside
    /// the menu as a gear line's is. Called each repaint while it's under the pointer; the tooltip closes once it isn't.
    /// </summary>
    internal static void HoverUnique(ushort id)
    {
        if (!s_uniqueItems.TryGetValue(id, out var item)) s_uniqueItems[id] = item = UniqueItem(id);
        if (item == null) return;
        s_menuHover = item;
        s_menuHoverSeen = Clock.Now;
    }

    private static PlannedItem? UniqueItem(ushort id)
    {
        try
        {
            if (UniqueList.getUnique(id) is not { } unique) return null;
            var item = new PlannedItem
            {
                Place = PlannedPlace.Slot,
                ItemType = UniqueList.getUniqueItemType(id),
                SubType = unique.subTypes is { Count: > 0 } subs ? subs[0] : (ushort)0,
                Unique = id,
                RollsFilled = true,
            };
            item.Rarity = RarityOf(id, 0);
            Check(item);
            return item.Unmakeable ? null : item;
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("planner:unique item", $"Planner: unique {id} couldn't be made for its tooltip: {e.Message}", 30);
            return null;
        }
    }
}
