using EpochAssist.Features;
using EpochAssist.Ui;
using HarmonyLib;
using MelonLoader;

[assembly: MelonInfo(typeof(EpochAssist.Core.Entry), EpochAssist.Core.BuildInfo.Name, EpochAssist.Core.BuildInfo.Version, EpochAssist.Core.BuildInfo.Author)]
[assembly: MelonGame("Eleventh Hour Games", "Last Epoch")]
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
        AutoCastSettings.Init();
        ApplyPatches(new HarmonyLib.Harmony("EpochAssist"));
        Log.Info($"{BuildInfo.Name} {BuildInfo.Version} loaded");
    }

    public override void OnUpdate()
    {
        Run("Menu", Menu.OnUpdate);
        Run("Hud", Hud.OnUpdate);
        Run("InputGate", InputGate.Apply);
        Run("AutoCast", AutoCast.OnUpdate);
    }

    public override void OnGUI()
    {
        Run("AutoCast.OnGUI", AutoCast.OnGUI);
        Run("Hud.OnGUI", Hud.OnGUI);
        Run("Menu.OnGUI", Menu.OnGUI);
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName) => OnSceneChanged();

    public override void OnSceneWasUnloaded(int buildIndex, string sceneName) => OnSceneChanged();

    public override void OnApplicationQuit() => Config.Save();

    private static void OnSceneChanged()
    {
        Player.Reset();
        Run("AutoCast.OnSceneChanged", AutoCast.OnSceneChanged);
    }

    // One class at a time, so a stale target can't stop the rest from binding.
    private static void ApplyPatches(HarmonyLib.Harmony harmony)
    {
        int failed = 0;
        foreach (Type type in AccessTools.GetTypesFromAssembly(typeof(Entry).Assembly))
        {
            try
            {
                harmony.CreateClassProcessor(type).Patch();
            }
            catch (Exception e)
            {
                failed++;
                Log.Error($"Patch failed: {type.FullName}: {e.InnerException?.Message ?? e.Message}");
            }
        }
        if (failed > 0) Log.Warn($"{failed} patch class(es) failed");
    }

    private static void Run(string feature, Action action)
    {
        try { action(); }
        catch (Exception e) { Log.ErrorThrottled(feature, $"{feature}: {e}"); }
    }
}
