using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

/// <summary>
/// Keyboard focus for the menu. Every control a mouse can use reports itself right after it is drawn, in reading order,
/// with a key that stays the same from frame to frame. The order comes from the last repaint; the navigation keys move
/// focus along it (wrapping) at the next layout pass, where the focused control also sees its activation or adjustment,
/// so a change made by key lays out like a click. A control that disappears passes focus to the next one in order.
/// Only while the menu draws; reset when it closes.
/// </summary>
internal static class Focus
{
    private static readonly List<string> s_order = new();
    private static readonly List<string> s_building = new();
    private static readonly Dictionary<string, int> s_seen = new();
    private static string? s_focused;
    private static int s_focusedIndex = -1;
    private static int s_appliedNav;
    private static string? s_activate;
    private static string? s_adjustKey;
    private static int s_adjust;
    private static bool s_scrollTo;
    private static Rect? s_scrollTarget;

    /// <summary>Set by the menu while it draws inside its scroll view, where control rects are in content space.</summary>
    public static bool InScroll;

    /// <summary>Set by the menu while it draws; controls drawn elsewhere (the HUD panel) aren't focusable.</summary>
    public static bool Active;

    /// <summary>Prefix for the controls of the page being drawn.</summary>
    public static string Scope = string.Empty;

    /// <summary>The focused control's key, or null.</summary>
    public static string? Current => s_focused;

    /// <summary>A text field just received focus by key and should take the keyboard.</summary>
    public static string? FieldToFocus { get; private set; }

    public static void Reset()
    {
        s_order.Clear();
        s_building.Clear();
        s_focused = null;
        s_focusedIndex = -1;
        s_activate = s_adjustKey = null;
        s_scrollTo = false;
        s_scrollTarget = null;
        InScroll = false;
        FieldToFocus = null;
    }

    /// <summary>
    /// Called by the menu at the start of every GUI event it draws. Keys are applied at the first layout pass after they
    /// were read, once: input events lay out early in a frame, before the hotkey layer reads that frame's keys.
    /// </summary>
    public static void BeginEvent(MenuNav nav, int navCount)
    {
        s_seen.Clear();
        Scope = string.Empty;
        EventType type = Event.current.type;
        if (type == EventType.Repaint) s_building.Clear();
        if (type == EventType.Layout && s_appliedNav != navCount)
        {
            s_appliedNav = navCount;
            Apply(nav);
        }
    }

    /// <summary>Called by the menu after a repaint: the order just drawn becomes the order keys move along.</summary>
    public static void EndRepaint()
    {
        s_order.Clear();
        s_order.AddRange(s_building);
        if (s_focused == null) return;
        int index = s_order.IndexOf(s_focused);
        if (index >= 0)
        {
            s_focusedIndex = index;
        }
        else if (s_order.Count > 0)
        {
            // Its control is gone (hidden by a setting, another page): the next one in order takes over.
            s_focusedIndex = Math.Clamp(s_focusedIndex, 0, s_order.Count - 1);
            s_focused = s_order[s_focusedIndex];
        }
        else
        {
            s_focused = null;
            s_focusedIndex = -1;
        }
    }

    /// <summary>Where a control just focused by key sits in the scroll view's content, once; null when nothing to scroll to.</summary>
    public static Rect? TakeScrollTarget()
    {
        Rect? target = s_scrollTarget;
        s_scrollTarget = null;
        return target;
    }

    /// <summary>True in the layout pass of the frame focus moved onto this control by key.</summary>
    public static bool Arrived(string key) => Event.current.type == EventType.Layout && FieldToFocus != null && FieldToFocus == Full(key);

    /// <summary>Makes a control focused, as a click on it does.</summary>
    public static void Take(string key) => s_focused = Full(key);

    /// <summary>Reports the control just drawn with GUILayout; true in the frame it is activated by key.</summary>
    public static bool Item(string key) => Item(key, null, out _);

    public static bool Item(string key, Rect rect) => Item(key, rect, out _);

    /// <summary>Reports a control; true in the frame it is activated by key, and adjust is -1 or +1 in the frame it is changed.</summary>
    public static bool Item(string key, Rect? rect, out int adjust)
    {
        adjust = 0;
        if (!Active || !GUI.enabled) return false;
        string full = Unique(Full(key));
        switch (Event.current.type)
        {
            case EventType.Repaint:
                Rect r = rect ?? GUILayoutUtility.GetLastRect();
                s_building.Add(full);
                if (full == s_focused)
                {
                    Theme.DrawSliced(new Rect(r.x - 2f, r.y - 2f, r.width + 4f, r.height + 4f), Theme.FocusRing, Theme.FocusRadius);
                    if (s_scrollTo)
                    {
                        s_scrollTo = false;
                        if (InScroll) s_scrollTarget = r;
                    }
                }
                return false;
            case EventType.Layout:
                if (full == s_adjustKey)
                {
                    s_adjustKey = null;
                    adjust = s_adjust;
                }
                if (full != s_activate) return false;
                s_activate = null;
                return true;
            default:
                return false;
        }
    }

    // The same key twice in one pass (two settings with one label) gets a count, so each control stays apart.
    private static string Unique(string key)
    {
        int n = s_seen.TryGetValue(key, out int seen) ? seen + 1 : 1;
        s_seen[key] = n;
        return n == 1 ? key : $"{key}#{n}";
    }

    private static string Full(string key) => Scope.Length > 0 ? $"{Scope}/{key}" : key;

    private static void Apply(MenuNav nav)
    {
        FieldToFocus = null;
        // Moving on leaves a text field. Set here, in a GUI pass: set from Update it doesn't stick.
        if (nav.Move != 0) GUIUtility.keyboardControl = 0;
        if (nav.Move != 0 && s_order.Count > 0)
        {
            int index = s_focused == null ? (nav.Move > 0 ? 0 : s_order.Count - 1)
                : ((s_order.IndexOf(s_focused) is int at && at >= 0 ? at : s_focusedIndex) + nav.Move + s_order.Count) % s_order.Count;
            s_focusedIndex = index;
            s_focused = s_order[index];
            s_scrollTo = true;
            FieldToFocus = s_focused;
        }
        if (s_focused == null) return;
        if (nav.Activate) s_activate = s_focused;
        if (nav.Adjust != 0)
        {
            s_adjustKey = s_focused;
            s_adjust = nav.Adjust;
        }
    }
}
