using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

// The selected theme: the menu's own GUISkin copy plus extra styles, built from the theme's values with procedural
// textures, and built again when the theme, font or weight changes.
internal static class Theme
{
    // The values the skin was built from; Slate until the first build.
    private static ThemeValues s_values = ThemeValues.Presets[0].Values;
    private static string? s_builtIdentity;

    public static ThemeValues Values => s_values;
    public static Color Text => s_values.Text;
    public static Color Dim => s_values.Dim;
    public static Color Accent => s_values.Accent;
    public static Color AccentText => s_values.AccentText;
    public static Color OnAccent => s_values.OnAccent;
    public static Color Good => s_values.StateActive;
    public static Color Pause => s_values.StatePaused;
    public static Color Off => s_values.StateOff;
    /// <summary>Base window and sidebar opacity, before the player's Background setting.</summary>
    public static float Opacity => s_values.Opacity;

    // Corner radii the drawing code slices textures with; each matches the texture it goes with.
    public static int WindowRadius => s_values.RadiusWindow;
    public static int ChipRadius => s_values.RadiusChip;
    public static int PillRadius => Math.Min(16, s_values.RadiusWindow + 2);
    public static int PipRadius => Math.Max(0, s_values.RadiusChip - 4);
    public static int PanelRadius => Math.Min(12, s_values.RadiusCard + 2);
    /// <summary>Horizontal padding inside a card, both sides together.</summary>
    public static float CardInset => s_values.CardPad * 2f;

    public const float SidebarWidth = 150f;

    private static GUISkin? s_skin;
    private static bool s_failed;
    // Fonts made with Font.CreateDynamicFontFromOSFont garble IMGUI text in this game, so weight comes from
    // FontStyle on Unity's built-in font. The skin is rebuilt when the setting changes.
    private static int s_builtWeight = -1;
    private static int s_builtFont = -1;
    private static float s_builtCorners = -1f;
    // Config.Font options: game font asset names; null keeps Unity's built-in font. The game owns these, never destroy them.
    private static readonly string?[] s_gameFonts = { null, "PretendardVariable", "NotoSansJP-Regular" };
    private static FontStyle s_body = FontStyle.Normal;
    private static readonly List<Texture2D> s_textures = new();

    public static GUIStyle Sidebar = null!, NavTitle = null!, NavVersion = null!, NavItem = null!, NavIcon = null!, NavHint = null!, Main = null!;
    public static GUIStyle PageTitle = null!, Badge = null!, Close = null!;
    public static GUIStyle Card = null!, CardTitle = null!, RowLabel = null!, Hint = null!, Value = null!;
    public static GUIStyle SearchField = null!, Placeholder = null!, FieldClear = null!, SubTab = null!, Segment = null!, SmallButton = null!, KeyChip = null!;
    public static GUIStyle Track = null!, TrackFill = null!, SliderBody = null!, Thumb = null!;
    public static GUIStyle ChipNumber = null!, ChipName = null!;
    public static GUIStyle Pill = null!, PillLabel = null!, Pip = null!, Panel = null!, ToastText = null!;
    public static Texture2D SwitchOff = null!, SwitchOffHover = null!, SwitchOn = null!, SwitchOnHover = null!;
    // Drawn by the menu itself so its opacity setting applies; the styles carry no background.
    public static Texture2D WindowBg = null!, SidebarBg = null!;
    public static Texture2D Gear = null!, Dot = null!, Ring = null!, Square = null!, Bars = null!, Dash = null!, FocusRing = null!, Divider = null!, ChipOff = null!, ChipOn = null!, ChipPaused = null!;
    public static Texture2D PipOn = null!, PipOff = null!, PipPaused = null!;
    public static Texture2D BadgeActive = null!, BadgeWaiting = null!, BadgeHolding = null!, BadgePaused = null!, BadgeOff = null!;

    /// <summary>The themed skin, or null to draw with the default one.</summary>
    public static GUISkin? Skin
    {
        get
        {
            if (s_failed) return null;
            var theme = Themes.Resolve(Config.Theme.Value);
            if (s_skin != null && (s_builtWeight != Config.FontWeight.Value || s_builtFont != Config.Font.Value || s_builtIdentity != theme.Identity
                || s_builtCorners != Config.Corners.Value)) Release();
            if (s_skin != null) return s_skin;
            try
            {
                Build(theme);
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

    private static void Build(ResolvedTheme theme)
    {
        // The player's Corners setting scales the theme's radii, within the range theme files may use.
        s_builtCorners = Config.Corners.Value;
        var v = theme.Values.Clone();
        int Round(int radius) => Math.Clamp(Mathf.RoundToInt(radius * s_builtCorners), 0, 16);
        v.RadiusWindow = Round(v.RadiusWindow);
        v.RadiusCard = Round(v.RadiusCard);
        v.RadiusControl = Round(v.RadiusControl);
        v.RadiusChip = Round(v.RadiusChip);
        s_values = v;
        s_builtIdentity = theme.Identity;
        var skin = UnityEngine.Object.Instantiate((UnityEngine.Object)GUI.skin).Cast<GUISkin>();
        skin.hideFlags = HideFlags.HideAndDontSave;
        s_builtWeight = Config.FontWeight.Value;
        s_builtFont = Config.Font.Value;
        Font? gameFont = FindGameFont(s_builtFont);
        if (gameFont != null) skin.font = gameFont;
        s_body = s_builtWeight == 1 ? FontStyle.Bold : FontStyle.Normal;
        int rWindow = v.RadiusWindow, rCard = v.RadiusCard, rControl = v.RadiusControl, rChip = v.RadiusChip;
        int rBadge = Math.Min(11, rChip + 2), rPanel = PanelRadius, rBar = Math.Min(3, rControl), rTrack = Math.Min(2, rControl);

        skin.window = new GUIStyle(skin.window) { border = Offset(Slice(rWindow)), padding = Offset(0), overflow = Offset(0), contentOffset = Vector2.zero };
        WindowBg = Rounded(rWindow, rWindow, v.Window, v.WindowEdge, 1f);
        Background(skin.window, null);

        skin.label = new GUIStyle(skin.label) { fontSize = v.FontBody, fontStyle = s_body, wordWrap = false, richText = true, padding = new RectOffset(0, 0, 2, 2) };
        TextColor(skin.label, v.Text);
        skin.button = MakeButton(skin.button, v.Control, v.ControlHover, v.ControlActive, v.Accent);
        skin.box = new GUIStyle(skin.box) { border = Offset(Slice(rCard)), padding = Offset(10) };
        Background(skin.box, Rounded(rCard, rCard, v.Card, v.CardEdge, 1f));

        skin.scrollView = new GUIStyle(skin.scrollView);
        Background(skin.scrollView, null);
        skin.verticalScrollbar = new GUIStyle(skin.verticalScrollbar)
        {
            fixedWidth = 6f, border = Offset(Slice(rBar)), margin = new RectOffset(6, 0, 0, 0), padding = Offset(0),
        };
        Background(skin.verticalScrollbar, Rounded(rBar, rBar, v.Sidebar, v.Sidebar, 0f));
        skin.verticalScrollbarThumb = new GUIStyle(skin.verticalScrollbarThumb) { fixedWidth = 6f, border = Offset(Slice(rBar)) };
        Background(skin.verticalScrollbarThumb, Rounded(rBar, rBar, v.ControlActive, v.ControlActive, 0f));
        skin.verticalScrollbarThumb.hover.background = Rounded(rBar, rBar, v.Dim, v.Dim, 0f);
        skin.horizontalScrollbar = new GUIStyle(skin.horizontalScrollbar) { fixedHeight = 0f };
        Background(skin.horizontalScrollbar, null);

        // Rounded on the left only, so it sits flush against the content area.
        Sidebar = new GUIStyle { border = new RectOffset(Slice(rWindow), 2, Slice(rWindow), Slice(rWindow)), padding = new RectOffset(10, 10, 14, 12) };
        SidebarBg = Rounded(rWindow, 0, v.Sidebar, v.Sidebar, 0f);
        NavTitle = new GUIStyle(skin.label) { fontSize = v.FontNav, fontStyle = FontStyle.Bold, padding = new RectOffset(6, 0, 0, 0) };
        NavVersion = new GUIStyle(skin.label) { fontSize = v.FontCaption, padding = new RectOffset(6, 0, 0, 0) };
        TextColor(NavVersion, v.Dim);
        NavItem = MakeButton(skin.button, Color.clear, v.NavHover, v.Control, v.Accent);
        NavItem.alignment = TextAnchor.MiddleLeft;
        NavItem.fontStyle = FontStyle.Bold;
        NavItem.fixedHeight = 32f;
        NavItem.padding = new RectOffset(26, 10, 0, 1);
        NavItem.margin = new RectOffset(0, 0, 0, 4);
        TextColor(NavItem, v.Dim);
        NavItem.hover.textColor = v.Text;
        SetOnText(NavItem, v.OnAccent);
        // A small square button for the Settings gear, on the closing hint's line.
        NavIcon = MakeButton(skin.button, Color.clear, v.NavHover, v.Control, v.Accent);
        NavIcon.fixedWidth = 24f;
        NavIcon.fixedHeight = 24f;
        NavIcon.padding = new RectOffset(0, 0, 0, 0);
        NavIcon.margin = new RectOffset(0, 6, 0, 0);
        Main = new GUIStyle { padding = new RectOffset(16, 16, 12, 14) };

        PageTitle = new GUIStyle(skin.label) { fontSize = v.FontTitle, fontStyle = FontStyle.Bold, padding = new RectOffset(0, 0, 1, 0) };
        Badge = new GUIStyle(skin.label)
        {
            fontSize = v.FontSmall, fontStyle = FontStyle.Bold, border = Offset(Slice(rBadge)), alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(20, 9, 0, 1), margin = new RectOffset(8, 0, 3, 0), fixedHeight = 22f,
        };
        Close = MakeButton(skin.button, Color.clear, v.ControlHover, v.ControlActive, v.Accent);
        Close.fontSize = v.FontTitle;
        Close.fixedWidth = 26f;
        Close.fixedHeight = 26f;
        Close.padding = new RectOffset(0, 0, 0, 3);
        Close.margin = new RectOffset(8, 0, 0, 0);
        TextColor(Close, v.Dim);
        Close.hover.textColor = v.Text;

        // Padding comes from one theme value: the sides take it whole, top and bottom a little less.
        int pad = v.CardPad;
        Card = new GUIStyle(skin.box) { margin = new RectOffset(0, 0, 0, v.CardGap), padding = new RectOffset(pad, pad, Math.Max(pad - 4, 4), Math.Max(pad - 2, 10)) };
        CardTitle = new GUIStyle(skin.label) { fontSize = v.FontCaption, fontStyle = FontStyle.Bold, padding = new RectOffset(0, 0, 0, 4) };
        TextColor(CardTitle, v.Dim);
        // No left margin, so layout labels line up with the switch rows, which draw their label at the row's edge.
        RowLabel = new GUIStyle(skin.label) { alignment = TextAnchor.MiddleLeft, margin = new RectOffset(0, 4, 0, 0) };
        Hint = new GUIStyle(skin.label) { fontSize = v.FontSmall, fontStyle = FontStyle.Normal, wordWrap = true, margin = new RectOffset(0, 0, 2, 4) };
        TextColor(Hint, v.Dim);
        NavHint = new GUIStyle(Hint) { wordWrap = false, alignment = TextAnchor.MiddleLeft, fixedHeight = 24f, margin = new RectOffset(0, 0, 0, 0) };
        Value = new GUIStyle(skin.label) { alignment = TextAnchor.MiddleRight, margin = new RectOffset(4, 0, 0, 0) };

        skin.textField = new GUIStyle(skin.textField)
        {
            border = Offset(Slice(rControl)), padding = new RectOffset(9, 9, 5, 5), margin = new RectOffset(0, 6, 0, 0),
            fontSize = v.FontBody, fontStyle = FontStyle.Normal, fixedHeight = 26f, alignment = TextAnchor.MiddleLeft, wordWrap = false,
        };
        Background(skin.textField, Rounded(rControl, rControl, v.InputBg, v.InputEdge, 1f));
        skin.textField.focused.background = Rounded(rControl, rControl, v.InputBg, v.Accent, 1f);
        skin.textField.onFocused.background = skin.textField.focused.background;
        TextColor(skin.textField, v.Text);
        skin.settings.cursorColor = v.Text;
        skin.settings.selectionColor = new Color(v.Accent.r, v.Accent.g, v.Accent.b, 0.45f);
        // Room on the right for the clear button drawn inside it.
        SearchField = new GUIStyle(skin.textField) { padding = new RectOffset(9, 28, 5, 5), margin = new RectOffset(0, 0, 0, 0) };
        Placeholder = new GUIStyle(skin.label)
        {
            fontSize = v.FontBody, fontStyle = FontStyle.Normal, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(10, 28, 0, 1),
        };
        TextColor(Placeholder, v.Dim);
        FieldClear = new GUIStyle(skin.label) { fontSize = v.FontBody + 3, alignment = TextAnchor.MiddleCenter, padding = new RectOffset(0, 0, 0, 2) };
        TextColor(FieldClear, v.Dim);
        FieldClear.hover.textColor = v.Text;

        SubTab = MakeButton(skin.button, Color.clear, v.ControlHover, v.ControlActive, v.Control);
        SubTab.fontSize = v.FontSmall;
        SubTab.fontStyle = FontStyle.Bold;
        SubTab.margin = new RectOffset(0, 4, 0, 0);
        TextColor(SubTab, v.Dim);
        SubTab.hover.textColor = v.Text;
        SetOnText(SubTab, v.Text);

        Segment = MakeButton(skin.button, v.Control, v.ControlHover, v.ControlActive, v.Accent);
        Segment.margin = new RectOffset(0, 4, 2, 2);
        SmallButton = MakeButton(skin.button, v.Control, v.ControlHover, v.ControlActive, v.Accent);
        SmallButton.fontSize = v.FontSmall;
        SmallButton.fixedHeight = 24f;
        SmallButton.padding = new RectOffset(10, 10, 2, 3);
        SmallButton.margin = new RectOffset(0, 4, 2, 2);
        KeyChip = new GUIStyle(skin.label)
        {
            alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, border = Offset(Slice(rControl)),
            padding = new RectOffset(9, 9, 2, 3), margin = new RectOffset(4, 6, 2, 2), fixedHeight = 24f,
        };
        Background(KeyChip, Rounded(rControl, rControl, v.InputBg, v.InputEdge, 1f));
        TextColor(KeyChip, v.AccentText);

        Track = new GUIStyle { border = Offset(Slice(rTrack)) };
        Track.normal.background = Rounded(rTrack, rTrack, v.Track, v.Track, 0f);
        TrackFill = new GUIStyle { border = Offset(Slice(rTrack)) };
        TrackFill.normal.background = Rounded(rTrack, rTrack, v.Accent, v.Accent, 0f);
        SliderBody = new GUIStyle { fixedHeight = 18f };
        Thumb = new GUIStyle { fixedWidth = 16f, fixedHeight = 16f };
        Thumb.normal.background = Circle(16, v.Thumb);
        Thumb.hover.background = Circle(16, v.ThumbHover);
        Thumb.active.background = Circle(16, v.AccentText);

        ChipNumber = new GUIStyle(skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = v.FontTitle - 2, fontStyle = FontStyle.Bold };
        ChipName = new GUIStyle(skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = v.FontCaption - 1, clipping = TextClipping.Clip };
        ChipOff = Rounded(rChip, rChip, v.Sidebar, v.CardEdge, 1f);
        ChipOn = Rounded(rChip, rChip, v.ChipOn, v.Accent, 1.5f);
        ChipPaused = Rounded(rChip, rChip, v.ChipPaused, v.StatePaused, 1.5f);

        SwitchOff = Switch(on: false, v.Track, v.Dim);
        SwitchOffHover = Switch(on: false, v.ControlActive, v.Text);
        SwitchOn = Switch(on: true, v.Accent, v.OnAccent);
        SwitchOnHover = Switch(on: true, v.AccentHover, v.OnAccent);
        // State shapes, white and tinted when drawn, so colour is never the only cue.
        Gear = Paint(32, 32, (x, y) =>
        {
            float px = x + 0.5f - 16f, py = y + 0.5f - 16f;
            float angle = Mathf.Atan2(py, px), distance = Mathf.Sqrt(px * px + py * py);
            // Eight teeth around a body, with a hole in the middle; edges smoothed over about a pixel.
            float edge = Mathf.Cos(angle * 8f) > 0.25f ? 14.5f : 11f;
            float body = Mathf.Clamp01(edge - distance + 0.5f);
            float hole = Mathf.Clamp01(5f - distance + 0.5f);
            return Shade(Color.white, Mathf.Clamp01(body - hole));
        });
        Dot = Circle(10, Color.white);
        Ring = Paint(10, 10, (x, y) => Shade(Color.white, Mathf.Clamp01(Coverage(CircleDistance(x + 0.5f, y + 0.5f, 5f, 5f, 4.5f)) - Coverage(CircleDistance(x + 0.5f, y + 0.5f, 5f, 5f, 2.5f)))));
        Square = Paint(10, 10, (x, y) => Shade(Color.white, Coverage(RectDistance(x + 0.5f, y + 0.5f, 5f, 5f, 4f, 4f))));
        Bars = Paint(10, 10, (x, y) => Shade(Color.white, Coverage(Mathf.Min(RectDistance(x + 0.5f, y + 0.5f, 3f, 5f, 1.2f, 4.5f), RectDistance(x + 0.5f, y + 0.5f, 7f, 5f, 1.2f, 4.5f)))));
        Dash = Paint(10, 10, (x, y) => Shade(Color.white, Coverage(RectDistance(x + 0.5f, y + 0.5f, 5f, 5f, 4f, 1.2f))));
        FocusRing = Rounded(rControl + 2, rControl + 2, Color.clear, v.Focus, 2f);
        Divider = Paint(1, 1, (_, _) => v.CardEdge);

        BadgeActive = Rounded(rBadge, rBadge, v.BadgeActive, v.BadgeActive, 0f);
        BadgeWaiting = Rounded(rBadge, rBadge, v.BadgeWaiting, v.BadgeWaiting, 0f);
        BadgeHolding = Rounded(rBadge, rBadge, v.BadgeHolding, v.BadgeHolding, 0f);
        BadgePaused = Rounded(rBadge, rBadge, v.BadgePaused, v.BadgePaused, 0f);
        BadgeOff = Rounded(rBadge, rBadge, v.BadgeOff, v.BadgeOff, 0f);

        int rPill = PillRadius;
        Pill = new GUIStyle { border = Offset(Slice(rPill)) };
        Pill.normal.background = Rounded(rPill, rPill, v.Window, v.WindowEdge, 1f);
        Pill.hover.background = Rounded(rPill, rPill, v.Card, v.InputEdge, 1f);
        PillLabel = new GUIStyle(skin.label) { fontSize = v.FontSmall, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
        Pip = new GUIStyle(skin.label) { fontSize = v.FontCaption, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, padding = Offset(0) };
        int rPip = PipRadius;
        PipOn = Rounded(rPip, rPip, v.Accent, v.Accent, 0f);
        PipOff = Rounded(rPip, rPip, Color.clear, v.PipEdge, 1f);
        PipPaused = Rounded(rPip, rPip, v.ChipPaused, v.StatePaused, 1f);
        Panel = new GUIStyle { border = Offset(Slice(rPanel)), padding = new RectOffset(12, 12, 10, 12) };
        Panel.normal.background = Rounded(rPanel, rPanel, v.Window, v.WindowEdge, 1f);
        ToastText = new GUIStyle(skin.label) { wordWrap = true, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(28, 14, 9, 10) };
        // World labels: white text tinted by the caller's colour, so the theme gives the font and size.
        var worldLabel = new GUIStyle(skin.label) { fontSize = v.FontSmall, alignment = TextAnchor.MiddleCenter, font = skin.font };
        TextColor(worldLabel, Color.white);
        Draw.LabelStyle = worldLabel;

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
        s_builtCorners = -1f;
        s_builtIdentity = null;
        Draw.LabelStyle = null;
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
        border = Slice(border);
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
        StatusState.Active => BadgeActive,
        StatusState.Waiting => BadgeWaiting,
        StatusState.Holding => BadgeHolding,
        StatusState.Paused => BadgePaused,
        _ => BadgeOff,
    };

    public static Color StateColor(StatusState state) => state switch
    {
        StatusState.Active => s_values.StateActive,
        StatusState.Waiting => s_values.StateWaiting,
        StatusState.Holding => s_values.StateHolding,
        StatusState.Paused => s_values.StatePaused,
        _ => s_values.StateOff,
    };

    /// <summary>The dot for a state: Active filled circle, Waiting ring, Holding square, Paused two bars, Off a dash.</summary>
    public static Texture2D StateShape(StatusState state) => state switch
    {
        StatusState.Active => Dot,
        StatusState.Waiting => Ring,
        StatusState.Holding => Square,
        StatusState.Paused => Bars,
        _ => Dash,
    };

    public static int FocusRadius => s_values.RadiusControl + 2;

    /// <summary>Textures the skin holds now, for the dev bridge.</summary>
    public static int TextureCount => s_textures.Count;

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
        TextColor(style, s_values.Text);
        if (on == s_values.Accent) SetOnText(style, s_values.OnAccent);
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

    // Square corners still need a slice wide enough for the edge; every rounded texture is cut at this border.
    private static int Slice(int radius) => Math.Max(radius, 2);

    // A 9-sliceable rounded rectangle with left and right corner radii (0 = square).
    private static Texture2D Rounded(int left, int right, Color fill, Color edge, float edgeWidth)
    {
        int r = Slice(Math.Max(left, right));
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

    // Signed distance to an axis-aligned rectangle (centre, half sizes); square corners.
    private static float RectDistance(float px, float py, float cx, float cy, float hw, float hh) =>
        Mathf.Max(Mathf.Abs(px - cx) - hw, Mathf.Abs(py - cy) - hh);

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
