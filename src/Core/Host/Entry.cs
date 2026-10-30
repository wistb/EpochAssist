#if DEV
using EpochAssist.Dev;
#endif
using EpochAssist.Features;
using EpochAssist.Ui;
using MelonLoader;

[assembly: MelonInfo(typeof(EpochAssist.Core.Entry), EpochAssist.Core.BuildInfo.Name, EpochAssist.Core.BuildInfo.Version, EpochAssist.Core.BuildInfo.Author)]
[assembly: MelonGame("Eleventh Hour Games", "Last Epoch")]
// Each feature applies its own patches through FeatureHost.
[assembly: HarmonyDontPatchAll]

namespace EpochAssist.Core;

public static class BuildInfo
{
    public const string Name = "Epoch Assist";
    public const string Author = "Crash";
    public const string Version = "0.1.0";
    /// <summary>The game version this build was tested against; the release script sets it, empty until then.</summary>
    public const string TestedGameVersion = "";
}

/// <summary>
/// The mod's whole lifetime. MelonLoader drives it through <see cref="Entry"/> in a release;
/// in development the dev loader finds this class by name and drives it instead, so keep these signatures.
/// </summary>
public static class Runtime
{
    /// <summary>True when running under the dev loader.</summary>
    public static bool Hosted { get; private set; }

    /// <summary>When this copy of the mod started; under the dev loader it changes with every reload.</summary>
    public static DateTime LoadedAt { get; private set; }

    /// <summary>Six hex characters stamped at compile time; every build has its own.</summary>
    public static string BuildId { get; } =
        typeof(Runtime).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildId")?.Value ?? "unknown";

    /// <summary>A text field has focus; the dev loader reads it by name so Ctrl+F5 doesn't reload while typing.</summary>
    public static bool Typing => GameState.Typing;

    /// <summary>
    /// Set by the dev reload command; the dev loader reads it by name and reloads on its next update. Reading it clears
    /// it, so a reload that can't go ahead isn't tried every frame.
    /// </summary>
    public static bool ReloadRequested
    {
        get
        {
            bool requested = s_reloadRequested;
            s_reloadRequested = false;
            return requested;
        }
        internal set => s_reloadRequested = value;
    }
    private static bool s_reloadRequested;

    public static void Init(bool hosted)
    {
        Hosted = hosted;
        LoadedAt = DateTime.Now;
        // The UI layer's sections of the General group, declared before it registers.
        HudSettings.Declare();
        LookSettings.Declare();
        KeySettings.Declare();
        SessionSettings.Declare();
        Config.Init();
        // Features read rarity and danger colours from Core; the theme keeps them up to date.
        ColourRoles.Sync = Theme.SyncRoles;
        Edits.RecoverSaved();
        CoreCommands.Register();
        UiCommands.Register();
        // The host orders features by their Needs and RunsAfter (FeatureHost.StartAll); this list only breaks ties, which
        // are shown in this order.
        FeatureHost.Register(new GameStateFeature(), new GameDataFeature(), new PlayerModelFeature(), new WorldModelFeature(), new VisualsFeature(), new GameUiFeature(), new GameEventsFeature(), new ProfilesFeature(), new SessionsFeature(), new HotkeysFeature(), new MenuFeature(), new HudFeature(), new ToastFeature(), new DiagnosticsFeature(), new ConsoleFeature(),
            new InputGateFeature(),
            new DispatcherFeature(), new MacrosFeature(), new InteractFeature(), new MapRevealFeature(), new PlannerFeature(), new StashFeature());
#if DEV
        // Dev code isn't compiled into release builds (src/Dev/).
        if (hosted) FeatureHost.Register(new DevBridgeFeature(), new CrashTestFeature(), new InteractTraceFeature(), new StashTraceFeature(), new DevWindowFeature());
#endif
        FeatureHost.StartAll();
        Log.Info($"{BuildInfo.Name} {BuildInfo.Version} build {BuildId} loaded{(hosted ? " (dev)" : string.Empty)}");
    }

    public static void Update()
    {
        FeatureHost.UpdateAll();
        FeatureHost.LateUpdateAll();
        Scheduler.RunHostJobs();
        Edits.Update();
        int level = Config.LogLevel.Value;
        if (level != Log.Level)
        {
            Log.Level = level;
            Log.Info($"Log level: {Log.LevelName(level).ToLowerInvariant()}");
        }
    }

    public static void OnGUI() => FeatureHost.GuiAll();

    private static string s_lastScene = string.Empty;

    // MelonLoader reports both loads and unloads; pass on only a change of the active scene, once.
    public static void SceneChanged()
    {
        string scene = GameState.ActiveSceneName;
        if (string.Equals(scene, s_lastScene, StringComparison.Ordinal)) return;
        s_lastScene = scene;
        FeatureHost.SceneChangedAll();
    }

    public static void Save()
    {
        // A quit ends the session normally; written before the game closes.
        if (FeatureHost.Find("sessions") is { State: FeatureState.Running }) Sessions.EndForQuit();
        Profiles.Save();
        Config.Save();
    }

    /// <summary>Undoes everything the mod did: features stopped and unpatched, input given back, textures freed.</summary>
    public static void Shutdown()
    {
        FeatureHost.StopAll();
        Edits.RestoreAll();
        InputGate.Release();
        Theme.Release();
        ColourRoles.Sync = null;
        Draw.Release();
        Config.Save();
        Scheduler.DropHostJobs();
    }
}

public class Entry : MelonMod
{
    public override void OnInitializeMelon() => Runtime.Init(hosted: false);
    public override void OnUpdate() => Runtime.Update();
    public override void OnGUI() => Runtime.OnGUI();
    public override void OnSceneWasLoaded(int buildIndex, string sceneName) => Runtime.SceneChanged();
    public override void OnSceneWasUnloaded(int buildIndex, string sceneName) => Runtime.SceneChanged();
    public override void OnApplicationQuit() => Runtime.Save();
    public override void OnDeinitializeMelon() => Runtime.Shutdown();
}
