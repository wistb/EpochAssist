using UnityEngine;

namespace EpochAssist.Core;

/// <summary>
/// The mod's one way to read keys, mouse buttons, the pointer and window focus. Each read is Unity's own.
/// </summary>
internal static class InputState
{
    public static bool Key(KeyCode key) => Input.GetKey(key);

    public static bool KeyDown(KeyCode key) => Input.GetKeyDown(key);

    public static bool Button(int button) => Input.GetMouseButton(button);

    public static bool ButtonDown(int button) => Input.GetMouseButtonDown(button);

    /// <summary>Any key or mouse button 0–2 held (keys pressed this frame included).</summary>
    public static bool AnyHeld => Input.anyKey || Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2);

    /// <summary>The pointer in screen pixels, bottom-left origin, as Input.mousePosition.</summary>
    public static Vector3 Pointer => Input.mousePosition;

    /// <summary>Whether input reaches the game: the window has focus.</summary>
    public static bool Focused => Application.isFocused;

    /// <summary>The window's own focus, for rules about focus changing hands.</summary>
    public static bool RealFocused => Application.isFocused;
}
