using HarmonyLib;
using UnityEngine;

namespace EpochAssist.Dev;

/// <summary>
/// The game acts on its input only while the window has focus. While a bridge batch injects input, the window reads as
/// focused to everything that asks Unity, as the spec decides for injected input; the real value stays readable for
/// the mod's rules about focus changing hands.
/// </summary>
internal static class GameFocus
{
    /// <summary>The optional requirement group of the patch.</summary>
    public const string Group = "input: game focus";

    private static bool s_lastReal;
    private static bool s_seen;

    [HarmonyPatch(typeof(Application), "get_isFocused")]
    internal static class IsFocusedPatch
    {
        private static void Postfix(ref bool __result)
        {
            s_lastReal = __result;
            s_seen = true;
            if (VirtualInput.Active) __result = true;
        }
    }

    /// <summary>The window's own focus, past the patch.</summary>
    public static bool Real()
    {
        s_seen = false;
        bool value = Application.isFocused;
        return s_seen ? s_lastReal : value;
    }
}
