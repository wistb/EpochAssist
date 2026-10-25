using EpochAssist.Core;
using S = EpochAssist.Features.AutoCastSettings;

namespace EpochAssist.Features;

internal static partial class AutoCast
{
    // The ungated part, each update; the count itself is the enemy scan job.
    private static void UpdateEnemyGate(Run run)
    {
        bool gated = S.RequireEnemies.Value;
        if (!gated) run.EnemyGateOpen = true;
        if (!gated && !S.AimAtClosestEnemy.Value) run.ClosestEnemy = null;
    }

    private static void ScanEnemyGate(Run run)
    {
        bool gated = S.RequireEnemies.Value;
        if (!gated && !S.AimAtClosestEnemy.Value) return;

        try
        {
            ScanEnemies(run);
        }
        catch (Exception e)
        {
            // If the scan breaks, fail open so autocast keeps working ungated.
            run.EnemiesInRange = 0;
            run.EnemyWeightInRange = 0f;
            run.ClosestEnemy = null;
            run.EnemyGateOpen = true;
            Log.ErrorThrottled("autocast:enemies", $"Autocast enemy scan failed: {e.Message}", 30);
            return;
        }

        run.EnemyGateOpen = !gated || run.EnemyWeightInRange >= S.EnemyMinWeight.Value;
    }

    // Alive, active enemies from the world model: the count and weight within the gate's range, and the closest within
    // the aim range. Distances are straight-line from the player, as the world model measures them this frame.
    private static void ScanEnemies(Run run)
    {
        int count = 0;
        float weight = 0f;
        WorldEntry? closest = null;

        if (S.RequireEnemies.Value)
        {
            var inRange = new WorldQuery { Kind = WorldKind.Enemy, Alive = true, Active = true, Range = S.EnemyRange.Value };
            count = World.Count(inRange);
            weight = World.Weight(inRange, s_enemyWeight);
        }
        if (S.AimAtClosestEnemy.Value) closest = World.Nearest(new WorldQuery { Kind = WorldKind.Enemy, Alive = true, Active = true, Range = S.AimRange.Value });

        run.EnemiesInRange = count;
        run.EnemyWeightInRange = weight;
        run.ClosestEnemy = closest;
    }

    private static readonly Func<Rarity, float> s_enemyWeight = EnemyWeight;

    private static float EnemyWeight(Rarity rarity) => rarity switch
    {
        Rarity.Boss => S.WeightBoss.Value,
        Rarity.Rare => S.WeightRare.Value,
        Rarity.Magic => S.WeightMagic.Value,
        _ => S.WeightNormal.Value
    };
}
