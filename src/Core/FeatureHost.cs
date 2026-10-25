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
        feature.OverBudget = false;
        feature.OverSince = null;
        feature.OverStreak = feature.UnderStreak = 0;
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
                Scheduler.RemoveOwner(feature);
                Bus.RemoveOwner(feature);
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
        Scheduler.RemoveOwner(feature);
        Bus.RemoveOwner(feature);
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
        // A feature's due jobs just before its Update and its spread steps just after, so they keep the place in the
        // frame the hand-rolled timers had.
        foreach (var feature in s_features)
        {
            if (feature.State != FeatureState.Running) continue;
            Scheduler.RunDue(feature);
            Action update = feature.Update;
#if DEV
            if (feature == s_waste && s_wasteUpdate != null) update = s_wasteUpdate;
#endif
            if (feature.State == FeatureState.Running) Guard(feature, "update", update);
            if (feature.State == FeatureState.Running) Scheduler.RunSpread(feature);
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
    private const int BudgetSeconds = 3;
    // A second with a loading screen or a scene change in it doesn't count for the budget either way.
    private static bool s_skipSecond;
    private static int s_sceneStamp = -1;

    // The last frame's time per feature goes into its buckets, once, before this frame's first call. When that starts
    // a new second, each running feature's average for the second just done is checked against its budget.
    private static void CloseFrame()
    {
        int frame = Time.frameCount;
        if (frame == s_closedFrame) return;
        s_closedFrame = frame;
        int second = (int)Time.unscaledTime;
        long total = 0;
        bool skip = s_skipSecond, rolled = false;
        foreach (var feature in s_features)
        {
            total += feature.Frames.Current;
            if (feature.Frames.Close(second) && feature.State == FeatureState.Running)
            {
                rolled = true;
                if (!skip) CheckBudget(feature);
            }
        }
        if (rolled) s_skipSecond = false;
        if (GameState.Loading || GameState.SceneChangedAt != s_sceneStamp)
        {
            s_sceneStamp = GameState.SceneChangedAt;
            s_skipSecond = true;
        }
        Diagnostics.ModFrames.Current = total;
        Diagnostics.ModFrames.Close(second);
        Diagnostics.TimedCallsLastFrame = Diagnostics.TimedCalls;
        Diagnostics.TimedCalls = 0;
        s_nestedTicks = 0;
    }

    // 3 counted seconds over the budget set the warning (one log line), 3 under clear it; nothing else happens.
    private static void CheckBudget(Feature feature)
    {
        double ms = feature.Frames.LastSecondMs;
        if (ms > feature.BudgetMs)
        {
            feature.UnderStreak = 0;
            if (++feature.OverStreak < BudgetSeconds || feature.OverBudget) return;
            feature.OverBudget = true;
            feature.OverSince = DateTime.Now;
            Log.WarnThrottled($"budget:{feature.Id}", $"{feature.Name} is over its frame budget: avg {ms:F2} ms, budget {feature.BudgetMs:F2} ms", 30);
            return;
        }
        feature.OverStreak = 0;
        if (!feature.OverBudget || ++feature.UnderStreak < BudgetSeconds) return;
        feature.OverBudget = false;
        feature.OverSince = null;
        feature.UnderStreak = 0;
    }

#if DEV
    // Dev only (`perf waste`): spins inside one feature's update guard, to check the budget warning.
    private static Feature? s_waste;
    private static Action? s_wasteUpdate;

    /// <summary>Spins for ms inside the feature's update guard each frame; null stops it.</summary>
    internal static void SetWaste(Feature? feature, double ms)
    {
        s_waste = feature;
        s_wasteUpdate = feature == null ? null : () =>
        {
            Spin(ms);
            feature.Update();
        };
    }

    private static void Spin(double ms)
    {
        long until = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000d);
        while (Stopwatch.GetTimestamp() < until) { }
    }
#endif

    // Ticks spent in guards nested inside the one running now; they belong to their own owners, not to it.
    private static long s_nestedTicks;

    /// <summary>Runs an action in an owner's guard (its time and errors are that owner's), from inside another guard
    /// too: the scheduler's jobs and the bus's handlers.</summary>
    internal static void Guarded(Feature owner, string phase, Action action) => Guard(owner, phase, action);

    // Each call is timed for the Diagnostics page unless the timers are off (dev, to measure their own cost). A guard
    // inside another (a bus handler raised from GameState's update) counts on its own owner and is taken off the outer one.
    private static void Guard(Feature feature, string phase, Action action)
    {
        if (Diagnostics.TimersOff)
        {
            Run(feature, phase, action);
            return;
        }
        long outerNested = s_nestedTicks;
        s_nestedTicks = 0;
        long start = Stopwatch.GetTimestamp();
        Run(feature, phase, action);
        long elapsed = Stopwatch.GetTimestamp() - start;
        feature.Frames.Current += elapsed - s_nestedTicks;
        s_nestedTicks = outerNested + elapsed;
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
        Scheduler.RemoveOwner(feature);
        Bus.RemoveOwner(feature);
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
            if (feature.PatchGroups.TryGetValue(type, out string? group) && (!feature.Has(group) || Array.IndexOf(feature.DemandGroups, group) >= 0)) continue;
            PatchClass(feature, type);
        }
    }

    private static bool PatchClass(Feature feature, Type type)
    {
        try
        {
            feature.HarmonyInstance!.CreateClassProcessor(type).Patch();
            return true;
        }
        catch (Exception e)
        {
            string message = $"{type.Name}: {e.InnerException?.Message ?? e.Message}";
            feature.PatchFailures.Add(message);
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
        if (feature.DemandGroupsOn.Contains(group) == on) return;
        var classes = feature.PatchClasses.Where(t => feature.PatchGroups.TryGetValue(t, out string? g) && g == group).ToList();
        if (on)
        {
            feature.HarmonyInstance ??= new HarmonyLib.Harmony($"EpochAssist.{feature.Id}");
            feature.DemandGroupsOn.Add(group);
            foreach (var type in classes) PatchClass(feature, type);
            return;
        }
        feature.DemandGroupsOn.Remove(group);
        var harmony = feature.HarmonyInstance;
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
        feature.DemandGroupsOn.Clear();
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
