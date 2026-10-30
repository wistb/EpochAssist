using System.Diagnostics.CodeAnalysis;

namespace EpochAssist.Core;

/// <summary>A list's verdict: whether it passes, and the first switched-on entry that made it fail (-1 when none).</summary>
internal readonly record struct ListVerdict(bool Passed, int FirstFailing)
{
    public static readonly ListVerdict Pass = new(true, -1);
}

/// <summary>A tree's verdict: whether it passes, and the node that made it fail (-1 when none): for an "all" its first
/// failing item (followed into a failing group), for an "any" its first switched-on item (followed), for a "none" the
/// first item that passes.</summary>
internal readonly record struct TreeVerdict(bool Passed, int FirstFailing)
{
    public static readonly TreeVerdict Pass = new(true, -1);
}

/// <summary>
/// A caller's state for checking one tree: each node's last result and whether it counted, and since when each
/// condition has counted (for "for"). Bound to one list; binding another (a new stored text) resets it. Dropped with its
/// owner (a run, the feature's run holder). Sized once per bound list, so checks allocate nothing.
/// </summary>
internal sealed class TreeState
{
    public ConditionList? List { get; private set; }
    public ConditionResult[] Results = Array.Empty<ConditionResult>();
    /// <summary>Whether each node counted as passing at the last check (after "for").</summary>
    public bool[] Counted = Array.Empty<bool>();
    /// <summary>When each condition began to pass without a break, before "for" (NaN while it doesn't).</summary>
    public float[] PassingSince = Array.Empty<float>();
    public TreeVerdict Verdict { get; internal set; } = TreeVerdict.Pass;
    /// <summary>The owner's note of the frame it was last checked (an editor checks once a frame).</summary>
    public int Frame = -1;

    /// <summary>Binds the state to a list; a different list resets results and timers.</summary>
    public void Bind(ConditionList list)
    {
        if (ReferenceEquals(list, List)) return;
        List = list;
        int n = Math.Max(1, list.NodeCount);
        if (Results.Length < n)
        {
            Results = new ConditionResult[n];
            Counted = new bool[n];
            PassingSince = new float[n];
        }
        Array.Clear(Results);
        Array.Clear(Counted);
        Array.Fill(PassingSince, float.NaN);
        Verdict = TreeVerdict.Pass;
    }

    /// <summary>Forgets the bound list (results and timers start again on the next check).</summary>
    public void Reset() => List = null;
}

/// <summary>
/// Checks condition lists. Every switched-on entry is checked (no short cut, so readouts and the dev verbs show every
/// value); pass counts as pass, fail and invalid as fail, unknown by the entry's "true when unresolvable" switch. A kind
/// that throws answers unknown with its message and never takes the caller down.
/// </summary>
internal static class Conditions
{
    /// <summary>Checks a list's root items into results (one per root item, caller-owned and big enough; switched-off
    /// items are left as they were) and gives its verdict. Groups are checked through their items; "for" isn't timed
    /// here (it needs a TreeState). An invalid list fails. Allocates nothing.</summary>
    public static ListVerdict Check(ConditionList list, ConditionContext ctx, ConditionResult[] results)
    {
        if (list.Invalid != null) return new ListVerdict(false, -1);
        int first = -1;
        var nodes = list.Nodes;
        int item = 0;
        for (int i = 0; i < nodes.Length; i = list.End(i), item++)
        {
            var entry = nodes[i];
            if (!entry.On || item >= results.Length) continue;
            bool counts = CheckNode(list, i, ctx, null, out var result);
            results[item] = result;
            if (!counts && first < 0) first = item;
        }
        return new ListVerdict(first < 0, first);
    }

    /// <summary>Checks a whole tree into the state (bound to the list first) and gives its verdict; "for" timers run on
    /// ctx.Now. Every switched-on node is checked. An invalid list fails. Allocates nothing.</summary>
    public static TreeVerdict Check(ConditionList list, ConditionContext ctx, TreeState state)
    {
        state.Bind(list);
        if (list.Invalid != null) return state.Verdict = new TreeVerdict(false, -1);
        var nodes = list.Nodes;
        bool passed = true;
        int first = -1;
        for (int i = 0; i < nodes.Length; i = list.End(i))
        {
            if (!nodes[i].On) continue;
            if (CheckNode(list, i, ctx, state, out _) || !passed) continue;
            passed = false;
            first = Culprit(list, i, state);
        }
        return state.Verdict = new TreeVerdict(passed, first);
    }

    // One node and, for a group, everything below it; true when it counts. With a state, every node's result and count
    // are kept and "for" is timed.
    private static bool CheckNode(ConditionList list, int node, ConditionContext ctx, TreeState? state, out ConditionResult result)
    {
        var entry = list.Nodes[node];
        bool counts;
        if (entry.Group is not { } group || entry.Invalid != null)
        {
            result = Check(entry, ctx);
            counts = Counts(entry, result);
            if (state != null)
            {
                // Since when it has passed without a break (what set a macro off); with "for" it counts only once it has
                // passed for the whole time, and any break starts the time again.
                if (!counts) state.PassingSince[node] = float.NaN;
                else if (float.IsNaN(state.PassingSince[node])) state.PassingSince[node] = ctx.Now;
                if (entry.For > 0f) counts = counts && ctx.Now - state.PassingSince[node] >= entry.For - 1e-4f;
            }
        }
        else
        {
            int on = 0, passing = 0;
            for (int i = node + 1; i < list.End(node); i = list.End(i))
            {
                if (!list.Nodes[i].On) continue;
                on++;
                if (CheckNode(list, i, ctx, state, out _)) passing++;
            }
            counts = group.Type switch
            {
                "any" => on == 0 || passing > 0,
                "none" => passing == 0,
                _ => passing == on,
            };
            result = ConditionResult.Of(counts, passing, GroupNote(on));
        }
        if (state != null)
        {
            state.Results[node] = result;
            state.Counted[node] = counts;
        }
        return counts;
    }

    // The node to name when a node fails: a condition itself; in a failing "all" its first failing item, in a failing
    // "any" its first switched-on item (followed down), in a failing "none" its first item that passes.
    private static int Culprit(ConditionList list, int node, TreeState state)
    {
        var entry = list.Nodes[node];
        if (entry.Group is not { } group || entry.Invalid != null) return node;
        for (int i = node + 1; i < list.End(node); i = list.End(i))
        {
            if (!list.Nodes[i].On) continue;
            bool counted = state.Counted[i];
            switch (group.Type)
            {
                case "none" when counted:
                    return i;
                case "any":
                case "all" when !counted:
                    return Culprit(list, i, state);
            }
        }
        return node;
    }

    private static readonly string[] s_groupNotes = Enumerable.Range(0, ConditionList.Limit + 1).Select(n => $"of {n}").ToArray();

    // "of 3": a group's switched-on items, beside the passing count; made once per count.
    private static string GroupNote(int on) => on < s_groupNotes.Length ? s_groupNotes[on] : $"of {on}";

    /// <summary>One entry: invalid when it can't be checked here, else its kind's answer.</summary>
    public static ConditionResult Check(ConditionEntry entry, ConditionContext ctx)
    {
        if (entry.Invalid is { } invalid || entry.KindDef is not { } kind) return ConditionResult.Invalid(entry.Invalid ?? "needs a newer version");
        try
        {
            return kind.Check(ctx, entry.Values);
        }
        catch (Exception e)
        {
            Log.ErrorThrottled($"conditions:{kind.Id}", $"Condition {kind.Id} failed: {e.Message}", 30);
            return ConditionResult.Unknown(e.Message);
        }
    }

    /// <summary>Whether a result lets the entry pass.</summary>
    public static bool Counts(ConditionEntry entry, ConditionResult result) => result.State switch
    {
        ConditionState.Pass => true,
        ConditionState.Unknown => entry.TrueWhenUnknown,
        _ => false,
    };

    /// <summary>"pass", "fail", "unknown", "invalid": for readouts and the dev verbs.</summary>
    public static string StateWord(ConditionState state) => state switch
    {
        ConditionState.Pass => "pass",
        ConditionState.Fail => "fail",
        ConditionState.Unknown => "unknown",
        _ => "invalid",
    };

    /// <summary>The first failing line of a tree, as words with its value: "health below 50% (78%)"; a named condition's
    /// line names its own first failing line ("Worth fighting false: weight 3 within 12 m (weight 3)").</summary>
    public static string FailingWords(ConditionList list, TreeState state)
    {
        int node = state.Verdict.FirstFailing;
        if (node < 0 || node >= list.NodeCount) return list.Invalid ?? "conditions";
        var entry = list.Nodes[node];
        var result = node < state.Results.Length ? state.Results[node] : default;
        if (entry.Invalid == null && entry.KindDef is { FailTextExplains: true } && result.State is ConditionState.Pass or ConditionState.Fail) return entry.FailText;
        string value = node < state.Results.Length ? ValueText(entry, result) : string.Empty;
        return value.Length > 0 ? $"{entry.Words} ({value})" : entry.Words;
    }

    /// <summary>A result as a readout shows it: the value, or why it's unknown or invalid.</summary>
    public static string ValueText(ConditionEntry entry, ConditionResult result) => entry.IsGroup && result.State != ConditionState.Invalid
        ? $"{ArgSpec.Format(result.Number)} {result.Note} pass"
        : result.State switch
    {
        ConditionState.Invalid => result.Reason ?? "invalid",
        ConditionState.Unknown => result.Reason ?? "unknown",
        _ => entry.KindDef?.ShowValue(result) ?? string.Empty,
    };
}

/// <summary>
/// The condition lists features expose by name, for the dev cond and why verbs: a setting (with a short name for its
/// whole document, "gate"), the list names it offers, how a name maps to a key, and the context a check of that list
/// uses. The slot macros register "every" and "1"–"5"; a name it doesn't list may still resolve ("-1/2").
/// </summary>
internal static class ConditionTargets
{
    public sealed record Target(string Owner, string DocName, ConditionSetting Setting, Func<IEnumerable<string>> Names,
        Func<string, string?> KeyFor, Func<string, ConditionContext> ContextFor);

    private static readonly List<Target> s_targets = new();

    public static IReadOnlyList<Target> All => s_targets;

    /// <summary>Registers a setting's lists; the owner removes them when it stops.</summary>
    public static void Register(Target target)
    {
        s_targets.RemoveAll(t => t.Owner == target.Owner && t.DocName == target.DocName);
        s_targets.Add(target);
    }

    public static void Remove(string owner) => s_targets.RemoveAll(t => t.Owner == owner);

    /// <summary>The target and list key a list name resolves to.</summary>
    public static bool TryResolve(string name, [NotNullWhen(true)] out Target? target, [NotNullWhen(true)] out string? key)
    {
        foreach (var t in s_targets)
        {
            if (t.KeyFor(name) is { } found)
            {
                target = t;
                key = found;
                return true;
            }
        }
        target = null;
        key = null;
        return false;
    }

    public static Target? ByDocName(string name) => s_targets.FirstOrDefault(t => t.DocName.Equals(name, StringComparison.OrdinalIgnoreCase));
}
