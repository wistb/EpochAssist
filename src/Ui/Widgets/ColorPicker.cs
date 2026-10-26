using EpochAssist.Core;
using UnityEngine;
using static EpochAssist.Ui.Widgets;

namespace EpochAssist.Ui;

/// <summary>
/// A colour setting's control: a swatch dropdown (the empty value first, then the named colours, then Custom…, which
/// opens the picker) and the picker as a popup: a saturation and brightness square, a hue strip, a preview and a hex
/// field. A drag applies when it is let go, because a new accent rebuilds the skin. The picker's textures are freed with
/// the skin.
/// </summary>
internal static class ColorPicker
{
    /// <summary>Wide enough for the empty label or the longest swatch name, with room for the swatch.</summary>
    public static float WidthFor(ColorSetting setting) => Mathf.Min(220f, Dropdown.WidthFor(Options(setting)) + 16f);
    private const int SquareTexture = 64;

    private static Texture2D? s_hue;
    private static Texture2D? s_square;
    private static float s_squareHue = -1f;

    public static void Release()
    {
        if (s_hue != null) UnityEngine.Object.Destroy(s_hue);
        if (s_square != null) UnityEngine.Object.Destroy(s_square);
        s_hue = s_square = null;
        s_squareHue = -1f;
    }

    /// <summary>The options as the dropdown lists them: the empty value, the swatches, then Custom….</summary>
    public static List<string> Options(ColorSetting setting)
    {
        var options = new List<string> { setting.EmptyLabel };
        options.AddRange(setting.Swatches.Select(s => s.Name));
        options.Add("Custom…");
        return options;
    }

    /// <summary>The setting's value as the dropdown shows it: a swatch's name, the empty label or the hex.</summary>
    public static string Shown(ColorSetting setting)
    {
        string value = setting.Value;
        int named = Array.FindIndex(setting.Swatches, s => s.Hex == value);
        return value.Length == 0 ? setting.EmptyLabel : named >= 0 ? setting.Swatches[named].Name : value;
    }

    // The colour the empty value ("Theme") stands for: the theme's own colour for that accent's role.
    private static Color EmptyColor(ColorSetting setting) =>
        setting == LookSettings.AccentSecondary ? Theme.ThemeHighlight : setting == LookSettings.AccentTertiary ? Theme.ThemeAccentText : Theme.ThemeAccent;

    // The swatches' headings, moved past the empty value, then one for Custom….
    private static (string Label, int First)[]? Groups(ColorSetting setting, int options) => setting.Groups?
        .Select(g => (g.Label, g.First + 1)).Append(("Your own", options - 1)).ToArray();

    public static void Draw(Rect r, string key, ColorSetting setting, string? tip = null)
    {
        var options = Options(setting);
        var swatches = new List<Color?> { EmptyColor(setting) };
        foreach (var (_, hex) in setting.Swatches) swatches.Add(ColorSetting.TryParse(hex, out Color c) ? c : null);
        swatches.Add(setting.TryGetColor(out Color own) ? own : null);
        string value = setting.Value;
        int named = Array.FindIndex(setting.Swatches, s => s.Hex == value);
        int selected = value.Length == 0 ? 0 : named >= 0 ? named + 1 : options.Count - 1;
        Dropdown.Draw(r, key, options, selected, pick =>
        {
            if (pick == 0) setting.Value = string.Empty;
            else if (pick <= setting.Swatches.Length) setting.Value = setting.Swatches[pick - 1].Hex;
            else Popups.Open(new ColorPickerPopup(setting, Popups.ToWindow(r)));
        }, Groups(setting, options.Count), Shown(setting), swatches, tip);
    }

    // The picker body inside its popup's rect.
    internal sealed class ColorPickerPopup : IPopup
    {
        private const string HexControl = "EpochAssistColorHex";
        private const float Pad = 8f;
        private readonly ColorSetting _setting;
        private float _h, _s, _v;
        private string? _hexEdit;
        // What a press is dragging: 1 the square, 2 the hue strip.
        private int _dragging;
        // What the arrow keys change: 0 hue, 1 saturation, 2 brightness.
        private int _channel;

        public ColorPickerPopup(ColorSetting setting, Rect anchor)
        {
            _setting = setting;
            Color start = setting.TryGetColor(out Color c) ? c : EmptyColor(setting);
            ToHsv(start, out _h, out _s, out _v);
            Rect = Popups.Place(new Vector2(240f, 160f), anchor);
        }

        public Rect Rect { get; }
        private Rect Square => new(Rect.x + Pad, Rect.y + Pad, 150f, 110f);
        private Rect Hue => new(Rect.x + Pad, Rect.y + Pad + 118f, 150f, 14f);
        private Rect Preview => new(Rect.x + Pad + 160f, Rect.y + Pad, 64f, 40f);
        private Rect Hex => new(Rect.x + Pad + 160f, Rect.y + Pad + 48f, 64f, 24f);

        public void HandleEvent(Event e)
        {
            Vector2 at = e.mousePosition;
            switch (e.type)
            {
                case EventType.MouseDown when e.button == 0:
                    _dragging = Square.Contains(at) ? 1 : Hue.Contains(at) ? 2 : 0;
                    if (Hex.Contains(at)) GUI.FocusControl(HexControl);
                    break;
                case EventType.MouseDrag when _dragging != 0:
                    break;
                case EventType.MouseUp when _dragging != 0:
                    Move(at);
                    _dragging = 0;
                    Commit();
                    return;
                default:
                    return;
            }
            Move(at);
        }

        private void Move(Vector2 at)
        {
            if (_dragging == 1)
            {
                _s = Mathf.Clamp01((at.x - Square.x) / Square.width);
                _v = 1f - Mathf.Clamp01((at.y - Square.y) / Square.height);
            }
            else if (_dragging == 2)
            {
                _h = Mathf.Clamp01((at.x - Hue.x) / Hue.width);
            }
        }

        public bool HandleKey(Event e) => false;

        // Up and Down pick hue, saturation or brightness; Left and Right change it; Enter closes.
        public bool HandleNav(MenuNav nav)
        {
            if (nav.Tab) return true;
            if (nav.Move != 0) _channel = (_channel + nav.Move + 3) % 3;
            if (nav.Adjust != 0)
            {
                if (_channel == 0) _h = Mathf.Repeat(_h + nav.Adjust / 36f, 1f);
                else if (_channel == 1) _s = Mathf.Clamp01(_s + nav.Adjust * 0.05f);
                else _v = Mathf.Clamp01(_v + nav.Adjust * 0.05f);
                Commit();
            }
            if (nav.Activate) Popups.Close();
            return false;
        }

        public void Closed(bool commit)
        {
            if (GUI.GetNameOfFocusedControl() == HexControl) GUIUtility.keyboardControl = 0;
        }

        private void Commit() => _setting.Value = ColorSetting.Format(FromHsv(_h, _s, _v));

        public void Draw()
        {
            if (Event.current.type == EventType.Repaint)
            {
                Theme.DrawSliced(Rect, Theme.PopupBg, Theme.CardRadius);
                GUI.DrawTexture(Square, SquareFor(_h));
                Marker(new Vector2(Square.x + _s * Square.width, Square.yMax - _v * Square.height));
                GUI.DrawTexture(Hue, HueStrip());
                Color previous = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, previous.a);
                GUI.DrawTexture(new Rect(Hue.x + _h * Hue.width - 1.5f, Hue.y - 2f, 3f, Hue.height + 4f), Texture2D.whiteTexture);
                GUI.color = previous;
                Tint(Preview, Theme.Swatch, FromHsv(_h, _s, _v), Theme.ChipRadius);
                string channel = _channel == 0 ? "Hue" : _channel == 1 ? "Saturation" : "Brightness";
                Widgets.Paint(new Rect(Hex.x, Hex.yMax + 6f, 70f, 16f), channel, Theme.Crumb);
            }
            HexField();
        }

        // #RRGGBB, applied as soon as what is typed is a whole colour.
        private void HexField()
        {
            bool editing = GUI.GetNameOfFocusedControl() == HexControl;
            if (!editing) _hexEdit = null;
            string shown = _hexEdit ?? ColorSetting.Format(FromHsv(_h, _s, _v));
            GUI.SetNextControlName(HexControl);
            string typed = GUI.TextField(Hex, shown, 7, Widgets.Style(() => Theme.TreeFilter, GUI.skin.textField));
            if (!editing || typed == shown) return;
            _hexEdit = typed;
            string hex = (typed.StartsWith("#") ? typed : "#" + typed).ToUpperInvariant();
            if (!ColorSetting.TryParse(hex, out Color c)) return;
            ToHsv(c, out _h, out _s, out _v);
            _setting.Value = hex;
        }
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
