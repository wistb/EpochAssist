using EpochAssist.Ui;
using MelonLoader;

[assembly: MelonInfo(typeof(EpochAssist.Core.Entry), EpochAssist.Core.BuildInfo.Name, EpochAssist.Core.BuildInfo.Version, EpochAssist.Core.BuildInfo.Author)]
[assembly: MelonGame("Eleventh Hour Games", "Last Epoch")]
[assembly: HarmonyDontPatchAll]

namespace EpochAssist.Core;

public static class BuildInfo
{
    public const string Name = "Epoch Assist";
    public const string Author = "wistb";
    public const string Version = "0.1.0";
}

public class Entry : MelonMod
{
    private static HarmonyLib.Harmony? s_harmony;

    public override void OnInitializeMelon()
    {
        Config.Init();
        s_harmony = new HarmonyLib.Harmony("EpochAssist");
        Log.Info($"{BuildInfo.Name} {BuildInfo.Version} loaded");
    }

    public override void OnUpdate()
    {
        Run("Menu", Menu.OnUpdate);
    }

    public override void OnGUI()
    {
        Run("Menu", Menu.OnGUI);
    }

    public override void OnApplicationQuit()
    {
        Config.Save();
    }

    private static void Run(string feature, Action action)
    {
        try { action(); }
        catch (Exception e) { Log.ErrorThrottled(feature, $"{feature}: {e}"); }
    }
}
