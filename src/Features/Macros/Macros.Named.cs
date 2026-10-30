using EpochAssist.Core;
using S = EpochAssist.Features.MacroSettings;

namespace EpochAssist.Features;

// Named conditions in the engine (contracts/named-format.md): the resolver named-condition lines ask, made in Start and
// dropped in Shutdown (lines read "macros off" while it is gone); every tree that can use one, for "Used by"; and the
// dev explanations.
internal static partial class Macros
{
    private static NamedConditions? s_named;

    /// <summary>The named conditions' resolver while the feature runs, or null.</summary>
    internal static NamedConditions? Named => s_named;

    private static void StartNamed()
    {
        s_named = new NamedConditions(NamedDoc, NeutralContext, SlotMacros.TallyAt, PlacesGeneration, UsingTrees) { Switched = NamedSwitched };
        ConditionKinds.Named = s_named;
    }

    private static void StopNamed()
    {
        ConditionKinds.Named = null;
        s_named = null;
        s_benchNamed = null;
    }

    // The dev bench's named conditions, in memory only: the stored ones, any starter missing from them and three more.
    private static ConditionDoc? s_benchNamed;

    private static ConditionDoc NamedDoc() => s_benchNamed ?? S.NamedConditions.Doc;

    /// <summary>What a named condition is checked in: the feature's tally and now, with no slot, macro or cast target.</summary>
    internal static ConditionContext NeutralContext() => SlotMacros.ContextFor(-1) with { CastTarget = null, Named = true };

    /// <summary>Registers the named conditions for the dev cond and why verbs (after the slot lists, so a list name is
    /// theirs first): a named condition by name or id, its ends tree as "&lt;name&gt; ends" or "&lt;id&gt;/ends".</summary>
    internal static void RegisterNamedTarget(string owner)
    {
        ConditionTargets.Register(new ConditionTargets.Target(owner, "named", S.NamedConditions,
            () => NamedConditions.IdsOf(S.NamedConditions.Doc).Select(id => NamedConditions.Name(S.NamedConditions.Doc, id)).ToList(),
            NamedKey, _ => NeutralContext()));
    }

    /// <summary>The list key of a named condition (by name ignoring case, or by id) or of its ends tree; null when none.</summary>
    internal static string? NamedKey(string name)
    {
        var doc = S.NamedConditions.Doc;
        name = name.Trim();
        if (name.EndsWith(NamedConditions.EndsSuffix, StringComparison.Ordinal))
            return NamedConditions.IsNamed(doc, name[..^NamedConditions.EndsSuffix.Length]) ? name : null;
        if (name.EndsWith(" ends", StringComparison.OrdinalIgnoreCase) && NamedConditions.IdByName(doc, name[..^5]) is { } owner)
            return owner + NamedConditions.EndsSuffix;
        return NamedConditions.IdByName(doc, name);
    }

    // "Engaged: true" when debug notifications are on.
    private static void NamedSwitched(string id, bool value)
    {
        if (!S.DebugNotifications.Value) return;
        Toasts.Show("macros", $"{NamedConditions.Name(S.NamedConditions.Doc, id)}: {(value ? "true" : "false")}", ToastKind.Info, repeats: true);
    }

    // ---- The trees that can use a named condition ----

    private static int s_placesGeneration;
    private static (int Library, string? Gate, string? Slots) s_placesSeen = (-1, null, null);

    // Goes up when the library, the shared conditions or the slot lists change.
    private static int PlacesGeneration()
    {
        int library = S.LibraryDoc.Generation;
        string gate = S.GateConditions.Value, slots = S.SlotConditions.Value;
        if (library != s_placesSeen.Library || !ReferenceEquals(gate, s_placesSeen.Gate) || !ReferenceEquals(slots, s_placesSeen.Slots))
        {
            s_placesSeen = (library, gate, slots);
            s_placesGeneration++;
        }
        return s_placesGeneration;
    }

    /// <summary>Every tree outside the named conditions that can hold a named-condition line, with its place and the
    /// editor key that opens it: library macros and their steps' trees, Shared Conditions, each slot list.</summary>
    internal static IEnumerable<NamedPlace> UsingTrees()
    {
        foreach (var set in S.LibraryDoc.Doc.Sets)
        {
            foreach (var macro in set.Macros)
            {
                string place = $"{(set.Name.Length > 0 ? set.Name : "(no name)")} › {(macro.Name.Length > 0 ? macro.Name : "(no name)")}";
                string key = $"{set.Id}/{macro.Id}";
                yield return new NamedPlace(place, key, macro.All);
                for (int i = 0; i < macro.Actions.Count; i++)
                {
                    if (macro.Actions[i].Until is { } until) yield return new NamedPlace($"{place} (step {i + 1})", key, until);
                }
            }
        }
        yield return new NamedPlace("Shared Conditions", EveryKey, S.GateConditions.List(S.EveryList));
        var slots = S.SlotConditions.Doc;
        foreach (string key in slots.Order)
        {
            int slash = key.LastIndexOf('/');
            if (slash < 0 || !int.TryParse(key[..slash], out int form) || !int.TryParse(key[(slash + 1)..], out int slot)) continue;
            // A slot's page shows the current form's list, so only that one opens.
            string? open = form == SlotMacros.CurrentFormKey ? $"{SlotsKey}/{slot}" : null;
            yield return new NamedPlace($"Slot {slot} ({(form == -1 ? "human" : $"form {form}")})", open, slots.List(key));
        }
    }

#if DEV
    // ---- Dev bench (macro bench n named) ----

    private static readonly string[] s_starterIds = { "worth", "travel", "pack", "engaged", "danger", "boss" };

    // The bench's named conditions: the stored document, the starters it lacks (from the default) and bench1–bench3,
    // each four lines of the written-out bench (vitals, a buff, enemies around hero and cursor).
    private static void BenchNamed(bool on)
    {
        if (!on)
        {
            s_benchNamed = null;
            return;
        }
        var json = S.NamedConditions.Doc.ToJson();
        var lists = json["lists"] as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
        json["lists"] = lists;
        var starters = ConditionDoc.Parse(S.NamedConditions.Default, out _)!.ToJson()["lists"] as System.Text.Json.Nodes.JsonObject;
        foreach (var (key, list) in starters ?? new System.Text.Json.Nodes.JsonObject())
        {
            if (!lists.ContainsKey(key)) lists[key] = ConditionDoc.Clone(list);
        }
        for (int i = 1; i <= 3; i++)
        {
            float range = 8 + i * 4;
            lists["bench" + i] = System.Text.Json.Nodes.JsonNode.Parse($"{{\"name\":\"Bench {i}\",\"all\":["
                + "{\"kind\":\"player.health\",\"args\":{\"compare\":\"below\",\"percent\":99},\"on\":true,\"trueWhenUnknown\":false},"
                + "{\"kind\":\"buff.missing\",\"args\":{\"buff\":81},\"on\":true,\"trueWhenUnknown\":false},"
                + $"{{\"kind\":\"enemies.weight\",\"args\":{{\"range\":{range},\"weight\":3}},\"on\":true,\"trueWhenUnknown\":false}},"
                + $"{{\"kind\":\"enemies.count\",\"args\":{{\"range\":{range + 2},\"count\":2,\"centre\":\"cursor\"}},\"on\":true,\"trueWhenUnknown\":false}}]}}");
        }
        s_benchNamed = ConditionDoc.Parse(json, out _);
    }

    // A bench macro's lines with named conditions: one line per starter and bench named condition, then one that fails.
    private static string BenchNamedLines()
    {
        var lines = s_starterIds.Concat(new[] { "bench1", "bench2", "bench3" })
            .Select(id => $"{{\"kind\":\"named.is\",\"args\":{{\"named\":\"{id}\",\"is\":\"true\"}},\"on\":true,\"trueWhenUnknown\":false}}");
        return string.Join(",", lines) + ",{\"kind\":\"player.health\",\"args\":{\"compare\":\"below\",\"percent\":1},\"on\":true,\"trueWhenUnknown\":false}";
    }

    // ---- Dev explanations (why named) ----

    /// <summary>
    /// Every named condition: name, id, value, sticky state, whether it was checked this frame, uses and its first
    /// failing line when false. With a name, that one alone with every node of its start tree (and ends tree).
    /// </summary>
    public static List<string> ExplainNamed(string? name)
    {
        var lines = new List<string>();
        var doc = S.NamedConditions.Doc;
        if (s_named is not { } named)
        {
            lines.Add("error why named: macros aren't running");
            return lines;
        }
        var ids = NamedConditions.IdsOf(doc).ToList();
        if (name != null)
        {
            if (NamedKey(name) is not { } found || !NamedConditions.IsNamedKey(found))
            {
                lines.Add($"error why named: no named condition \"{name}\"");
                return lines;
            }
            ids = new List<string> { found };
        }
        lines.Add($"ok why named: {NamedConditions.Count(doc)} named conditions, frame {UnityEngine.Time.frameCount}");
        foreach (string id in ids)
        {
            bool? value = named.Result(id, out string? reason);
            var state = named.StateOf(id);
            string result = value is bool v ? (v ? "true" : "false") : $"unknown ({reason})";
            string failing = value == false && state?.Tree.List is { } list && !state.Tree.Verdict.Passed ? $", first failing {list.Path(state.Tree.Verdict.FirstFailing)}{Conditions.FailingWords(list, state.Tree)}" : string.Empty;
            lines.Add($"named {NamedConditions.Name(doc, id)} ({id}): {result}, sticky {named.StickyText(id)}{(state?.HoldS > 0f ? $" hold {ArgSpec.Format(state.HoldS)} s" : string.Empty)}, "
                + $"used by {named.UsesOf(id).Count}{failing}");
            if (name == null || state == null) continue;
            DetailTree(lines, "start", state.Start, state.Tree);
            if (state.Ends != null) DetailTree(lines, "ends", state.Ends, state.EndsTree);
        }
        return lines;
    }

    private static void DetailTree(List<string> lines, string label, ConditionList list, TreeState state)
    {
        lines.Add($"{label}: {(state.List == null ? "not checked" : state.Verdict.Passed ? "pass" : "fail")}{(list.Invalid != null ? $" (invalid: {list.Invalid})" : string.Empty)}");
        if (!ReferenceEquals(state.List, list)) return;
        for (int i = 0; i < list.NodeCount; i++)
        {
            var node = list.Nodes[i];
            var result = state.Results[i];
            string counted = !node.On ? "off" : state.Counted[i] ? "counts" : "doesn't count";
            string inner = ConditionKinds.NamedIdOf(node) is { } inside && result.State is ConditionState.Pass or ConditionState.Fail && !state.Counted[i] ? $"  [{node.FailText}]" : string.Empty;
            lines.Add($"{new string(' ', 2 * list.Depth(i))}{RuleTreeRows.PathOf(list, i)} {node.Words}{(node.For > 0f ? $" for {ArgSpec.Format(node.For)} s" : string.Empty)}  "
                + $"{Conditions.ValueText(node, result)}  {Conditions.StateWord(result.State)}  {counted}{(result.Reason != null ? $" ({result.Reason})" : string.Empty)}{inner}");
        }
    }

    /// <summary>Where a named condition is used, for macro named uses.</summary>
    public static List<string> NamedUses(string name)
    {
        var lines = new List<string>();
        if (s_named is not { } named || NamedKey(name) is not { } id || !NamedConditions.IsNamedKey(id))
        {
            lines.Add(s_named == null ? "error macro named: macros aren't running" : $"error macro named: no named condition \"{name}\"");
            return lines;
        }
        var uses = named.UsesOf(id);
        lines.Add($"ok macro named uses {NamedConditions.Name(S.NamedConditions.Doc, id)} ({id}): {uses.Count} place{(uses.Count == 1 ? string.Empty : "s")}");
        foreach (var use in uses) lines.Add($"{use.Place}{(use.Open != null ? $" (opens {use.Open})" : string.Empty)}");
        return lines;
    }
#endif
}
