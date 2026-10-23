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

    public static void Init(bool hosted)
    {
        Hosted = hosted;
        LoadedAt = DateTime.Now;
        Config.Init();
        FeatureHost.Register(new GameStateFeature(), new MenuFeature(), new HudFeature(), new InputGateFeature(), new AutoCastFeature());
        if (hosted) FeatureHost.Register(new DevBridgeFeature());
        FeatureHost.StartAll();
        Log.Info($"{BuildInfo.Name} {BuildInfo.Version} build {BuildId} loaded{(hosted ? " (dev)" : string.Empty)}");
    }

    public static void Update()
    {
        FeatureHost.UpdateAll();
        Config.SaveIfDirty();
    }

    public static void OnGUI() => FeatureHost.GuiAll();

    private static string s_lastScene = string.Empty;

    // MelonLoader reports both loads and unloads; pass on only a change of the active scene, once.
    public static void SceneChanged()
    {
        string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? string.Empty;
        if (string.Equals(scene, s_lastScene, StringComparison.Ordinal)) return;
        s_lastScene = scene;
        FeatureHost.SceneChangedAll();
    }

    public static void Save() => Config.Save();

    /// <summary>Undoes everything the mod did: features stopped and unpatched, input given back, textures freed.</summary>
    public static void Shutdown()
    {
        FeatureHost.StopAll();
        InputGate.Release();
        Theme.Release();
        Draw.Release();
        Config.Save();
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
