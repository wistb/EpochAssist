using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

// Themed controls shared by the menu and the HUD; each falls back to the stock skin.
internal static class Widgets
{
    public const float LabelWidth = 120f;

    /// <summary>Set by the caller at the start of each OnGUI pass.</summary>
    public static bool Themed;

    // The key setting being rebound, or null.
    public static Key? Capturing;
    private static KeyCode[]? s_bindableKeys;

    public static GUIStyle Style(Func<GUIStyle> themed, GUIStyle fallback) => Themed ? themed() : fallback;

    // A full-width row with the label on the left and a switch on the right; the whole row is clickable.
    public static bool Switch(string label, bool value)
    {
        if (!Themed) return GUILayout.Toggle(value, label);

        Rect row = GUILayoutUtility.GetRect(10f, 28f, GUILayout.ExpandWidth(true));
        bool hover = GUI.enabled && row.Contains(Event.current.mousePosition);
        bool next = GUI.Toggle(row, value, GUIContent.none, GUIStyle.none);
        if (Event.current.type == EventType.Repaint)
        {
            var knob = new Rect(row.xMax - 36f, row.y + (row.height - 20f) / 2f, 36f, 20f);
            GUI.DrawTexture(knob, next ? (hover ? Theme.SwitchOnHover : Theme.SwitchOn) : (hover ? Theme.SwitchOffHover : Theme.SwitchOff));
            GUI.Label(new Rect(row.x, row.y, row.width - 44f, row.height), label, Theme.RowLabel);
        }
        return next;
    }

    /// <summary>Just the switch, for a header row.</summary>
    public static bool InlineSwitch(bool value)
    {
        if (!Themed) return GUILayout.Toggle(value, string.Empty, GUILayout.Width(20f));

        Rect r = GUILayoutUtility.GetRect(36f, 26f, GUILayout.Width(36f), GUILayout.Height(26f));
        bool hover = GUI.enabled && r.Contains(Event.current.mousePosition);
        bool next = GUI.Toggle(r, value, GUIContent.none, GUIStyle.none);
        if (Event.current.type == EventType.Repaint)
        {
            GUI.DrawTexture(new Rect(r.x, r.y + 3f, 36f, 20f),
                next ? (hover ? Theme.SwitchOnHover : Theme.SwitchOn) : (hover ? Theme.SwitchOffHover : Theme.SwitchOff));
        }
        return next;
    }

    // Double-clicking the slider returns resetTo, when given.
    public static float SliderRow(string label, float value, float min, float max, string valueText, float? resetTo = null)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, Style(() => Theme.RowLabel, GUI.skin.label), GUILayout.Width(LabelWidth), GUILayout.Height(28f));
        if (!Themed)
        {
            value = GUILayout.HorizontalSlider(value, min, max);
        }
        else
        {
            Rect r = GUILayoutUtility.GetRect(40f, 28f, GUILayout.ExpandWidth(true));
            Event e = Event.current;
            if (resetTo.HasValue && GUI.enabled && e.type == EventType.MouseDown && e.clickCount == 2 && r.Contains(e.mousePosition))
            {
                e.Use();
                value = resetTo.Value;
            }
            if (Event.current.type == EventType.Repaint)
            {
                float t = max > min ? Mathf.Clamp01((value - min) / (max - min)) : 0f;
                var track = new Rect(r.x, r.center.y - 2f, r.width, 4f);
                Theme.Track.Draw(track, false, false, false, false);
                Theme.TrackFill.Draw(new Rect(track.x, track.y, 8f + t * (r.width - 16f), track.height), false, false, false, false);
            }
            value = GUI.HorizontalSlider(new Rect(r.x, r.center.y - 9f, r.width, 18f), value, min, max, Theme.SliderBody, Theme.Thumb);
        }
        GUILayout.Label(valueText, Style(() => Theme.Value, GUI.skin.label), GUILayout.Width(52f), GUILayout.Height(28f));
        GUILayout.EndHorizontal();
        return value;
    }

    public static void KeyRow(Key key)
    {
        GUIStyle button = Style(() => Theme.SmallButton, GUI.skin.button);
        GUIStyle rowLabel = Style(() => Theme.RowLabel, GUI.skin.label);
        GUILayout.BeginHorizontal(GUILayout.Height(30f));
        GUILayout.Label(key.Label, rowLabel, GUILayout.Width(LabelWidth), GUILayout.Height(30f));
        if (Capturing == key)
        {
            GUILayout.Label(Colored("Press a key...", Theme.AccentText), rowLabel, GUILayout.Height(30f));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Cancel", button)) Capturing = null;
        }
        else
        {
            GUILayout.Label(key.Value == KeyCode.None ? "Not set" : key.Value.ToString(), Style(() => Theme.KeyChip, GUI.skin.box));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Change", button)) Capturing = key;
            if (key.Clearable && key.Value != KeyCode.None && GUILayout.Button("Clear", button))
                key.Value = KeyCode.None;
            else if (!key.Clearable && !key.IsDefault && GUILayout.Button("Reset", button))
                key.Reset();
        }
        GUILayout.EndHorizontal();
    }

    /// <summary>Starts a card; returns true when its Reset button was pressed (shown only when canReset).</summary>
    public static bool BeginCard(string title, bool canReset = false)
    {
        GUILayout.BeginVertical(Style(() => Theme.Card, GUI.skin.box));
        GUILayout.BeginHorizontal();
        GUILayout.Label(title, Style(() => Theme.CardTitle, GUI.skin.label));
        GUILayout.FlexibleSpace();
        bool reset = canReset && GUILayout.Button("Reset", Style(() => Theme.SmallButton, GUI.skin.button), GUILayout.ExpandWidth(false));
        GUILayout.EndHorizontal();
        return reset;
    }

    public static void EndCard() => GUILayout.EndVertical();

    public static void Hint(string text) => GUILayout.Label(text, Style(() => Theme.Hint, GUI.skin.label));

    public static string Colored(string text, Color color) => $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{text}</color>";

    /// <summary>Feeds key presses to a pending rebind. True while one is in progress.</summary>
    public static bool UpdateKeyCapture(bool menuVisible)
    {
        if (Capturing == null) return false;
        if (!menuVisible || Input.GetKeyDown(KeyCode.Escape))
        {
            Capturing = null;
            return true;
        }

        s_bindableKeys ??= Enum.GetValues<KeyCode>()
            .Where(k => k != KeyCode.None && k != KeyCode.Escape && (k < KeyCode.Mouse0 || k > KeyCode.Mouse6) && k < KeyCode.JoystickButton0)
            .ToArray();
        foreach (KeyCode key in s_bindableKeys)
        {
            if (!Input.GetKeyDown(key)) continue;
            // Holding the menu key to pause would toggle the menu.
            if (Capturing != Config.MenuKey && key == Config.MenuKey.Value) continue;

            Capturing.Value = key;
            Capturing = null;
            break;
        }
        return true;
    }
}
