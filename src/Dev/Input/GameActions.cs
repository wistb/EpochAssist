using HarmonyLib;
using Il2CppRewired;
using UnityEngine;

namespace EpochAssist.Dev;

/// <summary>
/// The game reads its actions (skills, movement, interact) from Rewired by action id, and Rewired takes its keyboard
/// from raw input, which the bridge's messages don't reach. While a batch injects input, an action the game's own
/// keyboard map binds to a key the bridge holds reads as held (and as pressed or released on that key's edge frame),
/// so the game's logic runs as for a real key, including the mod's input lock. The game also acts on input only while
/// the window has focus, which GameFocus covers.
/// </summary>
internal static class GameActions
{
    /// <summary>The optional requirement group of the patches.</summary>
    public const string Group = "input: game actions";

    // Rewired action ids the game uses stay well under this.
    private const int MaxActionId = 400;

    private static Dictionary<int, KeyCode>? s_actionKeys;

    /// <summary>Drops the cached bindings, so a batch sees the game's current keys.</summary>
    public static void Reset() => s_actionKeys = null;

    private static KeyCode KeyFor(Player player, int actionId)
    {
        if (s_actionKeys == null)
        {
            s_actionKeys = new Dictionary<int, KeyCode>();
            var maps = player.controllers?.maps;
            if (maps != null)
            {
                for (int id = 0; id < MaxActionId; id++)
                {
                    var map = maps.GetFirstElementMapWithAction(ControllerType.Keyboard, id, true);
                    if (map != null && map.keyCode != KeyCode.None) s_actionKeys[id] = map.keyCode;
                }
            }
        }
        return s_actionKeys.TryGetValue(actionId, out var key) ? key : KeyCode.None;
    }

    // Only the game's single input player, and only while the bridge injects.
    private static KeyCode Injected(Player player, int actionId) =>
        VirtualInput.Active && player.id == 0 ? KeyFor(player, actionId) : KeyCode.None;

    [HarmonyPatch(typeof(Player), nameof(Player.GetButton), typeof(int))]
    internal static class ButtonPatch
    {
        private static void Postfix(Player __instance, int __0, ref bool __result)
        {
            if (!__result && Injected(__instance, __0) is var key && key != KeyCode.None) __result = VirtualInput.Key(key);
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.GetButtonDown), typeof(int))]
    internal static class ButtonDownPatch
    {
        private static void Postfix(Player __instance, int __0, ref bool __result)
        {
            if (!__result && Injected(__instance, __0) is var key && key != KeyCode.None) __result = VirtualInput.KeyDown(key);
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.GetButtonUp), typeof(int))]
    internal static class ButtonUpPatch
    {
        private static void Postfix(Player __instance, int __0, ref bool __result)
        {
            if (!__result && Injected(__instance, __0) is var key && key != KeyCode.None) __result = VirtualInput.KeyUp(key);
        }
    }

    // Skills aim where the game's input manager says the mouse is, which is Unity's real cursor; while the bridge has
    // a pointer set, they aim there instead.
    [HarmonyPatch(typeof(Il2Cpp.EpochInputManager), nameof(Il2Cpp.EpochInputManager.GetMousePosition))]
    internal static class MousePositionPatch
    {
        private static void Postfix(ref Vector3 __result)
        {
            if (VirtualInput.Active && VirtualInput.Pointer != null && VirtualInput.ScreenPointer is { } p) __result = p;
        }
    }
}
