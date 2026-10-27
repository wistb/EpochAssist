using System.Runtime.InteropServices;
using UnityEngine;

namespace EpochAssist.Dev;

/// <summary>
/// Mouse and key messages posted to the game's own window, from inside the game. Unity builds its IMGUI events from
/// them (positions included, in windows too) and its mouse-button reads see the buttons, with the game unfocused; the
/// real cursor, focus and other windows are untouched. Unity's key reads, the wheel, the game's own UI pointer and
/// Rewired don't take them.
/// </summary>
internal static class WindowMessages
{
    private const uint WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmChar = 0x0102, WmMouseMove = 0x0200;
    private static readonly uint[] s_buttonDown = { 0x0201, 0x0204, 0x0207 };
    private static readonly uint[] s_buttonUp = { 0x0202, 0x0205, 0x0208 };

    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);

    private static IntPtr s_window;

    private static IntPtr Window
    {
        get
        {
            if (s_window == IntPtr.Zero) s_window = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            return s_window;
        }
    }

    public static void Move(Vector2 at, int flags) => Post(WmMouseMove, flags, Point(at));

    public static void Button(int button, bool down, Vector2 at, int flags) =>
        Post(down ? s_buttonDown[button] : s_buttonUp[button], flags, Point(at));

    /// <summary>A key's down or up message, for keys Windows has a code for; others are left to the mod's reads.</summary>
    public static void Key(KeyCode key, bool down)
    {
        if (VirtualKey(key) is not uint vk) return;
        uint scan = MapVirtualKey(vk, 0);
        long lParam = 1 | (scan << 16) | (Extended(key) ? 1L << 24 : 0);
        if (!down) lParam |= (1L << 30) | (1L << 31);
        Post(down ? WmKeyDown : WmKeyUp, (int)vk, (IntPtr)lParam);
    }

    public static void Character(char c) => Post(WmChar, c, (IntPtr)1);

    private static void Post(uint message, int wParam, IntPtr lParam)
    {
        if (Window == IntPtr.Zero) return;
        PostMessage(Window, message, (IntPtr)wParam, lParam);
    }

    // Client pixels from the top left, as Windows packs them.
    private static IntPtr Point(Vector2 at) => (IntPtr)(((int)at.y << 16) | ((int)at.x & 0xFFFF));

    private static bool Extended(KeyCode key) => key is KeyCode.LeftArrow or KeyCode.RightArrow or KeyCode.UpArrow or KeyCode.DownArrow
        or KeyCode.Home or KeyCode.End or KeyCode.PageUp or KeyCode.PageDown or KeyCode.Insert or KeyCode.Delete
        or KeyCode.RightControl or KeyCode.RightAlt or KeyCode.KeypadEnter;

    private static uint? VirtualKey(KeyCode key)
    {
        if (key >= KeyCode.A && key <= KeyCode.Z) return (uint)('A' + (key - KeyCode.A));
        if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9) return (uint)('0' + (key - KeyCode.Alpha0));
        if (key >= KeyCode.Keypad0 && key <= KeyCode.Keypad9) return 0x60u + (uint)(key - KeyCode.Keypad0);
        if (key >= KeyCode.F1 && key <= KeyCode.F15) return 0x70u + (uint)(key - KeyCode.F1);
        return key switch
        {
            KeyCode.Backspace => 0x08, KeyCode.Tab => 0x09, KeyCode.Return or KeyCode.KeypadEnter => 0x0D, KeyCode.Escape => 0x1B,
            KeyCode.Space => 0x20, KeyCode.PageUp => 0x21, KeyCode.PageDown => 0x22, KeyCode.End => 0x23, KeyCode.Home => 0x24,
            KeyCode.LeftArrow => 0x25, KeyCode.UpArrow => 0x26, KeyCode.RightArrow => 0x27, KeyCode.DownArrow => 0x28,
            KeyCode.Insert => 0x2D, KeyCode.Delete => 0x2E,
            KeyCode.LeftShift or KeyCode.RightShift => 0x10, KeyCode.LeftControl or KeyCode.RightControl => 0x11,
            KeyCode.Semicolon => 0xBA, KeyCode.Equals => 0xBB, KeyCode.Comma => 0xBC, KeyCode.Minus => 0xBD, KeyCode.Period => 0xBE,
            KeyCode.Slash => 0xBF, KeyCode.BackQuote => 0xC0, KeyCode.LeftBracket => 0xDB, KeyCode.Backslash => 0xDC,
            KeyCode.RightBracket => 0xDD, KeyCode.Quote => 0xDE,
            _ => null,
        };
    }
}
