using System.Reflection;
using HarmonyLib;

namespace EpochAssist.Core;

/// <summary>A game member a feature relies on: a type and a member name (a method, field or property).</summary>
internal readonly record struct Requirement(Type Type, string Member);

/// <summary>
/// Checks, before a feature starts, that the game still has everything it uses: its declared requirements and
/// every method its Harmony patches target. After a game update a feature that lost something is marked
/// unavailable instead of failing mid-play.
/// </summary>
internal static class Compat
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public static List<string> Missing(Feature feature)
    {
        var missing = new List<string>();

        Requirement[] requirements;
        try
        {
            requirements = feature.Requirements;
        }
        catch (Exception e)
        {
            // A type the feature names no longer exists in the game's assemblies.
            missing.Add($"game type ({e.GetType().Name}: {e.Message})");
            return missing;
        }
        foreach (var req in requirements)
        {
            if (req.Type.GetMember(req.Member, All).Length == 0) missing.Add($"{req.Type.Name}.{req.Member}");
        }

        foreach (var patchClass in feature.PatchClasses)
        {
            try
            {
                var target = HarmonyMethod.Merge(HarmonyMethodExtensions.GetFromType(patchClass));
                // Classes that pick their target in code (TargetMethod) are checked when patched.
                if (target.declaringType == null || target.methodName == null) continue;
                if (AccessTools.Method(target.declaringType, target.methodName, target.argumentTypes) == null)
                    missing.Add($"{target.declaringType.Name}.{target.methodName} (patch {patchClass.Name})");
            }
            catch (Exception e)
            {
                missing.Add($"patch {patchClass.Name} ({e.GetType().Name})");
            }
        }
        return missing;
    }
}
