using System.Text.Json.Nodes;
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
        // The enemy gate (its switch, range and weight, the Shared Conditions entry's own), the range ring and what the
        // gate sees now; then the enemy weights every enemy weight condition uses.
        var enemies = new MenuPage("Enemies");
        enemies.Rows.Add(new HintRow(() => "Holds slot macros until the enemies in range add up to the weight to cast.") { Key = "gate.hint" });
        enemies.Rows.Add(new ValueToggle("Enemy gate", () => S.GateEntry() is { On: true }, SetGateOn) { Key = "gate.on" });
        enemies.Rows.Add(GateNumber("range", "Range", 15f, 2f, 60f, " m"));
        enemies.Rows.Add(GateNumber("weight", "Weight to cast", 1f, 1f, 100f, string.Empty));
        enemies.Rows.Add(new SettingRow(S.ShowRangeRing));
        enemies.Rows.Add(new LiveText("Now", GateReadout) { VisibleWhen = () => S.GateEntry() is { On: true } });
        enemies.Rows.Add(new HeadingRow("Enemy weights"));
        enemies.Rows.Add(new HintRow(() => "What each enemy in range adds, for the gate and every enemy weight condition.") { Key = "weights.hint" });
        enemies.Rows.AddRange(S.Gate.Settings.Where(s => s.Fold != null).OfType<Number>().Select(n => new SettingRow(n) { HelpAsTip = true }));
        entry.Pages.Add(keys);
        entry.Pages.Add(pausing);
        entry.Pages.Add(enemies);
        return entry;
    }

    // Through the gate's quick-toggle action, as the HUD and footer flip it.
    private static void SetGateOn(bool on)
    {
        if (S.GateEntry() is { } gate && gate.On != on) Actions.Run(SettingsRegistry.ToggleAction(S.Group, "EnemyGate"), "menu");
    }

    // One of the gate entry's numbers; shown while it is still an enemy weight condition (Shared Conditions can change
    // its kind), editable while it is on.
    private static ValueNumber GateNumber(string arg, string label, float fallback, float min, float max, string unit) =>
        new(label, () => S.GateEntry()?.Args[arg] is JsonValue v && v.TryGetValue(out double d) ? (float)d : fallback,
            value => S.GateConditions.Edit(S.EveryList, list =>
            {
                int i = list.IndexOf(S.GateEntryId);
                if (i >= 0) list.Entries[i].Args[arg] = value;
                return list;
            }, out _), min, max, 1f, unit)
        {
            Key = "gate." + arg,
            Slider = true,
            VisibleWhen = () => S.GateEntry() is { Kind: "enemies.weight" },
            EnabledWhen = () => S.GateEntry() is { On: true },
        };

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
