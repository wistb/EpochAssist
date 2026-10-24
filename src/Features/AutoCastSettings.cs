using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Features;

internal static class AutoCastSettings
{
    // Version 2: HoldToPauseKey is a Hotkey (chord text) instead of a KeyCode.
    public static readonly SettingsGroup Group = new("EpochAssist_AutoCast", "Autocast", version: 2) { ActionKey = "autocast" };

    private static readonly Section Main = Group.Section("main", "Autocast");
    public static readonly Toggle Enabled = new(Main, "Enabled", "Autocast", false) { QuickLabel = "Autocast" };

    public static readonly Section Controls = Group.Section("controls", "Controls");
    public static readonly Choice<KeyCode> ModifierKey = new(Controls, "ModifierKey", "Modifier", KeyCode.LeftControl,
        (KeyCode.LeftControl, "Left Ctrl"), (KeyCode.LeftAlt, "Left Alt"), (KeyCode.LeftShift, "Left Shift"),
        (KeyCode.RightControl, "Right Ctrl"), (KeyCode.RightAlt, "Right Alt"), (KeyCode.RightShift, "Right Shift"))
    {
        Help = "Hold it and press a skill's key to toggle that slot.",
    };
    public static readonly Hotkey HoldToPauseKey = new(Controls, "HoldToPauseKey", "Hold to pause", string.Empty, HotkeyKind.Hold, HotkeyScope.Gameplay)
    {
        Clearable = true, Help = "While held, autocast stops and lets go of any channel.",
    };

    public static readonly Section Pausing = Group.Section("pausing", "Pausing");
    public static readonly Toggle PauseOnZoneChange = new(Pausing, "PauseOnZoneChange", "Pause on zone change", true)
    {
        Help = "Press any skill yourself to resume.",
    };
    public static readonly Toggle PauseInNonCombatZones = new(Pausing, "PauseInNonCombatZones", "Pause in non-combat zones", false);

    public static readonly Section Targeting = Group.Section("targeting", "Targeting");
    public static readonly Toggle AimAtClosestEnemy = new(Targeting, "AimAtClosestEnemy", "Aim at closest enemy", false)
    {
        QuickLabel = "Aim at closest enemy", Help = "Uses the cursor when nothing is in range.",
    };
    public static readonly Number AimRange = new(Targeting, "AimRange", "Aim range", 20f, 2f, 60f)
    {
        Unit = " m", VisibleWhen = () => AimAtClosestEnemy.Value,
    };

    public static readonly Section Gate = Group.Section("gate", "Enemy gate");
    public static readonly Toggle RequireEnemies = new(Gate, "RequireEnemies", "Only cast with enemies in range", false)
    {
        QuickLabel = "Enemy gate",
    };
    public static readonly Number EnemyRange = new(Gate, "EnemyRange", "Range", 15f, 2f, 60f)
    {
        Unit = " m", VisibleWhen = () => RequireEnemies.Value,
    };
    public static readonly Number EnemyMinWeight = new(Gate, "EnemyMinWeight", "Minimum weight", 1f, 1f, 50f)
    {
        VisibleWhen = () => RequireEnemies.Value, Help = "Total weight of enemies in range needed to cast.",
    };
    public static readonly Toggle ShowRangeRing = new(Gate, "ShowRangeRing", "Show range ring", false)
    {
        QuickLabel = "Show range ring", VisibleWhen = () => RequireEnemies.Value,
    };
    public static readonly Number WeightNormal = Weight("WeightNormal", "Normal", 1f);
    public static readonly Number WeightMagic = Weight("WeightMagic", "Magic", 2f);
    public static readonly Number WeightRare = Weight("WeightRare", "Rare", 5f);
    public static readonly Number WeightBoss = Weight("WeightBoss", "Boss", 20f);

    /// <summary>The sections shown as cards on the Autocast page, in order.</summary>
    public static readonly Section[] PageSections = { Controls, Pausing, Targeting, Gate };

    public static void Register()
    {
        // Version 1 stored KeyCode names, with "None" for unset; any other name is already a one-key chord.
        Group.Migration(2, _ =>
        {
            if (HoldToPauseKey.Value.Equals("None", StringComparison.OrdinalIgnoreCase)) HoldToPauseKey.Value = string.Empty;
            Log.Info($"Autocast HoldToPauseKey: '{HoldToPauseKey.Value}'");
        });
        Group.Register();
    }

    private static Number Weight(string id, string label, float defaultValue) => new(Gate, id, label, defaultValue, 0f, 50f)
    {
        Fold = "Enemy weights", VisibleWhen = () => RequireEnemies.Value,
    };
}
