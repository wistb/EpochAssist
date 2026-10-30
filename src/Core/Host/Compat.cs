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
/// A part of a model feature (player, world, data) that a feature reads. With a Group it is optional, like a member in a
/// named group; without one the feature is unavailable while the part is missing.
/// </summary>
internal readonly record struct PartNeed(string Model, string Part, string? Group = null);

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

        foreach (var need in feature.Parts)
        {
            if (Array.IndexOf(feature.Needs, need.Model) < 0 || PartMissing(need)) missing.Add(new MissingMember($"{need.Model}: {need.Part} (model part)", need.Group));
        }
#if DEV
        if (Unavailable().Contains(feature.Id)) missing.Add(new MissingMember("DevSimulated.member (simulated)", null));
        foreach (string entry in Simulated)
        {
            int colon = entry.IndexOf(':');
            if (colon < 0 || entry[..colon] != feature.Id) continue;
            string group = entry[(colon + 1)..];
            if (DeclaresGroup(feature, group)) missing.Add(new MissingMember($"{group} (simulated)", group));
        }
#endif

        foreach (var target in PatchTargets(feature))
        {
            if (target.Error != null) missing.Add(new MissingMember($"patch {target.Patch.Name} ({target.Error})", target.Group));
            else if (target.Matches.Count == 0) missing.Add(new MissingMember($"{target.Name} (patch {target.Patch.Name})", target.Group));
            // An overloaded name would bind to whichever overload Harmony picks, so it counts as missing too.
            else if (target.Matches.Count > 1) missing.Add(new MissingMember($"{target.Name} (patch {target.Patch.Name}, ambiguous: {target.Matches.Count} methods)", target.Group));
        }
        return missing;
    }

#if DEV
    /// <summary>Dev only: "feature id:group" entries reported as missing, to show what a lost model part does.</summary>
    internal static readonly HashSet<string> Simulated = new();

    /// <summary>Dev only: the process variable listing feature ids (comma-separated) that miss a required member at their
    /// next start, as after a game update. It outlives dev reloads, never the game.</summary>
    internal const string UnavailableVariable = "EPOCHASSIST_DEV_UNAVAILABLE";

    internal static HashSet<string> Unavailable() =>
        (Environment.GetEnvironmentVariable(UnavailableVariable) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
#endif

    /// <summary>
    /// A model's part counts as missing when the model lacks every group of it: "model: part", or both "model: part
    /// (offline)" and "model: part (online)" when its source differs by mode. A model that isn't registered, or doesn't
    /// declare the part, lacks it too. The model's missing groups are those of its last start.
    /// </summary>
    public static bool PartMissing(PartNeed need)
    {
        var model = FeatureHost.Find(need.Model);
        if (model == null) return true;
        var groups = PartGroups(model, need.Part);
        return groups.Count == 0 || groups.All(g => model.MissingGroups.Contains(g));
    }

    /// <summary>The requirement groups a model declares for one part, in either form.</summary>
    public static List<string> PartGroups(Feature model, string part)
    {
        string plain = $"{model.Id}: {part}";
        string offline = plain + " (offline)", online = plain + " (online)";
        var groups = new List<string>();
        foreach (string? group in DeclaredGroups(model))
        {
            if ((group == plain || group == offline || group == online) && !groups.Contains(group)) groups.Add(group);
        }
        return groups;
    }

    /// <summary>
    /// For a model's own reads: the group of a part (from PartGroups) that is missing in the current mode, or null when
    /// the part is there. A part split by mode uses its "(online)" or "(offline)" group.
    /// </summary>
    public static string? MissingPartGroup(Feature model, List<string> groups, bool online)
    {
        if (groups.Count == 0) return $"{model.Id}: (undeclared part)";
        string current = groups[0];
        if (groups.Count > 1)
        {
            string suffix = online ? "(online)" : "(offline)";
            foreach (string group in groups)
            {
                if (group.EndsWith(suffix, StringComparison.Ordinal)) current = group;
            }
        }
        return model.Has(current) ? null : current;
    }

    /// <summary>True when a feature declares the optional requirement group.</summary>
    public static bool DeclaresGroup(Feature feature, string group) => DeclaredGroups(feature).Contains(group);

    private static IEnumerable<string?> DeclaredGroups(Feature feature)
    {
        Requirement[] requirements;
        try
        {
            requirements = feature.Requirements;
        }
        catch (Exception)
        {
            return Array.Empty<string?>();
        }
        return requirements.Select(r => r.Group).Concat(feature.PatchGroups.Values).Where(g => g != null).Distinct();
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
