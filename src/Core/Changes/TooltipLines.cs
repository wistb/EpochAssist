using HarmonyLib;
using Il2Cpp;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

/// <summary>An item as a tooltip line provider sees it: its names, rarity and affixes (with tiers and rolls where the data has them).</summary>
internal readonly record struct TooltipItem(string? Name, string? BaseName, int Rarity, IReadOnlyList<DataEntry> Affixes);

/// <summary>
/// Extra lines in the game's own item tooltip, in its lore text style, below its content: the main tooltip and the
/// comparison one, for inventory, stash and ground items. The patch is on only while a provider exists, and a
/// provider goes with its owner. Postfixes only, so other mods' tooltip patches keep working.
/// </summary>
internal static class TooltipLines
{
    // Our block in the text, so a rebuilt tooltip never shows lines twice or lines of a stopped owner.
    private const string BlockStart = "<link=\"epochassist\">";
    private const string BlockEnd = "</link>";

    private static readonly List<(Feature Owner, Func<TooltipItem, IEnumerable<string>?> Lines)> s_providers = new();
    // The item each tooltip was last set to: UpdateInfo, which sets the lore text, runs after SetAs…Tooltip returns.
    private static readonly Dictionary<IntPtr, ItemDataUnpacked> s_items = new();

    public static int Count => s_providers.Count;

    /// <summary>Adds a provider: for each item tooltip it is asked for lines (none or null adds nothing).</summary>
    public static void Add(Feature owner, Func<TooltipItem, IEnumerable<string>?> lines)
    {
        s_providers.Add((owner, lines));
        Patch();
    }

    /// <summary>Removes the owner's providers.</summary>
    public static void Remove(Feature owner)
    {
        if (s_providers.RemoveAll(p => p.Owner == owner) > 0) Patch();
    }

    /// <summary>The host, with Edits.RemoveOwner (stop, fault, unavailable).</summary>
    internal static void RemoveOwner(Feature owner) => Remove(owner);

    internal static void Reset()
    {
        s_providers.Clear();
        s_items.Clear();
    }

    private static void Patch()
    {
        if (FeatureHost.Find("gameui") is { State: FeatureState.Running } feature) FeatureHost.SetPatchGroup(feature, GameUiFeature.TooltipGroup, s_providers.Count > 0);
        if (s_providers.Count == 0) s_items.Clear();
    }

    /// <summary>The group's patch is live (the feature runs and a provider exists).</summary>
    public static bool Patched => FeatureHost.Find("gameui") is { } feature && feature.DemandGroupsOn.Contains(GameUiFeature.TooltipGroup);

    private static TooltipItem Describe(ItemDataUnpacked item)
    {
        var names = GameData.Item(item);
        var affixes = new List<DataEntry>();
        float modifier = GameData.AffixModifier(item) ?? 0f;
        var list = item.affixes;
        for (int i = 0; list != null && i < list.Count; i++)
        {
            var affix = list[i];
            affixes.Add(GameData.Affix(affix.affixId, affix.affixTier, affix.affixRoll, modifier));
        }
        return new TooltipItem(names.Name ?? item.FullName, names.BaseName, item.rarity, affixes);
    }

    // Each provider in its owner's guard: a throw adds nothing and counts against that owner.
    private static string? Block(ItemDataUnpacked? item)
    {
        if (item == null || !Game.Alive(item) || s_providers.Count == 0) return null;
        var described = Describe(item);
        var lines = new List<string>();
        foreach (var (owner, provider) in s_providers.ToList())
        {
            if (owner.State != FeatureState.Running) continue;
            FeatureHost.Guarded(owner, "tooltip lines", () =>
            {
                if (provider(described) is { } more) lines.AddRange(more.Where(l => !string.IsNullOrEmpty(l)));
            });
        }
        return lines.Count == 0 ? null : BlockStart + string.Join("\n", lines) + BlockEnd;
    }

    private static void Append(Il2CppTMPro.TextMeshProUGUI? text, ItemDataUnpacked? item)
    {
        if (text == null || !Game.Alive(text)) return;
        string current = Strip(text.text ?? string.Empty);
        string? block = Block(item);
        string next = block == null ? current : current + (current.Length > 0 ? "\n" : string.Empty) + block;
        if (next != text.text) text.text = next;
        if (block != null && !text.gameObject.activeSelf) text.gameObject.SetActive(true);
    }

    private static string Strip(string text)
    {
        int start = text.IndexOf(BlockStart, StringComparison.Ordinal);
        if (start < 0) return text;
        int cut = start > 0 && text[start - 1] == '\n' ? start - 1 : start;
        return text.Substring(0, cut);
    }

    private static void Guard(string phase, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Log.ErrorThrottled($"gameui:{phase}", $"Tooltip lines ({phase}) failed: {e.Message}", 30);
        }
    }

    [HarmonyPatch(typeof(UITooltipItem), nameof(UITooltipItem.SetAsItemTooltip))]
    private static class SetAsItemTooltip_Patch
    {
        private static void Postfix(UITooltipItem __instance, ItemDataUnpacked __3) => Guard("item", () => s_items[__instance.Pointer] = __3);
    }

    [HarmonyPatch(typeof(UITooltipItem), nameof(UITooltipItem.SetAsGroundTooltip))]
    private static class SetAsGroundTooltip_Patch
    {
        private static void Postfix(UITooltipItem __instance, ItemDataUnpacked __3) => Guard("ground", () => s_items[__instance.Pointer] = __3);
    }

    // Other tooltip kinds (affix shards, blessings) get no lines.
    [HarmonyPatch(typeof(UITooltipItem), nameof(UITooltipItem.SetAsAffixTooltip))]
    private static class SetAsAffixTooltip_Patch
    {
        private static void Postfix(UITooltipItem __instance) => Guard("affix", () => s_items.Remove(__instance.Pointer));
    }

    [HarmonyPatch(typeof(UITooltipItem), nameof(UITooltipItem.UpdateInfo))]
    private static class UpdateInfo_Patch
    {
        private static void Postfix(UITooltipItem __instance) =>
            Guard("main", () => Append(__instance.loreText, s_items.TryGetValue(__instance.Pointer, out var item) ? item : null));
    }

    [HarmonyPatch(typeof(UITooltipItem), nameof(UITooltipItem.ExpandTooltip))]
    private static class ExpandTooltip_Patch
    {
        private static void Postfix(UITooltipItem __instance, ItemDataUnpacked __3) => Guard("compare", () => Append(__instance.compareLoreText, __3));
    }
}
