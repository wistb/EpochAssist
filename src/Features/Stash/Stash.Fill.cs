using EpochAssist.Core;
using Il2Cpp;

namespace EpochAssist.Features;

internal static partial class Stash
{
    private static (ulong Fingerprint, string Preset, int Uniques, Dictionary<string, int> Tabs, Dictionary<string, (int Items, int Cells)> Detail)? s_fill;

    /// <summary>Changes when a unique list from the planner builds may have changed.</summary>
    private static int UniquesGeneration()
    {
        try
        {
            return Planner.LibraryGeneration;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>A preset tab's rules with its unique list: stored, or the planner builds' uniques with the preset's edits.</summary>
    public static TabRules RulesOf(PresetTab tab)
    {
        if (tab.Uniques is not { } source) return tab.Rules;
        var ids = new List<int>(source.Kind == UniqueSource.Builds ? BuildUniques() : tab.Rules.Uniques);
        foreach (int id in source.Added)
            if (!ids.Contains(id)) ids.Add(id);
        ids.RemoveAll(source.Removed.Contains);
        return tab.Rules.WithUniques(ids);
    }

    private static IEnumerable<int> BuildUniques()
    {
        try
        {
            return Planner.LibraryUniques().Select(u => (int)u);
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("stash:uniques", $"Stash: planner uniques unreadable: {e.Message}", 30);
            return Enumerable.Empty<int>();
        }
    }

    /// <summary>
    /// Full layout: per section, how many tabs the stash's items for it fill. Each item goes to the first section in preset
    /// order whose rules the game's own test matches; a section holds 12 × 17 cells a tab, at least one tab. Kept per read.
    /// </summary>
    public static IReadOnlyDictionary<string, int> FillCounts(StashSnapshot snap, Preset preset)
    {
        int uniques = UniquesGeneration();
        if (s_fill is { } f && f.Fingerprint == snap.Fingerprint && f.Preset == preset.Id && f.Uniques == uniques) return f.Tabs;
        var detail = new Dictionary<string, (int Items, int Cells)>();
        var sections = preset.AllTabs().Where(p => !p.Tab.IsMerged).Select(p => (p.Tab.Key, Game: Rules.ToGame(RulesOf(p.Tab)))).ToList();
        int level = CharacterLevel();
        foreach (var (data, cells) in StashItems())
        {
            foreach (var (key, game) in sections)
            {
                if (game == null || !game.Match(data, level)) continue;
                var d = detail.GetValueOrDefault(key);
                detail[key] = (d.Items + 1, d.Cells + cells);
                break;
            }
        }
        var tabs = sections.ToDictionary(s => s.Key, s => Math.Max(1, (detail.GetValueOrDefault(s.Key).Cells + FillTabCells - 1) / FillTabCells));
        s_fill = (snap.Fingerprint, preset.Id, uniques, tabs, detail);
        return tabs;
    }

    /// <summary>Items and cells per section from the last Full layout count (for the dev verb).</summary>
    public static IReadOnlyDictionary<string, (int Items, int Cells)> FillDetail => s_fill?.Detail ?? new Dictionary<string, (int, int)>();

    /// <summary>Every item in the current stash's tabs, unpacked, with the cells it covers.</summary>
    public static IEnumerable<(ItemDataUnpacked Data, int Cells)> StashItems()
    {
        var result = new List<(ItemDataUnpacked, int)>();
        try
        {
            var holder = ItemContainersManager.Instance?.stash;
            var all = holder?.stashItemContainers;
            if (all == null || all.Length == 0) return result;
            int index = holder!._CurrentStashIndex_k__BackingField;
            var current = index >= 0 && index < all.Length ? all[index] : all[0];
            var containers = current?.containers;
            if (containers == null) return result;
            for (int i = 0; i < containers.Count; i++)
            {
                var content = containers[i]?.content;
                if (content == null) continue;
                for (int k = 0; k < content.Count; k++)
                    if (content[k] is { } e && e.data?.getAsUnpacked() is { } unpacked)
                        result.Add((unpacked, Math.Max(1, e.size.x * e.size.y)));
            }
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("stash:items", $"Stash: stash items unreadable: {e.Message}", 30);
        }
        return result;
    }

    private static int CharacterLevel()
    {
        try
        {
            return PlayerFinder.getLocalTreeData()?.characterLevel ?? 100;
        }
        catch
        {
            return 100;
        }
    }

    /// <summary>
    /// The order check: for each target, the stash items its rules take (the game's own test) are tested against every
    /// tab above it that stays the player's own; the first that takes one is named. Targets with no such items get none.
    /// </summary>
    public static void CheckOrder(StashSnapshot snap, Preview preview)
    {
        preview.Warnings.Clear();
        if (!Has(TestGroup)) return;
        try
        {
            var items = StashItems().Select(i => i.Data).ToList();
            if (items.Count == 0) return;
            int level = CharacterLevel();
            var targetTabs = new HashSet<int>(preview.Targets.Where(x => x.TabId != null).Select(x => x.TabId!.Value));
            // Each own tab with rules at its planned place: (folder place, tab place).
            var own = new List<(int Folder, int Index, TabSnap Tab, Il2Cpp.StashPriority Game)>();
            foreach (var f in preview.Folders.Where(f => f.ExistingId != null))
            {
                int i = 0;
                foreach (var t in snap.TabsIn(f.ExistingId!.Value))
                {
                    if (targetTabs.Contains(t.Id)) continue;
                    if (!t.Rules.IsEmpty && Rules.ToGame(t.Rules) is { } g) own.Add((f.Index, i, t, g));
                    i++;
                }
            }
            if (own.Count == 0) return;
            foreach (var x in preview.Targets)
            {
                if (Rules.ToGame(x.Rules) is not { } mine) continue;
                var above = own.Where(o => o.Folder < x.Folder.Index || o.Folder == x.Folder.Index && o.Index < x.Index).ToList();
                if (above.Count == 0) continue;
                foreach (var item in items)
                {
                    if (!mine.Match(item, level)) continue;
                    var taker = above.FirstOrDefault(o => o.Game.Match(item, level));
                    if (taker.Tab == null) continue;
                    preview.Warnings.Add(new OrderWarning(x.Tab.Key, taker.Tab.Id, taker.Tab.Name, ItemName(item)));
                    break;
                }
            }
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("stash:order", $"Stash: order check failed: {e.Message}", 30);
        }
    }

    private static string ItemName(ItemDataUnpacked item)
    {
        try
        {
            return item.FullName ?? "an item";
        }
        catch
        {
            return "an item";
        }
    }
}
