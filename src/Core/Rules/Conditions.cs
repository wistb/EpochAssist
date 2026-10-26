using System.Diagnostics.CodeAnalysis;

namespace EpochAssist.Core;

/// <summary>A list's verdict: whether it passes, and the first switched-on entry that made it fail (-1 when none).</summary>
internal readonly record struct ListVerdict(bool Passed, int FirstFailing)
{
    public static readonly ListVerdict Pass = new(true, -1);
}

/// <summary>
/// Checks condition lists. Every switched-on entry is checked (no short cut, so readouts and the dev verbs show every
/// value); pass counts as pass, fail and invalid as fail, unknown by the entry's "true when unresolvable" switch. A kind
/// that throws answers unknown with its message and never takes the caller down.
/// </summary>
internal static class Conditions
{
    /// <summary>Checks a list into results (one per entry, caller-owned and big enough; switched-off entries are left as
    /// they were) and gives its verdict. An invalid list fails. Allocates nothing.</summary>
    public static ListVerdict Check(ConditionList list, ConditionContext ctx, ConditionResult[] results)
    {
        if (list.Invalid != null) return new ListVerdict(false, -1);
        int first = -1;
        var entries = list.Entries;
        for (int i = 0; i < entries.Count && i < results.Length; i++)
        {
            var entry = entries[i];
            if (!entry.On) continue;
            var result = Check(entry, ctx);
            results[i] = result;
            if (!Counts(entry, result) && first < 0) first = i;
        }
        return new ListVerdict(first < 0, first);
    }

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

    /// <summary>A result as a readout shows it: the value, or why it's unknown or invalid.</summary>
    public static string ValueText(ConditionEntry entry, ConditionResult result) => result.State switch
    {
        ConditionState.Invalid => result.Reason ?? "invalid",
        ConditionState.Unknown => result.Reason ?? "unknown",
        _ => entry.KindDef?.ShowValue(result) ?? string.Empty,
    };
}

/// <summary>
/// The condition lists features expose by name, for the dev cond and why verbs: a setting (with a short name for its
/// whole document, "gate"), the list names it offers, how a name maps to a key, and the context a check of that list
/// uses. AutoCast registers "every" and "1"–"5"; a name it doesn't list may still resolve ("-1/2").
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
