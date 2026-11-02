using EpochAssist.Core;

namespace EpochAssist.Features;

internal static class StashSettings
{
    // Stored ids are a contract. Renaming or retyping one raises the version and adds a migration for it. Presets and the
    // per-stash choices are files, not settings, so profiles and share codes never carry them.
    public static readonly SettingsGroup Group = new("EpochAssist_Stash", "Stash", version: 1) { ActionKey = "stash", Module = "stash" };

    public static readonly Section Main = Group.Section("main", "Stash layouts");
    // On by default: the page only reads until a button that says what it sends is pressed.
    public static readonly Toggle Enabled = new(Main, "Enabled", "Stash layouts", true)
    {
        QuickLabel = "Stash layouts", Help = "The Stash tab: the stash beside a preset's layout, built with the game's own stash buttons.",
    };

    // What the page shows about items that sit in the wrong tab for the preview. Only read: items are never moved.
    public static readonly Section Items = Group.Section("items", "Items");
    public static readonly Toggle ItemCounts = new(Items, "ItemCounts", "Count items to move", true)
    {
        Help = "On the Stash page, beside each tab that holds items: how many the game would file into another tab once the preview is made.",
    };
    public static readonly Toggle ItemOutlines = new(Items, "ItemOutlines", "Outline them in the stash window", true)
    {
        Help = "While the Stash page is open, items in the game's stash window that belong in another tab get an amber outline.",
    };
    public static readonly Toggle ItemLabels = new(Items, "ItemLabels", "Say where an item belongs", true)
    {
        Help = "While the Stash page is open, the stash item under the pointer says which tab the game would file it into.",
    };

    // Stash search: finding items by their numbers across every tab. Only read: items are never moved.
    public static readonly Section Search = Group.Section("search", "Stash search");
    public static readonly Toggle SearchEnabled = new(Search, "SearchEnabled", "Stash search", true)
    {
        QuickLabel = "Stash search", Help = "The Filter page in the Stash tab: find items by tier, value and roll across every stash tab.",
    };
    public static readonly Toggle SearchBadges = new(Search, "SearchBadges", "Show matched value", true)
    {
        Help = "Each result shows the number it matched on, beside its name or in its corner: the stat's value, a roll's quality, a tier, a count or a sum.",
    };
    public static readonly Toggle SearchHighlight = new(Search, "SearchHighlight", "Outline matches", true)
    {
        Help = "While a search shows results, matches in the stash window's open tab get an outline and the rest are dimmed.",
    };

    public static readonly Number SearchDim = new(Search, "SearchDim", "Dim other items", 0.6f, 0f, 1f)
    {
        Step = 0.05f, Slider = true, Percent = true,
        Help = "How dark the open tab's other items get while matches are outlined.",
    };
    public static readonly Toggle SearchFilterButton = new(Search, "SearchFilterButton", "Filter button", true)
    {
        Help = "A Filter button beside the stash window's search box opens the Filter page.",
    };
    public static readonly Toggle SearchPanel = new(Search, "SearchPanel", "Filter panel", true)
    {
        Help = "A ~ search typed in the stash's search box also lists its matches in a panel beside Quick View, with tooltips and click to focus.",
    };
    public static readonly Toggle SearchBox = new(Search, "SearchBox", "~ searches", true)
    {
        Help = "Text in the stash's own search box that starts with ~ is read as a stash search: stats by name with values, "
            + "tiers and roll quality, totals, brackets and counts, beside the game's own words. The game then shows the matches as it does its own.",
    };

    /// <summary>Whether anything shows where items belong; with all three off the items aren't checked.</summary>
    public static bool AnyItemHints => ItemCounts.Value || ItemOutlines.Value || ItemLabels.Value;

    public static void Register() => Group.Register();
}
