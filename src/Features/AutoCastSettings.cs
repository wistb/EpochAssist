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

    public static void Init()
    {
        var c = MelonPreferences.CreateCategory("EpochAssist_AutoCast", "Epoch Assist - Auto-Cast");
        Enabled = c.CreateEntry("Enabled", false);
        ModifierKey = c.CreateEntry("ModifierKey", KeyCode.LeftControl);
        HoldToPauseKey = c.CreateEntry("HoldToPauseKey", KeyCode.None);
        PauseOnZoneChange = c.CreateEntry("PauseOnZoneChange", true);
        PauseInNonCombatZones = c.CreateEntry("PauseInNonCombatZones", false);
        AimAtClosestEnemy = c.CreateEntry("AimAtClosestEnemy", false);
        AimRange = c.CreateEntry("AimRange", 20f);
        RequireEnemies = c.CreateEntry("RequireEnemies", false);
        EnemyRange = c.CreateEntry("EnemyRange", 15f);
        ShowRangeRing = c.CreateEntry("ShowRangeRing", false);
        EnemyMinWeight = c.CreateEntry("EnemyMinWeight", 1f);
        WeightNormal = c.CreateEntry("WeightNormal", 1f);
        WeightMagic = c.CreateEntry("WeightMagic", 2f);
        WeightRare = c.CreateEntry("WeightRare", 5f);
        WeightBoss = c.CreateEntry("WeightBoss", 20f);
    }
}
