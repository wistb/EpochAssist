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

    /// <summary>Why this entry can't be checked here ("needs a newer version", "pick a buff"), or null.</summary>
    public string? Invalid
    {
        get
        {
            Resolve();
            return _invalid;
        }
    }

    private void Resolve()
    {
        if (_checked) return;
        _checked = true;
        if (Raw != null)
        {
            _invalid = "not a condition";
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
    }

    /// <summary>The line's sentence: the label (if any), then the kind's words, or the stored text when invalid.</summary>
    public string Words
    {
        get
        {
            if (Invalid != null || KindDef == null) return Label != null ? $"{Label}: {Raw?.ToJsonString() ?? Kind}" : Raw?.ToJsonString() ?? Kind;
            if (Label == null) return KindDef.Words(Values);
            return $"{Label}: {(KindDef.LabelledWords ?? KindDef.Words)(Values)}";
        }
    }

    /// <summary>The status reason when it holds a slot back.</summary>
    public string FailText => Invalid == null && KindDef?.FailText is { } fail ? fail(Values) : Words;

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

/// <summary>A flat list read as one "all" group: every switched-on entry must pass. Empty passes.</summary>
internal sealed class ConditionList
{
    /// <summary>Most entries a list takes; a longer stored list is kept and checked.</summary>
    public const int Limit = 10;

    public List<ConditionEntry> Entries { get; } = new();
    /// <summary>Fields of the list node besides "all", kept.</summary>
    public List<KeyValuePair<string, JsonNode?>> Extra { get; } = new();
    /// <summary>Why the whole list can't be read here (another group type, a newer document), or null. Such a list fails
    /// and is written back as stored.</summary>
    public string? Invalid { get; init; }
    /// <summary>The stored node of an invalid list.</summary>
    public JsonNode? Raw { get; init; }

    public static readonly ConditionList Empty = new();

    public bool Full => Entries.Count >= Limit;

    public int IndexOf(string id) => Entries.FindIndex(e => e.Id == id);
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
        return list;
    }

    private static ConditionEntry ParseEntry(JsonNode? node)
    {
        if (node is not JsonObject obj) return new ConditionEntry { Raw = Clone(node) ?? JsonValue.Create(string.Empty) };
        var entry = new ConditionEntry();
        foreach (var (key, value) in obj)
        {
            switch (key)
            {
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
        node["kind"] = entry.Kind;
        node["args"] = Clone(entry.Args);
        node["on"] = entry.On;
        node["trueWhenUnknown"] = entry.TrueWhenUnknown;
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
            int count = list.Invalid != null ? Math.Max(1, (list.Raw?["all"] as JsonArray)?.Count ?? 1) : list.Entries.Count(e => e.KindDef == null);
            if (count > 0) yield return (key, count);
        }
    }
}
