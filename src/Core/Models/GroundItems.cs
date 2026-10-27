using Il2Cpp;

namespace EpochAssist.Core;

/// <summary>
/// The loot filter's word on the items on the ground, read from each item's label; shared by the drops producer and
/// proximity interact, so there is one read. The callers declare the members they need in their own groups.
/// </summary>
internal static class GroundItems
{
    /// <summary>What an item's label says: Listed once the game has put the label in its list (a moment after the item
    /// appears); Rule is the filter rule that matched, 0 when no active filter judged it (crafting materials and keys
    /// are never judged, and with the filter off nothing is); Hidden when that rule hides it.</summary>
    public readonly record struct LabelRead(bool Listed, int Rule, bool Hidden);

    /// <summary>A loot filter is loaded and not turned off by the player.</summary>
    public static bool FilterActive
    {
        get
        {
            var manager = Il2CppItemFiltering.ItemFilterManager.Instance;
            return Game.Alive(manager) && manager!.Filter != null && !manager.ManualFilterDisable;
        }
    }

    /// <summary>The label's read; null when the item or its label is gone.</summary>
    public static LabelRead? Read(GroundItemVisuals visual)
    {
        if (!Game.Alive(visual)) return null;
        var label = visual.label;
        if (!Game.Alive(label)) return null;
        return new LabelRead(label!.isAddedToAll, label.CurrentRuleNumber, label.lastFilterResult == Il2CppItemFiltering.Rule.RuleOutcome.HIDE);
    }

    /// <summary>The filter's result once a rule judged the item: shown or hidden, and the rule; null before that, and
    /// for items no rule judges.</summary>
    public static (bool Shown, int Rule)? Verdict(GroundItemVisuals visual) =>
        Read(visual) is { Listed: true, Rule: > 0 } read ? (!read.Hidden, read.Rule) : null;
}
