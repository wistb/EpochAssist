using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Features;

internal static class AutoCastSettings
{
    public static readonly SettingsGroup Group = new("EpochAssist_AutoCast", "Auto-Cast");

    private static readonly Section Main = Group.Section("main", "Auto-Cast");
    public static readonly Toggle Enabled = new(Main, "Enabled", "Auto-cast", false) { QuickLabel = "Auto-cast" };

    public static readonly Section Controls = Group.Section("controls", "Controls");
    public static readonly Choice<KeyCode> ModifierKey = new(Controls, "ModifierKey", "Modifier", KeyCode.LeftControl,
        (KeyCode.LeftControl, "Left Ctrl"), (KeyCode.LeftAlt, "Left Alt"), (KeyCode.LeftShift, "Left Shift"))
    {
        Help = "Hold it and press a skill's key to toggle that slot.",
    };
    public static readonly Key HoldToPauseKey = new(Controls, "HoldToPauseKey", "Hold to pause", KeyCode.None)
    {
        Clearable = true, Help = "While held, auto-cast stops and lets go of any channel.",
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

    /// <summary>The sections shown as tabs on the Auto-Cast page.</summary>
    public static readonly Section[] Tabs = { Controls, Pausing, Targeting, Gate };

    public static void Register() => Group.Register();

    private static Number Weight(string id, string label, float defaultValue) => new(Gate, id, label, defaultValue, 0f, 50f)
    {
        Fold = "Enemy weights", VisibleWhen = () => RequireEnemies.Value,
    };
}
