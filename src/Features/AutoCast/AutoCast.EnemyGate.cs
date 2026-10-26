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

    private const float HoverFallbackMetres = 2f;
    private static int s_hoverFrame = -1;
    private static WorldEntry? s_hovered;

    /// <summary>
    /// The enemy under the cursor: the world entry whose object the cursor ray hits (or a parent of what it hits),
    /// else the nearest enemy within 2 m of the cursor point (GF-79's fallback); null for none. Once per frame.
    /// </summary>
    internal static WorldEntry? HoveredEnemy()
    {
        int frame = UnityEngine.Time.frameCount;
        if (frame == s_hoverFrame) return s_hovered;
        s_hoverFrame = frame;
        s_hovered = null;
        if (CursorPoint(out var hit) is not UnityEngine.Vector3 point) return null;
        var entries = World.Entries;
        WorldEntry? near = null;
        float nearest = HoverFallbackMetres;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Kind != WorldKind.Enemy || entry.Gone || entry.Dead || !entry.Active) continue;
            if (hit != null && IsOrParent(entry.Transform, hit))
            {
                s_hovered = entry;
                return entry;
            }
            float d = UnityEngine.Vector3.Distance(entry.Position, point);
            if (d <= nearest)
            {
                nearest = d;
                near = entry;
            }
        }
        return s_hovered = near;
    }

    // Whether the entry's transform is the hit one or one of its parents (colliders sit on child objects).
    private static bool IsOrParent(UnityEngine.Transform? entry, UnityEngine.Transform hit)
    {
        if (!Game.Alive(entry)) return false;
        var t = hit;
        for (int depth = 0; depth < 6 && Game.Alive(t); depth++, t = t!.parent)
        {
            if (t!.Pointer == entry!.Pointer) return true;
        }
        return false;
    }

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
            bool open = !gate.On || s_run is not { } r || GateCounts(r, gate);
            return (gate.On, range, count, weight, open, counted);
        }
    }
}
