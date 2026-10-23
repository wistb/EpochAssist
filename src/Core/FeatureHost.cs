using UnityEngine;

namespace EpochAssist.Core;

// Runs every feature: start, per-frame hooks, stop, with each feature's errors and patches kept apart.
internal static class FeatureHost
{
    private const int MaxErrors = 5;
    private const float ErrorWindowSeconds = 10f;

    private static readonly List<Feature> s_features = new();

    public static IReadOnlyList<Feature> All => s_features;

    public static T? Get<T>() where T : Feature => s_features.OfType<T>().FirstOrDefault();

    /// <summary>Update order is registration order.</summary>
    public static void Register(params Feature[] features) => s_features.AddRange(features);

    public static void StartAll()
    {
        foreach (var feature in s_features) Start(feature);
    }

    public static void StopAll()
    {
        for (int i = s_features.Count - 1; i >= 0; i--) Stop(s_features[i]);
    }

    public static void Start(Feature feature)
    {
        if (feature.State == FeatureState.Running) return;
        feature.LastError = null;
        feature.RecentErrors.Clear();
        try
        {
            if (!feature.SettingsRegistered)
            {
                feature.RegisterSettings();
                feature.SettingsRegistered = true;
            }
            var missing = Compat.Missing(feature);
            var required = missing.Where(m => m.Group == null).Select(m => m.Text).ToList();
            if (required.Count > 0)
            {
                feature.State = FeatureState.Unavailable;
                feature.LastError = "the game no longer has " + string.Join(", ", required.Take(3)) + (required.Count > 3 ? $" and {required.Count - 3} more" : string.Empty);
                Log.Warn($"{feature.Name} unavailable: {string.Join(", ", required)}");
                return;
            }
            feature.MissingGroups.Clear();
            foreach (var group in missing.GroupBy(m => m.Group!))
            {
                feature.MissingGroups.Add(group.Key);
                Log.Warn($"{feature.Name} runs without '{group.Key}': the game no longer has {string.Join(", ", group.Select(m => m.Text))}");
            }
            Patch(feature);
            feature.Start();
            feature.State = FeatureState.Running;
        }
        catch (Exception e)
        {
            Fault(feature, "start", e);
        }
    }

    public static void Stop(Feature feature)
    {
        if (feature.State is FeatureState.Stopped or FeatureState.Unavailable) return;
        bool wasRunning = feature.State == FeatureState.Running;
        feature.State = FeatureState.Stopped;
        if (wasRunning) ShutdownQuietly(feature);
        Unpatch(feature);
    }

    public static void Restart(Feature feature)
    {
        Stop(feature);
        Start(feature);
        Log.Info($"{feature.Name} restarted");
    }

    public static void UpdateAll()
    {
        foreach (var feature in s_features)
        {
            if (feature.State == FeatureState.Running) Guard(feature, "update", feature.Update);
        }
    }

    public static void GuiAll()
    {
        for (var layer = GuiLayer.World; layer <= GuiLayer.Menu; layer++)
        {
            foreach (var feature in s_features)
            {
                if (feature.Layer == layer && feature.State == FeatureState.Running) Guard(feature, "draw", feature.OnGUI);
            }
        }
    }

    public static void SceneChangedAll()
    {
        foreach (var feature in s_features)
        {
            if (feature.State == FeatureState.Running) Guard(feature, "scene change", feature.OnSceneChanged);
        }
    }

    private static void Guard(Feature feature, string phase, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            RecordError(feature, phase, e);
        }
    }

    private static void RecordError(Feature feature, string phase, Exception e)
    {
        feature.LastError = $"{phase}: {e.Message}";
        Log.ErrorThrottled($"feature:{feature.Id}:{phase}", $"{feature.Name} {phase} failed: {e}");

        float now = Time.unscaledTime;
        feature.RecentErrors.Enqueue(now);
        while (feature.RecentErrors.Count > 0 && now - feature.RecentErrors.Peek() > ErrorWindowSeconds) feature.RecentErrors.Dequeue();
        if (feature.RecentErrors.Count >= MaxErrors) Fault(feature, phase, e);
    }

    private static void Fault(Feature feature, string phase, Exception e)
    {
        bool wasRunning = feature.State == FeatureState.Running;
        feature.State = FeatureState.Faulted;
        feature.LastError = $"{phase}: {e.Message}";
        Log.Error($"{feature.Name} stopped after errors in {phase}: {e.Message}");
        if (wasRunning) ShutdownQuietly(feature);
        Unpatch(feature);
    }

    private static void ShutdownQuietly(Feature feature)
    {
        try
        {
            feature.Shutdown();
        }
        catch (Exception e)
        {
            Log.Error($"{feature.Name} shutdown failed: {e}");
        }
    }

    // One class at a time, so a stale target can't stop the rest from binding.
    private static void Patch(Feature feature)
    {
        feature.PatchFailures.Clear();
        var classes = feature.PatchClasses;
        if (classes.Length == 0) return;

        feature.HarmonyInstance ??= new HarmonyLib.Harmony($"EpochAssist.{feature.Id}");
        foreach (var type in classes)
        {
            if (feature.PatchGroups.TryGetValue(type, out string? group) && !feature.Has(group)) continue;
            try
            {
                feature.HarmonyInstance.CreateClassProcessor(type).Patch();
            }
            catch (Exception e)
            {
                string message = $"{type.Name}: {e.InnerException?.Message ?? e.Message}";
                feature.PatchFailures.Add(message);
                Log.Error($"{feature.Name} patch failed: {message}");
            }
        }
    }

    private static void Unpatch(Feature feature)
    {
        try
        {
            feature.HarmonyInstance?.UnpatchSelf();
        }
        catch (Exception e)
        {
            Log.Error($"{feature.Name} unpatch failed: {e.Message}");
        }
    }
}
