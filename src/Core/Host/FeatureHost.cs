namespace EpochAssist.Core;

// Runs every feature: start, per-frame hooks, stop, with each feature's errors and patches kept apart.
internal static partial class FeatureHost
{
    private const int MaxErrors = 5;
    private const float ErrorWindowSeconds = 10f;

    private static readonly List<Feature> s_features = new();
    // Features found unavailable while StartAll runs; they share one toast at its end.
    private static readonly List<string> s_unavailable = new();
    private static bool s_startingAll;
    // Features whose Needs or RunsAfter can't be ordered (a need that isn't a feature, a cycle), with the reason; they
    // stay unavailable.
    private static Dictionary<Feature, string> s_orderProblems = new();

    public static IReadOnlyList<Feature> All => s_features;

    public static T? Get<T>() where T : Feature => s_features.OfType<T>().FirstOrDefault();

    public static Feature? Find(string id) => s_features.FirstOrDefault(f => f.Id == id);

    /// <summary>Adds features; StartAll orders them by their Needs and RunsAfter, so where they are registered doesn't
    /// matter (ties keep registration order).</summary>
    public static void Register(params Feature[] features)
    {
        foreach (var feature in features)
        {
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
        // Every order the host uses (start, update, draw, stop, dependents, restart actions, All) is this one.
        var (order, problems) = UpdateOrder.Sort(s_features);
        s_features.Clear();
        s_features.AddRange(order);
        s_orderProblems = problems;
        foreach (var feature in s_features) RegisterRestart(feature);
        StartTogether(s_features);
        // Other mods on our patch targets, or another autocaster: one toast per load. Nothing is turned off.
        ModOverlap.CheckAfterStart();
    }

    /// <summary>Adds features after StartAll (modules chosen at the first-start prompt) and starts them now. The order is
    /// sorted again over every feature; the ones already running keep running.</summary>
    public static void Add(params Feature[] features)
    {
        Register(features);
        var (order, problems) = UpdateOrder.Sort(s_features);
        s_features.Clear();
        s_features.AddRange(order);
        s_orderProblems = problems;
        foreach (var feature in features) RegisterRestart(feature);
        StartTogether(features);
    }

    private static void RegisterRestart(Feature feature) =>
        Actions.Register("host", $"feature.restart.{feature.Id}", $"Restart {feature.Name}", () => Restart(feature));

    // Starts each in order; the unavailable ones share one toast at the end.
    private static void StartTogether(IReadOnlyCollection<Feature> features)
    {
        var starting = features.ToHashSet();
        s_startingAll = true;
        try
        {
            foreach (var feature in s_features.Where(starting.Contains).ToList()) Start(feature);
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
        feature.Record.LastError = null;
        feature.Record.LastErrorAt = null;
        feature.Record.RecentErrors.Clear();
        feature.Record.Frames.Reset();
        feature.Record.OverBudget = false;
        feature.Record.OverSince = null;
        feature.Record.OverStreak = feature.Record.UnderStreak = 0;
        try
        {
            if (!feature.Record.SettingsRegistered)
            {
                feature.RegisterSettings();
                feature.Record.SettingsRegistered = true;
            }
            // Settings first, so a blocked feature's page still has them.
            if (s_orderProblems.TryGetValue(feature, out string? problem))
            {
                feature.Record.State = FeatureState.Unavailable;
                feature.Record.LastError = problem;
                Log.Error($"{feature.Name} unavailable: {problem}");
                if (s_startingAll) s_unavailable.Add(feature.Name);
                else Toasts.Show("host", $"{feature.Name} is unavailable after a game update.", ToastKind.Warning);
                Owned.Release(feature);
                return;
            }
            string? blocker = feature.Needs.FirstOrDefault(id => Find(id)?.State != FeatureState.Running);
            if (blocker != null)
            {
                Block(feature, blocker);
                return;
            }
            feature.Record.BlockedBy = null;
            var missing = Compat.Missing(feature);
            Diagnostics.Remember(feature, missing);
            var required = missing.Where(m => m.Group == null).Select(m => m.Text).ToList();
            if (required.Count > 0)
            {
                feature.Record.State = FeatureState.Unavailable;
                feature.Record.LastError = "the game no longer has " + string.Join(", ", required.Take(3)) + (required.Count > 3 ? $" and {required.Count - 3} more" : string.Empty);
                Log.Warn($"{feature.Name} unavailable: {string.Join(", ", required)}");
                if (s_startingAll) s_unavailable.Add(feature.Name);
                else Toasts.Show("host", $"{feature.Name} is unavailable after a game update.", ToastKind.Warning);
                Owned.Release(feature);
                return;
            }
            feature.Record.MissingGroups.Clear();
            foreach (var group in missing.GroupBy(m => m.Group!))
            {
                feature.Record.MissingGroups.Add(group.Key);
                Log.Warn($"{feature.Name} runs without '{group.Key}': the game no longer has {string.Join(", ", group.Select(m => m.Text))}");
            }
            Patch(feature);
            feature.Start();
            feature.Record.State = FeatureState.Running;
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
        feature.Record.State = FeatureState.Stopped;
        if (wasRunning) ShutdownQuietly(feature);
        Unpatch(feature);
        Owned.Release(feature);
    }

    public static void Restart(Feature feature)
    {
        Stop(feature);
        Start(feature);
        Log.Info($"{feature.Name} restarted");
    }

    private static void Fault(Feature feature, string phase, Exception e)
    {
        bool wasRunning = feature.State == FeatureState.Running;
        if (wasRunning) StopDependents(feature);
        feature.Record.State = FeatureState.Faulted;
        feature.Record.LastError = $"{phase}: {e.Message}";
        feature.Record.LastErrorAt = DateTime.Now;
        Log.Error($"{feature.Name} stopped after errors in {phase}: {e.Message}");
        bool saved = Reports.Write(feature, phase, e);
        Toasts.Show("host", $"{feature.Name} stopped after an error: {feature.Record.LastError.TrimEnd('.')}.{(saved ? " A report was saved." : string.Empty)}", ToastKind.Warning);
        if (wasRunning) ShutdownQuietly(feature);
        Unpatch(feature);
        Owned.Release(feature);
    }

    // "A", "A and B", "A, B and C".
    private static string JoinNames(List<string> names) =>
        names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";

    // Stopped, not faulted: it starts again by itself once the need runs.
    private static void Block(Feature feature, string need)
    {
        feature.Record.State = FeatureState.Stopped;
        feature.Record.BlockedBy = need;
        feature.Record.LastError = $"needs {Find(need)?.Name ?? need}";
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
}
