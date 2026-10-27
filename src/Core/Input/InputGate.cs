using HarmonyLib;
using Il2Cpp;

namespace EpochAssist.Core;

/// <summary>
/// Keeps game input off while the menu is open, or while a mouse button pressed on the HUD is held (hovering alone
/// blocks nothing). Only a lock this mod took is ever released, so a lock the game holds stays the game's.
/// </summary>
internal static class InputGate
{
    public static bool Menu;
    public static bool Hud;

    private static bool s_blocking;
    private static bool s_ownsLock;
    private static bool s_menuWas;
    private static bool s_hudWas;

    /// <summary>The HUD's hit test in screen pixels (top-left origin); set by the HUD while it runs.</summary>
    public static Func<UnityEngine.Vector2, bool>? HudHitTest;
    /// <summary>The menu window's hit test in screen pixels (top-left origin); set by the menu while it runs.</summary>
    public static Func<UnityEngine.Vector2, bool>? MenuHitTest;
    /// <summary>Off when the game's UI event system can't be read (set by InputGateFeature).</summary>
    public static bool CanHoldUiEvents = true;
    // The event system this gate turned off. A disabled one is no longer EventSystem.current, so it is kept here to be
    // turned back on.
    private static UnityEngine.EventSystems.EventSystem? s_heldUiEvents;
    private static bool s_hudPress;
    private static int s_hudTailFrames;
    private static bool s_ownsMouseMoveBlock;
    // The press block outlasts the release by a few frames, in case the game acts on the release.
    private const int HudReleaseTailFrames = 3;

    /// <summary>Off when the game lacks blockMouseMovementInput (set by InputGateFeature).</summary>
    public static bool CanBlockMouseMovement = true;

    /// <summary>Off when the game lacks the members for stopping movement (set by InputGateFeature).</summary>
    public static bool CanStopMovement = true;

    /// <summary>True while the UI holds input (also what holds the camera's wheel zoom).</summary>
    public static bool Blocking => s_blocking;

    public static void Apply()
    {
        bool want = Menu || Hud;
        bool menuStarted = Menu && !s_menuWas, hudStarted = Hud && !s_hudWas;
        s_menuWas = Menu;
        s_hudWas = Hud;
        if (!want && !s_blocking) return;

        var input = Game.Input;
        if (want)
        {
            // Under the lock the game stops reading keys, so a walk started by a held direction key never gets its
            // release and carries on. The menu ends all movement; the HUD only a keyboard walk, so click-to-move
            // passing over the pill isn't cut short.
            if (menuStarted || hudStarted) StopMovement(input, keyboardOnly: !menuStarted);
            s_blocking = true;
            // Re-checked every frame: the game may clear the flag itself, for example on a scene load.
            if (input != null && !input.forceDisableInput)
            {
                input.forceDisableInput = true;
                s_ownsLock = true;
            }
            return;
        }

        if (s_ownsLock && input != null) input.forceDisableInput = false;
        s_ownsLock = false;
        s_blocking = false;
    }

    // Runs just before the game reads input each frame, so a click on the HUD is blocked in the frame it happens
    // instead of reaching the game first. The press counts only if it started on the HUD, so a held click-to-move
    // dragged across the pill is left alone.
    private static void BeforeGameInput()
    {
        var mouse = InputState.Pointer;
        bool over = HudHitTest != null && HudHitTest(new UnityEngine.Vector2(mouse.x, UnityEngine.Screen.height - mouse.y));
        bool down = InputState.ButtonDown(0) || InputState.ButtonDown(1) || InputState.ButtonDown(2);
        bool held = InputState.Button(0) || InputState.Button(1) || InputState.Button(2);

        if (down && over) s_hudPress = true;
        if (s_hudPress && !held)
        {
            s_hudPress = false;
            s_hudTailFrames = HudReleaseTailFrames;
        }
        else if (!s_hudPress && s_hudTailFrames > 0)
        {
            s_hudTailFrames--;
        }
        Hud = s_hudPress || s_hudTailFrames > 0;

        // Click-to-move is handled apart from the general lock, so while the cursor is on the HUD the game's own
        // mouse-movement block is held as well. It covers only click-to-move; keys and skills keep working.
        HoldMouseMovementBlock(over && !Menu);
        // The game's own UI (the world map, its buttons and panels) takes clicks through Unity's event system, which
        // the lock above doesn't reach: while the cursor is over the menu window or the HUD, that system is held off,
        // so a click there lands only on ours.
        var point = new UnityEngine.Vector2(mouse.x, UnityEngine.Screen.height - mouse.y);
        HoldUiEvents(over || (Menu && MenuHitTest != null && MenuHitTest(point)));
        Apply();
    }

    private static void HoldUiEvents(bool hold)
    {
        if (!CanHoldUiEvents) return;
        if (hold)
        {
            var events = UnityEngine.EventSystems.EventSystem.current;
            if (s_heldUiEvents != null || !Game.Alive(events) || !events!.enabled) return;
            events.enabled = false;
            s_heldUiEvents = events;
        }
        else if (s_heldUiEvents != null)
        {
            if (Game.Alive(s_heldUiEvents)) s_heldUiEvents.enabled = true;
            s_heldUiEvents = null;
        }
    }

    private static void HoldMouseMovementBlock(bool hold)
    {
        var input = Game.Input;
        if (input == null || !CanBlockMouseMovement) return;
        if (hold)
        {
            // Re-set every frame: the game times this flag out by itself.
            if (!input.blockMouseMovementInput)
            {
                input.blockMouseMovementInput = true;
                s_ownsMouseMoveBlock = true;
            }
        }
        else if (s_ownsMouseMoveBlock)
        {
            input.blockMouseMovementInput = false;
            s_ownsMouseMoveBlock = false;
        }
    }

    private static void StopMovement(EpochInputManager? input, bool keyboardOnly)
    {
        if (!CanStopMovement || input == null) return;
        try
        {
            var movement = input.movementProcessor;
            if (!Game.Alive(movement)) return;
            // Read before the lock is set, while the game still tracks the keys.
            bool keyHeld = input.isKeyboardMovementButtonHeld;
            if (keyboardOnly && !keyHeld) return;
            // Tested in game: only a zero keyboard direction ends a keyboard walk (what a key release sends);
            // StopActiveMovement does not, but it does end a click-to-move.
            movement.ProcessKeyboardMovement(UnityEngine.Vector2.zero, false);
            if (!keyboardOnly) movement.StopActiveMovement();
            Log.Info($"input: stopped movement ({(keyboardOnly ? "HUD" : "menu")}{(keyHeld ? ", key held" : string.Empty)})");
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("input:stop", $"Stopping movement failed: {e.Message}", 30);
        }
    }

    /// <summary>Gives input back to the game, whatever was blocking it.</summary>
    public static void Release()
    {
        Menu = false;
        Hud = false;
        s_hudPress = false;
        s_hudTailFrames = 0;
        HoldMouseMovementBlock(false);
        HoldUiEvents(false);
        Apply();
    }

    // The camera reads the wheel in Update and moves targetZoom (currentZoom eases after it); supressZoomInput
    // does not stop that. While blocking, both are put back as they were before Update ran.
    [HarmonyPatch(typeof(EpochInputManager), nameof(EpochInputManager.Update))]
    internal static class InputUpdate_Patch
    {
        private static void Prefix() => BeforeGameInput();
    }

    [HarmonyPatch(typeof(CameraManager), nameof(CameraManager.Update))]
    internal static class CameraUpdate_Patch
    {
        private static void Prefix(CameraManager __instance, out (float Target, float Current) __state) =>
            __state = (__instance.targetZoom, __instance.currentZoom);

        private static void Postfix(CameraManager __instance, (float Target, float Current) __state)
        {
            if (!s_blocking) return;
            __instance.targetZoom = __state.Target;
            __instance.currentZoom = __state.Current;
        }
    }
}
