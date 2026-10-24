using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

// Slate theme: the menu's own GUISkin copy plus extra styles, built once from procedural textures.
internal static class Theme
{
    public static readonly Color Text = Hex(0xE4E7EC);
    public static readonly Color Dim = Hex(0x8E96A4);
    public static readonly Color Accent = Hex(0x4C8DFF);
    public static readonly Color AccentText = Hex(0x7DAAFF);
    public static readonly Color Good = Hex(0x3FCF8E);
    public static readonly Color Pause = Hex(0xF2B84B);
    public static readonly Color Off = Hex(0x5A6272);

    private static readonly Color s_windowFill = Hex(0x0F1115);
    private static readonly Color s_edge = Hex(0x232833);
    private static readonly Color s_sidebar = Hex(0x12151B);
    private static readonly Color s_card = Hex(0x161A21);
    private static readonly Color s_cardEdge = Hex(0x222833);
    private static readonly Color s_control = Hex(0x232935);
    private static readonly Color s_controlHover = Hex(0x2B3240);
    private static readonly Color s_controlActive = Hex(0x343C4C);
    private static readonly Color s_track = Hex(0x2A303B);

    public const float SidebarWidth = 150f;

    private static GUISkin? s_skin;
    private static bool s_failed;
    // Fonts made with Font.CreateDynamicFontFromOSFont garble IMGUI text in this game, so weight comes from
    // FontStyle on Unity's built-in font. The skin is rebuilt when the setting changes.
    private static int s_builtWeight = -1;
    private static int s_builtFont = -1;
    // Config.Font options: game font asset names; null keeps Unity's built-in font. The game owns these, never destroy them.
    private static readonly string?[] s_gameFonts = { null, "PretendardVariable", "NotoSansJP-Regular" };
    private static FontStyle s_body = FontStyle.Normal;
    private static readonly List<Texture2D> s_textures = new();

    public static GUIStyle Sidebar = null!, NavTitle = null!, NavVersion = null!, NavItem = null!, Main = null!;
    public static GUIStyle PageTitle = null!, Badge = null!, Close = null!;
    public static GUIStyle Card = null!, CardTitle = null!, RowLabel = null!, Hint = null!, Value = null!;
    public static GUIStyle SearchField = null!, SubTab = null!, Segment = null!, SmallButton = null!, KeyChip = null!;
    public static GUIStyle Track = null!, TrackFill = null!, SliderBody = null!, Thumb = null!;
    public static GUIStyle ChipNumber = null!, ChipName = null!;
    public static GUIStyle Pill = null!, PillLabel = null!, Pip = null!, Panel = null!;
    public static Texture2D SwitchOff = null!, SwitchOffHover = null!, SwitchOn = null!, SwitchOnHover = null!;
    // Drawn by the menu itself so its opacity setting applies; the styles carry no background.
    public static Texture2D WindowBg = null!, SidebarBg = null!;
    public static Texture2D Dot = null!, Divider = null!, ChipOff = null!, ChipOn = null!, ChipPaused = null!;
    public static Texture2D PipOn = null!, PipOff = null!, PipPaused = null!;
    public static Texture2D BadgeGood = null!, BadgePause = null!, BadgeWaiting = null!, BadgeOff = null!;

    /// <summary>The themed skin, or null to draw with the default one.</summary>
    public static GUISkin? Skin
    {
        get
        {
            if (s_failed) return null;
            if (s_skin != null && (s_builtWeight != Config.FontWeight.Value || s_builtFont != Config.Font.Value)) Release();
            if (s_skin != null) return s_skin;
            try
            {
                Build();
            }
            catch (Exception e)
            {
                s_failed = true;
                s_skin = null;
                Log.Warn($"Menu theme unavailable, using the default skin: {e.Message}");
            }
            return s_skin;
        }
    }

    private static void Build()
    {
        var skin = UnityEngine.Object.Instantiate((UnityEngine.Object)GUI.skin).Cast<GUISkin>();
        skin.hideFlags = HideFlags.HideAndDontSave;
        s_builtWeight = Config.FontWeight.Value;
        s_builtFont = Config.Font.Value;
        Font? gameFont = FindGameFont(s_builtFont);
        if (gameFont != null) skin.font = gameFont;
        s_body = s_builtWeight == 1 ? FontStyle.Bold : FontStyle.Normal;

        skin.window = new GUIStyle(skin.window) { border = Offset(14), padding = Offset(0), overflow = Offset(0), contentOffset = Vector2.zero };
        WindowBg = Rounded(14, 14, s_windowFill, s_edge, 1f);
        Background(skin.window, null);

        skin.label = new GUIStyle(skin.label) { fontSize = 13, fontStyle = s_body, wordWrap = false, richText = true, padding = new RectOffset(0, 0, 2, 2) };
        TextColor(skin.label, Text);
        skin.button = MakeButton(skin.button, s_control, s_controlHover, s_controlActive, Accent);
        skin.box = new GUIStyle(skin.box) { border = Offset(10), padding = Offset(10) };
        Background(skin.box, Rounded(10, 10, s_card, s_cardEdge, 1f));

        skin.scrollView = new GUIStyle(skin.scrollView);
        Background(skin.scrollView, null);
        skin.verticalScrollbar = new GUIStyle(skin.verticalScrollbar)
        {
            fixedWidth = 6f, border = Offset(3), margin = new RectOffset(6, 0, 0, 0), padding = Offset(0),
        };
        Background(skin.verticalScrollbar, Rounded(3, 3, s_sidebar, s_sidebar, 0f));
        skin.verticalScrollbarThumb = new GUIStyle(skin.verticalScrollbarThumb) { fixedWidth = 6f, border = Offset(3) };
        Background(skin.verticalScrollbarThumb, Rounded(3, 3, s_controlActive, s_controlActive, 0f));
        skin.verticalScrollbarThumb.hover.background = Rounded(3, 3, Dim, Dim, 0f);
        skin.horizontalScrollbar = new GUIStyle(skin.horizontalScrollbar) { fixedHeight = 0f };
        Background(skin.horizontalScrollbar, null);

        // Rounded on the left only, so it sits flush against the content area.
        Sidebar = new GUIStyle { border = new RectOffset(14, 2, 14, 14), padding = new RectOffset(10, 10, 14, 12) };
        SidebarBg = Rounded(14, 0, s_sidebar, s_sidebar, 0f);
        NavTitle = new GUIStyle(skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, padding = new RectOffset(6, 0, 0, 0) };
        NavVersion = new GUIStyle(skin.label) { fontSize = 11, padding = new RectOffset(6, 0, 0, 0) };
        TextColor(NavVersion, Dim);
        NavItem = MakeButton(skin.button, Color.clear, Hex(0x1C2029), Hex(0x232935), Accent);
        NavItem.alignment = TextAnchor.MiddleLeft;
        NavItem.fontStyle = FontStyle.Bold;
        NavItem.fixedHeight = 32f;
        NavItem.padding = new RectOffset(26, 10, 0, 1);
        NavItem.margin = new RectOffset(0, 0, 0, 4);
        TextColor(NavItem, Dim);
        NavItem.hover.textColor = Text;
        SetOnText(NavItem, Color.white);
        Main = new GUIStyle { padding = new RectOffset(16, 16, 12, 14) };

        PageTitle = new GUIStyle(skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, padding = new RectOffset(0, 0, 1, 0) };
        Badge = new GUIStyle(skin.label)
        {
            fontSize = 12, fontStyle = FontStyle.Bold, border = Offset(11), alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(20, 9, 0, 1), margin = new RectOffset(8, 0, 3, 0), fixedHeight = 22f,
        };
        Close = MakeButton(skin.button, Color.clear, s_controlHover, s_controlActive, Accent);
        Close.fontSize = 18;
        Close.fixedWidth = 26f;
        Close.fixedHeight = 26f;
        Close.padding = new RectOffset(0, 0, 0, 3);
        Close.margin = new RectOffset(8, 0, 0, 0);
        TextColor(Close, Dim);
        Close.hover.textColor = Text;

        Card = new GUIStyle(skin.box) { margin = new RectOffset(0, 0, 0, 10), padding = new RectOffset(14, 14, 10, 12) };
        CardTitle = new GUIStyle(skin.label) { fontSize = 11, fontStyle = FontStyle.Bold, padding = new RectOffset(0, 0, 0, 4) };
        TextColor(CardTitle, Dim);
        // No left margin, so layout labels line up with the switch rows, which draw their label at the row's edge.
        RowLabel = new GUIStyle(skin.label) { alignment = TextAnchor.MiddleLeft, margin = new RectOffset(0, 4, 0, 0) };
        Hint = new GUIStyle(skin.label) { fontSize = 12, fontStyle = FontStyle.Normal, wordWrap = true, margin = new RectOffset(0, 0, 2, 4) };
        TextColor(Hint, Dim);
        Value = new GUIStyle(skin.label) { alignment = TextAnchor.MiddleRight, margin = new RectOffset(4, 0, 0, 0) };

        skin.textField = new GUIStyle(skin.textField)
        {
            border = Offset(8), padding = new RectOffset(9, 9, 5, 5), margin = new RectOffset(0, 6, 0, 0),
            fontSize = 13, fontStyle = FontStyle.Normal, fixedHeight = 26f, alignment = TextAnchor.MiddleLeft, wordWrap = false,
        };
        Background(skin.textField, Rounded(8, 8, Hex(0x0F1115), Hex(0x2F3644), 1f));
        skin.textField.focused.background = Rounded(8, 8, Hex(0x0F1115), Accent, 1f);
        skin.textField.onFocused.background = skin.textField.focused.background;
        TextColor(skin.textField, Text);
        skin.settings.cursorColor = Text;
        skin.settings.selectionColor = Hex(0x4C8DFF, 0.45f);
        SearchField = skin.textField;

        SubTab = MakeButton(skin.button, Color.clear, s_controlHover, s_controlActive, s_control);
        SubTab.fontSize = 12;
        SubTab.fontStyle = FontStyle.Bold;
        SubTab.margin = new RectOffset(0, 4, 0, 0);
        TextColor(SubTab, Dim);
        SubTab.hover.textColor = Text;
        SetOnText(SubTab, Text);

        Segment = MakeButton(skin.button, s_control, s_controlHover, s_controlActive, Accent);
        Segment.margin = new RectOffset(0, 4, 2, 2);
        SmallButton = MakeButton(skin.button, s_control, s_controlHover, s_controlActive, Accent);
        SmallButton.fontSize = 12;
        SmallButton.fixedHeight = 24f;
        SmallButton.padding = new RectOffset(10, 10, 2, 3);
        SmallButton.margin = new RectOffset(0, 4, 2, 2);
        KeyChip = new GUIStyle(skin.label)
        {
            alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, border = Offset(8),
            padding = new RectOffset(9, 9, 2, 3), margin = new RectOffset(4, 6, 2, 2), fixedHeight = 24f,
        };
        Background(KeyChip, Rounded(8, 8, Hex(0x0F1115), Hex(0x2F3644), 1f));
        TextColor(KeyChip, AccentText);

        Track = new GUIStyle { border = Offset(2) };
        Track.normal.background = Rounded(2, 2, s_track, s_track, 0f);
        TrackFill = new GUIStyle { border = Offset(2) };
        TrackFill.normal.background = Rounded(2, 2, Accent, Accent, 0f);
        SliderBody = new GUIStyle { fixedHeight = 18f };
        Thumb = new GUIStyle { fixedWidth = 16f, fixedHeight = 16f };
        Thumb.normal.background = Circle(16, Color.white);
        Thumb.hover.background = Circle(16, Hex(0xDCE6FF));
        Thumb.active.background = Circle(16, AccentText);

        ChipNumber = new GUIStyle(skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 16, fontStyle = FontStyle.Bold };
        ChipName = new GUIStyle(skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 10, clipping = TextClipping.Clip };
        ChipOff = Rounded(9, 9, s_sidebar, s_cardEdge, 1f);
        ChipOn = Rounded(9, 9, Hex(0x16264A), Accent, 1.5f);
        ChipPaused = Rounded(9, 9, Hex(0x2B2414), Pause, 1.5f);

        SwitchOff = Switch(on: false, s_track, Dim);
        SwitchOffHover = Switch(on: false, s_controlActive, Text);
        SwitchOn = Switch(on: true, Accent, Color.white);
        SwitchOnHover = Switch(on: true, Hex(0x6AA1FF), Color.white);
        Dot = Circle(10, Color.white);
        Divider = Paint(1, 1, (_, _) => s_cardEdge);

        BadgeGood = Rounded(11, 11, Hex(0x12301F), Hex(0x12301F), 0f);
        BadgePause = Rounded(11, 11, Hex(0x3A2E14), Hex(0x3A2E14), 0f);
        BadgeWaiting = Rounded(11, 11, Hex(0x16264A), Hex(0x16264A), 0f);
        BadgeOff = Rounded(11, 11, s_control, s_control, 0f);

        Pill = new GUIStyle { border = Offset(16) };
        Pill.normal.background = Rounded(16, 16, s_windowFill, s_edge, 1f);
        Pill.hover.background = Rounded(16, 16, s_card, Hex(0x2F3644), 1f);
        PillLabel = new GUIStyle(skin.label) { fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
        Pip = new GUIStyle(skin.label) { fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, padding = Offset(0) };
        PipOn = Rounded(5, 5, Accent, Accent, 0f);
        PipOff = Rounded(5, 5, Color.clear, Hex(0x3A4150), 1f);
        PipPaused = Rounded(5, 5, Hex(0x2B2414), Pause, 1f);
        Panel = new GUIStyle { border = Offset(12), padding = new RectOffset(12, 12, 10, 12) };
        Panel.normal.background = Rounded(12, 12, s_windowFill, s_edge, 1f);

        s_skin = skin;
    }

    private static Font? FindGameFont(int choice)
    {
        string? name = choice >= 0 && choice < s_gameFonts.Length ? s_gameFonts[choice] : null;
        if (name == null) return null;
        foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Font>()))
        {
            if (obj.name == name) return obj.Cast<Font>();
        }
        Log.Warn($"Game font '{name}' not loaded, using the default");
        return null;
    }

    /// <summary>Destroys the skin and every texture made for it; the next use builds them again.</summary>
    public static void Release()
    {
        s_builtWeight = -1;
        s_builtFont = -1;
        foreach (var texture in s_textures)
        {
            if (texture != null) UnityEngine.Object.Destroy(texture);
        }
        s_textures.Clear();
        if (s_skin != null) UnityEngine.Object.Destroy(s_skin);
        s_skin = null;
        s_failed = false;
        s_sliced.Clear();
    }

    private static readonly Dictionary<int, GUIStyle> s_sliced = new();

    /// <summary>Draws a rounded texture 9-sliced, so its corners keep their shape at any size.</summary>
    public static void DrawSliced(Rect rect, Texture2D texture, int border)
    {
        if (!s_sliced.TryGetValue(border, out var style))
        {
            style = new GUIStyle { border = Offset(border) };
            s_sliced[border] = style;
        }
        style.normal.background = texture;
        style.Draw(rect, false, false, false, false);
    }

    public static Texture2D BadgeFor(StatusState state) => state switch
    {
        StatusState.Active => BadgeGood,
        StatusState.Holding or StatusState.Paused => BadgePause,
        StatusState.Waiting => BadgeWaiting,
        _ => BadgeOff,
    };

    public static Color StateColor(StatusState state) => state switch
    {
        StatusState.Active => Good,
        StatusState.Holding or StatusState.Paused => Pause,
        StatusState.Waiting => AccentText,
        _ => Dim,
    };

    private static GUIStyle MakeButton(GUIStyle from, Color normal, Color hover, Color active, Color on)
    {
        var style = new GUIStyle(from)
        {
            border = Offset(8), padding = new RectOffset(10, 10, 4, 5), overflow = Offset(0),
            fontSize = 13, fontStyle = s_body, alignment = TextAnchor.MiddleCenter, fixedHeight = 26f,
        };
        style.normal.background = Rounded(8, 8, normal, normal, 0f);
        style.hover.background = Rounded(8, 8, hover, hover, 0f);
        style.active.background = Rounded(8, 8, active, active, 0f);
        style.focused.background = style.normal.background;
        Color onHover = Color.Lerp(on, Color.white, 0.1f);
        style.onNormal.background = Rounded(8, 8, on, on, 0f);
        style.onHover.background = Rounded(8, 8, onHover, onHover, 0f);
        style.onActive.background = style.onHover.background;
        style.onFocused.background = style.onNormal.background;
        TextColor(style, Text);
        if (on == Accent) SetOnText(style, Color.white);
        return style;
    }

    private static void SetOnText(GUIStyle style, Color color)
    {
        style.onNormal.textColor = color;
        style.onHover.textColor = color;
        style.onActive.textColor = color;
        style.onFocused.textColor = color;
    }

    private static void Background(GUIStyle style, Texture2D? texture)
    {
        style.normal.background = texture;
        style.hover.background = texture;
        style.active.background = texture;
        style.focused.background = texture;
        style.onNormal.background = texture;
        style.onHover.background = texture;
        style.onActive.background = texture;
        style.onFocused.background = texture;
    }

    private static void TextColor(GUIStyle style, Color color)
    {
        style.normal.textColor = color;
        style.hover.textColor = color;
        style.active.textColor = color;
        style.focused.textColor = color;
        style.onNormal.textColor = color;
        style.onHover.textColor = color;
        style.onActive.textColor = color;
        style.onFocused.textColor = color;
    }

    private static RectOffset Offset(int all) => new(all, all, all, all);

    public static Color Hex(int rgb, float alpha = 1f) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);

    // A 9-sliceable rounded rectangle with left and right corner radii (0 = square).
    private static Texture2D Rounded(int left, int right, Color fill, Color edge, float edgeWidth)
    {
        int r = Math.Max(left, right);
        int size = r * 2 + 2;
        return Paint(size, size, (x, y) =>
        {
            float px = x + 0.5f, py = y + 0.5f;
            float radius = px < size / 2f ? left : right;
            float d = BoxDistance(px, py, size, size, radius);
            Color c = edgeWidth > 0f ? Color.Lerp(fill, edge, Mathf.Clamp01(d + edgeWidth + 0.5f)) : fill;
            return Shade(c, Coverage(d));
        });
    }

    private static Texture2D Circle(int size, Color color) =>
        Paint(size, size, (x, y) => Shade(color, Coverage(CircleDistance(x + 0.5f, y + 0.5f, size / 2f, size / 2f, size / 2f - 0.5f))));

    private static Texture2D Switch(bool on, Color track, Color knob)
    {
        const int w = 36, h = 20;
        float knobX = on ? w - h / 2f : h / 2f;
        return Paint(w, h, (x, y) =>
        {
            float px = x + 0.5f, py = y + 0.5f;
            Color c = Shade(track, Coverage(BoxDistance(px, py, w, h, h / 2f)));
            float k = Coverage(CircleDistance(px, py, knobX, h / 2f, h / 2f - 3f));
            return Over(Shade(knob, k), c);
        });
    }

    // Signed distance to a rounded box filling the texture (negative inside).
    private static float BoxDistance(float px, float py, float w, float h, float r)
    {
        float qx = Mathf.Abs(px - w / 2f) - (w / 2f - r);
        float qy = Mathf.Abs(py - h / 2f) - (h / 2f - r);
        float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude;
        return outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
    }

    private static float CircleDistance(float px, float py, float cx, float cy, float r) =>
        new Vector2(px - cx, py - cy).magnitude - r;

    private static float Coverage(float distance) => Mathf.Clamp01(0.5f - distance);

    private static Color Shade(Color c, float coverage) => new(c.r, c.g, c.b, c.a * coverage);

    private static Color Over(Color top, Color bottom)
    {
        float a = top.a + bottom.a * (1f - top.a);
        if (a <= 0f) return Color.clear;
        Color rgb = (top * top.a + bottom * bottom.a * (1f - top.a)) / a;
        return new Color(rgb.r, rgb.g, rgb.b, a);
    }

    private static Texture2D Paint(int w, int h, Func<int, int, Color> pixel)
    {
        var texture = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                texture.SetPixel(x, y, pixel(x, y));
            }
        }
        texture.Apply();
        s_textures.Add(texture);
        return texture;
    }
}
