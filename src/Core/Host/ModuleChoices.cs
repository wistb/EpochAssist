using MelonLoader.Utils;

namespace EpochAssist.Core;

/// <summary>
/// Which modules load: one Toggle per catalogue module in a shared group (the same in every profile), read once at start,
/// plus whether the first-start choice was made. A change takes effect at the next start; nothing here starts or stops a
/// module. The Modules page draws these toggles; they are never ordinary settings rows, quick toggles or actions.
/// </summary>
internal static class ModuleChoices
{
    // Version 2: the switches became load choices; sessions and Answered are new (version 1 had the other five).
    public static readonly SettingsGroup Group = new("EpochAssist_Features", "Modules", version: 2) { ActionKey = "features", Shared = true, Hidden = true };
    private static readonly Section Section = Group.Section("features", "Modules");
    private static readonly Dictionary<string, Toggle> s_toggles = new();
    private static Toggle? s_answered;
    private static string s_decision = string.Empty;

    /// <summary>
    /// True when nothing of the mod was ever stored: no "[EpochAssist" section in MelonPreferences.cfg and no UserData/EpochAssist
    /// folder (one holding only the dev loader's folder counts as none). Run before any group registers, since
    /// registering writes defaults.
    /// </summary>
    public static bool DetectFresh()
    {
#if DEV
        s_devStart = Environment.GetEnvironmentVariable(DevStartVariable);
        Environment.SetEnvironmentVariable(DevStartVariable, null);
        if (s_devStart == "fresh") return true;
#endif
        return FilesFresh();
    }

    /// <summary>The fresh test on the files alone: nothing in the cfg and no folder of ours.</summary>
    public static bool FilesFresh()
    {
        string userData = MelonEnvironment.UserDataDirectory;
        string folder = Path.Combine(userData, "EpochAssist");
        try
        {
            if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any(p => !string.Equals(Path.GetFileName(p), "dev", StringComparison.OrdinalIgnoreCase))) return false;
        }
        catch (Exception e)
        {
            Log.Warn($"Modules: could not read {folder}: {e.Message}");
            return false;
        }
        try
        {
            foreach (string line in File.ReadLines(Path.Combine(userData, "MelonPreferences.cfg")))
            {
                if (line.StartsWith("[EpochAssist", StringComparison.Ordinal)) return false;
            }
        }
        catch (Exception)
        {
            // Missing or unreadable: no sections.
        }
        return true;
    }

    /// <summary>Declares a toggle per module and registers the group. A fresh install starts with every module off and the
    /// choice not made yet. Once per load.</summary>
    public static void Register(bool fresh)
    {
        if (s_answered != null) return;
        foreach (var module in Modules.All) s_toggles[module.Id] = new Toggle(Section, module.Id, module.Name, true);
        s_answered = new Toggle(Section, "Answered", "Choice made", true);
        int storedVersion = -1;
#if DEV
        if (s_devStart == "upgrade") ForgetStored();
#endif
        // Stored switches keep their values; modules with nothing stored load (every one, for builds before the switches).
        Group.Migration(2, category =>
        {
            storedVersion = category.GetEntry<int>("_SettingsVersion")?.Value ?? 0;
            s_answered.Value = true;
        });
        Group.Register();
        if (fresh)
        {
            foreach (var toggle in s_toggles.Values) toggle.Value = false;
            s_answered.Value = false;
            s_decision = "fresh";
        }
        else if (!s_answered.Value) s_decision = "prompt open";
        else s_decision = storedVersion == 0 ? "upgrade" : "answered";
    }

    /// <summary>"fresh", "prompt open", "upgrade" or "answered": how this start chose, for the log.</summary>
    public static string Decision => s_decision;

    /// <summary>The first-start choice was made (or the install is older than it).</summary>
    public static bool Answered => s_answered?.Value ?? true;

    /// <summary>The stored choice: load this module at start.</summary>
    public static bool Choice(string id) => s_toggles.TryGetValue(id, out var toggle) && toggle.Value;

    public static Toggle? For(string id) => s_toggles.TryGetValue(id, out var toggle) ? toggle : null;

#if DEV
    /// <summary>Dev only: the process variable that makes the next start (a dev reload) a fresh install ("fresh") or an
    /// upgrade from before the Modules group ("upgrade"). Read once, then cleared; it outlives the reload, never the
    /// game.</summary>
    internal const string DevStartVariable = "EPOCHASSIST_DEV_MODULES_START";
    private static string? s_devStart;

    // As if nothing of the group was stored: version 0, the module choices at their default. The values are in memory
    // already (a reload), so they are reset before the group binds to them.
    private static void ForgetStored()
    {
        if (MelonLoader.MelonPreferences.GetCategory(Group.Id) is not { } category) return;
        if (category.GetEntry<int>("_SettingsVersion") is { } version) version.Value = 0;
        foreach (var module in Modules.All)
            if (category.GetEntry<bool>(module.Id) is { } entry) entry.Value = true;
        if (category.GetEntry<bool>("Answered") is { } answered) answered.Value = false;
    }
#endif

    /// <summary>The first-start answer: every module's choice, and the choice made.</summary>
    public static void Answer(IReadOnlyCollection<string> chosen)
    {
        foreach (var (id, toggle) in s_toggles) toggle.Value = chosen.Contains(id);
        if (s_answered != null) s_answered.Value = true;
    }
}
