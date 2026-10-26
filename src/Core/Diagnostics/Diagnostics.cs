using System.Text.RegularExpressions;
using MelonLoader.Utils;

namespace EpochAssist.Core;

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
    /// <summary>Textures the UI holds, set by the theme when it builds.</summary>
    public static Func<int>? UiTextures;
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
                f.Record.Frames.AverageMs, f.Record.Frames.WorstMs)
            {
                BudgetMs = f.BudgetMs, OverBudget = f.OverBudget, OverSince = f.OverSince, LastSecondMs = f.Record.Frames.LastSecondMs,
                Notes = f.State == FeatureState.Running ? Lines(() => f.DiagnosticNotes).ToList() : Array.Empty<string>(),
                Edits = f.Record.Edits.Select(e => e.Describe()).ToList(),
            };
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
            ProfilesText(), Lines(ThemeProblems).Concat(ReportProblems()).ToList(), Reports.List().Select(r => $"{r.File} ({r.Feature}, count {r.Count})").ToList())
        {
            Edits = Core.Edits.Lines().ToList(),
            PatchedMethods = FeatureHost.All.Sum(f => PatchedBy(f)),
            Textures = Draw.ResourceCount + (UiTextures?.Invoke() ?? 0),
        };
    }

    // Our own patches only: other mods' patches aren't the mod's changes.
    private static int PatchedBy(Feature feature)
    {
        try
        {
            return feature.Record.Harmony?.GetPatchedMethods().Count() ?? 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // Game members don't change while the game runs, so each feature is checked once per load (by id: the self-test is
    // a new object each time, and every check of a missing patch target makes Harmony log a warning).
    private static List<MissingMember> Missing(Feature feature)
    {
        if (!s_missing.TryGetValue(feature.Id, out var missing)) s_missing[feature.Id] = missing = Compat.Missing(feature);
        return missing;
    }

    /// <summary>Called by the host with what a start found missing: a restart (a dev `model miss`, or a model part
    /// coming back) can change it, so the cached list is replaced.</summary>
    public static void Remember(Feature feature, List<MissingMember> missing) => s_missing[feature.Id] = missing;

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
