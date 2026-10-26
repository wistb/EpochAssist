namespace EpochAssist.Core;

/// <summary>
/// Whether each feature the player can turn off is on: one Toggle per such feature in a shared group (the same in every
/// profile), read by the host before it starts the feature. Off means not started, so no patches and no cost.
/// </summary>
internal static class FeatureSwitches
{
    public static readonly SettingsGroup Group = new("EpochAssist_Features", "Features", version: 1) { ActionKey = "features", Shared = true };
    private static readonly Section Section = Group.Section("features", "Features");
    private static readonly Dictionary<string, Toggle> s_toggles = new();

    /// <summary>Declares a toggle for every feature that can be turned off, then registers the group. Once per load.</summary>
    public static void Declare(IEnumerable<Feature> features)
    {
        if (s_toggles.Count > 0) return;
        foreach (var feature in features.Where(f => f.CanTurnOff)) s_toggles[feature.Id] = new Toggle(Section, feature.Id, feature.Name, true);
        Group.Register();
    }

    /// <summary>On, or a feature that can't be turned off.</summary>
    public static bool IsOn(Feature feature) => !s_toggles.TryGetValue(feature.Id, out var toggle) || toggle.Value;

    public static Toggle? For(Feature feature) => s_toggles.TryGetValue(feature.Id, out var toggle) ? toggle : null;
}
