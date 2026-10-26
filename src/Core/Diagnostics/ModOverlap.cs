using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace EpochAssist.Core;

/// <summary>A loaded MelonLoader mod or plugin: Kind is mod, plugin, dev-loaded (us under the dev loader) or failed.</summary>
internal sealed record LoadedMod(string Name, string Version, string Author, string File, string Kind, bool Ours)
{
    /// <summary>Why a failed one didn't load.</summary>
    public string? Error { get; init; }

    /// <summary>"Terrible Tooltips 3.1.2 by medick (medick_Terrible_Tooltips.dll)".</summary>
    public string Line => Kind switch
    {
        "dev-loaded" => $"{Name} {Version} build {Runtime.BuildId} (dev, loaded by the dev loader)",
        "failed" => $"{Name} ({File}) failed to load{(Error is { Length: > 0 } ? ": " + Error : string.Empty)}",
        _ => $"{Name} {Version} by {Author} ({File}{(Kind == "plugin" ? ", plugin" : string.Empty)})",
    };
}

/// <summary>Another owner's patch on a method one of our features patches: the owner (a mod name, or its Harmony id) and
/// the patch kind (prefix, postfix, transpiler, finalizer).</summary>
internal readonly record struct OtherPatch(string Owner, string Kind);

/// <summary>A method one of our features patches (or patches while it needs it) that another owner patches too.</summary>
internal sealed record PatchOverlap(string Method, string Feature, bool OursApplied, IReadOnlyList<OtherPatch> Others)
{
    /// <summary>"UITooltipItem.SetAsItemTooltip (gameui, applied when needed): Terrible Tooltips postfix".</summary>
    public string Line => $"{Method} ({Feature}{(OursApplied ? string.Empty : ", applied when needed")}): {OthersText}";

    /// <summary>"LastEpoch_Hud prefix, LastEpoch_Hud postfix ×3": one entry per owner and kind.</summary>
    public string OthersText => string.Join(", ", Others.GroupBy(o => o).Select(g => $"{g.Key.Owner} {g.Key.Kind}{(g.Count() > 1 ? $" ×{g.Count()}" : string.Empty)}"));
}

/// <summary>What the overlap check found: the loaded mods, shared patch targets, loaded mods on the known list, and
/// lines for anything that couldn't be read.</summary>
internal sealed record OverlapReport(IReadOnlyList<LoadedMod> Mods, IReadOnlyList<PatchOverlap> Overlaps, IReadOnlyList<KnownOverlap> Known, DateTime CheckedAt,
    IReadOnlyList<string> Problems)
{
    /// <summary>A shared patch target or another autocaster: worth the start toast.</summary>
    public bool ToastWorthy => Overlaps.Count > 0 || Known.Any(k => k.Kinds.Contains(KnownMods.Autocaster));

    public IEnumerable<string> ModLines => Mods.Select(m => m.Line).Concat(Problems);
    public IEnumerable<string> OverlapLines => Overlaps.Select(o => o.Line);
    public IEnumerable<string> KnownLines => Known.Select(k => k.Line);

    /// <summary>Mods other than ours that overlap: the other owners of shared targets, then the known autocasters.</summary>
    public List<string> OverlappingNames() =>
        Overlaps.SelectMany(o => o.Others.Select(x => x.Owner))
            .Concat(Known.Where(k => k.Kinds.Contains(KnownMods.Autocaster)).Select(k => k.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>
/// Which other mods are loaded and where they overlap ours: MelonLoader's registered mods and plugins, other owners'
/// Harmony patches on our patch targets, and the compiled known list. A pure read: it sends nothing, holds no game
/// state and changes no feature. Every MelonLoader or Harmony read is wrapped, so a member that changed gives a "(could
/// not read: …)" line instead of an exception.
/// </summary>
internal static class ModOverlap
{
    private const string Unknown = "unknown";

    // Our features' patch targets, read once per load (game members don't change while the game runs).
    private static readonly Dictionary<string, List<PatchTarget>> s_targets = new();
    // Overlaps already reported, so a demand group applied again doesn't log the same one twice.
    private static readonly HashSet<string> s_logged = new();

#if DEV
    /// <summary>Dev only: names of pretend loaded mods (`mods simulate`), listed as mods until removed or a reload.</summary>
    internal static readonly List<string> Simulated = new();
#endif

    /// <summary>Every loaded mod and plugin, Epoch Assist included; read on each call.</summary>
    public static IReadOnlyList<LoadedMod> Mods() => ReadMods(new List<string>());

    /// <summary>Mods, patch overlaps and known overlaps now.</summary>
    public static OverlapReport Check()
    {
        var problems = new List<string>();
        var mods = ReadMods(problems);
        var overlaps = new List<PatchOverlap>();
        foreach (var feature in FeatureHost.All) overlaps.AddRange(FeatureOverlaps(feature, null, problems));
        var known = mods.Where(m => !m.Ours).Select(m => KnownMods.Match(m.Name, m.File)).OfType<KnownOverlap>().Distinct().ToList();
        return new OverlapReport(mods, overlaps, known, DateTime.Now, problems);
    }

    // Mods that load after ours apply their patches in their own initialisation, after our StartAll; MelonLoader has
    // initialised every mod by the first frame, so the start check waits a moment (game facts GF-68).
    private const float StartCheckDelay = 3f;

    /// <summary>End of the host's StartAll: checks once, a moment later, logs what overlaps and shows the start toast
    /// when it is worth one.</summary>
    internal static void CheckAfterStart() => Scheduler.HostAfter("mod overlap check", StartCheckDelay, CheckNow);

    /// <summary>The start check now (also the dev `mods simulate`).</summary>
    internal static void CheckNow()
    {
        try
        {
            var report = Check();
            foreach (var overlap in report.Overlaps) Remember(overlap);
            foreach (var known in report.Known) Log.Info($"Known overlap: {known.Line}");
            foreach (string problem in report.Problems) Log.Warn($"Mod overlap check: {problem}");
            if (!report.ToastWorthy) return;
            Toasts.Show("host", $"{JoinNames(report.OverlappingNames())} overlap{(report.OverlappingNames().Count == 1 ? "s" : string.Empty)} {BuildInfo.Name}; see Config › General › Diagnostics › Mods.", ToastKind.Warning);
        }
        catch (Exception e)
        {
            Log.WarnThrottled("modoverlap:start", $"Mod overlap check failed: {e.Message}");
        }
    }

    /// <summary>After a demand group is applied: rechecks that group's targets and logs any overlap not seen before.</summary>
    internal static void CheckMethod(Feature feature, string group)
    {
        try
        {
            foreach (var overlap in FeatureOverlaps(feature, group, new List<string>())) Remember(overlap);
        }
        catch (Exception e)
        {
            Log.WarnThrottled("modoverlap:group", $"Mod overlap check of {feature.Name} failed: {e.Message}");
        }
    }

    private static void Remember(PatchOverlap overlap)
    {
        foreach (var other in overlap.Others)
        {
            if (s_logged.Add($"{overlap.Method}|{overlap.Feature}|{other.Owner}|{other.Kind}"))
                Log.Warn($"{overlap.Method} ({overlap.Feature}) is also patched by {other.Owner} ({other.Kind})");
        }
    }

    // ---- Mods ----

    private static List<LoadedMod> ReadMods(List<string> problems)
    {
        var mods = new List<LoadedMod>();
        Read(problems, "mods", () => mods.AddRange(MelonMod.RegisteredMelons.Select(m => Describe(m, "mod"))));
        Read(problems, "plugins", () => mods.AddRange(MelonPlugin.RegisteredMelons.Select(m => Describe(m, "plugin"))));
        Read(problems, "failed mods", () =>
        {
            foreach (var assembly in MelonAssembly.LoadedAssemblies)
            {
                foreach (var rotten in assembly.RottenMelons)
                    mods.Add(new LoadedMod(rotten.type?.Name ?? Unknown, Unknown, Unknown, FileOf(assembly), "failed", false) { Error = rotten.errorMessage });
            }
        });
        // Under the dev loader this copy isn't a registered melon; the dev loader is, and lists itself above.
        if (Runtime.Hosted)
            mods.Insert(0, new LoadedMod(BuildInfo.Name, BuildInfo.Version, BuildInfo.Author, "EpochAssist.dll", "dev-loaded", true));
#if DEV
        foreach (string name in Simulated) mods.Add(new LoadedMod(name, "(simulated)", Unknown, name + ".dll", "mod", false));
#endif
        return mods;
    }

    private static LoadedMod Describe(MelonBase melon, string kind)
    {
        string name = Safe(() => melon.Info.Name), version = Safe(() => melon.Info.Version), author = Safe(() => melon.Info.Author);
        string file = Safe(() => FileOf(melon.MelonAssembly));
        bool ours = Safe(() => melon.MelonAssembly.Assembly == typeof(Runtime).Assembly || (melon.MelonAssembly.Assembly.GetName().Name ?? string.Empty).StartsWith("EpochAssist", StringComparison.Ordinal));
        return new LoadedMod(name, version, author, file, kind, ours);
    }

    // The file name only, never the path (Diagnostics scrubbing).
    private static string FileOf(MelonAssembly assembly) => assembly.Location is { Length: > 0 } location ? Path.GetFileName(location) : Unknown;

    // ---- Patch overlaps ----

    // A feature's targets (or one group's), each method that another owner patches too.
    private static IEnumerable<PatchOverlap> FeatureOverlaps(Feature feature, string? onlyGroup, List<string> problems)
    {
        var overlaps = new List<PatchOverlap>();
        List<PatchTarget> targets;
        try
        {
            if (!s_targets.TryGetValue(feature.Id, out targets!)) s_targets[feature.Id] = targets = Compat.PatchTargets(feature);
        }
        catch (Exception e)
        {
            problems.Add($"(could not read: {feature.Id} patch targets, {e.Message})");
            return overlaps;
        }
        foreach (var target in targets)
        {
            if (onlyGroup != null && target.Group != onlyGroup) continue;
            bool demand = target.Group != null && Array.IndexOf(feature.DemandGroups, target.Group) >= 0;
            bool applied = feature.State == FeatureState.Running && (!demand || feature.Record.DemandGroupsOn.Contains(target.Group!));
            foreach (var method in target.Matches)
            {
                var others = new List<OtherPatch>();
                Read(problems, $"patches of {target.Name}", () => others.AddRange(OtherPatches(method)));
                if (others.Count > 0) overlaps.Add(new PatchOverlap(target.Name, feature.Id, applied, others));
            }
        }
        return overlaps;
    }

    private static IEnumerable<OtherPatch> OtherPatches(MethodBase method)
    {
        var info = HarmonyLib.Harmony.GetPatchInfo(method);
        if (info == null) yield break;
        foreach (var (kind, patches) in new[] { ("prefix", info.Prefixes), ("postfix", info.Postfixes), ("transpiler", info.Transpilers), ("finalizer", info.Finalizers) })
        {
            foreach (var patch in patches)
            {
                if (IsOurs(patch)) continue;
                yield return new OtherPatch(OwnerName(patch), kind);
            }
        }
    }

    // Ours: our owner ids, or a patch method in this assembly (two of our features on one method aren't an overlap).
    private static bool IsOurs(Patch patch) =>
        patch.owner.StartsWith("EpochAssist.", StringComparison.Ordinal) || patch.PatchMethod?.DeclaringType?.Assembly == typeof(Runtime).Assembly;

    // The mod the patch method belongs to, however the mod names its Harmony instance; the owner id when that fails.
    private static string OwnerName(Patch patch)
    {
        try
        {
            if (patch.PatchMethod != null && MelonAssembly.GetMelonAssemblyOfMember(patch.PatchMethod) is { } assembly
                && assembly.LoadedMelons.FirstOrDefault() is { } melon && melon.Info.Name is { Length: > 0 } name) return name;
        }
        catch (Exception)
        {
            // Falls back to the owner id.
        }
        return patch.owner;
    }

    // ---- Helpers ----

    private static void Read(List<string> problems, string what, Action read)
    {
        try
        {
            read();
        }
        catch (Exception e)
        {
            problems.Add($"(could not read: {what}, {e.GetType().Name}: {e.Message})");
        }
    }

    private static string Safe(Func<string> read)
    {
        try
        {
            return read() is { Length: > 0 } value ? value : Unknown;
        }
        catch (Exception)
        {
            return Unknown;
        }
    }

    private static bool Safe(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return false;
        }
    }

    // "A", "A and B", "A, B and C".
    private static string JoinNames(List<string> names) =>
        names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
}
