using System.Text.Json;
using System.Text.Json.Nodes;

namespace EpochAssist.Core;

/// <summary>
/// One condition in a list, as stored: kind id, args by name, its switch, "true when unresolvable", an optional id and
/// label, and any other field kept as it was. Read once per stored text; the kind and args are checked when first
/// asked (Invalid says why an entry can't be checked here).
/// </summary>
internal sealed class ConditionEntry
{
    public string Kind { get; set; } = string.Empty;
    /// <summary>Args by name, as stored (unknown names kept).</summary>
    public JsonObject Args { get; set; } = new();
    public bool On { get; set; } = true;
    public bool TrueWhenUnknown { get; set; }
    /// <summary>A stable name inside its list ("gate").</summary>
    public string? Id { get; set; }
    /// <summary>A player-visible name before the words ("Enemy gate").</summary>
    public string? Label { get; set; }
    /// <summary>Fields this version doesn't know, written back unchanged.</summary>
    public List<KeyValuePair<string, JsonNode?>> Extra { get; } = new();
    /// <summary>An entry that isn't an object at all: kept as it was, invalid.</summary>
    public JsonNode? Raw { get; init; }
    /// <summary>Set when this node is a group (all, any or none of its items) rather than a condition.</summary>
    public ConditionGroup? Group { get; set; }
    /// <summary>Seconds the condition must have passed without a break before it counts (0–60; 0 = at once).</summary>
    public float For { get; set; }
    /// <summary>Draw this condition's value as a chart on the debug overlay.</summary>
    public bool Chart { get; set; }
    /// <summary>Set when the node is past its tree's limits (lines or depth): it is kept and fails while on.</summary>
    internal string? LimitReason { get; set; }

    public bool IsGroup => Group != null;

    private bool _checked;
    private ConditionKind? _kind;
    private ArgValues _values;
    private string? _invalid;

    /// <summary>The declared kind, or null when this build doesn't know it (or the entry is invalid for another reason).</summary>
    public ConditionKind? KindDef
    {
        get
        {
            Resolve();
            return _kind;
        }
    }

    public ArgValues Values
    {
        get
        {
            Resolve();
            return _values;
        }
    }

    /// <summary>Why this entry can't be checked here ("needs a newer version", "select buff"), or null.</summary>
    public string? Invalid
    {
        get
        {
            Resolve();
            return _invalid;
        }
    }

    public const float MaxFor = 60f;

    private void Resolve()
    {
        if (_checked) return;
        _checked = true;
        if (Raw != null)
        {
            _invalid = "not a condition";
            return;
        }
        if (LimitReason != null)
        {
            _invalid = LimitReason;
            return;
        }
        if (Group != null) return;
        if (For < 0f || For > MaxFor || float.IsNaN(For))
        {
            _invalid = $"for {ArgSpec.Format(For)} s is outside 0–{ArgSpec.Format(MaxFor)} s";
            return;
        }
        _kind = ConditionKinds.Find(Kind);
        if (_kind == null)
        {
            _invalid = "needs a newer version";
            return;
        }
        var specs = _kind.Specs;
        var numbers = new float[specs.Length];
        var texts = new string?[specs.Length];
        for (int i = 0; i < specs.Length; i++)
        {
            Args.TryGetPropertyValue(specs[i].Name, out var node);
            if (!specs[i].TryRead(node, out numbers[i], out texts[i], out string reason))
            {
                _invalid = reason;
                break;
            }
        }
        _values = new ArgValues(specs, numbers, texts);
        if (_invalid == null && _kind.Validate?.Invoke(_values) is { } bad) _invalid = bad;
    }

    /// <summary>The line's sentence: the label (if any), then the kind's words, or the stored text when invalid.</summary>
    public string Words
    {
        get
        {
            if (Group != null) return Label != null ? $"{Label}: {Group.Words}" : Group.Words;
            // A known kind missing an argument reads by its name ("Buff ending: select buff").
            if (Invalid != null && KindDef != null && Raw == null) return $"{Label ?? KindDef.Label}: {Invalid}";
            if (Invalid != null || KindDef == null) return Label != null ? $"{Label}: {Raw?.ToJsonString() ?? Kind}" : Raw?.ToJsonString() ?? Kind;
            if (Label == null) return KindDef.Words(Values);
            return $"{Label}: {(KindDef.LabelledWords ?? KindDef.Words)(Values)}";
        }
    }

    /// <summary>The status reason when it holds a slot back.</summary>
    public string FailText => Invalid == null && KindDef?.FailText is { } fail ? fail(Values) : Words;

    /// <summary>A new group node of a type (all, any, none), empty.</summary>
    public static ConditionEntry NewGroup(string type) => new() { Group = new ConditionGroup { Type = type } };

    /// <summary>A new entry of a kind, with its defaults and its "true when unresolvable" starting value.</summary>
    public static ConditionEntry New(ConditionKind kind)
    {
        var entry = new ConditionEntry { Kind = kind.Id, On = true, TrueWhenUnknown = kind.TrueWhenUnknown };
        foreach (var arg in kind.Args)
        {
            if (arg.Default != null) entry.Args[arg.Name] = ConditionDoc.Clone(arg.Default);
        }
        return entry;
    }
}

/// <summary>
/// A group node's own part: its type and items (conditions or groups). "all": every switched-on item passes; "any": at
/// least one does (empty passes); "none": no switched-on item passes. Stored as an object with exactly one of "all",
/// "any", "none" as an array; its switch, id, label and kept fields are on the node's ConditionEntry.
/// </summary>
internal sealed class ConditionGroup
{
    public static readonly string[] Types = { "all", "any", "none" };

    public string Type { get; set; } = "all";
    public List<ConditionEntry> Items { get; } = new();

    /// <summary>"All of", "Any of", "None of".</summary>
    public string Words => Type switch { "any" => "Any of", "none" => "None of", _ => "All of" };
}

/// <summary>
/// A condition tree: the root "all" group's items (conditions or groups). Every switched-on item must pass; empty
/// passes. Read once per stored text and not changed after (edits work on copies), so the node index is built once.
/// </summary>
internal sealed class ConditionList
{
    /// <summary>Most nodes (conditions and groups) a tree takes; nodes past it in a stored tree are kept and invalid.</summary>
    public const int Limit = 30;
    /// <summary>Deepest group level (the root is level 1); deeper nodes are kept and invalid.</summary>
    public const int MaxDepth = 4;

    public List<ConditionEntry> Entries { get; } = new();
    /// <summary>Fields of the list node besides "all", kept.</summary>
    public List<KeyValuePair<string, JsonNode?>> Extra { get; } = new();
    /// <summary>Why the whole list can't be read here (another group type, a newer document), or null. Such a list fails
    /// and is written back as stored.</summary>
    public string? Invalid { get; init; }
    /// <summary>The stored node of an invalid list.</summary>
    public JsonNode? Raw { get; init; }

    public static readonly ConditionList Empty = new();

    public bool Full => NodeCount >= Limit;

    public int IndexOf(string id) => Entries.FindIndex(e => e.Id == id);

    private ConditionEntry[]? _nodes;
    private int[] _parent = Array.Empty<int>(), _end = Array.Empty<int>(), _depth = Array.Empty<int>();

    /// <summary>Every node below the root in pre-order (a group, then its items); a node's index is its place here.</summary>
    public ConditionEntry[] Nodes
    {
        get
        {
            if (_nodes == null) Index();
            return _nodes!;
        }
    }

    public int NodeCount => Nodes.Length;

    /// <summary>A node's parent group's index, or -1 for the root's items.</summary>
    public int Parent(int node) => _parent[node];
    /// <summary>The index after a node's last descendant (node + 1 for a condition).</summary>
    public int End(int node) => _end[node];
    /// <summary>The level of the group a node is in: 1 for the root's items.</summary>
    public int Depth(int node) => _depth[node];

    /// <summary>Builds the node index again (after a copy is changed in place, before it is read).</summary>
    public void Reindex() => _nodes = null;

    private void Index()
    {
        var nodes = new List<ConditionEntry>();
        var parents = new List<int>();
        var depths = new List<int>();
        void Walk(List<ConditionEntry> items, int parent, int depth)
        {
            foreach (var item in items)
            {
                int index = nodes.Count;
                nodes.Add(item);
                parents.Add(parent);
                depths.Add(depth);
                item.LimitReason = index >= Limit ? $"past the {Limit}-line limit"
                    : item.Group != null && depth + 1 > MaxDepth ? $"groups go {MaxDepth} levels deep at most" : null;
                if (item.Group != null) Walk(item.Group.Items, index, depth + 1);
            }
        }
        Walk(Entries, -1, 1);
        _parent = parents.ToArray();
        _depth = depths.ToArray();
        _end = new int[nodes.Count];
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            int end = i + 1;
            if (nodes[i].Group != null)
            {
                // The last descendant is the furthest end among the items.
                for (int j = i + 1; j < nodes.Count && IsBelow(j, i); j++) end = Math.Max(end, j + 1);
            }
            _end[i] = end;
        }
        _nodes = nodes.ToArray();
    }

    private bool IsBelow(int node, int group)
    {
        for (int p = _parent[node]; p >= 0; p = _parent[p])
        {
            if (p == group) return true;
        }
        return false;
    }

    /// <summary>"all › any 2 › ": the groups above a node, each with its place among its parent's items (from 1) when
    /// it isn't the root's first.</summary>
    public string Path(int node)
    {
        if (node < 0 || node >= NodeCount) return string.Empty;
        var parts = new List<string>();
        for (int p = Parent(node); p >= 0; p = Parent(p))
        {
            parts.Add($"{Nodes[p].Group!.Type} {PlaceInParent(p)}");
        }
        parts.Add("all");
        parts.Reverse();
        return string.Join(" › ", parts) + " › ";
    }

    /// <summary>A node's place among its parent's items, from 1.</summary>
    public int PlaceInParent(int node)
    {
        int parent = Parent(node), place = 0;
        for (int i = parent + 1; i <= node; i = End(i))
        {
            place++;
            if (i == node) break;
        }
        return place;
    }

    /// <summary>A list-level field beside "all" (move, waitMs), kept in Extra; null when absent.</summary>
    public JsonNode? Field(string name)
    {
        foreach (var (key, value) in Extra)
        {
            if (key == name) return value;
        }
        return null;
    }
}

/// <summary>
/// One condition setting's value: format version v, keyed lists, any other field kept. Saved format version 1
/// (contracts/conditions-format.md): unknown fields at every level are kept and written back unchanged; a list whose
/// group isn't "all" and every list of a newer document read as invalid and are kept.
/// </summary>
internal sealed class ConditionDoc
{
    public const int FormatVersion = 1;

    private static readonly JsonDocumentOptions s_readOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public int Version { get; private init; } = FormatVersion;
    public Dictionary<string, ConditionList> Lists { get; } = new();
    /// <summary>List keys in stored order, so a write keeps it.</summary>
    public List<string> Order { get; } = new();
    public List<KeyValuePair<string, JsonNode?>> Extra { get; } = new();
    /// <summary>A newer document's whole stored text, written back as it was.</summary>
    public JsonObject? Newer { get; private init; }

    public bool ReadOnly => Newer != null;

    public ConditionList List(string key) => Lists.TryGetValue(key, out var list) ? list : ConditionList.Empty;

    /// <summary>Sets a list (an empty one is dropped from the document).</summary>
    public void Set(string key, ConditionList list)
    {
        if (list.Entries.Count == 0 && list.Extra.Count == 0 && list.Raw == null)
        {
            Lists.Remove(key);
            Order.Remove(key);
            return;
        }
        if (!Lists.ContainsKey(key)) Order.Add(key);
        Lists[key] = list;
    }

    /// <summary>Parses stored text, or an object; null with the reason when it isn't a condition document.</summary>
    public static ConditionDoc? Parse(string text, out string reason)
    {
        try
        {
            return Parse(JsonNode.Parse(text, documentOptions: s_readOptions), out reason);
        }
        catch (Exception e)
        {
            reason = e.Message;
            return null;
        }
    }

    public static ConditionDoc? Parse(JsonNode? node, out string reason)
    {
        reason = string.Empty;
        if (node is not JsonObject root)
        {
            reason = "not a JSON object";
            return null;
        }
        int version = root["v"] is JsonValue v && v.TryGetValue(out int n) ? n : 0;
        if (version < 1)
        {
            reason = "no format version";
            return null;
        }
        if (version > FormatVersion)
        {
            // Kept whole; every list reads as invalid.
            var newer = new ConditionDoc { Version = version, Newer = (JsonObject)Clone(root)! };
            if (root["lists"] is JsonObject newerLists)
            {
                foreach (var (key, list) in newerLists)
                {
                    newer.Order.Add(key);
                    newer.Lists[key] = new ConditionList { Invalid = "made by a newer version", Raw = Clone(list) };
                }
            }
            return newer;
        }

        var doc = new ConditionDoc { Version = version };
        foreach (var (key, value) in root)
        {
            if (key == "v") continue;
            if (key != "lists")
            {
                doc.Extra.Add(new(key, Clone(value)));
                continue;
            }
            if (value is not JsonObject lists)
            {
                reason = "lists isn't an object";
                return null;
            }
            foreach (var (listKey, listNode) in lists)
            {
                doc.Order.Add(listKey);
                doc.Lists[listKey] = ParseList(listNode);
            }
        }
        return doc;
    }

    private static ConditionList ParseList(JsonNode? node)
    {
        if (node is not JsonObject obj || obj["all"] is not JsonArray all)
        {
            string? type = node is JsonObject other ? other.Select(p => p.Key).FirstOrDefault() : null;
            return new ConditionList { Invalid = type != null ? $"made by a newer version ({type})" : "not a condition list", Raw = Clone(node) };
        }
        var list = new ConditionList();
        foreach (var (key, value) in obj)
        {
            if (key != "all") list.Extra.Add(new(key, Clone(value)));
        }
        foreach (var item in all) list.Entries.Add(ParseEntry(item));
        // Indexed now, so limit reasons are set before any entry is first checked.
        _ = list.Nodes;
        return list;
    }

    /// <summary>A tree's root items from a stored array (a macro's trigger, a wait until's condition).</summary>
    public static ConditionList ParseItems(JsonArray? items)
    {
        var list = new ConditionList();
        if (items != null)
        {
            foreach (var item in items) list.Entries.Add(ParseEntry(item));
        }
        _ = list.Nodes;
        return list;
    }

    /// <summary>A tree's root items as a stored array.</summary>
    public static JsonArray ItemsJson(ConditionList list)
    {
        var all = new JsonArray();
        foreach (var entry in list.Entries) all.Add(EntryJson(entry));
        return all;
    }

    // An object with exactly one of all, any, none as an array (and no kind) is a group node. An empty kind counts as
    // none: a build without groups writes a group back as an entry with "kind": "" and empty args.
    private static string? GroupType(JsonObject obj)
    {
        if (obj.ContainsKey("kind") && !IsEmptyKind(obj["kind"])) return null;
        string? found = null;
        foreach (string type in ConditionGroup.Types)
        {
            if (obj[type] is not JsonArray) continue;
            if (found != null) return null;
            found = type;
        }
        return found;
    }

    private static bool IsEmptyKind(JsonNode? kind) => kind is JsonValue v && v.TryGetValue(out string? text) && text.Length == 0;

    // What a build without groups adds to a group node it writes back: dropped when the group is read again.
    private static bool OldEntryField(string key, JsonNode? value) => key switch
    {
        "kind" => IsEmptyKind(value),
        "args" => value is JsonObject { Count: 0 },
        "trueWhenUnknown" => true,
        _ => false,
    };

    private static ConditionEntry ParseEntry(JsonNode? node)
    {
        if (node is not JsonObject obj) return new ConditionEntry { Raw = Clone(node) ?? JsonValue.Create(string.Empty) };
        var entry = new ConditionEntry();
        if (GroupType(obj) is { } groupType)
        {
            entry.Group = new ConditionGroup { Type = groupType };
            foreach (var (key, value) in obj)
            {
                switch (key)
                {
                    case var _ when key == groupType:
                        foreach (var item in (JsonArray)value!) entry.Group.Items.Add(ParseEntry(item));
                        break;
                    case "on" when value is JsonValue o && o.TryGetValue(out bool on):
                        entry.On = on;
                        break;
                    case "id" when value is JsonValue i && i.TryGetValue(out string? id):
                        entry.Id = id;
                        break;
                    case "label" when value is JsonValue l && l.TryGetValue(out string? label):
                        entry.Label = label;
                        break;
                    case var _ when OldEntryField(key, value):
                        break;
                    default:
                        entry.Extra.Add(new(key, Clone(value)));
                        break;
                }
            }
            return entry;
        }
        foreach (var (key, value) in obj)
        {
            switch (key)
            {
                case "for" when value is JsonValue f && TryNumber(f, out double seconds):
                    entry.For = (float)seconds;
                    break;
                case "chart" when value is JsonValue c && c.TryGetValue(out bool chart):
                    entry.Chart = chart;
                    break;
                case "kind" when value is JsonValue k && k.TryGetValue(out string? kind):
                    entry.Kind = kind;
                    break;
                case "args" when value is JsonObject args:
                    entry.Args = (JsonObject)Clone(args)!;
                    break;
                case "on" when value is JsonValue o && o.TryGetValue(out bool on):
                    entry.On = on;
                    break;
                case "trueWhenUnknown" when value is JsonValue t && t.TryGetValue(out bool trueWhenUnknown):
                    entry.TrueWhenUnknown = trueWhenUnknown;
                    break;
                case "id" when value is JsonValue i && i.TryGetValue(out string? id):
                    entry.Id = id;
                    break;
                case "label" when value is JsonValue l && l.TryGetValue(out string? label):
                    entry.Label = label;
                    break;
                default:
                    entry.Extra.Add(new(key, Clone(value)));
                    break;
            }
        }
        return entry;
    }

    private static bool TryNumber(JsonValue json, out double value)
    {
        value = double.NaN;
        if (json.TryGetValue(out double d)) value = d;
        else if (json.TryGetValue(out float f)) value = f;
        else if (json.TryGetValue(out int i)) value = i;
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    /// <summary>The document as a JSON object, in a stable key order: v, lists, then kept fields.</summary>
    public JsonObject ToJson()
    {
        if (Newer != null) return (JsonObject)Clone(Newer)!;
        var lists = new JsonObject();
        foreach (string key in Order)
        {
            if (Lists.TryGetValue(key, out var list)) lists[key] = ListJson(list);
        }
        var root = new JsonObject { ["v"] = Version, ["lists"] = lists };
        foreach (var (key, value) in Extra) root[key] = Clone(value);
        return root;
    }

    /// <summary>The canonical compact text, as MelonPreferences stores it.</summary>
    public string ToText() => ToJson().ToJsonString();

    private static JsonNode? ListJson(ConditionList list)
    {
        if (list.Raw != null) return Clone(list.Raw);
        var all = new JsonArray();
        foreach (var entry in list.Entries) all.Add(EntryJson(entry));
        var node = new JsonObject { ["all"] = all };
        foreach (var (key, value) in list.Extra) node[key] = Clone(value);
        return node;
    }

    private static JsonNode? EntryJson(ConditionEntry entry)
    {
        if (entry.Raw != null) return Clone(entry.Raw);
        var node = new JsonObject();
        if (entry.Id != null) node["id"] = entry.Id;
        if (entry.Label != null) node["label"] = entry.Label;
        if (entry.Group is { } group)
        {
            var items = new JsonArray();
            foreach (var item in group.Items) items.Add(EntryJson(item));
            node[group.Type] = items;
            // A group's switch is written only when off: absent means on.
            if (!entry.On) node["on"] = false;
            foreach (var (key, value) in entry.Extra) node[key] = Clone(value);
            return node;
        }
        node["kind"] = entry.Kind;
        node["args"] = Clone(entry.Args);
        node["on"] = entry.On;
        node["trueWhenUnknown"] = entry.TrueWhenUnknown;
        if (entry.For != 0f) node["for"] = entry.For;
        if (entry.Chart) node["chart"] = true;
        foreach (var (key, value) in entry.Extra) node[key] = Clone(value);
        return node;
    }

    /// <summary>A copy of a node that can be given a new parent (the JSON library here has no DeepClone).</summary>
    public static JsonNode? Clone(JsonNode? node) => node == null ? null : JsonNode.Parse(node.ToJsonString());

    /// <summary>How many entries of each list this build can't read, for an import's result: "every: 1".</summary>
    public IEnumerable<(string Key, int Count)> InvalidCounts()
    {
        foreach (string key in Order)
        {
            if (!Lists.TryGetValue(key, out var list)) continue;
            int count = list.Invalid != null ? Math.Max(1, (list.Raw?["all"] as JsonArray)?.Count ?? 1) : list.Nodes.Count(e => !e.IsGroup && e.KindDef == null);
            if (count > 0) yield return (key, count);
        }
    }
}
