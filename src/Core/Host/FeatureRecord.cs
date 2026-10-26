namespace EpochAssist.Core;

/// <summary>
/// What the host tracks about one feature: its state and errors, budget, frame times and the things it owns. Only the
/// host, the scheduler (jobs) and the edit registry (edits) write it; other code reads the state through Feature.
/// </summary>
internal sealed class FeatureRecord
{
    public FeatureState State { get; set; }
    public string? LastError { get; set; }
    // When LastError was set by an error (local time); null when it isn't one.
    public DateTime? LastErrorAt { get; set; }
    // The need that kept or took the feature out of Running, or null.
    public string? BlockedBy { get; set; }
    public List<string> PatchFailures { get; } = new();
    // Optional requirement groups the game lacks.
    public HashSet<string> MissingGroups { get; } = new();

    // Over the budget for 3 counted seconds in a row (cleared after 3 under), and since when.
    public bool OverBudget { get; set; }
    public DateTime? OverSince { get; set; }
    public int OverStreak, UnderStreak;

    public readonly FrameTimes Frames = new();
    public readonly Queue<float> RecentErrors = new();
    // The scheduler's jobs owned by the feature; the host drops them when it leaves Running.
    public readonly List<Job> Jobs = new();
    // The registry's edits owned by the feature; the host removes them when it leaves Running.
    public readonly List<Edit> Edits = new();
    public HarmonyLib.Harmony? Harmony;
    // The DemandGroups the host has patched now.
    public readonly HashSet<string> DemandGroupsOn = new();
    public bool SettingsRegistered;
}
