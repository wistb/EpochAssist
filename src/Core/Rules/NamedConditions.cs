using System.Text.Json.Nodes;

namespace EpochAssist.Core;

/// <summary>A tree that can hold named-condition lines, with where it is as the player reads it ("Fight › Shadow Rend")
/// and the editor key that opens it ("s1/m2"), or null when nothing opens it.</summary>
internal sealed record NamedPlace(string Place, string? Open, ConditionList List);

/// <summary>One named condition's check state (not stored): its last result and frame, its trees' states, the sticky
/// latch, the loop guard and what debug notifications last showed.</summary>
internal sealed class NamedState
{
    public NamedState(string id) => Id = id;

    public string Id { get; }
    public ConditionList Start = ConditionList.Empty;
    public ConditionList? Ends;
    public float HoldS;
    /// <summary>The start tree, ends tree and hold time as text: a change resets the state.</summary>
    public string Content = string.Empty;
    public string LoopReason = string.Empty;

    public int Frame = -1;
    /// <summary>The enemy count's refresh time the last check saw.</summary>
    public float TallyAt = float.NaN;
    public bool Value;
    public readonly TreeState Tree = new();
    public readonly TreeState EndsTree = new();
    public bool Held;
    public float StartFailingSince = float.NaN;
    public float LastAsked = float.NegativeInfinity;
    public bool Checking;
    public bool Explaining;
    public bool? LastShown;
    public float LastShownAt = float.NegativeInfinity;

    /// <summary>It has an ends tree or a hold time, so once true it stays true for a while.</summary>
    public bool Sticky => Ends is { Entries.Count: > 0 } || HoldS > 0f;

    public void Reset()
    {
        Tree.Reset();
        EndsTree.Reset();
        Frame = -1;
        TallyAt = float.NaN;
        Value = false;
        Held = false;
        StartFailingSince = float.NaN;
    }
}

/// <summary>
/// The named conditions (contracts/named-format.md): trees with a stable id and a name in one condition document, keyed
/// by id ("worth"), each with an optional ends tree ("worth/ends") and hold time ("holdS"). Lines of kind named.is ask
/// this resolver, which checks a named condition the first time it is asked in a frame (never when nobody asks), with a
/// context of no slot, macro or cast target, and answers from that check for the rest of the frame. A check already
/// under way for the same id is a loop and answers unknown. After the first check of a list nothing allocates.
/// </summary>
internal sealed class NamedConditions : INamedResolver
{
    public const int MaxNamed = 30;
    public const int MaxName = 40;
    public const string EndsSuffix = "/ends";
    public const string NameRule = "1–40 characters, unique ignoring case; trimmed";
    public const string LimitReason = "30 named conditions at most";
    public const float MaxHold = 60f, HoldStep = 0.5f;
    // A named condition nobody asked for this long starts again (its "for" timers and latch), as a newly bound list.
    private const float ForgetSeconds = 1f;
    private const float ShownGapSeconds = 1f;

    private readonly Func<ConditionDoc> _doc;
    private readonly Func<ConditionContext> _context;
    private readonly Func<float> _tallyAt;
    private readonly Func<int> _placesGeneration;
    private readonly Func<IEnumerable<NamedPlace>> _places;
    private readonly Dictionary<string, NamedState> _states = new();
    private ConditionDoc? _seen;
    // The frame the document was last read: reading a setting goes through the profile in use, so it's read once a frame.
    private int _syncFrame = -1;

    /// <param name="tallyAt">When the enemy count the context reads last refreshed: a refresh later in the frame checks
    /// again, so a line read after it agrees with lines written out.</param>
    public NamedConditions(Func<ConditionDoc> doc, Func<ConditionContext> context, Func<float> tallyAt, Func<int> placesGeneration, Func<IEnumerable<NamedPlace>> places)
    {
        _doc = doc;
        _context = context;
        _tallyAt = tallyAt;
        _placesGeneration = placesGeneration;
        _places = places;
    }

    /// <summary>Called when a named condition's result changes (debug notifications), at most once a second per id.</summary>
    public Action<string, bool>? Switched { get; set; }

    // ---- Checking ----

    public bool? Result(string id, out string? reason)
    {
        reason = null;
        Sync();
        if (!_states.TryGetValue(id, out var state))
        {
            reason = ConditionKinds.MissingNamed;
            return null;
        }
        if (state.Checking)
        {
            reason = state.LoopReason;
            return null;
        }
        int frame = UnityEngine.Time.frameCount;
        float tallyAt = _tallyAt();
        if (state.Frame == frame && tallyAt.Equals(state.TallyAt)) return state.Value;
        float now = Clock.Now;
        if (now - state.LastAsked > ForgetSeconds) state.Reset();
        state.Checking = true;
        try
        {
            var ctx = _context() with { Named = true, Macro = null, CastTarget = null };
            bool start = Conditions.Check(state.Start, ctx, state.Tree).Passed;
            state.Value = Latch(state, start, ctx, now);
        }
        finally
        {
            state.Checking = false;
        }
        state.Frame = frame;
        state.TallyAt = tallyAt;
        state.LastAsked = now;
        if (Switched is { } switched && state.LastShown != state.Value && now - state.LastShownAt >= ShownGapSeconds)
        {
            // The first result after a reset isn't a switch.
            if (state.LastShown != null) switched(id, state.Value);
            state.LastShown = state.Value;
            state.LastShownAt = now;
        }
        return state.Value;
    }

    // Plain: the start tree's verdict. Sticky: true once the start tree passes, then until the ends tree passes or the
    // start tree has failed for the whole hold time; the ends tree is checked only while held.
    private static bool Latch(NamedState state, bool start, ConditionContext ctx, float now)
    {
        if (!state.Sticky)
        {
            state.Held = false;
            return start;
        }
        if (start)
        {
            state.Held = true;
            state.StartFailingSince = float.NaN;
            return true;
        }
        if (!state.Held) return false;
        if (float.IsNaN(state.StartFailingSince)) state.StartFailingSince = now;
        bool ends = state.Ends is { Entries.Count: > 0 } endsList && Conditions.Check(endsList, ctx, state.EndsTree).Passed;
        if (ends || (state.HoldS > 0f && now - state.StartFailingSince >= state.HoldS - 1e-4f))
        {
            state.Held = false;
            state.StartFailingSince = float.NaN;
            return false;
        }
        return true;
    }

    /// <summary>Starts every named condition again from false (zone, character or profile change).</summary>
    public void ResetAll()
    {
        foreach (var state in _states.Values) state.Reset();
    }

    /// <summary>A named condition's check state, for the dev verbs and the page's readout; null when it's missing.</summary>
    public NamedState? StateOf(string id)
    {
        Sync();
        return _states.TryGetValue(id, out var state) ? state : null;
    }

    // A new document: states of named conditions whose trees or hold time changed start again; the others keep their
    // timers and latch (a rename changes nothing).
    private void Sync()
    {
        int frame = UnityEngine.Time.frameCount;
        if (frame == _syncFrame) return;
        _syncFrame = frame;
        var doc = _doc();
        if (ReferenceEquals(doc, _seen)) return;
        _seen = doc;
        var ids = new HashSet<string>(IdsOf(doc));
        foreach (string gone in _states.Keys.Where(k => !ids.Contains(k)).ToList()) _states.Remove(gone);
        foreach (string id in ids)
        {
            if (!_states.TryGetValue(id, out var state)) _states[id] = state = new NamedState(id);
            state.Start = doc.List(id);
            state.Ends = doc.Lists.TryGetValue(id + EndsSuffix, out var ends) ? ends : null;
            state.HoldS = HoldOf(state.Start);
            state.LoopReason = $"loops back to {Name(doc, id)}";
            string content = Content(doc, id, null);
            if (content == state.Content) continue;
            state.Content = content;
            state.Reset();
        }
    }

    // ---- The resolver's other answers ----

    public string? NameOf(string id)
    {
        var doc = _doc();
        return IsNamed(doc, id) ? Name(doc, id) : null;
    }

    public ConditionList? StartOf(string id)
    {
        var doc = _doc();
        return IsNamed(doc, id) ? doc.List(id) : null;
    }

    public ConditionList? EndsOf(string id) => _doc().Lists.TryGetValue(id + EndsSuffix, out var ends) && IsNamed(_doc(), id) ? ends : null;

    public string FailText(string id, bool wanted)
    {
        var doc = _doc();
        if (!IsNamed(doc, id)) return ConditionKinds.MissingNamed;
        string name = Name(doc, id);
        // A failing "is false" line has nothing inside to point at.
        if (!wanted) return $"{name} true";
        if (StateOf(id) is not { } state || state.Explaining || state.Tree.List == null) return $"{name} false";
        state.Explaining = true;
        try
        {
            // A sticky one that let go has a failing start tree too, so its first failing line is the reason.
            return state.Tree.Verdict.Passed ? $"{name} false" : $"{name} false: {Conditions.FailingWords(state.Tree.List, state.Tree)}";
        }
        finally
        {
            state.Explaining = false;
        }
    }

    private (ConditionDoc? Doc, string? Editing) _offeredFor;
    private List<(string Id, string Name)> _offered = new();

    public IReadOnlyList<(string Id, string Name)> Offered(string? editing)
    {
        var doc = _doc();
        if (ReferenceEquals(_offeredFor.Doc, doc) && _offeredFor.Editing == editing) return _offered;
        _offeredFor = (doc, editing);
        _offered = IdsOf(doc).Where(id => editing == null || !Reaches(doc, id, editing)).Select(id => (id, Name(doc, id))).ToList();
        return _offered;
    }

    public bool Reaches(string from, string to) => Reaches(_doc(), from, to);

    /// <summary>"held", "holding 1.2 s", "off" for a sticky one; "plain" for one that isn't.</summary>
    public string StickyText(string id)
    {
        if (StateOf(id) is not { } state) return ConditionKinds.MissingNamed;
        if (!state.Sticky) return "plain";
        if (!state.Held) return "off";
        return float.IsNaN(state.StartFailingSince) ? "held" : $"holding {ArgSpec.Format(Clock.Now - state.StartFailingSince)} s";
    }

    // ---- Uses ----

    private readonly Dictionary<string, List<NamedPlace>> _uses = new();
    private HashSet<string> _inUse = new();

    /// <summary>Whether a macro, slot list or Shared Conditions reaches the named condition (directly or through others).</summary>
    public bool InUse(string id)
    {
        BuildUses();
        return _inUse.Contains(id);
    }
    private int _usesGeneration = int.MinValue;
    private ConditionDoc? _usesDoc;

    /// <summary>Where a named condition is used: library macros and their steps, slot lists, Shared Conditions, other named
    /// conditions (start or ends). Rebuilt when any of those change.</summary>
    public IReadOnlyList<NamedPlace> UsesOf(string id)
    {
        BuildUses();
        return _uses.TryGetValue(id, out var places) ? places : (IReadOnlyList<NamedPlace>)Array.Empty<NamedPlace>();
    }

    private void BuildUses()
    {
        var doc = _doc();
        int generation = _placesGeneration();
        if (generation == _usesGeneration && ReferenceEquals(doc, _usesDoc)) return;
        _usesGeneration = generation;
        _usesDoc = doc;
        _uses.Clear();
        var places = _places().ToList();
        var outside = new HashSet<string>();
        foreach (var place in places) Ids(place.List, outside);
        _inUse = Closure(doc, outside);
        foreach (string id in IdsOf(doc))
        {
            places.Add(new NamedPlace(Name(doc, id), "named/" + id, doc.List(id)));
            if (doc.Lists.TryGetValue(id + EndsSuffix, out var ends)) places.Add(new NamedPlace($"{Name(doc, id)} (ends)", "named/" + id, ends));
        }
        var ids = new HashSet<string>();
        foreach (var place in places)
        {
            ids.Clear();
            Ids(place.List, ids);
            foreach (string id in ids)
            {
                if (!_uses.TryGetValue(id, out var list)) _uses[id] = list = new List<NamedPlace>();
                list.Add(place);
            }
        }
    }

    // ---- The document: ids, names, reach ----

    /// <summary>A list key that is a named condition's (not an ends tree).</summary>
    public static bool IsNamedKey(string key) => !key.Contains('/');

    public static bool IsNamed(ConditionDoc doc, string id) => id.Length > 0 && IsNamedKey(id) && doc.Lists.ContainsKey(id);

    /// <summary>The named conditions' ids in stored order (the page's order).</summary>
    public static IEnumerable<string> IdsOf(ConditionDoc doc) => doc.Order.Where(k => IsNamedKey(k) && doc.Lists.ContainsKey(k));

    public static int Count(ConditionDoc doc) => IdsOf(doc).Count();

    /// <summary>The stored name, or the id when it has none.</summary>
    public static string Name(ConditionDoc doc, string id) =>
        doc.List(id).Field("name") is JsonValue v && v.TryGetValue(out string? name) && name.Trim().Length > 0 ? name.Trim() : id;

    /// <summary>The hold time in seconds; 0 when absent or outside 0–60.</summary>
    public static float HoldOf(ConditionList list) =>
        list.Field("holdS") is JsonValue v && MacroDoc.TryNumber(v, out double s) && s > 0 && s <= MaxHold ? (float)s : 0f;

    /// <summary>The id of the named condition with this name (ignoring case), or with this id; null when none.</summary>
    public static string? IdByName(ConditionDoc doc, string name)
    {
        name = name.Trim();
        foreach (string id in IdsOf(doc))
        {
            if (Name(doc, id).Equals(name, StringComparison.OrdinalIgnoreCase)) return id;
        }
        return IsNamed(doc, name) ? name : null;
    }

    /// <summary>Why a name can't be used (empty, too long, another has it), or null.</summary>
    public static string? NameProblem(ConditionDoc doc, string name, string? exceptId)
    {
        name = name.Trim();
        if (name.Length == 0) return "a name needs 1–40 characters";
        if (name.Length > MaxName) return "a name has 40 characters at most";
        foreach (string id in IdsOf(doc))
        {
            if (id != exceptId && Name(doc, id).Equals(name, StringComparison.OrdinalIgnoreCase)) return "another named condition has this name";
        }
        return null;
    }

    /// <summary>A name no named condition has: the name, then " (2)", " (3)" …</summary>
    public static string FreeName(ConditionDoc doc, string name)
    {
        if (NameProblem(doc, name, null) == null) return name.Trim();
        for (int n = 2; ; n++)
        {
            string suffix = $" ({n})";
            string candidate = (name.Length + suffix.Length > MaxName ? name[..(MaxName - suffix.Length)] : name) + suffix;
            if (NameProblem(doc, candidate, null) == null) return candidate;
        }
    }

    /// <summary>"n1", "n2", …: an id the document doesn't have (an ends tree's id counts too).</summary>
    public static string NewId(ConditionDoc doc, ICollection<string>? taken = null)
    {
        for (int n = 1; ; n++)
        {
            string id = "n" + n;
            if (!doc.Lists.ContainsKey(id) && !doc.Lists.ContainsKey(id + EndsSuffix) && taken?.Contains(id) != true) return id;
        }
    }

    /// <summary>Adds the ids of the named conditions a tree's lines name, switched on or not.</summary>
    public static void Ids(ConditionList list, ICollection<string> into)
    {
        if (list.Invalid != null) return;
        foreach (var node in list.Nodes)
        {
            if (ConditionKinds.NamedIdOf(node) is { } id && !into.Contains(id)) into.Add(id);
        }
    }

    // The ids a named condition's own trees name (start and ends).
    private static IEnumerable<string> Inner(ConditionDoc doc, string id)
    {
        var ids = new List<string>();
        Ids(doc.List(id), ids);
        if (doc.Lists.TryGetValue(id + EndsSuffix, out var ends)) Ids(ends, ids);
        return ids;
    }

    /// <summary>Whether a named condition is, or uses (through any others), the one with id to.</summary>
    public static bool Reaches(ConditionDoc doc, string from, string to)
    {
        var seen = new HashSet<string>();
        var stack = new Stack<string>();
        stack.Push(from);
        while (stack.Count > 0)
        {
            string id = stack.Pop();
            if (id == to) return true;
            if (!seen.Add(id)) continue;
            foreach (string inner in Inner(doc, id)) stack.Push(inner);
        }
        return false;
    }

    /// <summary>The ids and every named condition they use, through any depth (missing ones left out).</summary>
    public static HashSet<string> Closure(ConditionDoc doc, IEnumerable<string> ids)
    {
        var closure = new HashSet<string>();
        var stack = new Stack<string>(ids);
        while (stack.Count > 0)
        {
            string id = stack.Pop();
            if (!IsNamed(doc, id) || !closure.Add(id)) continue;
            foreach (string inner in Inner(doc, id)) stack.Push(inner);
        }
        return closure;
    }

    /// <summary>Points every named-condition line under a stored node at the mapped ids (others are left as they are).</summary>
    public static void RemapIds(JsonNode? node, IReadOnlyDictionary<string, string> map)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["kind"] is JsonValue k && k.TryGetValue(out string? kind) && kind == ConditionKinds.NamedKind
                    && obj["args"] is JsonObject args && args["named"] is JsonValue v && v.TryGetValue(out string? id) && map.TryGetValue(id, out string? to))
                {
                    args["named"] = to;
                }
                foreach (var (_, child) in obj.ToList()) RemapIds(child, map);
                break;
            case JsonArray array:
                foreach (var child in array.ToList()) RemapIds(child, map);
                break;
        }
    }

    // A named condition's start tree, ends tree and hold time as one text, its inner ids mapped (for import's compare).
    private static string Content(ConditionDoc doc, string id, IReadOnlyDictionary<string, string>? map)
    {
        var start = ConditionDoc.ItemsJson(doc.List(id));
        JsonNode? ends = doc.Lists.TryGetValue(id + EndsSuffix, out var endsList) ? ConditionDoc.ItemsJson(endsList) : null;
        if (map != null)
        {
            RemapIds(start, map);
            RemapIds(ends, map);
        }
        return $"{start.ToJsonString()}|{ends?.ToJsonString() ?? "-"}|{ArgSpec.Format(HoldOf(doc.List(id)))}";
    }

    // ---- Import (share codes) ----

    /// <summary>An imported copy whose name the player already had: "Use mine" points the set at theirs instead.</summary>
    public sealed record Clash(string SetName, string CopyId, string MineId);

    /// <summary>
    /// The named conditions of a share code merged into a document, inner ones first: one with the same name (ignoring
    /// case) and content as one already there is reused; one with the same name and other content is added as
    /// "<name> (2)" with a clash noted; others are added. Every added one gets a new id. Map takes each code id to the id
    /// now used. Null with the reason when the document would pass 30.
    /// </summary>
    public static ConditionDoc? Merge(ConditionDoc mine, ConditionDoc theirs, string setName, out Dictionary<string, string> map,
        List<string> report, List<Clash> clashes, out string reason)
    {
        map = new Dictionary<string, string>();
        reason = string.Empty;
        var order = new List<string>();
        var seen = new HashSet<string>();
        void Visit(string id)
        {
            if (!IsNamed(theirs, id) || !seen.Add(id)) return;
            foreach (string inner in Inner(theirs, id)) Visit(inner);
            order.Add(id);
        }
        foreach (string id in IdsOf(theirs)) Visit(id);

        var json = mine.ToJson();
        var lists = json["lists"] as JsonObject ?? new JsonObject();
        json["lists"] = lists;
        var theirsLists = theirs.ToJson()["lists"] as JsonObject ?? new JsonObject();
        var taken = new HashSet<string>();
        var added = new List<(string From, string To, string Name)>();
        foreach (string id in order)
        {
            string name = Name(theirs, id);
            string content = Content(theirs, id, map);
            string? mineId = IdByName(mine, name) is { } found && IsNamed(mine, found) && Name(mine, found).Equals(name, StringComparison.OrdinalIgnoreCase) ? found : null;
            if (mineId != null && Content(mine, mineId, null) == content)
            {
                map[id] = mineId;
                report.Add($"{name}: yours used");
                continue;
            }
            string newId = NewId(mine, taken);
            taken.Add(newId);
            map[id] = newId;
            string newName = mineId != null || added.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ? FreeAdded(mine, added, name) : name;
            added.Add((id, newId, newName));
            if (mineId != null)
            {
                clashes.Add(new Clash(setName, newId, mineId));
                report.Add($"{name}: added as {newName} — Use mine?");
            }
            else report.Add($"{name}: added");
        }
        int total = Count(mine) + added.Count;
        if (total > MaxNamed)
        {
            reason = $"you have {Count(mine)} named conditions; this set needs {added.Count} more ({MaxNamed} at most)";
            return null;
        }
        foreach (var (from, to, name) in added)
        {
            var list = ConditionDoc.Clone(theirsLists[from]) as JsonObject ?? new JsonObject { ["all"] = new JsonArray() };
            list["name"] = name;
            RemapIds(list, map);
            lists[to] = list;
            if (theirsLists[from + EndsSuffix] is { } ends)
            {
                var endsCopy = ConditionDoc.Clone(ends);
                RemapIds(endsCopy, map);
                lists[to + EndsSuffix] = endsCopy;
            }
        }
        foreach (string id in order)
        {
            if (Inner(theirs, id).Any(inner => Reaches(theirs, inner, id)))
                report.Add($"{Name(theirs, id)} loops back to {Name(theirs, id)}, so it reads unknown");
        }
        return ConditionDoc.Parse(json, out reason);
    }

    // A free name among the player's and the ones this import adds.
    private static string FreeAdded(ConditionDoc mine, List<(string From, string To, string Name)> added, string name)
    {
        for (int n = 2; ; n++)
        {
            string suffix = $" ({n})";
            string candidate = (name.Length + suffix.Length > MaxName ? name[..(MaxName - suffix.Length)] : name) + suffix;
            if (NameProblem(mine, candidate, null) == null && !added.Any(a => a.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
        }
    }
}
