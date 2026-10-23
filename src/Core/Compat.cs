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

        foreach (var patchClass in feature.PatchClasses)
        {
            feature.PatchGroups.TryGetValue(patchClass, out string? group);
            try
            {
                var target = HarmonyMethod.Merge(HarmonyMethodExtensions.GetFromType(patchClass));
                // Classes that pick their target in code (TargetMethod) are checked when patched.
                if (target.declaringType == null || target.methodName == null) continue;
                if (AccessTools.Method(target.declaringType, target.methodName, target.argumentTypes) == null)
                    missing.Add(new MissingMember($"{target.declaringType.Name}.{target.methodName} (patch {patchClass.Name})", group));
            }
            catch (Exception e)
            {
                missing.Add(new MissingMember($"patch {patchClass.Name} ({e.GetType().Name})", group));
            }
        }
        return missing;
    }
}
