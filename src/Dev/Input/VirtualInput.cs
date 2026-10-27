using UnityEngine;

namespace EpochAssist.Dev;

/// <summary>
/// What the dev bridge presses inside the game, with the game unfocused and no system-wide input. Keys go two ways:
/// the mod's own reads see them through InputState, and the game window gets the key messages so IMGUI text fields
/// edit as they do for real keys. The pointer and mouse buttons go as messages to the game window only, which Unity
/// turns into IMGUI events (windows included) and its mouse-button reads; the pointer is also kept here for the mod's
/// own pointer reads, since Unity's stays on the real cursor. A key change takes effect at the first read of the next
/// frame, so every reader in a frame sees the same state whatever order they run in.
/// </summary>
internal static class VirtualInput
{
    private const int ButtonCount = 3;

    private readonly record struct Change(KeyCode Key, bool Down, Vector2? Pointer, bool MovesPointer);

    private static readonly HashSet<KeyCode> s_heldKeys = new();
    private static readonly HashSet<KeyCode> s_pressedKeys = new();
    private static readonly bool[] s_postedButtons = new bool[ButtonCount];
    private static readonly List<Change> s_pending = new();
    // Keys pressed to type a character, whose window message was the character.
    private static readonly HashSet<KeyCode> s_typedKeys = new();
    private static Vector2? s_pointer;
    private static int s_tickFrame = -1;
    private static bool s_releasing;

    /// <summary>On from a batch's first injected change until its release has taken effect: focus gates count the game
    /// as focused meanwhile.</summary>
    public static bool Active { get; private set; }

    /// <summary>The injected pointer in game-window pixels from the top left, if one is set.</summary>
    public static Vector2? Pointer => s_pointer;

    public static bool Key(KeyCode key)
    {
        Tick();
        return s_heldKeys.Contains(key);
    }

    public static bool KeyDown(KeyCode key)
    {
        Tick();
        return s_pressedKeys.Contains(key);
    }

    public static bool AnyHeld
    {
        get
        {
            Tick();
            return s_heldKeys.Count > 0;
        }
    }

    /// <summary>The injected pointer in screen pixels from the bottom left, as Input.mousePosition, if one is set.</summary>
    public static Vector3? ScreenPointer
    {
        get
        {
            Tick();
            return s_pointer is { } p ? new Vector3(p.x, Screen.height - p.y, 0f) : null;
        }
    }

    // ---- The bridge verbs ----

    /// <summary>
    /// Presses or releases a key: the mod's reads see it from the next frame, and the window gets the key message now,
    /// or for typed text the character instead. Pressing a held key or releasing a free one does nothing.
    /// </summary>
    public static void QueueKey(KeyCode key, bool down, char character = '\0')
    {
        if (down == IsHeld(key)) return;
        Queue(new Change(key, down, null, false));
        // Unity makes its own character from a key message, without the Shift state ours lack; typed text goes as the
        // character alone so it arrives once and in the right case.
        if (character != '\0') s_typedKeys.Add(key);
        if (s_typedKeys.Contains(key))
        {
            if (down) WindowMessages.Character(character);
            else s_typedKeys.Remove(key);
        }
        else
        {
            WindowMessages.Key(key, down);
        }
    }

    /// <summary>Presses or releases a mouse button at the pointer, which must be set.</summary>
    public static void PostButton(int button, bool down)
    {
        if (s_postedButtons[button] == down || s_pointer is not { } p) return;
        s_postedButtons[button] = down;
        Active = true;
        WindowMessages.Button(button, down, p, Flags());
    }

    /// <summary>Moves the pointer (game-window pixels from the top left), or clears it with null.</summary>
    public static void QueuePointer(Vector2? point)
    {
        Queue(new Change(KeyCode.None, false, point, true));
        // The pointer is ours at once for the messages that follow it in the same step.
        s_pointer = point;
        if (point is { } p) WindowMessages.Move(p, Flags());
    }

    public static bool IsHeld(KeyCode key)
    {
        for (int i = s_pending.Count - 1; i >= 0; i--)
        {
            if (!s_pending[i].MovesPointer && s_pending[i].Key == key) return s_pending[i].Down;
        }
        return s_heldKeys.Contains(key);
    }

    public static bool IsHeld(int button) => s_postedButtons[button];

    private static void Queue(Change change)
    {
        s_pending.Add(change);
        s_releasing = false;
        Active = true;
    }

    // The key and button flags a mouse message carries.
    private static int Flags()
    {
        int flags = 0;
        if (s_postedButtons[0]) flags |= 0x0001;
        if (s_postedButtons[1]) flags |= 0x0002;
        if (s_postedButtons[2]) flags |= 0x0010;
        if (IsHeld(KeyCode.LeftShift) || IsHeld(KeyCode.RightShift)) flags |= 0x0004;
        if (IsHeld(KeyCode.LeftControl) || IsHeld(KeyCode.RightControl)) flags |= 0x0008;
        return flags;
    }

    /// <summary>
    /// Lets go of every key and button the bridge holds and clears the pointer, returning what was let go. Messages go
    /// out now; the mod's reads see the release next frame, which ends the active span. Immediate clears everything at
    /// once (the mod is going away).
    /// </summary>
    public static List<string> ReleaseAll(bool immediate)
    {
        var keys = s_heldKeys.Where(k => IsHeld(k)).Concat(s_pending.Where(c => !c.MovesPointer && c.Down).Select(c => c.Key)).Distinct().ToList();
        var released = keys.Select(k => k.ToString()).ToList();
        for (int b = 0; b < ButtonCount; b++)
        {
            if (s_postedButtons[b]) released.Add(ButtonName(b));
        }
        if (s_pointer != null) released.Add("pointer");
        if (!Active && released.Count == 0 && s_pending.Count == 0) return released;

        for (int b = 0; b < ButtonCount; b++) PostButton(b, false);
        foreach (var key in keys)
        {
            if (!s_typedKeys.Remove(key)) WindowMessages.Key(key, false);
        }
        s_pointer = null;
        if (immediate)
        {
            s_heldKeys.Clear();
            s_pressedKeys.Clear();
            s_pending.Clear();
            s_typedKeys.Clear();
            s_releasing = false;
            Active = false;
            return released;
        }
        s_pending.RemoveAll(c => c.Down || c.MovesPointer);
        foreach (var key in keys) s_pending.Add(new Change(key, false, null, false));
        s_pending.Add(new Change(KeyCode.None, false, null, true));
        s_releasing = Active;
        return released;
    }

    public static string ButtonName(int button) => button switch { 0 => "left", 1 => "right", _ => "middle" };

    /// <summary>What is held, for the bridge's state lines.</summary>
    public static string Describe()
    {
        Tick();
        var held = s_heldKeys.Select(k => k.ToString()).ToList();
        for (int b = 0; b < ButtonCount; b++)
        {
            if (s_postedButtons[b]) held.Add(ButtonName(b));
        }
        string pointer = s_pointer is { } p ? $"{p.x:F0} {p.y:F0}" : "none";
        return $"held {(held.Count > 0 ? string.Join(" ", held) : "none")}, pointer {pointer}, queued {s_pending.Count}, active {(Active ? "yes" : "no")}";
    }

    // ---- Frame ----

    /// <summary>Applies the changes queued before this frame, once per frame, at whichever read comes first.</summary>
    public static void Tick()
    {
        int frame = Time.frameCount;
        if (frame == s_tickFrame) return;
        s_tickFrame = frame;
        if (s_pressedKeys.Count > 0) s_pressedKeys.Clear();
        if (s_pending.Count == 0) return;

        foreach (var c in s_pending)
        {
            if (c.MovesPointer) s_pointer = c.Pointer;
            else if (c.Down && s_heldKeys.Add(c.Key)) s_pressedKeys.Add(c.Key);
            else if (!c.Down) s_heldKeys.Remove(c.Key);
        }
        s_pending.Clear();
        if (s_releasing)
        {
            s_releasing = false;
            Active = false;
        }
    }
}
