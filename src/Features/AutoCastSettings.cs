using EpochAssist.Core;
using MelonLoader;
using UnityEngine;

namespace EpochAssist.Features;

internal static class AutoCastSettings
{
    public static MelonPreferences_Entry<bool> Enabled = null!;
    public static MelonPreferences_Entry<KeyCode> ModifierKey = null!;
    public static MelonPreferences_Entry<KeyCode> HoldToPauseKey = null!;
    public static MelonPreferences_Entry<bool> PauseOnZoneChange = null!;
    public static MelonPreferences_Entry<bool> PauseInNonCombatZones = null!;
    public static MelonPreferences_Entry<bool> AimAtClosestEnemy = null!;
    public static MelonPreferences_Entry<float> AimRange = null!;
    public static MelonPreferences_Entry<bool> RequireEnemies = null!;
    public static MelonPreferences_Entry<float> EnemyRange = null!;
    public static MelonPreferences_Entry<bool> ShowRangeRing = null!;
    public static MelonPreferences_Entry<float> EnemyMinWeight = null!;
    public static MelonPreferences_Entry<float> WeightNormal = null!;
    public static MelonPreferences_Entry<float> WeightMagic = null!;
    public static MelonPreferences_Entry<float> WeightRare = null!;
    public static MelonPreferences_Entry<float> WeightBoss = null!;

    public static void Register()
    {
        var c = Config.Category("EpochAssist_AutoCast", "Epoch Assist - Auto-Cast");
        Enabled = Config.Entry(c, "Enabled", false);
        ModifierKey = Config.Entry(c, "ModifierKey", KeyCode.LeftControl);
        HoldToPauseKey = Config.Entry(c, "HoldToPauseKey", KeyCode.None);
        PauseOnZoneChange = Config.Entry(c, "PauseOnZoneChange", true);
        PauseInNonCombatZones = Config.Entry(c, "PauseInNonCombatZones", false);
        AimAtClosestEnemy = Config.Entry(c, "AimAtClosestEnemy", false);
        AimRange = Config.Entry(c, "AimRange", 20f);
        RequireEnemies = Config.Entry(c, "RequireEnemies", false);
        EnemyRange = Config.Entry(c, "EnemyRange", 15f);
        ShowRangeRing = Config.Entry(c, "ShowRangeRing", false);
        EnemyMinWeight = Config.Entry(c, "EnemyMinWeight", 1f);
        WeightNormal = Config.Entry(c, "WeightNormal", 1f);
        WeightMagic = Config.Entry(c, "WeightMagic", 2f);
        WeightRare = Config.Entry(c, "WeightRare", 5f);
        WeightBoss = Config.Entry(c, "WeightBoss", 20f);
    }
}
