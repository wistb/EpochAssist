using System.Text.Json;
using System.Text.Json.Nodes;

namespace EpochAssist.Features.PlannerFormats;

/// <summary>
/// Maxroll's planner data: the profile a link points at (a wrapper whose data field is JSON in a string, holding one
/// profile per stage) and the Export text (one profile on its own). Trees are a history of node ids, of which only the
/// first position entries count.
/// </summary>
internal static class MaxrollFormat
{
    public const string Source = "maxroll";
    public const string ExportSource = "maxroll-export";
    public const string PageUrl = "https://maxroll.gg/last-epoch/planner/";

    /// <summary>A linked build; profileNumber is the link's 1-based #n, out of range falls back to the planner's active
    /// profile.</summary>
    public static Parsed ParseLink(string json, int? profileNumber, DateTime now)
    {
        if (Parse(json) is not JsonObject outer) return Parsed.Fail(Parsed.NotABuild);
        if (outer.ContainsKey("error")) return Parsed.Fail(Parsed.NotFound);
        // A private profile comes without its data.
        if (Build.Str(outer["data"]) is not string dataText) return Parsed.Fail(outer.ContainsKey("id") ? Parsed.NotFound : Parsed.NotABuild);
        if (Parse(dataText) is not JsonObject data || data["profiles"] is not JsonArray profiles) return Parsed.Fail(Parsed.NotABuild);

        string? id = Build.Str(outer["id"]);
        var build = new Build
        {
            Id = $"{Source}-{id}",
            Source = Source,
            PlannerId = id,
            Url = id != null ? PageUrl + id : null,
            Name = Build.Str(outer["name"]) ?? "Maxroll build",
            Author = (outer["user"] as JsonObject)?["username"] is { } user ? Build.Str(user) : null,
            ImportedAt = now,
            GameVersion = Build.Str(outer["season"]) is string season ? $"Season {season}" : null,
            Raw = json,
        };
        foreach (var node in profiles)
        {
            if (node is not JsonObject profile) continue;
            var stage = ReadProfile(profile, inferSlots: false);
            if (stage.Name.Length == 0) stage.Name = $"Profile {build.Stages.Count + 1}";
            build.Stages.Add(stage);
        }
        if (build.Stages.Count == 0) return Parsed.Fail(Parsed.NotABuild);
        int active = Build.Int(data["activeProfile"]) ?? 0;
        build.FirstStage = profileNumber is int n && n >= 1 && n <= build.Stages.Count ? n - 1 : Math.Clamp(active, 0, build.Stages.Count - 1);
        return Parsed.Ok(build);
    }

    /// <summary>The text of Maxroll's Export button: one profile with no name or specialisation list. Parts left out of
    /// the export are empty trees.</summary>
    public static Parsed ParseExport(string json, DateTime now)
    {
        if (Parse(json) is not JsonObject profile || profile.ContainsKey("profiles") || !(profile.ContainsKey("passives") || profile.ContainsKey("skillTrees")))
            return Parsed.Fail(Parsed.NotABuild);
        string date = now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var stage = ReadProfile(profile, inferSlots: true);
        stage.Name = Build.Str(profile["name"]) ?? "Export";
        var build = new Build
        {
            Id = $"{ExportSource}-{now:yyyyMMddHHmmss}",
            Source = ExportSource,
            Name = $"Maxroll export {date}",
            ImportedAt = now,
            Raw = json,
        };
        build.Stages.Add(stage);
        return Parsed.Ok(build);
    }

    private static Stage ReadProfile(JsonObject profile, bool inferSlots)
    {
        var stage = new Stage
        {
            Name = Build.Str(profile["name"]) ?? string.Empty,
            ClassId = Build.Int(profile["class"]) ?? 0,
            Mastery = Build.Int(profile["mastery"]) ?? 0,
            Passive = ReadTree(string.Empty, profile["passives"] as JsonObject),
        };

        var trees = new List<PlannedTree>();
        if (profile["skillTrees"] is JsonObject skillTrees)
            foreach (var (abilityId, node) in skillTrees) trees.Add(ReadTree(abilityId, node as JsonObject));

        if (!inferSlots && profile["specializedSkills"] is JsonArray specialised)
        {
            // Names until the build is resolved, which also moves the matching tree into the skill.
            for (int i = 0; i < specialised.Count; i++)
                if (Build.Str(specialised[i]) is string name && name.Length > 0) stage.Skills.Add(new PlannedSkill { Name = name, Slot = i });
            stage.OtherTrees = trees;
        }
        else
        {
            foreach (var tree in trees)
            {
                if (tree.Total > 0 && stage.Skills.Count < Stage.BarSlots)
                    stage.Skills.Add(new PlannedSkill { AbilityId = tree.TreeId, Slot = stage.Skills.Count, Tree = tree });
                else
                    stage.OtherTrees.Add(tree);
            }
            stage.SlotsInferred = true;
        }

        if (profile["activeSkills"] is JsonArray bar)
            for (int i = 0; i < Stage.BarSlots && i < bar.Count; i++)
                stage.Bar[i] = Build.Str(bar[i]) is string name && name.Length > 0 ? name : null;
        return stage;
    }

    // {history, position}: a number adds one point to that node, {"<id>": n} adds n.
    private static PlannedTree ReadTree(string treeId, JsonObject? tree)
    {
        if (tree?["history"] is not JsonArray history) return new PlannedTree { TreeId = treeId };
        int count = Math.Clamp(Build.Int(tree["position"]) ?? history.Count, 0, history.Count);
        var steps = new List<(int, int)>(count);
        for (int i = 0; i < count; i++)
        {
            var entry = history[i];
            if (Build.Int(entry) is int node) steps.Add((node, 1));
            else if (entry is JsonObject o)
                foreach (var (key, value) in o)
                    if (int.TryParse(key, out int id) && Build.Int(value) is int add) steps.Add((id, add));
        }
        return PlannedTree.FromSteps(treeId, steps);
    }

    internal static JsonNode? Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
