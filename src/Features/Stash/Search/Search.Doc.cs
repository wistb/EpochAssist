using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EpochAssist.Features;

internal static partial class StashSearch
{
    public const int MaxGroups = 8;
    public const int MaxRows = 40;
    public const int MaxNameLength = 48;

    internal enum GroupType { And, Not, If, Count, WeightedSum }
    internal enum StatSource { Affix, AnyPrefix, AnySuffix, Implicit, UniqueRoll, Pseudo }
    internal enum RowPosition { Any, Prefix, Suffix, Sealed }
    internal enum RowKind { Any, Standard, Corrupted, Experimental, Champion, Set, Enchant, Weaver }
    internal enum Tri { Any, Yes, No }

    /// <summary>A min–max pair; either end may be empty. Invalid while min is above max (ignored until fixed).</summary>
    internal readonly record struct Range(double? Min, double? Max)
    {
        public bool IsEmpty => Min == null && Max == null;
        public bool Invalid => Min is { } lo && Max is { } hi && lo > hi;
        /// <summary>Whether a value is within the set ends; an empty or invalid range takes anything.</summary>
        public bool Holds(double v) => Invalid || (Min == null || v >= Min - 1e-6) && (Max == null || v <= Max + 1e-6);
        public override string ToString() => IsEmpty ? "any" : $"{Num(Min)}–{Num(Max)}";
        private static string Num(double? v) => v?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>What a row looks for: an affix by id, any prefix or suffix, an implicit line ("property:type:tags:st"),
    /// a unique's rolled line ("uniqueId:modIndex"), or a total (its key).</summary>
    internal sealed record StatRef(StatSource Source, string Id)
    {
        public int AffixId => int.TryParse(Id, out int id) ? id : -1;
    }

    internal sealed class StatRow
    {
        public StatRef Stat { get; set; } = new(StatSource.Affix, "0");
        public RowPosition Position { get; set; }
        public RowKind Kind { get; set; }
        public Range Tier { get; set; }
        /// <summary>One range per number the stat shows, in shown units; missing ones are empty.</summary>
        public List<Range> Values { get; set; } = new();
        /// <summary>Roll quality floor, 0–100.</summary>
        public double? QualityMin { get; set; }
        public double Weight { get; set; } = 1d;
        public bool On { get; set; } = true;
        /// <summary>A part this build can't read (a newer build's word); the row is kept and skipped in matching.</summary>
        public string? Unreadable { get; set; }
        public JsonObject Extra { get; set; } = new();

        public Range ValueAt(int i) => i < Values.Count ? Values[i] : default;
        public bool HasBounds => !Tier.IsEmpty || QualityMin != null || Values.Any(v => !v.IsEmpty);
    }

    internal sealed class StatGroup
    {
        public GroupType Type { get; set; }
        public bool On { get; set; } = true;
        /// <summary>Count and Weighted sum only.</summary>
        public Range Bound { get; set; }
        public List<StatRow> Rows { get; set; } = new();
        public string? Unreadable { get; set; }
        public JsonObject Extra { get; set; } = new();

        public bool HasBound => Type is GroupType.Count or GroupType.WeightedSum;
        /// <summary>Takes part in matching: on, readable, valid bound, at least one enabled readable row.</summary>
        public bool Active => On && Unreadable == null && !(HasBound && Bound.Invalid) && Rows.Any(r => r.On && r.Unreadable == null);
    }

    internal sealed class ItemFilters
    {
        public List<(int Type, int? Sub)> Types { get; set; } = new();
        public HashSet<string> Rarities { get; set; } = new();
        public List<int> Uniques { get; set; } = new();
        public Range Lp { get; set; }
        public Range Ww { get; set; }
        public Range Wt { get; set; }
        public Range Fp { get; set; }
        public Range LevelReq { get; set; }
        /// <summary>How many prefixes and suffixes the item has, sealed ones aside (the game's count): an open suffix on
        /// gear is a suffix count of at most 1.</summary>
        public Range Prefixes { get; set; }
        public Range Suffixes { get; set; }
        public Tri Corrupted { get; set; }
        public Tri Sealed { get; set; }
        /// <summary>The game's class requirement flag (Primalist 1, Mage 2, Sentinel 4, Acolyte 8, Rogue 16); null for any.</summary>
        public int? Class { get; set; }
        public JsonObject Extra { get; set; } = new();

        public bool Any => Types.Count > 0 || Rarities.Count > 0 || Uniques.Count > 0 || !Lp.IsEmpty || !Ww.IsEmpty || !Wt.IsEmpty
            || !Fp.IsEmpty || !LevelReq.IsEmpty || !Prefixes.IsEmpty || !Suffixes.IsEmpty || Corrupted != Tri.Any || Sealed != Tri.Any || Class != null;
    }

    public static readonly string[] RarityWords = { "normal", "magic", "rare", "exalted", "unique", "set", "legendary" };

    /// <summary>A search: item filters and up to eight stat groups. A plain value; the page edits a copy.</summary>
    internal sealed class SearchDoc
    {
        public string? Name { get; set; }
        public ItemFilters Filters { get; set; } = new();
        public List<StatGroup> Groups { get; set; } = new();
        public JsonObject Extra { get; set; } = new();
        /// <summary>Parts this build couldn't read, by path ("groups[1].type: weird"), for the page and the verbs.</summary>
        public List<string> Unknown { get; } = new();

        public int RowCount => Groups.Sum(g => g.Rows.Count);

        /// <summary>No filter and no group that takes part: lists nothing.</summary>
        public bool IsEmpty => !Filters.Any && !Groups.Any(g => g.Active);

        public string ToJson() => Write(this).ToJsonString(new JsonSerializerOptions { WriteIndented = false });

        public SearchDoc Clone() => Parse(ToJson(), out _) ?? new SearchDoc();

        /// <summary>The limits a page or import breaks, as short notes ("9 groups: at most 8").</summary>
        public List<string> Problems()
        {
            var notes = new List<string>();
            if (Groups.Count > MaxGroups) notes.Add($"{Groups.Count} groups: at most {MaxGroups}");
            if (RowCount > MaxRows) notes.Add($"{RowCount} rows: at most {MaxRows}");
            if (Name != null && (Name.Trim().Length == 0 || Name.Length > MaxNameLength)) notes.Add($"name: 1–{MaxNameLength} characters");
            foreach (var g in Groups.Where(g => g.HasBound && g.Bound.Invalid)) notes.Add($"{GroupWord(g.Type)} group: min above max, skipped");
            notes.AddRange(Unknown.Select(u => "can't read " + u));
            return notes;
        }
    }

    public static string GroupWord(GroupType type) => type switch
    {
        GroupType.And => "And", GroupType.Not => "Not", GroupType.If => "If", GroupType.Count => "Count", _ => "Weighted sum",
    };

    // ---- JSON (v 1) ----

    private static readonly string[] s_groupWords = { "and", "not", "if", "count", "wsum" };
    private static readonly string[] s_sourceWords = { "affix", "anyprefix", "anysuffix", "implicit", "unique", "pseudo" };
    private static readonly string[] s_posWords = { "any", "prefix", "suffix", "sealed" };
    private static readonly string[] s_kindWords = { "any", "standard", "corrupted", "experimental", "champion", "set", "enchant", "weaver" };
    private static readonly string[] s_triWords = { "any", "yes", "no" };

    /// <summary>Reads a search; null with the reason when it isn't one. Unknown fields are kept and listed.</summary>
    public static SearchDoc? Parse(string json, out string? error)
    {
        error = null;
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException e)
        {
            error = "not a search: " + e.Message;
            return null;
        }
        if (root == null)
        {
            error = "not a search";
            return null;
        }
        var doc = new SearchDoc();
        if (root["v"] is JsonValue v && v.TryGetValue(out int version) && version > 1) doc.Unknown.Add($"version {version}");
        doc.Name = Str(root["name"]);
        if (root["filters"] is JsonObject f) doc.Filters = ReadFilters(f, doc.Unknown);
        if (root["groups"] is JsonArray groups)
            for (int i = 0; i < groups.Count; i++)
                if (groups[i] is JsonObject g) doc.Groups.Add(ReadStatGroup(g, $"groups[{i}]", doc.Unknown));
        doc.Extra = Rest(root, doc.Unknown, string.Empty, "v", "name", "filters", "groups");
        return doc;
    }

    private static ItemFilters ReadFilters(JsonObject o, List<string> unknown)
    {
        var f = new ItemFilters();
        if (o["types"] is JsonArray types)
            foreach (var t in types.OfType<JsonObject>())
                if (Int(t["type"]) is int type) f.Types.Add((type, Int(t["sub"])));
        if (o["rarities"] is JsonArray rarities)
            foreach (var r in rarities)
            {
                string word = Str(r)?.ToLowerInvariant() ?? string.Empty;
                if (RarityWords.Contains(word)) f.Rarities.Add(word);
                else unknown.Add($"filters.rarities: {word}");
            }
        if (o["uniques"] is JsonArray uniques)
            foreach (var u in uniques)
                if (Int(u) is int id) f.Uniques.Add(id);
        f.Lp = ReadRange(o["lp"]);
        f.Ww = ReadRange(o["ww"]);
        f.Wt = ReadRange(o["wt"]);
        f.Fp = ReadRange(o["fp"]);
        f.LevelReq = ReadRange(o["lvl"]);
        f.Prefixes = ReadRange(o["prefixes"]);
        f.Suffixes = ReadRange(o["suffixes"]);
        f.Corrupted = Word(o["corrupted"], s_triWords, Tri.Any, "filters.corrupted", unknown);
        f.Sealed = Word(o["sealed"], s_triWords, Tri.Any, "filters.sealed", unknown);
        f.Class = Int(o["class"]);
        // "ilvl" is accepted and left out: the game keeps no item level on items.
        f.Extra = Rest(o, unknown, "filters.", "types", "rarities", "uniques", "lp", "ww", "wt", "fp", "lvl", "ilvl", "prefixes", "suffixes", "corrupted", "sealed", "class");
        return f;
    }

    private static StatGroup ReadStatGroup(JsonObject o, string path, List<string> unknown)
    {
        var g = new StatGroup { On = Bool(o["on"]) ?? true, Bound = ReadRange(o["bound"]) };
        string word = Str(o["type"])?.ToLowerInvariant() ?? "and";
        int type = Array.IndexOf(s_groupWords, word);
        if (type < 0)
        {
            g.Unreadable = $"type {word}";
            unknown.Add($"{path}.type: {word}");
        }
        else g.Type = (GroupType)type;
        if (o["rows"] is JsonArray rows)
            for (int i = 0; i < rows.Count; i++)
                if (rows[i] is JsonObject r) g.Rows.Add(ReadRow(r, $"{path}.rows[{i}]", unknown));
        g.Extra = Rest(o, unknown, path + ".", "type", "on", "bound", "rows");
        return g;
    }

    private static StatRow ReadRow(JsonObject o, string path, List<string> unknown)
    {
        var row = new StatRow
        {
            On = Bool(o["on"]) ?? true,
            Tier = ReadRange(o["tier"]),
            QualityMin = Num(o["quality"]),
            Weight = Num(o["weight"]) ?? 1d,
        };
        if (o["stat"] is JsonObject stat)
        {
            string src = Str(stat["src"])?.ToLowerInvariant() ?? "affix";
            int source = Array.IndexOf(s_sourceWords, src);
            string id = stat["id"] is JsonValue jv && jv.TryGetValue(out int n) ? n.ToString(CultureInfo.InvariantCulture) : Str(stat["id"]) ?? string.Empty;
            if (source < 0)
            {
                row.Unreadable = $"source {src}";
                unknown.Add($"{path}.stat.src: {src}");
            }
            else row.Stat = new StatRef((StatSource)source, id);
        }
        else
        {
            row.Unreadable = "no stat";
            unknown.Add($"{path}.stat: missing");
        }
        var pos = Word(o["pos"], s_posWords, RowPosition.Any, path + ".pos", unknown, out bool badPos);
        var kind = Word(o["kind"], s_kindWords, RowKind.Any, path + ".kind", unknown, out bool badKind);
        row.Position = pos;
        row.Kind = kind;
        if (badPos || badKind) row.Unreadable ??= "position or kind";
        if (o["values"] is JsonArray values)
            foreach (var value in values)
                row.Values.Add(ReadRange(value));
        row.Extra = Rest(o, unknown, path + ".", "stat", "pos", "kind", "tier", "values", "quality", "weight", "on");
        return row;
    }

    private static JsonObject Write(SearchDoc doc)
    {
        var root = new JsonObject { ["v"] = 1 };
        if (doc.Name != null) root["name"] = doc.Name;
        var f = doc.Filters;
        var filters = new JsonObject();
        if (f.Types.Count > 0)
            filters["types"] = new JsonArray(f.Types.Select(t => (JsonNode)(t.Sub is int sub ? new JsonObject { ["type"] = t.Type, ["sub"] = sub } : new JsonObject { ["type"] = t.Type })).ToArray());
        if (f.Rarities.Count > 0) filters["rarities"] = new JsonArray(RarityWords.Where(f.Rarities.Contains).Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
        if (f.Uniques.Count > 0) filters["uniques"] = new JsonArray(f.Uniques.Select(u => (JsonNode)u).ToArray());
        PutRange(filters, "lp", f.Lp);
        PutRange(filters, "ww", f.Ww);
        PutRange(filters, "wt", f.Wt);
        PutRange(filters, "fp", f.Fp);
        PutRange(filters, "lvl", f.LevelReq);
        PutRange(filters, "prefixes", f.Prefixes);
        PutRange(filters, "suffixes", f.Suffixes);
        if (f.Corrupted != Tri.Any) filters["corrupted"] = s_triWords[(int)f.Corrupted];
        if (f.Sealed != Tri.Any) filters["sealed"] = s_triWords[(int)f.Sealed];
        if (f.Class is int cls) filters["class"] = cls;
        Merge(filters, f.Extra);
        root["filters"] = filters;
        var groups = new JsonArray();
        foreach (var g in doc.Groups)
        {
            var go = new JsonObject { ["type"] = s_groupWords[(int)g.Type] };
            if (!g.On) go["on"] = false;
            if (g.HasBound) PutRange(go, "bound", g.Bound);
            var rows = new JsonArray();
            foreach (var r in g.Rows)
            {
                var ro = new JsonObject
                {
                    ["stat"] = new JsonObject
                    {
                        ["src"] = s_sourceWords[(int)r.Stat.Source],
                        ["id"] = r.Stat.Source == StatSource.Affix && int.TryParse(r.Stat.Id, out int aid) ? aid : r.Stat.Id,
                    },
                };
                if (r.Position != RowPosition.Any) ro["pos"] = s_posWords[(int)r.Position];
                if (r.Kind != RowKind.Any) ro["kind"] = s_kindWords[(int)r.Kind];
                PutRange(ro, "tier", r.Tier);
                if (r.Values.Any(v => !v.IsEmpty)) ro["values"] = new JsonArray(r.Values.Select(v => (JsonNode)RangeNode(v)).ToArray());
                if (r.QualityMin is double q) ro["quality"] = q;
                if (g.Type == GroupType.WeightedSum || r.Weight != 1d) ro["weight"] = r.Weight;
                if (!r.On) ro["on"] = false;
                Merge(ro, r.Extra);
                rows.Add(ro);
            }
            go["rows"] = rows;
            Merge(go, g.Extra);
            groups.Add(go);
        }
        root["groups"] = groups;
        Merge(root, doc.Extra);
        return root;
    }

    private static Range ReadRange(JsonNode? node) => node is JsonObject o ? new Range(Num(o["min"]), Num(o["max"])) : default;

    private static JsonObject RangeNode(Range r)
    {
        var o = new JsonObject();
        if (r.Min is double lo) o["min"] = lo;
        if (r.Max is double hi) o["max"] = hi;
        return o;
    }

    private static void PutRange(JsonObject o, string name, Range r)
    {
        if (!r.IsEmpty) o[name] = RangeNode(r);
    }

    // Unknown fields, copied so a newer build's parts survive a save; listed in Unknown.
    private static JsonObject Rest(JsonObject o, List<string> unknown, string path, params string[] known)
    {
        var rest = new JsonObject();
        foreach (var (key, value) in o)
        {
            if (known.Contains(key)) continue;
            rest[key] = Copy(value);
            unknown.Add(path + key);
        }
        return rest;
    }

    private static void Merge(JsonObject into, JsonObject extra)
    {
        foreach (var (key, value) in extra)
            if (!into.ContainsKey(key)) into[key] = Copy(value);
    }

    private static T Word<T>(JsonNode? node, string[] words, T fallback, string path, List<string> unknown) where T : struct, Enum =>
        Word(node, words, fallback, path, unknown, out _);

    private static T Word<T>(JsonNode? node, string[] words, T fallback, string path, List<string> unknown, out bool bad) where T : struct, Enum
    {
        bad = false;
        if (node == null) return fallback;
        string word = Str(node)?.ToLowerInvariant() ?? string.Empty;
        int i = Array.IndexOf(words, word);
        if (i >= 0) return (T)Enum.ToObject(typeof(T), i);
        bad = true;
        unknown.Add($"{path}: {word}");
        return fallback;
    }

    // net6 has no DeepClone: a node belongs to one parent, so copies go through text.
    private static JsonNode? Copy(JsonNode? node) => node == null ? null : JsonNode.Parse(node.ToJsonString());

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue(out double d) && d == Math.Floor(d) ? (int)d : null;

    private static double? Num(JsonNode? node) => node is JsonValue v && v.TryGetValue(out double d) ? d : null;

    private static bool? Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue(out bool b) ? b : null;
}
