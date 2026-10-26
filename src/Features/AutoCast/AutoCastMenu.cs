using EpochAssist.Core;
using S = EpochAssist.Features.AutoCastSettings;

namespace EpochAssist.Features;

// The Macros feature's settings as one page of the Macros tab (opened by the library's Settings button, found by
// search): Keys, Pausing, Aiming and Enemies, each a card with its settings. Run macros and the debug
// switches are the library's footer. The menu draws it.
internal static class AutoCastMenu
{
    /// <summary>
    /// The settings page as cards: Keys beside Pausing, then Aiming and Enemies across the page.
    /// </summary>
    public static MenuPage SettingsPage()
    {
        var page = new MenuPage("Settings") { InTree = false, Resettable = false };
        page.Rows.Add(new CardColumns(
            new CardRow("Keys", "How slots are toggled, and the key that stops macros while held.",
                new SettingRow(S.ModifierKey), new SettingRow(S.HoldToPauseKey)),
            new CardRow("Pausing", "When macros stop by themselves until you press a skill.",
                new SettingRow(S.PauseOnZoneChange), new SettingRow(S.PauseInNonCombatZones))));
        page.Rows.Add(new CardRow("Aiming", "Where slot macros aim their casts.",
            new SettingRow(S.AimAtClosestEnemy), new SettingRow(S.AimRange)));
        // The enemy weights every enemy weight condition uses, the range ring and what the gate sees now.
        var weights = S.Gate.Settings.Where(s => s.Fold != null).OfType<Number>().ToList();
        page.Rows.Add(new CardRow("Enemies", "How much each enemy counts in enemy weight lines (the enemy gate's included).",
            new NumberGroup(weights, weights[0].Fold),
            new SettingRow(S.ShowRangeRing),
            new LiveText("Enemy gate", GateReadout) { VisibleWhen = () => S.GateEntry() is { On: true } }));
        return page;
    }

    internal static string SlotHint(int slot)
    {
        if (!Player.Exists) return $"Slot {slot + 1} · not in game";
        string skill = AutoCast.SlotAbilityName(slot) ?? "no skill";
        int form = AutoCast.CurrentFormKey;
        return $"Slot {slot + 1}: {skill} · {(form == -1 ? "human form" : $"form {form}")}";
    }

    internal static PageStatus SlotStatus(int slot)
    {
        bool toggled = Status.Slots?[slot] ?? false;
        if (!toggled) return new PageStatus(StatusState.Off, "off");
        return AutoCast.SlotWaiting(slot) ? new PageStatus(StatusState.Holding, "waiting") : new PageStatus(StatusState.Active, "casting");
    }

    // The dispatcher's status while autocast runs, otherwise off.
    internal static FeatureStatus Status => FeatureHost.Get<AutoCastFeature>()?.Status ?? FeatureStatus.Off(string.Empty);


    // Off when no entry is on, casting while the list passes, holding while it fails.
    internal static PageStatus EveryStatus()
    {
        if (!S.GateConditions.List(S.EveryList).Entries.Any(e => e.On)) return new PageStatus(StatusState.Off, "off");
        // Outside the game (character select) its last check isn't current.
        if (!GameState.InGame) return new PageStatus(StatusState.Waiting, Macros.NoLiveData);
        return AutoCast.EveryPasses ? new PageStatus(StatusState.Active, "casting") : new PageStatus(StatusState.Paused, "holding");
    }

    private static (string, StatusState?) GateReadout()
    {
        var gate = AutoCast.GateNow;
        if (!gate.Counted) return (AutoCast.Scanning ? "not counted yet" : "not counted (autocast off)", StatusState.Waiting);
        return ($"{gate.Count} enemies, weight {gate.Weight:F0} · {(gate.Open ? "casting" : "holding")}", gate.Open ? StatusState.Active : StatusState.Paused);
    }
}
