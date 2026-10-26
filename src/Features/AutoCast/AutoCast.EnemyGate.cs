using EpochAssist.Core;
using S = EpochAssist.Features.AutoCastSettings;

namespace EpochAssist.Features;

internal static partial class AutoCast
{
    // The unscanned part, each update; the count itself is the enemy scan job.
    private static void UpdateEnemyGate(Run run)
    {
        if (!S.AimAtClosestEnemy.Value) run.ClosestEnemy = null;
    }

    // The enemy scan job (0.15 s): counts the ranges that switched-on enemy conditions use (the every-slot list, the
    // toggled slots' lists, and the list on an open editor page) in one pass, and finds the closest enemy to aim at.
    // With no range in use and aiming off, nothing is counted, as with the old gate off.
    private static void ScanEnemyGate(Run run)
    {
        var ranges = run.Ranges;
        ranges.Clear();
        ConditionEditor.EnemyRanges(S.GateConditions.List(S.EveryList), ranges);
        bool[] toggles = CurrentToggles();
        for (int i = 0; i < SlotCount; i++)
        {
            if (toggles[i]) ConditionEditor.EnemyRanges(S.SlotConditions.List(SlotKey(run.FormKey, i)), ranges);
        }
        ConditionEditor.ShownEnemyRanges(S.GateConditions, ranges);
        ConditionEditor.ShownEnemyRanges(S.SlotConditions, ranges);

        // A failed count leaves the tally failed: enemy conditions read unknown (the gate entry then passes, as the old
        // gate opened when its scan broke).
        if (ranges.Count > 0) run.Tally.Refresh(ranges, s_enemyWeight);
        if (!S.AimAtClosestEnemy.Value) return;
        try
        {
            run.ClosestEnemy = World.Nearest(new WorldQuery { Kind = WorldKind.Enemy, Alive = true, Active = true, Range = S.AimRange.Value });
        }
        catch (Exception e)
        {
            run.ClosestEnemy = null;
            Log.ErrorThrottled("autocast:enemies", $"Autocast enemy scan failed: {e.Message}", 30);
        }
    }

    private static readonly Func<Rarity, float> s_enemyWeight = EnemyWeight;

    private static float EnemyWeight(Rarity rarity) => rarity switch
    {
        Rarity.Boss => S.WeightBoss.Value,
        Rarity.Rare => S.WeightRare.Value,
        Rarity.Magic => S.WeightMagic.Value,
        _ => S.WeightNormal.Value
    };

    // The gate entry's index in the every-slot list, or null when the list has none.
    private static int? GateIndex(out ConditionEntry? gate)
    {
        var list = S.GateConditions.List(S.EveryList);
        int index = list.Invalid == null ? list.IndexOf(S.GateEntryId) : -1;
        gate = index >= 0 ? list.Entries[index] : null;
        return index >= 0 ? index : null;
    }

    /// <summary>The gate entry's numbers as of the last count: whether it is on, its range, the enemies and their weight in
    /// that range, and whether it lets autocast cast (for the Every slot page's readout and the dev gate verb).</summary>
    public static (bool On, float Range, int Count, float Weight, bool Open, bool Counted) GateNow
    {
        get
        {
            if (GateIndex(out var gate) is not int index || gate!.Invalid != null) return (false, 0f, 0, 0f, true, false);
            float range = gate.Values.Number("range");
            bool counted = s_run is { } run && run.Tally.TryGet(range, out _, out _, out _) && run.Tally.Failed == null;
            int count = 0;
            float weight = 0f;
            if (counted) s_run!.Tally.TryGet(range, out count, out weight, out _);
            bool open = !gate.On || s_run is not { } r || index >= r.Verdicts.Every.Length || Conditions.Counts(gate, r.Verdicts.Every[index]);
            return (gate.On, range, count, weight, open, counted);
        }
    }
}
