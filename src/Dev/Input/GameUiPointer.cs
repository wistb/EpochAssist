using HarmonyLib;
using Il2CppRewired.Integration.UnityUI;
using UnityEngine;

namespace EpochAssist.Dev;

/// <summary>
/// The game's own UI takes its pointer from Rewired's UI module, which asks Unity for the mouse position; that stays on
/// the real cursor, so a bridge click would land there. While the bridge has a pointer set, the module gets it
/// instead. Its buttons already come from the bridge's posted messages, and the game's world ignores those (gameplay
/// reads the mouse through Rewired's own input), so with this in place a bridge click is safe anywhere.
/// </summary>
internal static class GameUiPointer
{
    /// <summary>The optional requirement group of the patch.</summary>
    public const string Group = "input: game ui pointer";

    public const string Member = "Rewired_UI_IMouseInputSource_get_screenPosition";

    [HarmonyPatch(typeof(RewiredPointerInputModule.UnityInputSource), Member)]
    internal static class ScreenPositionPatch
    {
        private static void Postfix(ref Vector2 __result)
        {
            if (VirtualInput.Pointer != null && VirtualInput.ScreenPointer is { } p) __result = new Vector2(p.x, p.y);
        }
    }

    /// <summary>Whether the patch is in place: the dev feature runs and the game has the member.</summary>
    public static bool Available => Core.FeatureHost.Find("dev") is { State: Core.FeatureState.Running } dev && dev.Has(Group);
}
