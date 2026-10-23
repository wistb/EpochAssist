using HarmonyLib;
using Il2Cpp;

namespace EpochAssist.Core;

// Keeps game input off while the menu is open or the cursor is over the HUD.
internal static class InputGate
{
    public static bool Menu;
    public static bool Hud;

    private static bool s_blocked;
    private static bool s_before;

    public static bool Blocked => s_blocked;

    /// <summary>Gives input back to the game, whatever was blocking it.</summary>
    public static void Release()
    {
        Menu = false;
        Hud = false;
        Apply();
    }

    public static void Apply()
    {
        bool block = Menu || Hud;
        if (block == s_blocked) return;

        var input = EpochInputManager.instance;
        if (input == null) return;
        // forceDisableInput is the switch the game honours for its own controls; the camera's wheel zoom is held by the patch below.
        if (block)
        {
            s_before = input.forceDisableInput;
            input.forceDisableInput = true;
        }
        else
        {
            input.forceDisableInput = s_before;
        }
        s_blocked = block;
    }

    /// <summary>Patches owned by the menu feature (see MenuFeature).</summary>
    internal static class Patches
    {
        // The camera reads the wheel in Update and moves targetZoom (currentZoom eases after it); supressZoomInput
        // does not stop that. While blocked, both are put back as they were before Update ran.
        [HarmonyPatch(typeof(CameraManager), nameof(CameraManager.Update))]
        private static class CameraUpdate_Patch
        {
            private static void Prefix(CameraManager __instance, out (float Target, float Current) __state) =>
                __state = (__instance.targetZoom, __instance.currentZoom);

            private static void Postfix(CameraManager __instance, (float Target, float Current) __state)
            {
                if (!s_blocked) return;
                __instance.targetZoom = __state.Target;
                __instance.currentZoom = __state.Current;
            }
        }
    }
}
