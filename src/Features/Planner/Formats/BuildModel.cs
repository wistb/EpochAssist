using System.Text.Json;
using System.Text.Json.Nodes;

namespace EpochAssist.Features.PlannerFormats;

/// <summary>One planner build in one shape whatever its source: stages of trees, skills and bar, plus the planner's
/// own data as received, kept for later use (items, idols, blessings, weaver).</summary>
internal sealed class Build
{
    public const int FileVersion = 1;

    public string Id = string.Empty;
    /// <summary>maxroll, maxroll-export or let.</summary>
    public string Source = string.Empty;
    /// <summary>The planner page, or null when the build came without one.</summary>
    public string? Url;
    public string? PlannerId;
    public string Name = string.Empty;
    public string? Author;
    public DateTime ImportedAt;
    public string? GameVersion;
    public List<Stage> Stages = new();
    public int FirstStage;
    public string Raw = string.Empty;

    /// <summary>The stored form: the build's fields, the planner's data as received, and the parsed model.</summary>
    public JsonObject ToJson()
    {
        JsonNode? raw;
        try
        {
            raw = JsonNode.Parse(Raw);
        }
        catch (JsonException)
        {
            raw = Raw;
        }
        var stages = new JsonArray();
        foreach (var stage in Stages) stages.Add(stage.ToJson());
        return new JsonObject
        {
            ["version"] = FileVersion,
            ["source"] = Source,
            ["url"] = Url,
            ["plannerId"] = PlannerId,
            ["name"] = Name,
            ["author"] = Author,
            ["importedAt"] = ImportedAt.ToUniversalTime().ToString("o"),
            ["gameVersion"] = GameVersion,
            ["raw"] = raw,
            ["model"] = new JsonObject { ["firstStage"] = FirstStage, ["stages"] = stages },
        };
    }

    /// <summary>A build from its stored form, or null when the file isn't one this version reads.</summary>
    public static Build? FromJson(string id, JsonObject o)
    {
        if (Int(o["version"]) != FileVersion || o["model"] is not JsonObject model || model["stages"] is not JsonArray stages) return null;
        var build = new Build
        {
            Id = id,
            Source = Str(o["source"]) ?? string.Empty,
            Url = Str(o["url"]),
            PlannerId = Str(o["plannerId"]),
            Name = Str(o["name"]) ?? id,
            Author = Str(o["author"]),
            ImportedAt = DateTime.TryParse(Str(o["importedAt"]), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : DateTime.MinValue,
            GameVersion = Str(o["gameVersion"]),
            FirstStage = Int(model["firstStage"]) ?? 0,
            Raw = o["raw"] is JsonValue v && v.TryGetValue(out string? text) ? text : o["raw"]?.ToJsonString() ?? string.Empty,
        };
        foreach (var node in stages)
            if (node is JsonObject s) build.Stages.Add(Stage.FromJson(s));
        if (build.Stages.Count == 0) return null;
        build.FirstStage = Math.Clamp(build.FirstStage, 0, build.Stages.Count - 1);
        build.FillLevels(stages);
        return build;
    }

    // Builds stored before stages kept a level take it from the planner's data; nothing is written back, so the file
    // stays as it is until the next save.
    private void FillLevels(JsonArray stored)
    {
        bool missing = false;
        foreach (var node in stored) missing |= node is JsonObject s && !s.ContainsKey("level");
        if (!missing || MaxrollFormat.Parse(Raw) is not JsonObject root) return;
        var levels = new List<int>();
        if (Source == MaxrollFormat.Source)
        {
            if (Str(root["data"]) is string dataText && MaxrollFormat.Parse(dataText) is JsonObject data && data["profiles"] is JsonArray profiles)
                foreach (var node in profiles)
                    if (node is JsonObject profile) levels.Add(Stage.LevelOf(profile["level"]));
        }
        else if (Source == LetFormat.Source)
        {
            levels.Add(Stage.LevelOf(((root["data"] as JsonObject)?["bio"] as JsonObject)?["level"]));
        }
        for (int i = 0, s = 0; i < stored.Count && s < Stages.Count; i++)
        {
            if (stored[i] is not JsonObject o) continue;
            if (!o.ContainsKey("level") && s < levels.Count) Stages[s].Level = levels[s];
            s++;
        }
    }

    internal static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    internal static int? Int(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue(out int i)) return i;
        // A node built in memory keeps the type it was given.
        if (v.TryGetValue(out byte b)) return b;
        if (v.TryGetValue(out long l) && l is >= int.MinValue and <= int.MaxValue) return (int)l;
        if (v.TryGetValue(out double d) && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue) return (int)d;
        if (v.TryGetValue(out string? s) && int.TryParse(s, out i)) return i;
        return null;
    }
}

/// <summary>One step of a build: a planner profile on Maxroll, the whole build on Last Epoch Tools.</summary>
internal sealed class Stage
{
    public const int BarSlots = 5;

    public string Name = string.Empty;
    public int ClassId;
    /// <summary>The planner's mastery number; 0 is none.</summary>
    public int Mastery;
    /// <summary>Class and mastery nodes in one tree, as the game holds them.</summary>
    public PlannedTree Passive = new();
    /// <summary>The specialised skills, in slot order.</summary>
    public List<PlannedSkill> Skills = new();
    /// <summary>Trees the planner has for skills it doesn't specialise: kept, not guided.</summary>
    public List<PlannedTree> OtherTrees = new();
    /// <summary>Per bar slot, the ability's id, or a skill name until it's resolved; null is no plan for that slot.</summary>
    public string?[] Bar = new string?[BarSlots];
    /// <summary>A Maxroll export has no specialisation list, so its skills are the trees with points.</summary>
    public bool SlotsInferred;
    /// <summary>The character level the planner gives the stage; 0 = none.</summary>
    public int Level;

    /// <summary>A planner's level value, or 0 when it isn't a level from 1 to 255.</summary>
    public static int LevelOf(JsonNode? node) => Build.Int(node) is int level and >= 1 and <= 255 ? level : 0;

    public JsonObject ToJson()
    {
        var skills = new JsonArray();
        foreach (var skill in Skills)
            skills.Add(new JsonObject { ["abilityId"] = skill.AbilityId, ["name"] = skill.Name, ["slot"] = skill.Slot, ["tree"] = skill.Tree.ToJson() });
        var others = new JsonArray();
        foreach (var tree in OtherTrees) others.Add(tree.ToJson());
        var bar = new JsonArray();
        foreach (string? id in Bar) bar.Add(id);
        return new JsonObject
        {
            ["name"] = Name,
            ["classId"] = ClassId,
            ["mastery"] = Mastery,
            ["passive"] = Passive.ToJson(),
            ["skills"] = skills,
            ["otherTrees"] = others,
            ["bar"] = bar,
            ["slotsInferred"] = SlotsInferred,
            ["level"] = Level,
        };
    }

    public static Stage FromJson(JsonObject o)
    {
        var stage = new Stage
        {
            Name = Build.Str(o["name"]) ?? string.Empty,
            ClassId = Build.Int(o["classId"]) ?? 0,
            Mastery = Build.Int(o["mastery"]) ?? 0,
            Passive = o["passive"] is JsonObject p ? PlannedTree.FromJson(p) : new PlannedTree(),
            SlotsInferred = o["slotsInferred"] is JsonValue v && v.TryGetValue(out bool b) && b,
            Level = LevelOf(o["level"]),
        };
        if (o["skills"] is JsonArray skills)
        {
            foreach (var node in skills)
            {
                if (node is not JsonObject s) continue;
                stage.Skills.Add(new PlannedSkill
                {
                    AbilityId = Build.Str(s["abilityId"]) ?? string.Empty,
                    Name = Build.Str(s["name"]) ?? string.Empty,
                    Slot = Build.Int(s["slot"]) ?? stage.Skills.Count,
                    Tree = s["tree"] is JsonObject t ? PlannedTree.FromJson(t) : new PlannedTree(),
                });
            }
        }
        if (o["otherTrees"] is JsonArray others)
            foreach (var node in others)
                if (node is JsonObject t) stage.OtherTrees.Add(PlannedTree.FromJson(t));
        if (o["bar"] is JsonArray bar)
            for (int i = 0; i < BarSlots && i < bar.Count; i++) stage.Bar[i] = Build.Str(bar[i]);
        return stage;
    }
}

/// <summary>One step of an allocation order: a node and the points it adds.</summary>
internal readonly record struct Step(byte Node, byte Add);

/// <summary>Planned points per node of one tree, and the order to take them in when the planner gives one.</summary>
internal sealed class PlannedTree
{
    /// <summary>The passive tree: the class's tree id once resolved; a skill tree: the ability's id.</summary>
    public string TreeId = string.Empty;
    public SortedDictionary<byte, byte> Points = new();
    /// <summary>Null when the source gives no order.</summary>
    public List<Step>? Order;

    public int Total
    {
        get
        {
            int sum = 0;
            foreach (byte p in Points.Values) sum += p;
            return sum;
        }
    }

    /// <summary>A tree from steps in order: points are the sums, consecutive steps on one node are merged. Ids outside a
    /// byte are dropped, since no game node has one.</summary>
    public static PlannedTree FromSteps(string treeId, IEnumerable<(int Node, int Add)> steps)
    {
        var tree = new PlannedTree { TreeId = treeId, Order = new List<Step>() };
        foreach (var (node, add) in steps)
        {
            if (node is < 0 or > byte.MaxValue || add <= 0) continue;
            byte id = (byte)node;
            tree.Points[id] = (byte)Math.Min(byte.MaxValue, (tree.Points.TryGetValue(id, out byte had) ? had : 0) + add);
            var order = tree.Order;
            if (order.Count > 0 && order[^1].Node == id) order[^1] = new Step(id, (byte)Math.Min(byte.MaxValue, order[^1].Add + add));
            else order.Add(new Step(id, (byte)Math.Min(byte.MaxValue, add)));
        }
        return tree;
    }

    public JsonObject ToJson()
    {
        var points = new JsonObject();
        foreach (var (node, p) in Points) points[node.ToString()] = p;
        JsonArray? order = null;
        if (Order != null)
        {
            order = new JsonArray();
            foreach (var step in Order) order.Add(new JsonArray(step.Node, step.Add));
        }
        return new JsonObject { ["treeId"] = TreeId, ["points"] = points, ["order"] = order };
    }

    public static PlannedTree FromJson(JsonObject o)
    {
        var tree = new PlannedTree { TreeId = Build.Str(o["treeId"]) ?? string.Empty };
        if (o["points"] is JsonObject points)
            foreach (var (key, value) in points)
                if (byte.TryParse(key, out byte node) && Build.Int(value) is int p and > 0) tree.Points[node] = (byte)Math.Min(byte.MaxValue, p);
        if (o["order"] is JsonArray order)
        {
            tree.Order = new List<Step>();
            foreach (var node in order)
                if (node is JsonArray pair && pair.Count == 2 && Build.Int(pair[0]) is int n and >= 0 and <= byte.MaxValue && Build.Int(pair[1]) is int add and > 0)
                    tree.Order.Add(new Step((byte)n, (byte)Math.Min(byte.MaxValue, add)));
        }
        return tree;
    }
}

/// <summary>A specialised skill in a stage. Maxroll gives the skill's name; the id is filled when the build is
/// resolved against the game's data.</summary>
internal sealed class PlannedSkill
{
    public string AbilityId = string.Empty;
    public string Name = string.Empty;
    public int Slot;
    public PlannedTree Tree = new();
}

/// <summary>A parser's answer: a build, or one error word.</summary>
internal readonly record struct Parsed(Build? Build, string? Error)
{
    public const string NotFound = "not found";
    public const string NotABuild = "not a planner build";

    public static Parsed Ok(Build build) => new(build, null);
    public static Parsed Fail(string error) => new(null, error);
}
