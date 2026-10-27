using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

/// <summary>
/// The menu's compact controls, drawn in fixed rects (no GUILayout): switch, button, key box, check box and reset mark.
/// Each reports itself to <see cref="Focus"/> under the key it is given and offers its tooltip.
/// </summary>
internal static class Controls
{
    public const float Height = 24f;
    public const float SwitchWidth = 28f, SwitchHeight = 16f;

    /// <summary>A switch; returns the value after a click or Enter/Space on it.</summary>
    public static bool Switch(Rect r, bool value, string key, string? tip = null)
    {
        bool clicked = GUI.Button(r, GUIContent.none, GUIStyle.none);
        bool next = Focus.Item(key, r) || clicked ? !value : value;
        if (Event.current.type == EventType.Repaint)
        {
            var knob = new Rect(r.x, Mathf.Round(r.center.y - SwitchHeight / 2f), SwitchWidth, SwitchHeight);
            bool hover = GUI.enabled && r.Contains(Event.current.mousePosition);
            if (Widgets.Themed) GUI.DrawTexture(knob, next ? (hover ? Theme.SmallOnHover : Theme.SmallOn) : (hover ? Theme.SmallOffHover : Theme.SmallOff));
            else Widgets.Paint(knob, next ? "on" : "off", GUI.skin.button);
        }
        Popups.Tip(r, tip, key);
        return next;
    }

    public static float ButtonWidth(string label) =>
        Mathf.Ceil(Widgets.Style(() => Theme.ButtonText, GUI.skin.button).CalcSize(new GUIContent(label)).x);

    /// <summary>A button; true when clicked or activated by key.</summary>
    public static bool Button(Rect r, string label, string key, bool primary = false, string? tip = null)
    {
        bool clicked = GUI.Button(r, GUIContent.none, GUIStyle.none);
        if (Event.current.type == EventType.Repaint)
        {
            if (Widgets.Themed)
            {
                bool hover = GUI.enabled && r.Contains(Event.current.mousePosition);
                Theme.DrawSliced(r, primary ? (hover ? Theme.PrimaryHover : Theme.PrimaryBg) : (hover ? Theme.ButtonHover : Theme.ButtonBg), Theme.ControlRadius);
                Widgets.Paint(r, label, primary ? Theme.PrimaryText : Theme.ButtonText);
            }
            else
            {
                Widgets.Paint(r, label, GUI.skin.button);
            }
        }
        Popups.Tip(r, tip, key);
        return Focus.Item(key, r) || clicked;
    }

    /// <summary>A conflict with another mod: orange, whatever the theme, so it reads as a warning next to the state colours.</summary>
    public static readonly Color ConflictColor = new(1f, 0.514f, 0.169f);
    private static readonly Color s_conflict = ConflictColor;

    /// <summary>The "Mod conflict: <mods>" pill's width.</summary>
    public static float ConflictPillWidth(string text) => TextCache.Width(text, Widgets.Style(() => Theme.FieldText, GUI.skin.label)) + 24f;

    /// <summary>
    /// An orange pill naming the mods in conflict with what the page is about, with the reasons as its tip; true when
    /// clicked (it opens the Mods page).
    /// </summary>
    public static bool ConflictPill(Rect at, string text, string tip, string key)
    {
        bool clicked = GUI.Button(at, GUIContent.none, GUIStyle.none);
        if (Event.current.type == EventType.Repaint)
        {
            GUIStyle style = Widgets.Style(() => Theme.FieldText, GUI.skin.label);
            bool hover = at.Contains(Event.current.mousePosition);
            if (Widgets.Themed)
            {
                Color previous = GUI.color;
                GUI.color = new Color(s_conflict.r, s_conflict.g, s_conflict.b, (hover ? 0.32f : 0.22f) * previous.a);
                Theme.DrawSliced(at, Theme.Flat, Theme.ChipRadius);
                GUI.color = previous;
            }
            // A warning triangle before the words.
            const float icon = 14f;
            var iconRect = Menu.PixelSnap(new Rect(at.x + 7f, at.center.y - icon / 2f, icon, icon));
            Color tint = GUI.color;
            GUI.color = new Color(s_conflict.r, s_conflict.g, s_conflict.b, tint.a);
            GUI.DrawTexture(iconRect, Theme.IconWarning);
            GUI.color = tint;
            // 7 px before the icon and the words right after it (the text style pads its own left); the measured width runs a
            // little wide, so the right needs no more.
            var label = new Rect(at.x + 22f, at.y, at.width - 24f, at.height);
            Widgets.Paint(label, Widgets.Themed ? Widgets.Colored(text, Theme.LightText(s_conflict)) : text, style);
        }
        Popups.Tip(at, tip, key);
        return Focus.Item(key, at) || clicked;
    }

    private const float StatePillMax = 130f;

    /// <summary>A state pill's width for its text (with room for the shape), at most 130 px.</summary>
    public static float StatePillWidth(string text) =>
        Mathf.Min(StatePillMax, TextCache.Width(text, Widgets.Style(() => Theme.FieldText, GUI.skin.label)) + 26f);

    /// <summary>
    /// A live value or state as a small pill, left-aligned in the rect: a faint tint of the state's colour, its shape,
    /// and the text in that colour lightened enough to read on the tint.
    /// </summary>
    public static void StatePill(Rect at, string text, StatusState state)
    {
        if (Event.current.type != EventType.Repaint) return;
        float w = Mathf.Min(StatePillWidth(text), at.width);
        if (w < 20f) return;
        GUIStyle style = Widgets.Style(() => Theme.FieldText, GUI.skin.label);
        var pill = new Rect(at.x, at.y + 3f, w, at.height - 6f);
        Color colour = Theme.StateColor(state);
        if (Widgets.Themed)
        {
            Color previous = GUI.color;
            GUI.color = new Color(colour.r, colour.g, colour.b, 0.16f * previous.a);
            Theme.DrawSliced(pill, Theme.Flat, Theme.ChipRadius);
            GUI.color = previous;
        }
        Color readable = Theme.StateText(state);
        // The icon centred on the pill's own middle, on whole pixels.
        const float icon = 10f;
        var iconRect = Menu.PixelSnap(new Rect(pill.x + 7f, pill.center.y - icon / 2f, icon, icon));
        if (Widgets.Themed) Theme.Tinted(iconRect, Theme.StateIcon(state), readable);
        else Menu.DrawState(iconRect, state, readable);
        var words = new Rect(iconRect.xMax + 5f, pill.y, pill.xMax - iconRect.xMax - 7f, pill.height);
        Widgets.Paint(words, Widgets.Colored(Fit(text, style, words.width), readable), style);
    }

    /// <summary>A key binding's box: the display in the accent colour, or the capture prompt.</summary>
    public static void KeyBox(Rect r, string text, bool capturing)
    {
        if (Event.current.type != EventType.Repaint) return;
        if (!Widgets.Themed)
        {
            Widgets.Paint(r, text, GUI.skin.box);
            return;
        }
        Theme.DrawSliced(r, capturing ? Theme.InputFocus : Theme.InputBox, Theme.ControlRadius);
        Widgets.Paint(r, text, Theme.KeyText);
    }

    public static float KeyBoxWidth(string text) => Mathf.Max(56f, Mathf.Ceil(Widgets.Style(() => Theme.KeyText, GUI.skin.box).CalcSize(new GUIContent(text)).x));

    /// <summary>A check box: 0 off, 1 on, 2 mixed. True when clicked or activated by key.</summary>
    public static bool Check(Rect box, int state, bool hover)
    {
        if (Event.current.type == EventType.Repaint)
        {
            if (Widgets.Themed)
            {
                var r = new Rect(box.x, Mathf.Round(box.center.y - 6f), 12f, 12f);
                if (state == 0) Theme.DrawSliced(r, Theme.CheckOff, 3);
                else GUI.DrawTexture(r, state == 1 ? Theme.CheckOn : Theme.CheckMixed);
                if (hover && state == 0) Theme.Tinted(r, Theme.CheckOff, Theme.Text);
            }
            else
            {
                Widgets.Paint(box, state == 1 ? "[x]" : state == 2 ? "[-]" : "[ ]", GUI.skin.label);
            }
        }
        return false;
    }

    /// <summary>The ↺ mark after a changed setting's control; true when clicked or activated by key.</summary>
    public static bool ResetMark(Rect r, string key)
    {
        bool clicked = GUI.Button(r, GUIContent.none, GUIStyle.none);
        if (Event.current.type == EventType.Repaint && Widgets.Themed)
        {
            bool hover = GUI.enabled && r.Contains(Event.current.mousePosition);
            Theme.Tinted(new Rect(Mathf.Round(r.center.x - 6f), Mathf.Round(r.center.y - 6f), 12f, 12f), Theme.Reset, hover ? Theme.AccentText : Theme.Dim);
        }
        else if (Event.current.type == EventType.Repaint)
        {
            Widgets.Paint(r, "R", GUI.skin.label);
        }
        Popups.Tip(r, "Back to the default", key);
        return Focus.Item(key, r) || clicked;
    }

    /// <summary>A field's frame (dropdowns, number fields): the control fill and edge, the accent edge while open.</summary>
    public static void FieldFrame(Rect r, bool open)
    {
        if (Event.current.type != EventType.Repaint) return;
        if (Widgets.Themed) Theme.DrawSliced(r, open ? Theme.FieldOpen : Theme.FieldBg, Theme.ControlRadius);
        else Widgets.Paint(r, GUIContent.none, GUI.skin.box);
    }

    /// <summary>Muted text that wraps, for hints; measured once per text, width and theme build.</summary>
    public static float HintHeight(string text, float width) => TextCache.Height(text, Widgets.Style(() => Theme.Hint, GUI.skin.label), width);

    /// <summary>Text cut with an ellipsis to a width, cached.</summary>
    public static string Fit(string text, GUIStyle style, float width) => TextCache.Fit(text, style, width);
}

/// <summary>Measured text: wrapped heights and cut labels, by text, width and theme build, so a frame measures nothing twice.</summary>
internal static class TextCache
{
    private const int MaxEntries = 2048;
    private static readonly Dictionary<(string, int, int, int), float> s_heights = new();
    private static readonly Dictionary<(string, int, int, int), string> s_fits = new();

    public static float Height(string text, GUIStyle style, float width)
    {
        var key = (text, Mathf.RoundToInt(width), Size(style), Theme.Builds);
        if (s_heights.TryGetValue(key, out float height)) return height;
        if (s_heights.Count > MaxEntries) s_heights.Clear();
        height = style.CalcHeight(new GUIContent(text), width);
        s_heights[key] = height;
        return height;
    }

    private static readonly Dictionary<(string, int, int), float> s_widths = new();

    /// <summary>Text's width on one line.</summary>
    public static float Width(string text, GUIStyle style)
    {
        var key = (text, Size(style), Theme.Builds);
        if (s_widths.TryGetValue(key, out float width)) return width;
        if (s_widths.Count > MaxEntries) s_widths.Clear();
        width = style.CalcSize(new GUIContent(text)).x;
        s_widths[key] = width;
        return width;
    }

    public static string Fit(string text, GUIStyle style, float width)
    {
        var key = (text, Mathf.RoundToInt(width), Size(style), Theme.Builds);
        if (s_fits.TryGetValue(key, out string? cut)) return cut;
        if (s_fits.Count > MaxEntries) s_fits.Clear();
        cut = Menu.Fit(text, style, width);
        s_fits[key] = cut;
        return cut;
    }

    // Font size and weight, the parts of a style that change a measure.
    private static int Size(GUIStyle style) => style.fontSize * 4 + (int)style.fontStyle;

    public static void Clear()
    {
        s_heights.Clear();
        s_fits.Clear();
        s_widths.Clear();
    }
}
