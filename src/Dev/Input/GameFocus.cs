using EpochAssist.Core;
using HarmonyLib;
using UnityEngine;

namespace EpochAssist.Dev;

/// <summary>
/// The game acts on its input only while the window has focus, and turns its own input switch
/// (EpochInputManager.forceDisableInput) on whenever the window loses focus, off when it gets it back. While a bridge
/// batch injects input, the window reads as focused to everything that asks Unity, and the switch is held off unless
/// the mod's own menu or HUD lock wants it, as the spec decides for injected input; after the batch the switch goes
/// back to what the game would have. The game UI's input module likewise stops taking the pointer once the window has
/// lost focus (its m_HasFocus); it is told it has focus for the batch too. The real focus stays readable for the mod's rules about focus changing hands.
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

    // The game's switch was on when the batch began, and this turned it off.
    private static bool s_heldOff;
    // The game UI's input module ignores the pointer after the window has lost focus; this told it it has focus.
    private static Il2CppRewired.Integration.UnityUI.RewiredStandaloneInputModule? s_module;

    /// <summary>Each frame, from the dev bridge.</summary>
    public static void Update()
    {
        var input = Game.Input;
        if (!Game.Alive(input)) return;
        if (VirtualInput.Active)
        {
            if (input!.forceDisableInput && !InputGate.Blocking)
            {
                input.forceDisableInput = false;
                s_heldOff = true;
            }
            if (s_module == null && UiModule() is { m_HasFocus: false } module)
            {
                module.m_HasFocus = true;
                s_module = module;
            }
        }
        else if (s_heldOff || s_module != null)
        {
            Release();
        }
    }

    /// <summary>Gives the game its switch back as it would have it: on while the window is really unfocused.</summary>
    public static void Release()
    {
        bool real = Real();
        if (s_module != null)
        {
            if (Game.Alive(s_module) && !real) s_module.m_HasFocus = false;
            s_module = null;
        }
        if (!s_heldOff) return;
        s_heldOff = false;
        var input = Game.Alive(Game.Input) ? Game.Input : null;
        if (input != null && !real && !InputGate.Blocking) input.forceDisableInput = true;
    }

    private static Il2CppRewired.Integration.UnityUI.RewiredStandaloneInputModule? UiModule()
    {
        var current = UnityEngine.EventSystems.EventSystem.current;
        var module = Game.Alive(current) ? current!.currentInputModule : null;
        return Game.Alive(module) ? module!.TryCast<Il2CppRewired.Integration.UnityUI.RewiredStandaloneInputModule>() : null;
    }
}
