using System.Text;

namespace EpochAssist.Core;

/// <summary>One feature as the Diagnostics page and its copy show it.</summary>
internal sealed record FeatureDiagnostics(string Id, string Name, FeatureState State, FeatureStatus Status, string? LastError, DateTime? LastErrorAt,
    IReadOnlyList<string> PatchFailures, IReadOnlyList<string> MissingRequired, IReadOnlyList<string> MissingOptional, double AverageMs, double WorstMs)
{
    public double BudgetMs { get; init; }
    public bool OverBudget { get; init; }
    public DateTime? OverSince { get; init; }
    public double LastSecondMs { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    /// <summary>The feature's own game edits, one line each.</summary>
    public IReadOnlyList<string> Edits { get; init; } = Array.Empty<string>();

    /// <summary>The state as words: "running, paused (press any skill to resume)", "faulted at 14:01:50: ...".</summary>
    public string StateText => State switch
    {
        FeatureState.Running => $"running, {Status.State.ToString().ToLowerInvariant()}" + (Status.Detail.Length > 0 ? $" ({Status.Detail})" : string.Empty),
        FeatureState.Faulted => $"faulted{(LastErrorAt is { } at ? $" at {at:HH:mm:ss}" : string.Empty)}: {LastError}",
        FeatureState.Unavailable => $"unavailable: {LastError}",
        _ => LastError is { Length: > 0 } reason ? $"stopped: {reason}" : "stopped",
    };

    public string FrameText => $"avg {AverageMs:F2} ms, worst {WorstMs:F2} ms, budget {BudgetMs:F2} ms";

    /// <summary>Lines under the state: an error a running feature recovered from, a budget warning, patch failures,
    /// missing members and the feature's own notes.</summary>
    public IEnumerable<string> Details
    {
        get
        {
            if (State == FeatureState.Running && LastError != null && LastErrorAt is { } at) yield return $"last error at {at:HH:mm:ss}: {LastError}";
            if (State == FeatureState.Running && OverBudget && OverSince is { } since)
                yield return $"over budget: avg {LastSecondMs:F2} ms in the last second, budget {BudgetMs:F2} ms, since {since:HH:mm:ss}";
            foreach (string failure in PatchFailures) yield return $"patch failed: {failure}";
            if (MissingRequired.Count > 0 && State != FeatureState.Unavailable) yield return $"missing: {string.Join(", ", MissingRequired)}";
            foreach (string optional in MissingOptional) yield return $"missing optional: {optional}";
            foreach (string note in Notes) yield return note;
        }
    }
}

/// <summary>Everything the Diagnostics page shows, built on demand; ToText is what the copy and crash reports hold.</summary>
internal sealed record DiagnosticsSnapshot(DateTime BuiltAt, string Mode, string Scene, string SceneKind, IReadOnlyList<FeatureDiagnostics> Features,
    double ModAverageMs, double ModWorstMs, IReadOnlyList<string> SettingsFailures, string Profiles, IReadOnlyList<string> ProblemFiles,
    IReadOnlyList<string> Reports)
{
    /// <summary>Active game edits, one line each (owner, target, original, value, current, stack, drift).</summary>
    public IReadOnlyList<string> Edits { get; init; } = Array.Empty<string>();
    /// <summary>Methods our features have patched now, and the textures the mod holds.</summary>
    public int PatchedMethods { get; init; }
    public int Textures { get; init; }
    /// <summary>Loaded mods, other owners' patches on our targets, and loaded mods on the known-overlap list, one line each.</summary>
    public IReadOnlyList<string> Mods { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Overlaps { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> KnownOverlaps { get; init; } = Array.Empty<string>();
    /// <summary>Names of the modules this start didn't load, in catalogue order.</summary>
    public IReadOnlyList<string> NotLoaded { get; init; } = Array.Empty<string>();

    public string ChangesText => $"edits {Edits.Count}, patched methods {PatchedMethods}, textures {Textures}";

    public string TestedGameVersion => BuildInfo.TestedGameVersion.Length > 0 ? BuildInfo.TestedGameVersion : "not recorded";

    public string ModFrameText => $"avg {ModAverageMs:F2} ms, worst {ModWorstMs:F2} ms";

    public string ToText()
    {
        var text = new StringBuilder();
        text.Append($"{BuildInfo.Name} diagnostics\n");
        text.Append($"mod {BuildInfo.Version}, build {Runtime.BuildId}, dev {(Runtime.Hosted ? "yes" : "no")}\n");
        text.Append($"tested game version: {TestedGameVersion}\n");
        text.Append($"mode {Mode}, scene '{Scene}' ({SceneKind}), built {BuiltAt:yyyy-MM-dd HH:mm:ss}\n");
        text.Append($"mod frame time: {ModFrameText}\n");
        text.Append($"game changes: {ChangesText}\n\n");

        text.Append("features:\n");
        int width = Features.Count > 0 ? Math.Max(10, Features.Max(f => f.Id.Length)) : 10;
        string indent = new(' ', width + 3);
        foreach (var feature in Features)
        {
            text.Append("  ").Append(feature.Id.PadRight(width)).Append(' ').Append(feature.StateText);
            if (feature.State == FeatureState.Running) text.Append("; ").Append(feature.FrameText);
            text.Append('\n');
            foreach (string detail in feature.Details) text.Append(indent).Append(detail).Append('\n');
        }
        foreach (string module in NotLoaded) text.Append("  ").Append(module).Append(": not loaded\n");
        text.Append("settings: ").Append(SettingsFailures.Count > 0 ? string.Join("; ", SettingsFailures) : "none").Append('\n');
        text.Append("profiles: ").Append(Profiles).Append('\n');
        text.Append($"Edits: {Edits.Count}\n");
        foreach (string line in Edits) text.Append("  ").Append(line).Append('\n');
        Section(text, "mods", Mods);
        Section(text, "overlaps", Overlaps);
        Section(text, "known overlaps", KnownOverlaps);
        Section(text, "problem files", ProblemFiles);
        Section(text, "reports", Reports);
        return Diagnostics.Scrub(text.ToString().TrimEnd('\n'));
    }

    private static void Section(StringBuilder text, string title, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            text.Append(title).Append(": none\n");
            return;
        }
        text.Append(title).Append(":\n");
        foreach (string line in lines) text.Append("  ").Append(line).Append('\n');
    }
}
