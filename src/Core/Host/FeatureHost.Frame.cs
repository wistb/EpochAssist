using System.Diagnostics;
using UnityEngine;

namespace EpochAssist.Core;

internal static partial class FeatureHost
{
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
}
