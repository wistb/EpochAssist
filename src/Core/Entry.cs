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

public class Entry : MelonMod
{
    public override void OnInitializeMelon()
    {
        Config.Init();
        FeatureHost.Register(new MenuFeature(), new HudFeature(), new AutoCastFeature());
        FeatureHost.StartAll();
        Log.Info($"{BuildInfo.Name} {BuildInfo.Version} loaded");
    }

    public override void OnUpdate()
    {
        FeatureHost.UpdateAll();
        InputGate.Apply();
    }

    public override void OnGUI() => FeatureHost.GuiAll();

    public override void OnSceneWasLoaded(int buildIndex, string sceneName) => OnSceneChanged();

    public override void OnSceneWasUnloaded(int buildIndex, string sceneName) => OnSceneChanged();

    public override void OnApplicationQuit() => Config.Save();

    public override void OnDeinitializeMelon() => Shutdown();

    /// <summary>Undoes everything the mod did: features stopped and unpatched, input given back, textures freed.</summary>
    public static void Shutdown()
    {
        FeatureHost.StopAll();
        InputGate.Release();
        Theme.Release();
        Config.Save();
    }

    private static void OnSceneChanged()
    {
        Player.Reset();
        FeatureHost.SceneChangedAll();
    }
}
