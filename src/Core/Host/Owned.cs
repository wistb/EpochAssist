namespace EpochAssist.Core;

/// <summary>
/// Every registry a feature can own things in, in the order they are released. Every way out of Running (stop, fault,
/// found unavailable) calls Release once; a new registry adds its line to the list here, and its count in dev builds.
/// </summary>
internal static class Owned
{
    private static readonly (string Name, Action<Feature> Release)[] s_registries =
    {
        ("actions", f => Actions.RemoveOwner(f.Id)),
        ("jobs", Scheduler.RemoveOwner),
        ("edits", Edits.RemoveOwner),
        ("tooltip lines", TooltipLines.RemoveOwner),
        ("bus", Bus.RemoveOwner),
        ("commands", f => Commands.RemoveOwner(f.Id)),
        ("layer hooks", FeatureHost.RemoveLayerHooks),
    };

#if DEV
    // What each registry still holds for a feature, checked after every release.
    private static readonly Dictionary<string, Func<Feature, int>> s_counts = new()
    {
        ["actions"] = f => Actions.CountOwner(f.Id),
        ["jobs"] = f => f.Record.Jobs.Count,
        ["edits"] = f => f.Record.Edits.Count,
        ["tooltip lines"] = TooltipLines.CountOwner,
        ["bus"] = Bus.CountOwner,
        ["commands"] = f => Commands.CountOwner(f.Id),
        ["layer hooks"] = FeatureHost.CountLayerHooks,
    };
#endif

    /// <summary>Removes everything the feature owns in every registry, in a fixed order.</summary>
    public static void Release(Feature feature)
    {
        foreach (var registry in s_registries) registry.Release(feature);
#if DEV
        var left = new List<string>();
        foreach (var registry in s_registries)
        {
            if (!s_counts.TryGetValue(registry.Name, out var count)) left.Add($"{registry.Name} (no count)");
            else if (count(feature) is int n and > 0) left.Add($"{registry.Name} {n}");
        }
        if (left.Count > 0) Log.Error($"{feature.Name} still holds {string.Join(", ", left)} after release");
#endif
    }
}
