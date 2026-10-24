using System.Reflection;
using HarmonyLib;

namespace EpochAssist.Core;

/// <summary>
/// A game member a feature relies on: a type and a member name (a method, field or property). Members in a named
/// group are optional: if one is missing, only that group is off and the feature runs without it (see Feature.Has).
/// </summary>
internal readonly record struct Requirement(Type Type, string Member, string? Group = null);

/// <summary>Something the game lacks; Group is null for a required member, which makes the feature unavailable.</summary>
internal readonly record struct MissingMember(string Text, string? Group);

/// <summary>
/// Checks, before a feature starts, that the game still has everything it uses: its declared requirements and
/// every method its Harmony patches target. After a game update a feature that lost something is marked
/// unavailable instead of failing mid-play.
/// </summary>
internal static class Compat
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public static List<MissingMember> Missing(Feature feature)
    {
        var missing = new List<MissingMember>();

        Requirement[] requirements;
        try
        {
            requirements = feature.Requirements;
        }
        catch (Exception e)
        {
            // A type the feature names no longer exists in the game's assemblies.
            missing.Add(new MissingMember($"game type ({e.GetType().Name}: {e.Message})", null));
            return missing;
        }
        foreach (var req in requirements)
        {
            if (req.Type.GetMember(req.Member, All).Length == 0) missing.Add(new MissingMember($"{req.Type.Name}.{req.Member}", req.Group));
        }

        foreach (var target in PatchTargets(feature))
        {
            if (target.Error != null) missing.Add(new MissingMember($"patch {target.Patch.Name} ({target.Error})", target.Group));
            else if (target.Matches.Count == 0) missing.Add(new MissingMember($"{target.Name} (patch {target.Patch.Name})", target.Group));
            // An overloaded name would bind to whichever overload Harmony picks, so it counts as missing too.
            else if (target.Matches.Count > 1) missing.Add(new MissingMember($"{target.Name} (patch {target.Patch.Name}, ambiguous: {target.Matches.Count} methods)", target.Group));
        }
        return missing;
    }

    /// <summary>
    /// Every method a feature's patch classes name, with the methods that fit: without argument types every method of
    /// that name, declared or inherited; with them the exact match. Classes that pick their target in code
    /// (TargetMethod) are checked when patched, so they aren't listed.
    /// </summary>
    public static List<PatchTarget> PatchTargets(Feature feature)
    {
        var targets = new List<PatchTarget>();
        foreach (var patchClass in feature.PatchClasses)
        {
            feature.PatchGroups.TryGetValue(patchClass, out string? group);
            try
            {
                var target = HarmonyMethod.Merge(HarmonyMethodExtensions.GetFromType(patchClass));
                if (target.declaringType == null || target.methodName == null) continue;
                var type = target.declaringType;
                string name = target.methodName;
                var matches = target.argumentTypes == null
                    ? type.GetMethods(All).Where(m => m.Name == name).ToList()
                    : type.GetMethod(name, All, null, target.argumentTypes, null) is { } exact ? new List<MethodInfo> { exact } : new List<MethodInfo>();
                targets.Add(new PatchTarget(group, patchClass, $"{type.Name}.{name}", matches, null));
            }
            catch (Exception e)
            {
                targets.Add(new PatchTarget(group, patchClass, patchClass.Name, Array.Empty<MethodInfo>(), e.GetType().Name));
            }
        }
        return targets;
    }
}

/// <summary>A patch class's target: Name is "Type.Method"; Error is why the attribute couldn't be read, or null.</summary>
internal sealed record PatchTarget(string? Group, Type Patch, string Name, IReadOnlyList<MethodInfo> Matches, string? Error);
