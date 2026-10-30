#if DEV
using EpochAssist.Dev;
#endif
using EpochAssist.Features;

namespace EpochAssist.Core;

/// <summary>A part of the mod the player chooses to load: built and registered with the host only when chosen. Its
/// settings are registered on every start either way, so profiles and exports keep them.</summary>
internal sealed class Module
{
    public Module(string id, string name, string description, Func<Feature> create, Action registerSettings)
    {
        Id = id;
        Name = name;
        Description = description;
        Create = create;
        RegisterSettings = registerSettings;
    }

    /// <summary>The feature's id, and the choice's.</summary>
    public string Id { get; }
    public string Name { get; }
    /// <summary>One line on the Modules page and the first-start form.</summary>
    public string Description { get; }
    public Func<Feature> Create { get; }
    public Action RegisterSettings { get; }
    /// <summary>Dev builds under the dev loader: dev features that read this module, built with it.</summary>
    public Func<Feature>[] DevFeatures { get; init; } = Array.Empty<Func<Feature>>();
}

/// <summary>
/// The catalogue of modules, in display order, and which ones this start loaded. Fixed at start, except that answering
/// the first-start choice loads the chosen ones once. Core features aren't in it.
/// </summary>
internal static class Modules
{
    public static readonly IReadOnlyList<Module> All = new Module[]
    {
        new("macros", "Macros", "Casts skills by conditional rules: named conditions and custom rulesets.", () => new MacrosFeature(), MacroSettings.Register),
        new("planner", "Planner", "Imports Maxroll or Last Epoch Tools planners and displays them on game interfaces", () => new PlannerFeature(), PlannerSettings.Register),
        new("stash", "Stash", "Stash layout presets and application. Uses uniques in Planner if any planners are imported.", () => new StashFeature(), StashSettings.Register)
        {
#if DEV
            DevFeatures = new Func<Feature>[] { () => new StashTraceFeature() },
#endif
        },
        new("interact", "Interact", "Pick up and open nearby interactables automatically.", () => new InteractFeature(), InteractSettings.Register)
        {
#if DEV
            DevFeatures = new Func<Feature>[] { () => new InteractTraceFeature() },
#endif
        },
        new("mapreveal", "Map Reveal", "Fully reveal a zone in the map overlay. Reskin player icons and map outlines.", () => new MapRevealFeature(), MapRevealSettings.Register),
        // Its sections are in the General group, registered with it.
        new("sessions", "Sessions", "Record game sessions and display statistics in the mini HUD.", () => new SessionsFeature(), () => { }),
    };

    private static readonly HashSet<string> s_loaded = new();
    // Built while the first-start choice is open, outside the host: never registered, started or shown.
    private static readonly List<(Module Module, Feature[] Features)> s_pending = new();

    /// <summary>Registered with the host this start (running, unavailable or faulted alike).</summary>
    public static bool Loaded(string id) => s_loaded.Contains(id);

    /// <summary>The first-start choice is open: every module is built, none registered.</summary>
    public static bool Pending => s_pending.Count > 0;

    /// <summary>The stored choice differs from what this start loaded.</summary>
    public static bool RestartNeeded(string id) => ModuleChoices.Choice(id) != Loaded(id);

    public static Module? Find(string id) => All.FirstOrDefault(m => m.Id == id);

    /// <summary>"Macros is not loaded" for a module's id, or null while it is loaded.</summary>
    public static string? NotLoaded(string id) => Loaded(id) ? null : $"{Find(id)?.Name ?? id} is not loaded";

    /// <summary>Modules that loaded late (the first-start answer), after they started; the menu adds their pages.</summary>
    public static event Action<IReadOnlyList<string>>? LoadedLate;

    /// <summary>Every module's settings, as data, loaded or not.</summary>
    public static void RegisterAllSettings()
    {
        foreach (var module in All) module.RegisterSettings();
    }

    /// <summary>The features of the modules chosen to load; while the first-start choice is open, none (every module
    /// waits in Pending).</summary>
    public static Feature[] CreateChosen()
    {
        s_loaded.Clear();
        s_pending.Clear();
        var chosen = new List<Feature>();
        foreach (var module in All)
        {
            if (!ModuleChoices.Answered)
            {
                s_pending.Add((module, Build(module)));
                continue;
            }
            if (!ModuleChoices.Choice(module.Id)) continue;
            chosen.AddRange(Build(module));
            s_loaded.Add(module.Id);
        }
        Log.Info($"Modules: {ModuleChoices.Decision}" + (ModuleChoices.Answered ? $"; loaded {Names(m => s_loaded.Contains(m.Id))}; not loaded {Names(m => !s_loaded.Contains(m.Id))}" : string.Empty));
        return chosen.ToArray();
    }

    /// <summary>The first-start answer: stores the choices, then registers and starts the chosen modules now. The others
    /// are dropped; they never touched the game.</summary>
    public static void LoadPending(IReadOnlyCollection<string> ids)
    {
        if (!Pending) return;
        ModuleChoices.Answer(ids);
        var features = new List<Feature>();
        var loaded = new List<string>();
        foreach (var (module, built) in s_pending)
        {
            if (!ids.Contains(module.Id)) continue;
            features.AddRange(built);
            s_loaded.Add(module.Id);
            loaded.Add(module.Id);
        }
        s_pending.Clear();
        Toasts.Dismiss("modules");
        Config.Save();
        Log.Info($"Modules: chosen; loaded {Names(m => s_loaded.Contains(m.Id))}; not loaded {Names(m => !s_loaded.Contains(m.Id))}");
        if (features.Count == 0) return;
        FeatureHost.Add(features.ToArray());
        foreach (string id in loaded) SettingsRegistry.RegisterActions(id);
        LoadedLate?.Invoke(loaded);
    }

    public static void Shutdown()
    {
        s_pending.Clear();
        s_loaded.Clear();
        LoadedLate = null;
    }

    // The module's feature and, under the dev loader, its dev features; their settings are registered already.
    private static Feature[] Build(Module module)
    {
        var features = new List<Feature> { module.Create() };
        if (Runtime.Hosted) features.AddRange(module.DevFeatures.Select(create => create()));
        foreach (var feature in features) feature.Record.SettingsRegistered = true;
        return features.ToArray();
    }

    private static string Names(Func<Module, bool> which)
    {
        var names = All.Where(which).Select(m => m.Id).ToList();
        return names.Count == 0 ? "none" : string.Join(", ", names);
    }
}
