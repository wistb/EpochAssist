using EpochAssist.Core;
using S = EpochAssist.Features.MacroSettings;

namespace EpochAssist.Features;

// The Macros feature's settings in the Config tree (opened by the library's Settings button, found by search): Keys,
// Pausing and Enemies, a sub-tab each (the slot macros' shared target is on the Shared Conditions page). Run macros
// and the debug switches are the library's footer. The menu draws it.
internal static class MacroSettingsPage
{
    /// <summary>The settings in the Config tree, first in it: Keys, Pausing and Enemies, a sub-tab each.</summary>
    public static readonly MenuEntry ConfigEntry = ConfigSettings();

    private static MenuEntry ConfigSettings()
    {
        // No status row: the page title's tag already shows the state and why.
        var entry = new MenuEntry(MenuCategory.Combat, order: 20) { Title = "Macros" };
        var keys = new MenuPage("Keys");
        keys.Rows.Add(new SettingRow(S.ModifierKey));
        keys.Rows.Add(new SettingRow(S.HoldToPauseKey));
        var pausing = new MenuPage("Pausing");
        pausing.Rows.Add(new SettingRow(S.PauseOnZoneChange));
        pausing.Rows.Add(new SettingRow(S.PauseInNonCombatZones));
        // The enemy weights every enemy weight condition uses, the range ring and what the gate sees now.
        var enemies = new MenuPage("Enemies");
        var weights = S.Gate.Settings.Where(s => s.Fold != null).OfType<Number>().ToList();
        enemies.Rows.Add(new NumberGroup(weights, weights[0].Fold));
        enemies.Rows.Add(new SettingRow(S.ShowRangeRing));
        enemies.Rows.Add(new LiveText("Enemy gate", GateReadout) { VisibleWhen = () => S.GateEntry() is { On: true } });
        entry.Pages.Add(keys);
        entry.Pages.Add(pausing);
        entry.Pages.Add(enemies);
        return entry;
    }

    /// <summary>
    /// The shared slot target's rows (the Shared Conditions page), in the cast step's rows and words; its first change
    /// saves it (until then slot macros aim at the cursor). Changes go through the Macros tab's undo.
    /// </summary>
    internal static IEnumerable<MenuRow> SharedTargetRows() => TargetRows.Rows(new TargetRows.Access
    {
        Prefix = "sharedtarget.",
        Owner = TargetOwner.Slot,
        Get = () => S.SharedSlotTarget,
        Set = target =>
        {
            Macros.History.Watch();
            if (!S.SlotTargets.SetShared(target ?? TargetSpec.Cursor, out string reason)) Log.WarnThrottled("aiming:edit", $"Slot target not changed: {reason}");
        },
        StartRange = () => S.StartRange,
        Blocked = () => S.SlotTargets.Doc.ReadOnly ? "made by a newer version" : null,
    });

    internal static string SlotHint(int slot)
    {
        if (!Player.Exists) return $"Slot {slot + 1} · not in game";
        string skill = SlotMacros.SlotAbilityName(slot) ?? "no skill";
        int form = SlotMacros.CurrentFormKey;
        return $"Slot {slot + 1}: {skill} · {(form == -1 ? "human form" : $"form {form}")}";
    }

    internal static PageStatus SlotStatus(int slot)
    {
        bool toggled = Status.Slots?[slot] ?? false;
        if (!toggled) return new PageStatus(StatusState.Off, "off");
        if (SlotMacros.SlotNoTarget(slot)) return new PageStatus(StatusState.Holding, "no target");
        return SlotMacros.SlotWaiting(slot) ? new PageStatus(StatusState.Holding, "waiting") : new PageStatus(StatusState.Active, "casting");
    }

    // The dispatcher's status while the slot macros run, otherwise off.
    internal static FeatureStatus Status => FeatureHost.Get<MacrosFeature>()?.Status ?? FeatureStatus.Off(string.Empty);


    // Off when no entry is on, casting while the list passes, holding while it fails.
    internal static PageStatus EveryStatus()
    {
        if (!S.GateConditions.List(S.EveryList).Entries.Any(e => e.On)) return new PageStatus(StatusState.Off, "off");
        // Outside the game (character select) its last check isn't current.
        if (!GameState.InGame) return new PageStatus(StatusState.Waiting, Macros.NoLiveData);
        return SlotMacros.EveryPasses ? new PageStatus(StatusState.Active, "casting") : new PageStatus(StatusState.Paused, "holding");
    }

    private static (string, StatusState?) GateReadout()
    {
        var gate = SlotMacros.GateNow;
        if (!gate.Counted) return (SlotMacros.Scanning ? "not counted yet" : "not counted (macros off)", StatusState.Waiting);
        return ($"{gate.Count} enemies, weight {gate.Weight:F0} · {(gate.Open ? "casting" : "holding")}", gate.Open ? StatusState.Active : StatusState.Paused);
    }
}
