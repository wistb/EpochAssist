using System.Diagnostics;
using UnityEngine;

namespace EpochAssist.Core;

// Runs every feature: start, per-frame hooks, stop, with each feature's errors and patches kept apart.
internal static class FeatureHost
{
    private const int MaxErrors = 5;
    private const float ErrorWindowSeconds = 10f;

    private static readonly List<Feature> s_features = new();
    // Features found unavailable while StartAll runs; they share one toast at its end.
    private static readonly List<string> s_unavailable = new();
    private static bool s_startingAll;

    public static IReadOnlyList<Feature> All => s_features;

    public static T? Get<T>() where T : Feature => s_features.OfType<T>().FirstOrDefault();

    public static Feature? Find(string id) => s_features.FirstOrDefault(f => f.Id == id);

    /// <summary>Update order is registration order, so a feature's needs must be registered before it.</summary>
    public static void Register(params Feature[] features)
    {
        foreach (var feature in features)
        {
            foreach (string need in feature.Needs)
            {
                // An unknown need keeps the feature stopped; a late one starts it late and updates it before its need.
                if (Find(need) == null) Log.Error($"{feature.Name} needs '{need}', which is unknown or registered after it; register needs first");
            }
            foreach (var part in feature.Parts)
            {
                // The part then counts as missing, as the model may not run when the feature starts.
                if (Array.IndexOf(feature.Needs, part.Model) < 0) Log.Error($"{feature.Name} reads the part '{part.Part}' of '{part.Model}', which isn't in its needs; add it to Needs");
            }
            s_features.Add(feature);
        }
    }

    public static void StartAll()
    {
        foreach (var feature in s_features)
        {
            var target = feature;
            Actions.Register("host", $"feature.restart.{feature.Id}", $"Restart {feature.Name}", () => Restart(target));
        }
        s_startingAll = true;
        try
        {
            foreach (var feature in s_features) Start(feature);
        }
        finally
        {
            s_startingAll = false;
        }
        if (s_unavailable.Count > 0)
        {
            string verb = s_unavailable.Count == 1 ? "is" : "are";
            Toasts.Show("host", $"{JoinNames(s_unavailable)} {verb} unavailable after a game update.", ToastKind.Warning);
            s_unavailable.Clear();
        }
    }

    public static void StopAll()
    {
        for (int i = s_features.Count - 1; i >= 0; i--) Stop(s_features[i]);
        Actions.Clear();
        Commands.Clear();
    }

    public static void Start(Feature feature)
    {
        if (feature.State == FeatureState.Running) return;
        feature.LastError = null;
        feature.LastErrorAt = null;
        feature.RecentErrors.Clear();
        feature.Frames.Reset();
        try
        {
            if (!feature.SettingsRegistered)
            {
                feature.RegisterSettings();
                feature.SettingsRegistered = true;
            }
            // Settings first, so a blocked feature's page still has them.
            string? blocker = feature.Needs.FirstOrDefault(id => Find(id)?.State != FeatureState.Running);
            if (blocker != null)
            {
                Block(feature, blocker);
                return;
            }
            feature.BlockedBy = null;
            var missing = Compat.Missing(feature);
            Diagnostics.Remember(feature, missing);
            var required = missing.Where(m => m.Group == null).Select(m => m.Text).ToList();
            if (required.Count > 0)
            {
                feature.State = FeatureState.Unavailable;
                feature.LastError = "the game no longer has " + string.Join(", ", required.Take(3)) + (required.Count > 3 ? $" and {required.Count - 3} more" : string.Empty);
                Log.Warn($"{feature.Name} unavailable: {string.Join(", ", required)}");
                if (s_startingAll) s_unavailable.Add(feature.Name);
                else Toasts.Show("host", $"{feature.Name} is unavailable after a game update.", ToastKind.Warning);
                Actions.RemoveOwner(feature.Id);
                Commands.RemoveOwner(feature.Id);
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
            return;
        }
        StartBlockedBy(feature);
    }

    public static void Stop(Feature feature)
    {
        if (feature.State is FeatureState.Stopped or FeatureState.Unavailable) return;
        bool wasRunning = feature.State == FeatureState.Running;
        if (wasRunning) StopDependents(feature);
        feature.State = FeatureState.Stopped;
        if (wasRunning) ShutdownQuietly(feature);
        Unpatch(feature);
        Actions.RemoveOwner(feature.Id);
        Commands.RemoveOwner(feature.Id);
    }

    public static void Restart(Feature feature)
    {
        Stop(feature);
        Start(feature);
        Log.Info($"{feature.Name} restarted");
    }

    public static void UpdateAll()
    {
        CloseFrame();
        foreach (var feature in s_features)
        {
            if (feature.State == FeatureState.Running) Guard(feature, "update", feature.Update);
        }
    }

    public static void LateUpdateAll()
    {
        foreach (var feature in s_features)
        {
            if (feature.State == FeatureState.Running) Guard(feature, "late update", feature.LateUpdate);
        }
    }

    public static void GuiAll()
    {
        for (var layer = GuiLayer.World; layer <= GuiLayer.Menu; layer++)
        {
            // World drawing uses one camera for the frame, taken here and dropped after, whatever a feature does.
            if (layer == GuiLayer.World) Draw.Begin();
#if DEV
            if (layer == GuiLayer.Hud) Dev.DevCommands.DrawBackdrop();
#endif
            try
            {
                foreach (var feature in s_features)
                {
                    if (feature.Layer == layer && feature.State == FeatureState.Running) Guard(feature, "draw", feature.OnGUI);
                }
            }
            finally
            {
                if (layer == GuiLayer.World) Draw.End();
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

    private static int s_closedFrame = -1;

    // The last frame's time per feature goes into its buckets, once, before this frame's first call.
    private static void CloseFrame()
    {
        int frame = Time.frameCount;
        if (frame == s_closedFrame) return;
        s_closedFrame = frame;
        int second = (int)Time.unscaledTime;
        long total = 0;
        foreach (var feature in s_features)
        {
            total += feature.Frames.Current;
            feature.Frames.Close(second);
        }
        Diagnostics.ModFrames.Current = total;
        Diagnostics.ModFrames.Close(second);
        Diagnostics.TimedCallsLastFrame = Diagnostics.TimedCalls;
        Diagnostics.TimedCalls = 0;
    }

    // Each call is timed for the Diagnostics page unless the timers are off (dev, to measure their own cost).
    private static void Guard(Feature feature, string phase, Action action)
    {
        if (Diagnostics.TimersOff)
        {
            Run(feature, phase, action);
            return;
        }
        long start = Stopwatch.GetTimestamp();
        Run(feature, phase, action);
        feature.Frames.Current += Stopwatch.GetTimestamp() - start;
        Diagnostics.TimedCalls++;
    }

    private static void Run(Feature feature, string phase, Action action)
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
        feature.LastErrorAt = DateTime.Now;
        Log.ErrorThrottled($"feature:{feature.Id}:{phase}", $"{feature.Name} {phase} failed: {e}");

        float now = Time.unscaledTime;
        feature.RecentErrors.Enqueue(now);
        while (feature.RecentErrors.Count > 0 && now - feature.RecentErrors.Peek() > ErrorWindowSeconds) feature.RecentErrors.Dequeue();
        if (feature.RecentErrors.Count >= MaxErrors) Fault(feature, phase, e);
    }

    private static void Fault(Feature feature, string phase, Exception e)
    {
        bool wasRunning = feature.State == FeatureState.Running;
        if (wasRunning) StopDependents(feature);
        feature.State = FeatureState.Faulted;
        feature.LastError = $"{phase}: {e.Message}";
        feature.LastErrorAt = DateTime.Now;
        Log.Error($"{feature.Name} stopped after errors in {phase}: {e.Message}");
        bool saved = Reports.Write(feature, phase, e);
        Toasts.Show("host", $"{feature.Name} stopped after an error: {feature.LastError.TrimEnd('.')}.{(saved ? " A report was saved." : string.Empty)}", ToastKind.Warning);
        if (wasRunning) ShutdownQuietly(feature);
        Unpatch(feature);
        Actions.RemoveOwner(feature.Id);
        Commands.RemoveOwner(feature.Id);
    }

    // "A", "A and B", "A, B and C".
    private static string JoinNames(List<string> names) =>
        names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";

    // Stopped, not faulted: it starts again by itself once the need runs.
    private static void Block(Feature feature, string need)
    {
        feature.State = FeatureState.Stopped;
        feature.BlockedBy = need;
        feature.LastError = $"needs {Find(need)?.Name ?? need}";
    }

    // Dependents go first, latest registered first, each through its own Shutdown (and so its own dependents).
    private static void StopDependents(Feature feature)
    {
        for (int i = s_features.Count - 1; i >= 0; i--)
        {
            var dependent = s_features[i];
            if (dependent.State != FeatureState.Running || Array.IndexOf(dependent.Needs, feature.Id) < 0) continue;
            Stop(dependent);
            Block(dependent, feature.Id);
            Log.Info($"{dependent.Name} stopped: needs {feature.Name}");
        }
    }

    private static void StartBlockedBy(Feature feature)
    {
        foreach (var dependent in s_features)
        {
            if (dependent.State == FeatureState.Stopped && dependent.BlockedBy == feature.Id) Start(dependent);
        }
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
