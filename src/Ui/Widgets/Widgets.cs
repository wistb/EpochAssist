using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

// Themed controls shared by the menu and the HUD; each falls back to the stock skin.
internal static class Widgets
{
    /// <summary>Set by the caller at the start of each OnGUI pass.</summary>
    public static bool Themed;

    public static GUIStyle Style(Func<GUIStyle> themed, GUIStyle fallback) => Themed ? themed() : fallback;

    /// <summary>A control's click, or its activation by key; call right after drawing it with GUILayout.</summary>
    public static bool Pressed(bool clicked, string key) => Focus.Item(key) || clicked;

    // A full-width row with the label on the left and a switch on the right; the whole row is clickable.
    public static bool Switch(string label, bool value)
    {
        if (!Themed) return GUILayout.Toggle(value, label);

        Rect row = GUILayoutUtility.GetRect(10f, 28f, GUILayout.ExpandWidth(true));
        bool hover = GUI.enabled && row.Contains(Event.current.mousePosition);
        bool next = GUI.Toggle(row, value, GUIContent.none, GUIStyle.none);
        if (Focus.Item("switch." + label, row)) next = !value;
        if (Event.current.type == EventType.Repaint)
        {
            var knob = new Rect(row.xMax - 36f, row.y + (row.height - 20f) / 2f, 36f, 20f);
            GUI.DrawTexture(knob, next ? (hover ? Theme.SwitchOnHover : Theme.SwitchOn) : (hover ? Theme.SwitchOffHover : Theme.SwitchOff));
            Paint(new Rect(row.x, row.y, row.width - 44f, row.height), label, Theme.RowLabel);
        }
        return next;
    }

    /// <summary>Just the switch, for a header row.</summary>
    public static bool InlineSwitch(bool value, string key)
    {
        if (!Themed) return GUILayout.Toggle(value, string.Empty, GUILayout.Width(20f));

        Rect r = GUILayoutUtility.GetRect(36f, 26f, GUILayout.Width(36f), GUILayout.Height(26f));
        bool hover = GUI.enabled && r.Contains(Event.current.mousePosition);
        bool next = GUI.Toggle(r, value, GUIContent.none, GUIStyle.none);
        if (Focus.Item(key, r)) next = !value;
        if (Event.current.type == EventType.Repaint)
        {
            GUI.DrawTexture(new Rect(r.x, r.y + 3f, 36f, 20f),
                next ? (hover ? Theme.SwitchOnHover : Theme.SwitchOn) : (hover ? Theme.SwitchOffHover : Theme.SwitchOff));
        }
        return next;
    }

    public static void Hint(string text) => GUILayout.Label(text, Style(() => Theme.Hint, GUI.skin.label));

    /// <summary>
    /// Draws text or a frame in the repaint pass without taking a control id. GUI.Label and GUI.Box take one, so drawn
    /// only while repainting they shift the ids of every control after them: a text field then looks unfocused and
    /// loses its keys.
    /// </summary>
    public static void Paint(Rect r, GUIContent content, GUIStyle style) => style.Draw(r, content, false, false, false, false);

    public static void Paint(Rect r, string text, GUIStyle style) => Paint(r, new GUIContent(text), style);

    public static string Colored(string text, Color color) => $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{text}</color>";
}
