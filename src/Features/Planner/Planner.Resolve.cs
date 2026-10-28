using EpochAssist.Core;
using EpochAssist.Features.PlannerFormats;
using Il2Cpp;

namespace EpochAssist.Features;

/// <summary>A build's ids checked against this game version: what doesn't exist, and nodes planned past their maximum.
/// Unknown ids stay in the build and are skipped by marks, next steps and counts.</summary>
internal sealed class BuildResolution
{
    public readonly List<(string TreeId, byte Node)> UnknownNodes = new();
    public readonly List<string> UnknownSkills = new();
    public readonly List<(string TreeId, byte Node, byte Planned, byte Max)> CappedNodes = new();
    /// <summary>Set when the game's tree or ability data couldn't be read, so nothing could be checked.</summary>
    public bool Unread;

    public bool Empty => UnknownNodes.Count == 0 && UnknownSkills.Count == 0 && CappedNodes.Count == 0;
}

internal static partial class Planner
{
    // Tree data and abilities only change with a game update, which restarts the game.
    private static readonly Dictionary<string, BuildResolution> s_resolutions = new();

    /// <summary>The build checked against the game's tree and ability data, cached per build. Fills in the passive tree
    /// id from the class, and ability ids for Maxroll's skill names (moving the matching tree into the skill).</summary>
    public static BuildResolution Resolve(Build build)
    {
        if (s_resolutions.TryGetValue(build.Id, out var cached) && !cached.Unread) return cached;
        var result = new BuildResolution();
        try
        {
            var trees = GlobalTreeData.get();
            if (!Game.Alive(trees) || !AbilityManager.Available)
            {
                result.Unread = true;
                return result;
            }
            foreach (var stage in build.Stages) ResolveStage(stage, trees!, result);
        }
        catch (Exception e)
        {
            result.Unread = true;
            Log.ErrorThrottled("planner:resolve", $"Planner couldn't check a build against the game data: {e.Message}", 30);
        }
        s_resolutions[build.Id] = result;
        return result;
    }

    /// <summary>Drops a build's cached check, after it's replaced or deleted.</summary>
    public static void ForgetResolution(string buildId) => s_resolutions.Remove(buildId);

    private static void ResolveStage(Stage stage, GlobalTreeData trees, BuildResolution result)
    {
        var cls = CharacterClassList.getClass(stage.ClassId);
        if (stage.Passive.TreeId.Length == 0 && Game.Alive(cls)) stage.Passive.TreeId = cls!.treeID ?? string.Empty;
        CheckTree(stage.Passive, trees, result);

        foreach (var skill in stage.Skills)
        {
            if (skill.AbilityId.Length == 0 && FindAbility(skill.Name, cls) is { } id)
            {
                skill.AbilityId = id;
                int at = stage.OtherTrees.FindIndex(t => t.TreeId == id);
                if (at >= 0)
                {
                    skill.Tree = stage.OtherTrees[at];
                    stage.OtherTrees.RemoveAt(at);
                }
                else if (skill.Tree.TreeId.Length == 0)
                {
                    skill.Tree.TreeId = id;
                }
            }
            if (!IsAbilityId(skill.AbilityId))
            {
                AddOnce(result.UnknownSkills, skill.AbilityId.Length > 0 ? skill.AbilityId : skill.Name);
                continue;
            }
            CheckTree(skill.Tree, trees, result);
        }

        for (int i = 0; i < stage.Bar.Length; i++)
        {
            if (stage.Bar[i] is not string entry || IsAbilityId(entry)) continue;
            if (FindAbility(entry, cls) is { } id) stage.Bar[i] = id;
            else AddOnce(result.UnknownSkills, entry);
        }
    }

    // The lookup answers an id it doesn't have with some other ability, so the answer's own id is compared.
    private static bool IsAbilityId(string id)
    {
        if (id.Length == 0) return false;
        var ability = AbilityManager.getPlayerAbility(id);
        return Game.Alive(ability) && ability!.playerAbilityID == id;
    }

    private static void CheckTree(PlannedTree tree, GlobalTreeData trees, BuildResolution result)
    {
        if (tree.Points.Count == 0) return;
        var data = tree.TreeId.Length > 0 ? trees.getTreeData(tree.TreeId) : null;
        foreach (var (node, planned) in tree.Points)
        {
            var global = data?.getNode(node);
            if (global == null) result.UnknownNodes.Add((tree.TreeId, node));
            else if (global.maxPoints < planned) result.CappedNodes.Add((tree.TreeId, node, planned, global.maxPoints));
        }
    }

    // Maxroll names skills by the ability's display name or its Unity object name; the class's own abilities first,
    // then every player ability.
    internal static string? FindAbility(string name, CharacterClass? cls)
    {
        if (name.Length == 0) return null;
        if (Game.Alive(cls) && Match(cls!.getAllAbilities(), name) is { } own) return own;
        var manager = AbilityManager.instance;
        return Game.Alive(manager) ? Match(manager!.playerAbilities, name) : null;
    }

    private static string? Match(Il2CppSystem.Collections.Generic.List<Ability>? abilities, string name)
    {
        for (int i = 0; abilities != null && i < abilities.Count; i++)
        {
            var ability = abilities[i];
            if (!Game.Alive(ability) || string.IsNullOrEmpty(ability!.playerAbilityID)) continue;
            if (Same(ability.name, name) || Same(ability.abilityName, name)) return ability.playerAbilityID;
        }
        return null;
    }

    private static bool Same(string? a, string b) =>
        a != null && string.Equals(a.Replace(" ", string.Empty), b.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase);

    private static void AddOnce(List<string> list, string value)
    {
        if (!list.Contains(value)) list.Add(value);
    }
}
