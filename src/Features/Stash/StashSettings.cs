using EpochAssist.Core;

namespace EpochAssist.Features;

internal static class StashSettings
{
    // Stored ids are a contract. Renaming or retyping one raises the version and adds a migration for it. Presets and the
    // per-stash choices are files, not settings, so profiles and share codes never carry them.
    public static readonly SettingsGroup Group = new("EpochAssist_Stash", "Stash", version: 1) { ActionKey = "stash" };

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

    /// <summary>Whether anything shows where items belong; with all three off the items aren't checked.</summary>
    public static bool AnyItemHints => ItemCounts.Value || ItemOutlines.Value || ItemLabels.Value;

    public static void Register() => Group.Register();
}
