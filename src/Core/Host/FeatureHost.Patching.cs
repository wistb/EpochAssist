namespace EpochAssist.Core;

internal static partial class FeatureHost
{
    // One class at a time, so a stale target can't stop the rest from binding.
    private static void Patch(Feature feature)
    {
        feature.Record.PatchFailures.Clear();
        var classes = feature.PatchClasses;
        if (classes.Length == 0) return;

        feature.Record.Harmony ??= new HarmonyLib.Harmony($"EpochAssist.{feature.Id}");
        foreach (var type in classes)
        {
            if (feature.PatchGroups.TryGetValue(type, out string? group) && (!feature.Has(group) || Array.IndexOf(feature.DemandGroups, group) >= 0)) continue;
            PatchClass(feature, type);
        }
    }

    private static bool PatchClass(Feature feature, Type type)
    {
        try
        {
            feature.Record.Harmony!.CreateClassProcessor(type).Patch();
            return true;
        }
        catch (Exception e)
        {
            string message = $"{type.Name}: {e.InnerException?.Message ?? e.Message}";
            feature.Record.PatchFailures.Add(message);
            Log.Error($"{feature.Name} patch failed: {message}");
            return false;
        }
    }

    /// <summary>
    /// Patches or unpatches the classes of one of the feature's DemandGroups on its own Harmony instance, so a patch is
    /// live only while something needs it. Ignored for a group that isn't one, is missing from the game, is already in
    /// that state, or a feature that isn't running. Stop and fault unpatch everything as before.
    /// </summary>
    public static void SetPatchGroup(Feature feature, string group, bool on)
    {
        if (feature.State != FeatureState.Running || Array.IndexOf(feature.DemandGroups, group) < 0 || !feature.Has(group)) return;
        if (feature.Record.DemandGroupsOn.Contains(group) == on) return;
        var classes = feature.PatchClasses.Where(t => feature.PatchGroups.TryGetValue(t, out string? g) && g == group).ToList();
        if (on)
        {
            feature.Record.Harmony ??= new HarmonyLib.Harmony($"EpochAssist.{feature.Id}");
            feature.Record.DemandGroupsOn.Add(group);
            foreach (var type in classes) PatchClass(feature, type);
            ModOverlap.CheckMethod(feature, group);
            return;
        }
        feature.Record.DemandGroupsOn.Remove(group);
        var harmony = feature.Record.Harmony;
        if (harmony == null) return;
        foreach (var target in Compat.PatchTargets(feature))
        {
            if (target.Group != group) continue;
            foreach (var original in target.Matches)
            {
                try
                {
                    harmony.Unpatch(original, HarmonyLib.HarmonyPatchType.All, harmony.Id);
                }
                catch (Exception e)
                {
                    Log.Error($"{feature.Name} unpatch of {target.Name} failed: {e.Message}");
                }
            }
        }
    }

    private static void Unpatch(Feature feature)
    {
        feature.Record.DemandGroupsOn.Clear();
        try
        {
            feature.Record.Harmony?.UnpatchSelf();
        }
        catch (Exception e)
        {
            Log.Error($"{feature.Name} unpatch failed: {e.Message}");
        }
    }
}
