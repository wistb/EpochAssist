using EpochAssist.Core;
using S = EpochAssist.Features.AutoCastSettings;

namespace EpochAssist.Features;

// Autocast's place in the menu, as data: the slot chips and toggle hint above every page, the slots in the tree, and a
// page per settings section. The menu draws it.
internal static class AutoCastMenu
{
    public static readonly MenuEntry Entry = new(MenuCategory.Combat, order: 10)
    {
        Switch = S.Enabled,
        ContentEnabled = () => S.Enabled.Value,
        Top =
        {
            new ChipStrip(Chips),
            new HintRow(ToggleHint),
        },
        Groups = { new TreeGroup("Slots", SlotItems) },
    };

    // A page per slot, opened from the "Slots" tree items: the slot's own conditions for the current form.
    private static readonly MenuPage[] s_slotPages = Enumerable.Range(0, AutoCast.Slots).Select(SlotPage).ToArray();

    static AutoCastMenu()
    {
        Entry.Pages.Add(new MenuPage(S.Controls));
        Entry.Pages.Add(new MenuPage(S.Pausing));
        Entry.Pages.Add(new MenuPage(S.Targeting));
        Entry.Pages.Add(EveryPage());
        // Last, so the node opens Controls first; they aren't listed in the tree, the slot items open them.
        Entry.Pages.AddRange(s_slotPages);
    }

    // The every-slot list (the enemy gate is its first entry, switched on its line and in the HUD), the enemy weights
    // every enemy weight condition uses, the range ring and what the gate sees now.
    private static MenuPage EveryPage()
    {
        var page = new MenuPage(S.Gate) { Status = EveryStatus };
        page.Rows.Add(new HintRow(() => "Checked for every slot before its own conditions."));
        page.Rows.Add(ConditionEditor.List(S.GateConditions, () => S.EveryList, () => AutoCast.ContextFor(-1), "Conditions", () => AutoCast.Scanning,
            "The enemy gate can be switched off, not removed."));
        var weights = S.Gate.Settings.Where(s => s.Fold != null).OfType<Number>().ToList();
        page.Rows.Add(new HeadingRow(weights[0].Fold!));
        page.Rows.Add(new NumberGroup(weights, weights[0].Fold));
        page.Rows.Add(new SettingRow(S.ShowRangeRing));
        page.Rows.Add(new HeadingRow("Now") { VisibleWhen = () => S.GateEntry() is { On: true } });
        page.Rows.Add(new LiveText("Enemy gate", GateReadout) { VisibleWhen = () => S.GateEntry() is { On: true } });
        return page;
    }

    private static MenuPage SlotPage(int slot) => new($"Slot {slot + 1}")
    {
        InTree = false,
        Status = () => SlotStatus(slot),
        Resettable = false,
        Rows =
        {
            new HintRow(() => SlotHint(slot)),
            ConditionEditor.List(S.SlotConditions, () => AutoCast.SlotKey(AutoCast.CurrentFormKey, slot), () => AutoCast.ContextFor(slot),
                "Conditions", () => AutoCast.Scanning),
            new HintRow(() => "All conditions that are on must pass for the slot to cast. Every slot's conditions come first.") { Key = "hint.all" },
        },
    };

    private static string SlotHint(int slot)
    {
        if (!Player.Exists) return $"Slot {slot + 1} · not in game";
        string skill = AutoCast.SlotAbilityName(slot) ?? "no skill";
        int form = AutoCast.CurrentFormKey;
        return $"Slot {slot + 1}: {skill} · {(form == -1 ? "human form" : $"form {form}")}";
    }

    private static PageStatus SlotStatus(int slot)
    {
        bool toggled = Status.Slots?[slot] ?? false;
        if (!toggled) return new PageStatus(StatusState.Off, "off");
        return AutoCast.SlotWaiting(slot) ? new PageStatus(StatusState.Holding, "waiting") : new PageStatus(StatusState.Active, "casting");
    }

    // The dispatcher's status while autocast runs, otherwise off.
    private static FeatureStatus Status => FeatureHost.Get<AutoCastFeature>()?.Status ?? FeatureStatus.Off(string.Empty);

    private static IReadOnlyList<Chip> Chips()
    {
        FeatureStatus status = Status;
        var chips = new Chip[AutoCast.Slots];
        for (int i = 0; i < chips.Length; i++)
        {
            bool toggled = status.Slots?[i] ?? false;
            // A slot its own conditions hold back looks like one the old gate held.
            ChipState state = !toggled ? ChipState.Off : status.State == StatusState.Active && !AutoCast.SlotWaiting(i) ? ChipState.On : ChipState.Paused;
            chips[i] = new Chip((i + 1).ToString(), AutoCast.SlotAbilityName(i) ?? "-", state);
        }
        return chips;
    }

    // Why autocast waits or holds is in the page's status tag.
    private static string ToggleHint() => $"Hold {AutoCast.ModifierKeyName} and press a skill's key to toggle its slot.";

    private static IReadOnlyList<TreeItem> SlotItems()
    {
        FeatureStatus status = Status;
        var items = new TreeItem[AutoCast.Slots];
        for (int i = 0; i < items.Length; i++)
        {
            bool toggled = status.Slots?[i] ?? false;
            StatusState state = !toggled ? StatusState.Off : AutoCast.SlotWaiting(i) ? StatusState.Holding : StatusState.Active;
            items[i] = new TreeItem((i + 1).ToString(), AutoCast.SlotAbilityName(i) ?? "-", state, s_slotPages[i]);
        }
        return items;
    }

    // Off when no entry is on, casting while the list passes, holding while it fails.
    private static PageStatus EveryStatus()
    {
        if (!S.GateConditions.List(S.EveryList).Entries.Any(e => e.On)) return new PageStatus(StatusState.Off, "off");
        return AutoCast.EveryPasses ? new PageStatus(StatusState.Active, "casting") : new PageStatus(StatusState.Paused, "holding");
    }

    private static (string, StatusState?) GateReadout()
    {
        var gate = AutoCast.GateNow;
        if (!gate.Counted) return (AutoCast.Scanning ? "not counted yet" : "not counted (autocast off)", StatusState.Waiting);
        return ($"{gate.Count} enemies, weight {gate.Weight:F0} · {(gate.Open ? "casting" : "holding")}", gate.Open ? StatusState.Active : StatusState.Paused);
    }
}
