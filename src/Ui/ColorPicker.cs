using EpochAssist.Core;
using UnityEngine;
using static EpochAssist.Ui.Widgets;

namespace EpochAssist.Ui;

/// <summary>
/// A colour setting's row: swatches (the empty value first, then the named colours, then Custom) and, for Custom, a
/// picker with a saturation and brightness square, a hue strip and a hex field. A drag applies when it is let go,
/// because a new accent rebuilds the skin. The picker's textures are freed with the skin.
/// </summary>
internal static class ColorPicker
{
    private const float SwatchSize = 26f;
    private const float Gap = 6f;
    private const int SquareTexture = 64;
    private const string HexControl = "EpochAssistColorHex";

    private static Texture2D? s_hue;
    private static Texture2D? s_square;
    private static float s_squareHue = -1f;
    // The setting whose picker is open, and the colour being edited there (hue, saturation, value; 0..1).
    private static ColorSetting? s_open;
    private static float s_h, s_s, s_v;
    private static string? s_hexEdit;

    public static void Release()
    {
        if (s_hue != null) UnityEngine.Object.Destroy(s_hue);
        if (s_square != null) UnityEngine.Object.Destroy(s_square);
        s_hue = s_square = null;
        s_squareHue = -1f;
    }

    public static void Row(ColorSetting setting)
    {
        GUILayout.Label(setting.Label, Style(() => Theme.RowLabel, GUI.skin.label), GUILayout.Height(26f));
        if (!Themed)
        {
            GUILayout.Label(setting.Value.Length > 0 ? setting.Value : setting.EmptyLabel);
            return;
        }

        string value = setting.Value;
        int named = Array.FindIndex(setting.Swatches, s => s.Hex == value);
        bool custom = value.Length > 0 && named < 0;
        var items = new List<(string Key, Color? Color, bool Selected, Action Pick)>
        {
            ("theme", Theme.ThemeAccent, value.Length == 0, () => Choose(setting, string.Empty)),
        };
        foreach (var (name, hex) in setting.Swatches)
        {
            ColorSetting.TryParse(hex, out Color c);
            items.Add((name, c, value == hex, () => Choose(setting, hex)));
        }
        items.Add(("custom", custom && setting.TryGetColor(out Color own) ? own : null, custom || s_open == setting, () => Open(setting)));

        int perRow = Mathf.Max(1, Mathf.FloorToInt((SettingsView.RowWidth + Gap) / (SwatchSize + Gap)));
        for (int start = 0; start < items.Count; start += perRow)
        {
            GUILayout.BeginHorizontal();
            for (int i = start; i < Math.Min(start + perRow, items.Count); i++)
            {
                var item = items[i];
                if (i > start) GUILayout.Space(Gap);
                Rect r = GUILayoutUtility.GetRect(SwatchSize, SwatchSize, GUILayout.Width(SwatchSize), GUILayout.Height(SwatchSize));
                bool clicked = GUI.Button(r, GUIContent.none, GUIStyle.none);
                if (Focus.Item("swatch." + item.Key, r) || clicked) item.Pick();
                if (Event.current.type == EventType.Repaint) DrawSwatch(r, item.Color, item.Selected);
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(Gap);
        }

        string chosen = value.Length == 0 ? $"{setting.EmptyLabel}: the theme's own" : named >= 0 ? setting.Swatches[named].Name : $"Custom {value}";
        Hint(chosen);
        if (custom && s_open != setting) Open(setting);
        if (s_open == setting) Picker(setting);
    }

    private static void Choose(ColorSetting setting, string hex)
    {
        setting.Value = hex;
        if (s_open == setting) s_open = null;
    }

    // Opens the picker on the current colour (the theme's own when none is set).
    private static void Open(ColorSetting setting)
    {
        s_open = setting;
        Color start = setting.TryGetColor(out Color c) ? c : Theme.ThemeAccent;
        ToHsv(start, out s_h, out s_s, out s_v);
        s_hexEdit = null;
    }

    private static void DrawSwatch(Rect r, Color? color, bool selected)
    {
        bool hover = r.Contains(Event.current.mousePosition);
        if (selected || hover)
        {
            Color ring = selected ? Theme.Text : Theme.Dim;
            Tint(new Rect(r.x - 3f, r.y - 3f, r.width + 6f, r.height + 6f), Theme.SwatchRing, ring, Theme.ChipRadius + 3);
        }
        if (color is Color c)
        {
            Tint(r, Theme.Swatch, c, Theme.ChipRadius);
            return;
        }
        // Custom with no colour yet: a control-coloured swatch with a plus.
        Tint(r, Theme.Swatch, Theme.Values.Control, Theme.ChipRadius);
        Widgets.Paint(r, "+", Theme.FieldClear);
    }

    private static void Picker(ColorSetting setting)
    {
        float width = Mathf.Min(SettingsView.RowWidth - 110f, 220f);
        GUILayout.BeginHorizontal();
        Rect square = GUILayoutUtility.GetRect(width, 110f, GUILayout.Width(width), GUILayout.Height(110f));
        GUILayout.Space(12f);
        GUILayout.BeginVertical();
        Rect preview = GUILayoutUtility.GetRect(48f, 48f, GUILayout.Width(48f), GUILayout.Height(48f));
        GUILayout.Space(8f);
        HexField(setting);
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();
        GUILayout.Space(6f);
        Rect hue = GUILayoutUtility.GetRect(width, 14f, GUILayout.Width(width), GUILayout.Height(14f));
        GUILayout.Space(4f);

        // Mouse: press and drag on the square or strip; the colour applies on release.
        if (Drag(square, out Vector2 at, out bool released))
        {
            s_s = at.x;
            s_v = 1f - at.y;
            if (released) Commit(setting);
        }
        if (Drag(hue, out Vector2 along, out bool hueReleased))
        {
            s_h = Mathf.Clamp01(along.x);
            if (hueReleased) Commit(setting);
        }
        // Keys: Left and Right change saturation on the square and hue on the strip.
        Focus.Item("picker.square", square, out int saturation);
        if (saturation != 0)
        {
            s_s = Mathf.Clamp01(s_s + saturation * 0.05f);
            Commit(setting);
        }
        Focus.Item("picker.hue", hue, out int step);
        if (step != 0)
        {
            s_h = Mathf.Repeat(s_h + step / 36f, 1f);
            Commit(setting);
        }

        if (Event.current.type != EventType.Repaint) return;
        GUI.DrawTexture(square, SquareFor(s_h));
        Marker(new Vector2(square.x + s_s * square.width, square.yMax - s_v * square.height));
        GUI.DrawTexture(hue, HueStrip());
        Color previous = GUI.color;
        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(hue.x + s_h * hue.width - 1.5f, hue.y - 2f, 3f, hue.height + 4f), Texture2D.whiteTexture);
        GUI.color = previous;
        Tint(preview, Theme.Swatch, FromHsv(s_h, s_s, s_v), Theme.ChipRadius);
    }

    // #RRGGBB, applied as soon as what is typed is a whole colour.
    private static void HexField(ColorSetting setting)
    {
        Rect r = GUILayoutUtility.GetRect(96f, 26f, GUILayout.Width(96f), GUILayout.Height(26f));
        bool editing = GUI.GetNameOfFocusedControl() == HexControl;
        if (!editing) s_hexEdit = null;
        string shown = s_hexEdit ?? ColorSetting.Format(FromHsv(s_h, s_s, s_v));
        GUI.SetNextControlName(HexControl);
        string typed = GUI.TextField(r, shown, 7, GUI.skin.textField);
        if (Focus.Item("picker.hex", r) || Focus.Arrived("picker.hex")) GUI.FocusControl(HexControl);
        if (!editing || typed == shown) return;
        s_hexEdit = typed;
        string hex = (typed.StartsWith("#") ? typed : "#" + typed).ToUpperInvariant();
        if (!ColorSetting.TryParse(hex, out Color c)) return;
        ToHsv(c, out s_h, out s_s, out s_v);
        setting.Value = hex;
    }

    private static void Commit(ColorSetting setting) => setting.Value = ColorSetting.Format(FromHsv(s_h, s_s, s_v));

    // A press inside the rect starts a drag; the position (0..1 across and down) is reported while it lasts.
    private static bool Drag(Rect r, out Vector2 at, out bool released)
    {
        int id = GUIUtility.GetControlID(FocusType.Passive);
        Event e = Event.current;
        at = Vector2.zero;
        released = false;
        if (!GUI.enabled) return false;
        switch (e.type)
        {
            case EventType.MouseDown when e.button == 0 && r.Contains(e.mousePosition):
                GUIUtility.hotControl = id;
                break;
            case EventType.MouseDrag when GUIUtility.hotControl == id:
                break;
            case EventType.MouseUp when GUIUtility.hotControl == id:
                GUIUtility.hotControl = 0;
                released = true;
                break;
            default:
                return false;
        }
        e.Use();
        at = new Vector2(Mathf.Clamp01((e.mousePosition.x - r.x) / r.width), Mathf.Clamp01((e.mousePosition.y - r.y) / r.height));
        return true;
    }

    private static void Marker(Vector2 at)
    {
        Tint(new Rect(at.x - 7f, at.y - 7f, 14f, 14f), Theme.Ring, Color.black, 0);
        Tint(new Rect(at.x - 6f, at.y - 6f, 12f, 12f), Theme.Ring, Color.white, 0);
    }

    private static void Tint(Rect r, Texture2D texture, Color color, int border)
    {
        Color previous = GUI.color;
        GUI.color = new Color(color.r, color.g, color.b, color.a * previous.a);
        if (border > 0) Theme.DrawSliced(r, texture, border);
        else GUI.DrawTexture(r, texture);
        GUI.color = previous;
    }

    private static Texture2D HueStrip()
    {
        if (s_hue != null) return s_hue;
        s_hue = new Texture2D(128, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
        for (int x = 0; x < 128; x++) s_hue.SetPixel(x, 0, FromHsv(x / 127f, 1f, 1f));
        s_hue.Apply();
        return s_hue;
    }

    // Saturation across, value up; redrawn only when the hue changes.
    private static Texture2D SquareFor(float hue)
    {
        s_square ??= new Texture2D(SquareTexture, SquareTexture, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp,
        };
        if (Mathf.Approximately(hue, s_squareHue)) return s_square;
        s_squareHue = hue;
        for (int y = 0; y < SquareTexture; y++)
        {
            for (int x = 0; x < SquareTexture; x++) s_square.SetPixel(x, y, FromHsv(hue, x / (SquareTexture - 1f), y / (SquareTexture - 1f)));
        }
        s_square.Apply();
        return s_square;
    }

    private static Color FromHsv(float h, float s, float v)
    {
        float c = v * s, hp = Mathf.Repeat(h, 1f) * 6f, x = c * (1f - Mathf.Abs(hp % 2f - 1f)), m = v - c;
        (float r, float g, float b) = hp switch
        {
            < 1f => (c, x, 0f),
            < 2f => (x, c, 0f),
            < 3f => (0f, c, x),
            < 4f => (0f, x, c),
            < 5f => (x, 0f, c),
            _ => (c, 0f, x),
        };
        return new Color(r + m, g + m, b + m, 1f);
    }

    private static void ToHsv(Color color, out float h, out float s, out float v)
    {
        float max = Mathf.Max(color.r, Mathf.Max(color.g, color.b)), min = Mathf.Min(color.r, Mathf.Min(color.g, color.b)), d = max - min;
        v = max;
        s = max > 0f ? d / max : 0f;
        if (d <= 0f) h = 0f;
        else if (max == color.r) h = Mathf.Repeat((color.g - color.b) / d / 6f, 1f);
        else if (max == color.g) h = ((color.b - color.r) / d + 2f) / 6f;
        else h = ((color.r - color.g) / d + 4f) / 6f;
    }
}
