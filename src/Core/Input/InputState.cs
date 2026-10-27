using UnityEngine;

namespace EpochAssist.Core;

/// <summary>
/// The mod's one way to read keys, mouse buttons, the pointer and window focus. Each read is Unity's own; a dev build
/// also counts the keys and pointer the dev bridge injects, so bridge checks run the mod's gates and scopes as real
/// input does.
/// </summary>
internal static class InputState
{
    public static bool Key(KeyCode key) =>
        Input.GetKey(key)
#if DEV
        || Dev.VirtualInput.Key(key)
#endif
        ;

    public static bool KeyDown(KeyCode key) =>
        Input.GetKeyDown(key)
#if DEV
        || Dev.VirtualInput.KeyDown(key)
#endif
        ;

    // Dev bridge clicks go to the game window as messages, so Unity's own reads already see their buttons.
    public static bool Button(int button) => Input.GetMouseButton(button);

    public static bool ButtonDown(int button) => Input.GetMouseButtonDown(button);

    /// <summary>Any key or mouse button 0–2 held (keys pressed this frame included).</summary>
    public static bool AnyHeld =>
        Input.anyKey || Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2)
#if DEV
        || Dev.VirtualInput.AnyHeld
#endif
        ;

    /// <summary>The pointer in screen pixels, bottom-left origin, as Input.mousePosition.</summary>
    public static Vector3 Pointer
    {
        get
        {
#if DEV
            if (Dev.VirtualInput.ScreenPointer is { } injected) return injected;
#endif
            return Input.mousePosition;
        }
    }

    /// <summary>Whether input reaches the game: the window has focus, or (dev) a bridge batch is injecting input.</summary>
    public static bool Focused =>
        Application.isFocused
#if DEV
        || Dev.VirtualInput.Active
#endif
        ;

    /// <summary>The window's own focus, for rules about focus changing hands.</summary>
    public static bool RealFocused => Application.isFocused;
}
