using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

// Themed controls shared by the menu and the HUD; each falls back to the stock skin.
internal static class Widgets
{
    public const float LabelWidth = 120f;

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
            GUI.Label(new Rect(row.x, row.y, row.width - 44f, row.height), label, Theme.RowLabel);
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

    // Double-clicking the slider returns resetTo, when given. Left and Right move a focused slider by one step.
    public static float SliderRow(string label, float value, float min, float max, string valueText, float? resetTo = null, float step = 1f)
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
            Focus.Item("slider." + label, r, out int adjust);
            if (adjust != 0) value = Mathf.Clamp(value + adjust * step, min, max);
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
        if (Hotkeys.Capturing == key)
        {
            GUILayout.Label(Colored("Press a key...", Theme.AccentText), rowLabel, GUILayout.Height(30f));
            GUILayout.FlexibleSpace();
            if (Pressed(GUILayout.Button("Cancel", button), $"key.{key.Id}.cancel")) Hotkeys.CancelCapture();
        }
        else
        {
            GUILayout.Label(key.Value == KeyCode.None ? "Not set" : key.Value.ToString(), Style(() => Theme.KeyChip, GUI.skin.box));
            GUILayout.FlexibleSpace();
            if (Pressed(GUILayout.Button("Change", button), $"key.{key.Id}.change")) Hotkeys.BeginCapture(key);
            if (key.Clearable && key.Value != KeyCode.None && Pressed(GUILayout.Button("Clear", button), $"key.{key.Id}.clear"))
                key.Value = KeyCode.None;
            else if (!key.Clearable && !key.IsDefault && Pressed(GUILayout.Button("Reset", button), $"key.{key.Id}.reset"))
                key.Reset();
        }
        GUILayout.EndHorizontal();
    }

    // A binding: its chord, Change and Clear (or Reset), and a warning line for each conflict.
    public static void HotkeyRow(Hotkey hotkey)
    {
        GUIStyle button = Style(() => Theme.SmallButton, GUI.skin.button);
        GUIStyle rowLabel = Style(() => Theme.RowLabel, GUI.skin.label);
        GUILayout.BeginHorizontal(GUILayout.Height(30f));
        GUILayout.Label(hotkey.Label, rowLabel, GUILayout.Width(LabelWidth), GUILayout.Height(30f));
        if (Hotkeys.Capturing == hotkey)
        {
            string prompt = hotkey.Kind == HotkeyKind.Hold ? "Press keys or a mouse button..." : "Press keys...";
            GUILayout.Label(Colored(prompt, Theme.AccentText), rowLabel, GUILayout.Height(30f));
            GUILayout.FlexibleSpace();
            if (Pressed(GUILayout.Button("Cancel", button), $"key.{hotkey.Id}.cancel")) Hotkeys.CancelCapture();
        }
        else
        {
            GUILayout.Label(hotkey.Display, Style(() => Theme.KeyChip, GUI.skin.box));
            GUILayout.FlexibleSpace();
            // Capture is read by the hotkey layer, so it needs that layer running.
            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && FeatureHost.Find("hotkeys")?.State == FeatureState.Running;
            if (Pressed(GUILayout.Button("Change", button), $"key.{hotkey.Id}.change")) Hotkeys.BeginCapture(hotkey);
            GUI.enabled = wasEnabled;
            if (hotkey.Clearable && hotkey.Value.Length > 0 && Pressed(GUILayout.Button("Clear", button), $"key.{hotkey.Id}.clear"))
                hotkey.Value = string.Empty;
            else if (!hotkey.Clearable && !hotkey.IsDefault && Pressed(GUILayout.Button("Reset", button), $"key.{hotkey.Id}.reset"))
                hotkey.Reset();
        }
        GUILayout.EndHorizontal();
        foreach (string warning in HotkeyConflicts.For(hotkey)) Hint(Colored(warning, Theme.Pause));
    }

    /// <summary>Starts a card; returns true when its Reset button was pressed (shown only when canReset).</summary>
    public static bool BeginCard(string title, bool canReset = false)
    {
        GUILayout.BeginVertical(Style(() => Theme.Card, GUI.skin.box));
        GUILayout.BeginHorizontal();
        GUILayout.Label(title, Style(() => Theme.CardTitle, GUI.skin.label));
        GUILayout.FlexibleSpace();
        bool reset = canReset && Pressed(GUILayout.Button("Reset", Style(() => Theme.SmallButton, GUI.skin.button), GUILayout.ExpandWidth(false)), "reset." + title);
        GUILayout.EndHorizontal();
        return reset;
    }

    public static void EndCard() => GUILayout.EndVertical();

    public static void Hint(string text) => GUILayout.Label(text, Style(() => Theme.Hint, GUI.skin.label));

    public static string Colored(string text, Color color) => $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{text}</color>";
}
