using EpochAssist.Features.PlannerFormats;

namespace EpochAssist.Features;

// Runtime only, never stored.
internal enum GuidanceState { Matches, Partial, ToPlace, NotInBuild }

[Flags]
internal enum TreeFlags
{
    None = 0,
    /// <summary>The character has another mastery: the build's mastery nodes are left out, and the character's mastery points are not in the build.</summary>
    DifferentMastery = 1,
    /// <summary>A specialised skill the stage doesn't plan.</summary>
    SkillNotInBuild = 2,
    /// <summary>A planned skill the character hasn't specialised.</summary>
    SkillMissing = 4,
    /// <summary>The planner gives no order for this tree, so it has no next steps.</summary>
    NoOrder = 8,
}

/// <summary>The character's trees and bar as read from the game, rebuilt in place on each change. Entries at 0 points
/// are left out, so a node with points is one the character has.</summary>
internal sealed class Allocation
{
    public int ClassId;
    /// <summary>The game's mastery number; 0 is none.</summary>
    public int Mastery;
    public int Level;
    public readonly Dictionary<byte, byte> Passive = new();
    /// <summary>Specialised skills by ability id.</summary>
    public readonly Dictionary<string, AllocatedSkill> Skills = new();
    /// <summary>Per bar slot, the ability's id, or null for an empty slot.</summary>
    public readonly string?[] Bar = new string?[Stage.BarSlots];
    public ulong Signature;
    /// <summary>The game's tree data is absent (a zone load): these are the last values read.</summary>
    public bool Stale;
}

internal sealed class AllocatedSkill
{
    public int Slot;
    public readonly Dictionary<byte, byte> Points = new();
}

/// <summary>One node's guidance: its state, the planned and allocated points, and its next step number (0 for none)
/// with the points that step adds.</summary>
internal readonly record struct NodeGuide(GuidanceState State, byte Planned, byte Allocated, int NextStep, byte NextAdd)
{
    /// <summary>Points past the plan.</summary>
    public byte Extra => Planned > 0 && Allocated > Planned ? (byte)(Allocated - Planned) : (byte)0;
}

/// <summary>One of the next steps: its number, the node and the points to add.</summary>
internal readonly record struct NextStep(int Number, byte Node, byte Add);

/// <summary>A checklist section: planned items done out of the total, and the ids still open by kind. Node ids for a
/// tree, slot indexes for the bar.</summary>
internal sealed class ChecklistSection
{
    public int Done;
    public int Total;
    public readonly List<int> Missing = new();
    public readonly List<int> Partial = new();
    public readonly List<int> OffPlan = new();
    /// <summary>Planned ids this game version doesn't have; skipped by the counts.</summary>
    public int Unknown;
}

internal sealed class TreeGuide
{
    public string TreeId = string.Empty;
    public TreeFlags Flags;
    public readonly Dictionary<byte, NodeGuide> Nodes = new();
    public readonly List<NextStep> Next = new();
    public readonly ChecklistSection Checklist = new();
}

internal sealed class SkillGuide
{
    public string AbilityId = string.Empty;
    public string Name = string.Empty;
    /// <summary>The planned and the specialised slot; -1 for none.</summary>
    public int PlannedSlot = -1;
    public int Slot = -1;
    public GuidanceState State;
    public readonly TreeGuide Tree = new();
}

/// <summary>A stage compared with the character: the passive tree, each planned or specialised skill, and the bar.</summary>
internal sealed class Guidance
{
    public readonly TreeGuide Passive = new();
    public readonly List<SkillGuide> Skills = new();
    /// <summary>Per bar slot; null where the stage plans nothing and the slot is empty.</summary>
    public readonly GuidanceState?[] Bar = new GuidanceState?[Stage.BarSlots];
    public readonly ChecklistSection BarChecklist = new();
    public bool DifferentMastery;
}

internal static partial class Planner
{
    // The running count per node while the order is walked; only used on the main thread.
    private static readonly Dictionary<byte, int> s_running = new();
    private static readonly Dictionary<byte, byte> s_noPoints = new();

    /// <summary>
    /// Compares a stage with the character. Per node: allocated equal to planned matches; fewer or more is partial
    ///; none of a planned node is to place; points in a node the stage doesn't use are not in the build.
    /// Ids this game version lacks are skipped. With another mastery chosen, the planned mastery nodes are left out and the points in the character's mastery are not in the build.
    /// <paramref name="requiredMastery"/> is each passive node's mastery (0 for the base class), or null when the game
    /// data couldn't be read.
    /// </summary>
    public static Guidance Compare(Stage stage, BuildResolution resolution, Allocation allocation, IReadOnlyDictionary<byte, byte>? requiredMastery, int nextCount)
    {
        var guidance = new Guidance { DifferentMastery = allocation.Mastery != 0 && stage.Mastery != 0 && allocation.Mastery != stage.Mastery };
        var unknown = new HashSet<(string, byte)>(resolution.UnknownNodes);
        var capped = new Dictionary<(string, byte), byte>();
        foreach (var (treeId, node, _, max) in resolution.CappedNodes) capped[(treeId, node)] = max;

        // Only mastery nodes are left out, and only with another mastery chosen.
        Func<byte, bool>? excluded = guidance.DifferentMastery && requiredMastery != null
            ? node => requiredMastery.TryGetValue(node, out byte m) && m != 0
            : null;
        CompareTree(guidance.Passive, stage.Passive.TreeId, stage.Passive, allocation.Passive, unknown, capped, excluded, nextCount);
        if (guidance.DifferentMastery) guidance.Passive.Flags |= TreeFlags.DifferentMastery;

        var planned = new HashSet<string>();
        foreach (var skill in stage.Skills)
        {
            if (skill.AbilityId.Length == 0 || resolution.UnknownSkills.Contains(skill.AbilityId)) continue;
            planned.Add(skill.AbilityId);
            var guide = new SkillGuide { AbilityId = skill.AbilityId, Name = skill.Name, PlannedSlot = skill.Slot };
            if (allocation.Skills.TryGetValue(skill.AbilityId, out var have))
            {
                guide.Slot = have.Slot;
                CompareTree(guide.Tree, skill.AbilityId, skill.Tree, have.Points, unknown, capped, null, nextCount);
                var list = guide.Tree.Checklist;
                guide.State = list.Done == list.Total && list.OffPlan.Count == 0 ? GuidanceState.Matches : GuidanceState.Partial;
            }
            else
            {
                CompareTree(guide.Tree, skill.AbilityId, skill.Tree, s_noPoints, unknown, capped, null, nextCount);
                guide.Tree.Flags |= TreeFlags.SkillMissing;
                guide.State = GuidanceState.ToPlace;
            }
            guidance.Skills.Add(guide);
        }
        foreach (var (abilityId, have) in allocation.Skills)
        {
            if (planned.Contains(abilityId)) continue;
            var guide = new SkillGuide { AbilityId = abilityId, Slot = have.Slot, State = GuidanceState.NotInBuild };
            CompareTree(guide.Tree, abilityId, null, have.Points, unknown, capped, null, 0);
            guide.Tree.Flags |= TreeFlags.SkillNotInBuild;
            guidance.Skills.Add(guide);
        }

        CompareBar(guidance, stage, allocation, resolution);
        return guidance;
    }

    private static void CompareTree(TreeGuide guide, string treeId, PlannedTree? plan, IReadOnlyDictionary<byte, byte> allocated,
        HashSet<(string, byte)> unknown, Dictionary<(string, byte), byte> capped, Func<byte, bool>? excluded, int nextCount)
    {
        guide.TreeId = treeId;
        var list = guide.Checklist;
        if (plan != null)
        {
            foreach (var (node, points) in plan.Points)
            {
                if (unknown.Contains((treeId, node)))
                {
                    list.Unknown++;
                    continue;
                }
                if (excluded?.Invoke(node) == true) continue;
                // A node planned past its maximum in this game version is guided to that maximum.
                byte p = capped.TryGetValue((treeId, node), out byte max) ? Math.Min(points, max) : points;
                byte a = allocated.TryGetValue(node, out byte have) ? have : (byte)0;
                var state = a == p ? GuidanceState.Matches : a == 0 ? GuidanceState.ToPlace : GuidanceState.Partial;
                guide.Nodes[node] = new NodeGuide(state, p, a, 0, 0);
                list.Total++;
                if (state == GuidanceState.Matches) list.Done++;
                else (state == GuidanceState.ToPlace ? list.Missing : list.Partial).Add(node);
            }
        }
        foreach (var (node, a) in allocated)
        {
            // Points in a left-out node are not in the build, planned there or not: another mastery's points show as such.
            if (a == 0 || guide.Nodes.ContainsKey(node) || (plan != null && plan.Points.ContainsKey(node) && excluded?.Invoke(node) != true)) continue;
            guide.Nodes[node] = new NodeGuide(GuidanceState.NotInBuild, 0, a, 0, 0);
            list.OffPlan.Add(node);
        }
        if (plan == null) return;
        if (plan.Order == null)
        {
            guide.Flags |= TreeFlags.NoOrder;
            return;
        }
        NextSteps(guide, treeId, plan.Order, allocated, unknown, excluded, nextCount);
    }

    // Walks the order with a running count per node; a step whose running total passes the allocated points is next,
    // with the points still to add. A node keeps the number of its first next step.
    private static void NextSteps(TreeGuide guide, string treeId, List<Step> order, IReadOnlyDictionary<byte, byte> allocated,
        HashSet<(string, byte)> unknown, Func<byte, bool>? excluded, int nextCount)
    {
        s_running.Clear();
        int number = 0;
        foreach (var step in order)
        {
            if (number >= nextCount) break;
            if (unknown.Contains((treeId, step.Node)) || excluded?.Invoke(step.Node) == true || !guide.Nodes.TryGetValue(step.Node, out var node)) continue;
            int before = s_running.TryGetValue(step.Node, out int r) ? r : 0;
            int after = Math.Min(before + step.Add, node.Planned);
            s_running[step.Node] = after;
            int a = allocated.TryGetValue(step.Node, out byte have) ? have : 0;
            if (after <= before || after <= a) continue;
            byte add = (byte)(after - Math.Max(before, a));
            number++;
            guide.Next.Add(new NextStep(number, step.Node, add));
            if (node.NextStep == 0) guide.Nodes[step.Node] = node with { NextStep = number, NextAdd = add };
        }
    }

    // A slot with the planned skill matches; an empty planned slot is to place; a skill the stage plans for another
    // slot is partial; a skill the stage doesn't plan on the bar is not in the build.
    private static void CompareBar(Guidance guidance, Stage stage, Allocation allocation, BuildResolution resolution)
    {
        var list = guidance.BarChecklist;
        for (int i = 0; i < Stage.BarSlots; i++)
        {
            string? planned = stage.Bar[i];
            if (planned != null && resolution.UnknownSkills.Contains(planned))
            {
                list.Unknown++;
                planned = null;
            }
            string? have = allocation.Bar[i];
            GuidanceState? state = null;
            if (planned != null)
            {
                list.Total++;
                state = have == planned ? GuidanceState.Matches : have == null ? GuidanceState.ToPlace
                    : Array.IndexOf(stage.Bar, have) >= 0 ? GuidanceState.Partial : GuidanceState.NotInBuild;
            }
            else if (have != null && Array.IndexOf(stage.Bar, have) < 0)
            {
                state = GuidanceState.NotInBuild;
            }
            guidance.Bar[i] = state;
            switch (state)
            {
                case GuidanceState.Matches: list.Done++; break;
                case GuidanceState.ToPlace: list.Missing.Add(i); break;
                case GuidanceState.Partial: list.Partial.Add(i); break;
                case GuidanceState.NotInBuild: list.OffPlan.Add(i); break;
            }
        }
    }
}
