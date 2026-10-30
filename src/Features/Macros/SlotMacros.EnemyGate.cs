using EpochAssist.Core;
using S = EpochAssist.Features.MacroSettings;

namespace EpochAssist.Features;

internal static partial class SlotMacros
{
    // The unscanned part, each update; the count itself is the enemy scan job.
    private static void UpdateEnemyGate(Run run)
    {
        if (!SharedNearest(out _)) run.ClosestEnemy = null;
    }

    // The enemy scan job (0.15 s): counts what switched-on enemy conditions need (the shared conditions, the toggled
    // slots' lists, the current class's enabled library macros, a running macro's, and the list on an open editor) in
    // one pass, and finds the closest enemy to aim at. With nothing in use and aiming off, nothing is counted, as with
    // the old gate off. The cursor point is read once, only when a count is around it.
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
        ConditionEditor.ShownEnemyRanges(S.NamedConditions, ranges);
        Macros.EnemyKeys(ranges);
        var subjects = run.Subjects;
        subjects.Clear();
        ConditionEditor.EnemySubjects(S.GateConditions.List(S.EveryList), subjects);
        for (int i = 0; i < SlotCount; i++)
        {
            if (toggles[i]) ConditionEditor.EnemySubjects(S.SlotConditions.List(SlotKey(run.FormKey, i)), subjects);
        }
        Macros.EnemySubjects(subjects);

        // A failed count leaves the tally failed: enemy conditions read unknown (the gate entry then passes, as the old
        // gate opened when its scan broke). The cursor point is read once for both.
        UnityEngine.Transform? hit = null;
        UnityEngine.Vector3? cursor = AnyCursor(ranges) || subjects.Count > 0 ? CursorPoint(out hit) : null;
        if (ranges.Count > 0) run.Tally.Refresh(ranges, s_enemyWeight, cursor);
        if (subjects.Count > 0) run.Tally.RefreshSubjects(subjects, cursor, hit);
        // The targets in use, picked from one pass over the enemies (its own error key: a failed pick never stops the
        // counts above).
        try
        {
            Targets.Begin();
            CollectTargets(run);
            Targets.Refresh();
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("macros:targets", $"Target refresh failed: {e.Message}", 30);
        }
        if (!SharedNearest(out float range)) return;
        try
        {
            run.ClosestEnemy = World.Nearest(new WorldQuery { Kind = WorldKind.Enemy, Alive = true, Active = true, Range = range });
        }
        catch (Exception e)
        {
            run.ClosestEnemy = null;
            Log.ErrorThrottled("macros:enemies", $"Macros enemy scan failed: {e.Message}", 30);
        }
    }

    // Whether the shared slot target is nearest within a range, else at cursor: the scan finds that enemy and the send re-checks it.
    private static bool SharedNearest(out float range)
    {
        var shared = S.SharedSlotTarget;
        range = shared.Range;
        return shared.Legacy && shared.Pick == TargetPick.Nearest;
    }

    private static readonly string[] s_slotOwners = { "slot 1", "slot 2", "slot 3", "slot 4", "slot 5" };

    // The sticky targets the owners use this scan: each toggled slot's (its own or the shared one), and the library
    // macros' cast steps and conditions.
    private static void CollectTargets(Run run)
    {
        bool named = Targets.WantOwners;
        bool[] toggles = CurrentToggles();
        var shared = S.SharedSlotTarget;
        for (int i = 0; i < SlotCount; i++)
        {
            if (!toggles[i]) continue;
            var target = S.EffectiveSlotTarget(run.FormKey, i);
            if (!target.Sticky || target.Invalid(TargetOwner.Slot) != null) continue;
            Targets.Collect(target.Key, named ? (ReferenceEquals(target, shared) ? $"shared ({s_slotOwners[i]})" : s_slotOwners[i]) : null);
        }
        // An open Shared Conditions or slot page shows its target's live pick.
        if (Macros.ShownSlotPage is int page)
        {
            var shown = page < 0 ? shared : S.EffectiveSlotTarget(run.FormKey, page);
            if (shown.Sticky && shown.Invalid(TargetOwner.Slot) == null) Targets.Collect(shown.Key, named ? "open page" : null);
        }
        // Lines about a target: the shared conditions (their cast target is the shared one) and each toggled slot's.
        ConditionEditor.EnemyTargets(S.GateConditions.List(S.EveryList), shared, named ? "shared conditions" : null);
        for (int i = 0; i < SlotCount; i++)
        {
            if (toggles[i]) ConditionEditor.EnemyTargets(S.SlotConditions.List(SlotKey(run.FormKey, i)), S.EffectiveSlotTarget(run.FormKey, i), named ? s_slotOwners[i] : null);
        }
        Macros.CollectTargets();
    }

    private static bool AnyCursor(List<TallyKey> keys)
    {
        for (int i = 0; i < keys.Count; i++)
        {
            if (keys[i].Cursor) return true;
        }
        return false;
    }

    /// <summary>The point under the cursor that skills aim at (the ground or the thing hit), or null without a player.</summary>
    internal static UnityEngine.Vector3? CursorPoint() => CursorPoint(out _);

    private static readonly Func<(UnityEngine.Vector3?, UnityEngine.Transform?)> s_cursorRead = () => (CursorPoint(out var hit), hit);

    internal static UnityEngine.Vector3? CursorPoint(out UnityEngine.Transform? hit)
    {
        hit = null;
        if (Player.Transform is not UnityEngine.Transform player) return null;
        try
        {
            return Il2Cpp.MouseManager.AbilityUseMousePoint(out hit, out _, false, player);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The enemy under the cursor (the one hovered read every owner uses), or null. Once per frame.</summary>
    internal static WorldEntry? HoveredEnemy() => Targets.Hovered();

    private static readonly Func<Rarity, float> s_enemyWeight = EnemyWeight;

    private static float EnemyWeight(Rarity rarity) => rarity switch
    {
        Rarity.Boss => S.WeightBoss.Value,
        Rarity.Rare => S.WeightRare.Value,
        Rarity.Magic => S.WeightMagic.Value,
        _ => S.WeightNormal.Value
    };

    // Whether the gate entry counted at the last check of the shared conditions (true before the first).
    private static bool GateCounts(Run run, ConditionEntry gate)
    {
        var tree = run.Verdicts.Every;
        if (tree.List == null) return true;
        int node = Array.IndexOf(tree.List.Nodes, gate);
        return node < 0 || node >= tree.Counted.Length || tree.Counted[node];
    }

    // The gate entry's index in the every-slot list, or null when the list has none.
    private static int? GateIndex(out ConditionEntry? gate)
    {
        var list = S.GateConditions.List(S.EveryList);
        int index = list.Invalid == null ? list.IndexOf(S.GateEntryId) : -1;
        gate = index >= 0 ? list.Entries[index] : null;
        return index >= 0 ? index : null;
    }

    /// <summary>The gate entry's numbers as of the last count: whether it is on, its range, the enemies and their weight in
    /// that range, and whether it lets the slot macros cast (for the Every slot page's readout and the dev gate verb).</summary>
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
            bool open = !gate.On || s_run is not { } r || GateCounts(r, gate);
            return (gate.On, range, count, weight, open, counted);
        }
    }
}
