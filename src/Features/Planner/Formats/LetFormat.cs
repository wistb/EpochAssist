using System.Text.Json.Nodes;

namespace EpochAssist.Features.PlannerFormats;

/// <summary>
/// Last Epoch Tools' build data, fetched from a link or pasted from the player's browser: one stage, points per node
/// with an optional order, and skill trees by slot. Its tree and node ids are the game's own.
/// </summary>
internal static class LetFormat
{
    public const string Source = "let";
    public const string DefaultName = "Last Epoch Tools build";

    /// <summary>A build from the build data; link is the planner link it came from, or null for a paste with none.</summary>
    public static Parsed Parse(string json, string? link, string? code, DateTime now)
    {
        if (MaxrollFormat.Parse(json) is not JsonObject root) return Parsed.Fail(Parsed.NotABuild);
        // A missing build still answers 200, with a code and an error.
        if (root.ContainsKey("error") && root["data"] == null) return Parsed.Fail(root.ContainsKey("code") ? Parsed.NotFound : Parsed.NotABuild);
        if (root["data"] is not JsonObject data || !(data.ContainsKey("bio") || data.ContainsKey("charTree"))) return Parsed.Fail(Parsed.NotABuild);

        string name = FirstText(root, "name", "title") ?? FirstText(data, "name", "title") ?? DefaultName;
        var bio = data["bio"] as JsonObject;
        var stage = new Stage
        {
            Name = name,
            ClassId = Build.Int(bio?["characterClass"]) ?? Build.Int(root["class"]) ?? 0,
            Mastery = Build.Int(bio?["chosenMastery"]) ?? Build.Int(root["mastery"]) ?? 0,
            Passive = ReadTree(string.Empty, (data["charTree"] as JsonObject)?["selected"] as JsonObject, data["charTreeProgression"] as JsonArray),
        };

        var progression = data["skillTreesProgression"] as JsonObject;
        if (data["skillTrees"] is JsonArray skillTrees)
        {
            foreach (var node in skillTrees)
            {
                if (node is not JsonObject tree || Build.Str(tree["treeID"]) is not string treeId || treeId.Length == 0) continue;
                stage.Skills.Add(new PlannedSkill
                {
                    AbilityId = treeId,
                    Slot = Build.Int(tree["slotNumber"]) ?? stage.Skills.Count,
                    Tree = ReadTree(treeId, tree["selected"] as JsonObject, progression?[treeId] as JsonArray),
                });
            }
            stage.Skills.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        }

        if (data["hud"] is JsonArray hud)
            for (int i = 0; i < Stage.BarSlots && i < hud.Count; i++)
                stage.Bar[i] = Build.Str(hud[i]) is string id && id.Length > 0 ? id : null;

        bool linked = link != null && code != null;
        var build = new Build
        {
            Id = linked ? $"{Source}-{code}" : $"{Source}-paste-{now:yyyyMMddHHmmss}",
            Source = Source,
            Url = linked ? link : null,
            PlannerId = linked ? code : null,
            Name = name,
            Author = FirstText(root, "author", "username") ?? FirstText(data, "author"),
            ImportedAt = now,
            GameVersion = Build.Str(root["created_for_build"]),
            Raw = json,
        };
        build.Stages.Add(stage);
        return Parsed.Ok(build);
    }

    // Points from the selected map (zero entries skipped); the order from the progression list, one point per entry,
    // or none when the list is missing or empty.
    private static PlannedTree ReadTree(string treeId, JsonObject? selected, JsonArray? progression)
    {
        PlannedTree tree;
        if (progression is { Count: > 0 })
        {
            var steps = new List<(int, int)>(progression.Count);
            foreach (var entry in progression)
                if (Build.Int(entry) is int node) steps.Add((node, 1));
            tree = PlannedTree.FromSteps(treeId, steps);
            tree.Points.Clear();
        }
        else
        {
            tree = new PlannedTree { TreeId = treeId };
        }
        if (selected != null)
            foreach (var (key, value) in selected)
                if (byte.TryParse(key, out byte node) && Build.Int(value) is int points and > 0) tree.Points[node] = (byte)Math.Min(byte.MaxValue, points);
        return tree;
    }

    private static string? FirstText(JsonObject o, params string[] keys)
    {
        foreach (string key in keys)
            if (Build.Str(o[key]) is string s && s.Trim().Length > 0) return s.Trim();
        return null;
    }
}
