using System.Text;
using System.Text.RegularExpressions;
using MelonLoader.Utils;

namespace EpochAssist.Core;

/// <summary>
/// A feature's time per frame (every guarded call: update, late update, draws, scene changes). Each frame's total goes
/// into a bucket per second of unscaled time; the last five seconds are kept.
/// </summary>
internal sealed class FrameTimes
{
    private const int Buckets = 5;

    private readonly long[] _sum = new long[Buckets];
    private readonly int[] _frames = new int[Buckets];
    private readonly long[] _worst = new long[Buckets];
    private readonly int[] _second = new int[Buckets];
    private int _latest = -1;

    /// <summary>Stopwatch ticks spent this frame so far.</summary>
    public long Current;

    public void Close(int second)
    {
        int i = second % Buckets;
        if (_second[i] != second || _latest < 0)
        {
            _second[i] = second;
            _sum[i] = 0;
            _frames[i] = 0;
            _worst[i] = 0;
        }
        _sum[i] += Current;
        _frames[i]++;
        _worst[i] = Math.Max(_worst[i], Current);
        _latest = second;
        Current = 0;
    }

    public void Reset()
    {
        Array.Clear(_frames);
        _latest = -1;
        Current = 0;
    }

    /// <summary>Average over the kept seconds, in milliseconds.</summary>
    public double AverageMs
    {
        get
        {
            long sum = 0;
            int frames = 0;
            for (int i = 0; i < Buckets; i++)
            {
                if (!Kept(i)) continue;
                sum += _sum[i];
                frames += _frames[i];
            }
            return frames > 0 ? Ms(sum) / frames : 0d;
        }
    }

    /// <summary>The slowest single frame of the kept seconds, in milliseconds.</summary>
    public double WorstMs
    {
        get
        {
            long worst = 0;
            for (int i = 0; i < Buckets; i++)
            {
                if (Kept(i)) worst = Math.Max(worst, _worst[i]);
            }
            return Ms(worst);
        }
    }

    private bool Kept(int i) => _latest >= 0 && _frames[i] > 0 && _second[i] > _latest - Buckets;

    public static double Ms(long ticks) => ticks * 1000d / System.Diagnostics.Stopwatch.Frequency;
}

/// <summary>One feature as the Diagnostics page and its copy show it.</summary>
internal sealed record FeatureDiagnostics(string Id, string Name, FeatureState State, FeatureStatus Status, string? LastError, DateTime? LastErrorAt,
    IReadOnlyList<string> PatchFailures, IReadOnlyList<string> MissingRequired, IReadOnlyList<string> MissingOptional, double AverageMs, double WorstMs)
{
    /// <summary>The state as words: "running, paused (press any skill to resume)", "faulted at 14:01:50: ...".</summary>
    public string StateText => State switch
    {
        FeatureState.Running => $"running, {Status.State.ToString().ToLowerInvariant()}" + (Status.Detail.Length > 0 ? $" ({Status.Detail})" : string.Empty),
        FeatureState.Faulted => $"faulted{(LastErrorAt is { } at ? $" at {at:HH:mm:ss}" : string.Empty)}: {LastError}",
        FeatureState.Unavailable => $"unavailable: {LastError}",
        _ => LastError is { Length: > 0 } reason ? $"stopped: {reason}" : "stopped",
    };

    public string FrameText => $"avg {AverageMs:F2} ms, worst {WorstMs:F2} ms";

    /// <summary>Lines under the state: an error a running feature recovered from, patch failures, missing members.</summary>
    public IEnumerable<string> Details
    {
        get
        {
            if (State == FeatureState.Running && LastError != null && LastErrorAt is { } at) yield return $"last error at {at:HH:mm:ss}: {LastError}";
            foreach (string failure in PatchFailures) yield return $"patch failed: {failure}";
            if (MissingRequired.Count > 0 && State != FeatureState.Unavailable) yield return $"missing: {string.Join(", ", MissingRequired)}";
            foreach (string optional in MissingOptional) yield return $"missing optional: {optional}";
        }
    }
}

/// <summary>Everything the Diagnostics page shows, built on demand; ToText is what the copy and crash reports hold.</summary>
internal sealed record DiagnosticsSnapshot(DateTime BuiltAt, string Mode, string Scene, string SceneKind, IReadOnlyList<FeatureDiagnostics> Features,
    double ModAverageMs, double ModWorstMs, IReadOnlyList<string> SettingsFailures, string Profiles, IReadOnlyList<string> ProblemFiles,
    IReadOnlyList<string> Reports)
{
    public string TestedGameVersion => BuildInfo.TestedGameVersion.Length > 0 ? BuildInfo.TestedGameVersion : "not recorded";

    public string ModFrameText => $"avg {ModAverageMs:F2} ms, worst {ModWorstMs:F2} ms";

    public string ToText()
    {
        var text = new StringBuilder();
        text.Append($"{BuildInfo.Name} diagnostics\n");
        text.Append($"mod {BuildInfo.Version}, build {Runtime.BuildId}, dev {(Runtime.Hosted ? "yes" : "no")}\n");
        text.Append($"tested game version: {TestedGameVersion}\n");
        text.Append($"mode {Mode}, scene '{Scene}' ({SceneKind}), built {BuiltAt:yyyy-MM-dd HH:mm:ss}\n");
        text.Append($"mod frame time: {ModFrameText}\n\n");

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
        text.Append("settings: ").Append(SettingsFailures.Count > 0 ? string.Join("; ", SettingsFailures) : "none").Append('\n');
        text.Append("profiles: ").Append(Profiles).Append('\n');
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

/// <summary>
/// What the mod is doing, for the Diagnostics page, its copy and crash reports, and the rule that keeps personal data
/// out of all of them and the log.
/// </summary>
internal static class Diagnostics
{
    // Characters a path can't hold, or that end one in a log line ("...\Foo.cs:line 42").
    private const string PathRest = @"[^\s""'<>|:*?]*";

    private static readonly string s_modData = Path.Combine(MelonEnvironment.UserDataDirectory, "EpochAssist");
    private static readonly Regex s_modDataPath = new(RootPattern(s_modData) + @"(?<rest>(?:[\\/]" + PathRest + ")?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    // Roots that can hold spaces ("Last Epoch", a profile folder), which the general rule would cut in two.
    private static readonly Regex[] s_spacedRoots = new[] { MelonEnvironment.GameRootDirectory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }
        .Where(root => !string.IsNullOrEmpty(root))
        .Select(root => new Regex(RootPattern(root) + @"(?:[\\/]" + PathRest + ")?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled))
        .ToArray();
    private static readonly Regex s_absolutePath = new(@"(?<![\w/\\])(?:[A-Za-z]:[\\/]|\\\\|/(?:home|mnt|Users)/)" + PathRest,
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex? s_userName = WordPattern(Environment.UserName);

    private static string? s_characterName;
    private static Regex? s_character;
    private static readonly Dictionary<string, List<MissingMember>> s_missing = new();

    /// <summary>The mod's total time per frame, over every feature.</summary>
    public static readonly FrameTimes ModFrames = new();

    /// <summary>Dev only: guarded calls go untimed, to measure what the timing itself costs.</summary>
    public static bool TimersOff { get; set; }

    /// <summary>Guarded calls timed so far this frame, and in the last whole frame.</summary>
    public static int TimedCalls, TimedCallsLastFrame;

    /// <summary>Theme file problems, set by the menu ("file: text" each).</summary>
    public static Func<IEnumerable<string>>? ThemeProblems;
    /// <summary>The profiles line, set by the profiles feature while it runs.</summary>
    public static Func<string>? ProfileStatus;

    /// <summary>A snapshot of every feature and the mod's state now.</summary>
    public static DiagnosticsSnapshot Build()
    {
        var features = FeatureHost.All.Select(f =>
        {
            var missing = Missing(f);
            return new FeatureDiagnostics(f.Id, f.Name, f.State, f.Status, f.LastError, f.LastErrorAt, f.PatchFailures.ToList(),
                missing.Where(m => m.Group == null).Select(m => m.Text).ToList(),
                missing.Where(m => m.Group != null).GroupBy(m => m.Group!).Select(g => $"{g.Key} ({string.Join(", ", g.Select(m => m.Text))})").ToList(),
                f.Frames.AverageMs, f.Frames.WorstMs);
        }).ToList();
        // Dev builds: the compat self-test, which is never started, shows how missing members are reported.
        if (CoreCommands.CompatSelfTest?.Invoke() is { } selfTest)
        {
            var missing = Missing(selfTest);
            var required = missing.Where(m => m.Group == null).Select(m => m.Text).ToList();
            features.Add(new FeatureDiagnostics(selfTest.Id, selfTest.Name, FeatureState.Unavailable, FeatureStatus.Off(string.Empty),
                "the game no longer has " + string.Join(", ", required), null, Array.Empty<string>(), required,
                missing.Where(m => m.Group != null).GroupBy(m => m.Group!).Select(g => $"{g.Key} ({string.Join(", ", g.Select(m => m.Text))})").ToList(), 0d, 0d));
        }
        bool? online = GameState.Online;
        string mode = !GameState.InGame ? "not in game" : online == true ? "online" : online == false ? "offline" : "unknown";
        string kind = GameState.Kind switch { SceneKind.NonCombat => "non-combat", SceneKind.Combat => "combat", SceneKind.Menu => "menu", _ => "none" };
        var settings = SettingsRegistry.Groups.Where(g => g.Failures.Count > 0).Select(g => $"{g.Name} settings not updated: {string.Join("; ", g.Failures)}").ToList();
        return new DiagnosticsSnapshot(DateTime.Now, mode, GameState.Scene, kind, features, ModFrames.AverageMs, ModFrames.WorstMs, settings,
            ProfilesText(), Lines(ThemeProblems).Concat(ReportProblems()).ToList(), Reports.List().Select(r => $"{r.File} ({r.Feature}, count {r.Count})").ToList());
    }

    // Game members don't change while the game runs, so each feature is checked once per load (by id: the self-test is
    // a new object each time, and every check of a missing patch target makes Harmony log a warning).
    private static List<MissingMember> Missing(Feature feature)
    {
        if (!s_missing.TryGetValue(feature.Id, out var missing)) s_missing[feature.Id] = missing = Compat.Missing(feature);
        return missing;
    }

    private static string ProfilesText()
    {
        if (ProfileStatus != null) return ProfileStatus();
        var profiles = FeatureHost.Find("profiles");
        return profiles == null || profiles.State == FeatureState.Running ? "off"
            : $"off ({(profiles.LastError is { Length: > 0 } reason ? reason : profiles.State.ToString().ToLowerInvariant())})";
    }

    private static IEnumerable<string> ReportProblems()
    {
        if (Reports.LastWriteError is { } error) yield return $"report not saved: {error}";
    }

    // A hook that throws shows as one line saying so, instead of breaking the whole page.
    private static IEnumerable<string> Lines(Func<IEnumerable<string>>? hook)
    {
        if (hook == null) return Enumerable.Empty<string>();
        try
        {
            return hook().ToList();
        }
        catch (Exception e)
        {
            return new[] { $"(could not read: {e.Message})" };
        }
    }

    /// <summary>The current character's name, set by the profiles while one is loaded; scrubbed as &lt;character&gt;.</summary>
    public static string? CharacterName
    {
        get => s_characterName;
        set
        {
            if (value == s_characterName) return;
            s_characterName = value;
            s_character = WordPattern(value);
        }
    }

    /// <summary>
    /// The text without personal data: paths under the mod's folder become UserData/EpochAssist/..., any other absolute
    /// path its last part, the user name &lt;user&gt; and the character's name &lt;character&gt;.
    /// </summary>
    public static string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = s_modDataPath.Replace(text, m => "UserData/EpochAssist" + m.Groups["rest"].Value.Replace('\\', '/'));
        foreach (var root in s_spacedRoots) text = root.Replace(text, m => LastSegment(m.Value));
        text = s_absolutePath.Replace(text, m => LastSegment(m.Value));
        if (s_userName != null) text = s_userName.Replace(text, "<user>");
        if (s_character != null) text = s_character.Replace(text, "<character>");
        return text;
    }

    private static string LastSegment(string path) =>
        path.Split('\\', '/').LastOrDefault(part => part.Length > 0 && !part.EndsWith(':')) ?? string.Empty;

    // A folder matched with either separator, at a word start.
    private static string RootPattern(string root) =>
        @"(?<![\w/\\])" + string.Join(@"[\\/]", root.TrimEnd('\\', '/').Split('\\', '/').Select(Regex.Escape));

    private static Regex? WordPattern(string? word) => string.IsNullOrWhiteSpace(word) ? null
        : new Regex(@"(?<!\w)" + Regex.Escape(word.Trim()) + @"(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
