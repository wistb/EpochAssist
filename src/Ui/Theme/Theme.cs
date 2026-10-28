using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

// The selected theme: the menu's own GUISkin copy plus extra styles, built from the theme's values with procedural
// textures, and built again when the theme, font or weight changes.
internal static partial class Theme
{
    // The values the skin was built from; Carbonfox until the first build.
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
    /// <summary>The corner radius of header tags (a page's state and a mod conflict), so they match.</summary>
    public static int TagRadius => Math.Min(11, ChipRadius + 2);
    /// <summary>The height of header tags.</summary>
    public const float TagHeight = 18f;
    public static int PillRadius => Math.Min(16, s_values.RadiusWindow + 2);
    // Concentric with the pill's corner: the pill's radius less the gap between the two edges, so both curves share a
    // centre at any Corners setting; at most half the slot (a circle). Where the pill's corner is smaller than the gap
    // the slots keep a slight rounding (maintainer), unless Corners is 0% (square).
    private const int MinPipRadius = 2;
    public static int PipRadius => LookSettings.Corners.Value <= 0f
        ? 0
        : Math.Clamp(PillRadius - Mathf.RoundToInt(Hud.PipInset), MinPipRadius, Mathf.FloorToInt(Hud.PipSize / 2f));
    public static int PanelRadius => Math.Min(12, s_values.RadiusCard + 2);
    /// <summary>Horizontal padding inside a card, both sides together.</summary>
    public static float CardInset => s_values.CardPad * 2f;

    // What the colour roles were last pushed from: the theme's identity, the Colour vision value and the theme files' read.
    private static string? s_rolesIdentity;
    private static int s_rolesVision = -1, s_rolesFiles = -1;

    private static GUISkin? s_skin;
    private static bool s_failed;
    // Fonts made with Font.CreateDynamicFontFromOSFont garble IMGUI text in this game, so weight comes from
    // FontStyle on Unity's built-in font. The skin is rebuilt when the setting changes.
    private static int s_builtWeight = -1;
    private static int s_builtFont = -1;
    private static float s_builtCorners = -1f;
    private static string? s_builtAccent;
    // LookSettings.Font options: game font asset names; null keeps Unity's built-in font. The game owns these, never destroy them.
    private static readonly string?[] s_gameFonts = { null, "PretendardVariable", "NotoSansJP-Regular" };
    private static FontStyle s_body = FontStyle.Normal;
    private static readonly List<Texture2D> s_textures = new();

    public static GUIStyle PageTitle = null!;
    public static GUIStyle Card = null!, CardTitle = null!, RowLabel = null!, Hint = null!, Value = null!;
    public static GUIStyle Placeholder = null!, SmallButton = null!;
    public static GUIStyle ChipNumber = null!, ChipName = null!;
    public static GUIStyle Pill = null!, PillLabel = null!, Pip = null!, Panel = null!, HintCard = null!, ToastText = null!;
    /// <summary>A hint card's accent stripe, white, to tint; see AccentStripe.</summary>
    public static GUIStyle HintStripe = null!;
    public static Texture2D SwitchOff = null!, SwitchOffHover = null!, SwitchOn = null!, SwitchOnHover = null!;
    public static Texture2D Chevron = null!, ChevronRight = null!, ChevronUp = null!;
    public static Texture2D IconDot = null!, IconRing = null!, IconSquare = null!, IconBars = null!, IconDash = null!, IconWarning = null!;
    public static Texture2D Swatch = null!, Dot = null!, Ring = null!, Square = null!, Bars = null!, Dash = null!, FocusRing = null!, ChipOff = null!, ChipOn = null!, ChipPaused = null!;
    public static Texture2D PipOn = null!, PipOff = null!, PipPaused = null!;
    public static Texture2D BadgeActive = null!, BadgeWaiting = null!, BadgeHolding = null!, BadgePaused = null!, BadgeOff = null!;
    /// <summary>A white badge, tinted when drawn: the state badges' shape in any colour (the conflict pill's orange).</summary>
    public static Texture2D BadgeTint = null!;

    /// <summary>The themed skin, or null to draw with the default one.</summary>
    public static GUISkin? Skin
    {
        get
        {
            if (s_failed) return null;
            var theme = Themes.Resolve(LookSettings.Theme.Value);
            if (s_skin != null && (s_builtWeight != LookSettings.FontWeight.Value || s_builtFont != LookSettings.Font.Value || s_builtIdentity != theme.Identity
                || s_builtCorners != LookSettings.Corners.Value || s_builtAccent != AccentKey)) Release();
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

    /// <summary>
    /// ColourRoles' sync hook, on the first role read in a frame: pushes the roles when the selected theme, the Colour
    /// vision setting or the theme files changed. A palette replaces every role; theme values apply with Standard. It
    /// needs no skin, so roles follow a switch made with the menu closed.
    /// </summary>
    public static void SyncRoles()
    {
        var theme = Themes.Resolve(LookSettings.Theme.Value);
        int vision = LookSettings.ColourVision.Value;
        if (ReferenceEquals(theme.Identity, s_rolesIdentity) && vision == s_rolesVision && Themes.Generation == s_rolesFiles) return;
        s_rolesIdentity = theme.Identity;
        s_rolesVision = vision;
        s_rolesFiles = Themes.Generation;
        ColourRoles.Push(Palettes.For(vision)?.Roles ?? theme.Values.Roles);
    }

    private static void Build(ResolvedTheme theme)
    {
        // The player's Corners setting scales the theme's radii, within the range theme files may use.
        s_builtCorners = LookSettings.Corners.Value;
        var v = theme.Values.Clone();
        int Round(int radius) => Math.Clamp(Mathf.RoundToInt(radius * s_builtCorners), 0, 16);
        v.RadiusWindow = Round(v.RadiusWindow);
        v.RadiusCard = Round(v.RadiusCard);
        v.RadiusControl = Round(v.RadiusControl);
        v.RadiusChip = Round(v.RadiusChip);
        ThemeAccent = v.Accent;
        ThemeHighlight = v.Highlight;
        ThemeAccentText = v.AccentText;
        s_builtAccent = AccentKey;
        if (LookSettings.Accent.TryGetColor(out Color accent)) ApplyAccent(v, accent);
        ApplyAccents(v);
        s_values = v;
        // Core draws and counts with what the theme hands it.
        Draw.DefaultBars = new(new Color(Off.r * 0.3f, Off.g * 0.3f, Off.b * 0.3f, 0.75f), Good, Accent);
        Diagnostics.UiTextures = () => TextureCount;
        s_builtIdentity = theme.Identity;
        var skin = UnityEngine.Object.Instantiate((UnityEngine.Object)GUI.skin).Cast<GUISkin>();
        skin.hideFlags = HideFlags.HideAndDontSave;
        s_builtWeight = LookSettings.FontWeight.Value;
        s_builtFont = LookSettings.Font.Value;
        Font? gameFont = FindGameFont(s_builtFont);
        if (gameFont != null) skin.font = gameFont;
        s_body = s_builtWeight == 1 ? FontStyle.Bold : FontStyle.Normal;
        int rWindow = v.RadiusWindow, rCard = v.RadiusCard, rControl = v.RadiusControl, rChip = v.RadiusChip;
        int rBadge = Math.Min(11, rChip + 2), rPanel = PanelRadius, rBar = Math.Min(3, rControl), rTrack = Math.Min(2, rControl);

        skin.window = new GUIStyle(skin.window) { border = Offset(Slice(rWindow)), padding = Offset(0), overflow = Offset(0), contentOffset = Vector2.zero };
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

        PageTitle = new GUIStyle(skin.label) { fontSize = v.FontTitle, fontStyle = FontStyle.Bold, padding = new RectOffset(0, 0, 1, 0) };
        // Padding comes from one theme value: the sides take it whole, top and bottom a little less.
        int pad = v.CardPad;
        Card = new GUIStyle(skin.box) { margin = new RectOffset(0, 0, 0, v.CardGap), padding = new RectOffset(pad, pad, Math.Max(pad - 4, 4), Math.Max(pad - 2, 10)) };
        CardTitle = new GUIStyle(skin.label) { fontSize = v.FontCaption, fontStyle = FontStyle.Bold, padding = new RectOffset(0, 0, 0, 4) };
        TextColor(CardTitle, v.Dim);
        // No left margin, so layout labels line up with the switch rows, which draw their label at the row's edge.
        RowLabel = new GUIStyle(skin.label) { alignment = TextAnchor.MiddleLeft, margin = new RectOffset(0, 4, 0, 0) };
        Hint = new GUIStyle(skin.label) { fontSize = v.FontSmall, fontStyle = FontStyle.Normal, wordWrap = true, margin = new RectOffset(0, 0, 2, 4) };
        // Hints in the secondary text colour: dim grey on the grey window was hard to read.
        TextColor(Hint, v.Secondary);
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
        Placeholder = new GUIStyle(skin.label)
        {
            fontSize = v.FontBody, fontStyle = FontStyle.Normal, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(10, 28, 0, 1),
        };
        TextColor(Placeholder, v.Dim);
        SmallButton = MakeButton(skin.button, v.Control, v.ControlHover, v.ControlActive, v.Accent);
        SmallButton.fontSize = v.FontSmall;
        SmallButton.fixedHeight = 24f;
        SmallButton.padding = new RectOffset(10, 10, 2, 3);
        SmallButton.margin = new RectOffset(0, 4, 2, 2);
        ChipNumber = new GUIStyle(skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = v.FontTitle - 2, fontStyle = FontStyle.Bold };
        ChipName = new GUIStyle(skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = v.FontCaption - 1, clipping = TextClipping.Clip };
        ChipOff = Rounded(rChip, rChip, v.Card, v.CardEdge, 1f);
        ChipOn = Rounded(rChip, rChip, v.ChipOn, v.Highlight, 1.5f);
        ChipPaused = Rounded(rChip, rChip, v.ChipPaused, v.StatePaused, 1.5f);

        SwitchOff = Switch(on: false, v.Track, v.Dim);
        SwitchOffHover = Switch(on: false, v.ControlActive, v.Text);
        SwitchOn = Switch(on: true, SwitchTrack(v, 0.7f), v.OnAccent);
        SwitchOnHover = Switch(on: true, SwitchTrack(v, 0.82f), v.OnAccent);
        // A down chevron for dropdowns: two strokes meeting at the bottom (the game's fonts have no arrow glyphs).
        Chevron = Paint(12, 12, (x, y) =>
        {
            static float Segment(float px, float py, float ax, float ay, float bx, float by)
            {
                float dx = bx - ax, dy = by - ay;
                float t = Mathf.Clamp01(((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy));
                float ex = px - (ax + t * dx), ey = py - (ay + t * dy);
                return Mathf.Sqrt(ex * ex + ey * ey);
            }
            float px = x + 0.5f, py = y + 0.5f;
            // Texture rows count from the bottom, so the point is at the low y.
            float d = Mathf.Min(Segment(px, py, 2f, 8f, 6f, 4f), Segment(px, py, 6f, 4f, 10f, 8f));
            return Shade(Color.white, Mathf.Clamp01(1.6f - d));
        });
        // The same chevron pointing right, for a folded group (rotating the drawing goes wrong inside a scroll view).
        ChevronRight = Paint(12, 12, (x, y) => Chevron.GetPixel(y, 11 - x));
        // And pointing up, for the menu's collapse button.
        ChevronUp = Paint(12, 12, (x, y) => Chevron.GetPixel(x, 11 - y));
        // State shapes, white and tinted when drawn, so colour is never the only cue.
        Dot = Circle(10, Color.white);
        Ring = Paint(10, 10, (x, y) => Shade(Color.white, Mathf.Clamp01(Coverage(CircleDistance(x + 0.5f, y + 0.5f, 5f, 5f, 4.5f)) - Coverage(CircleDistance(x + 0.5f, y + 0.5f, 5f, 5f, 2.5f)))));
        Square = Paint(10, 10, (x, y) => Shade(Color.white, Coverage(RectDistance(x + 0.5f, y + 0.5f, 5f, 5f, 4f, 4f))));
        Bars = Paint(10, 10, (x, y) => Shade(Color.white, Coverage(Mathf.Min(RectDistance(x + 0.5f, y + 0.5f, 3f, 5f, 1.2f, 4.5f), RectDistance(x + 0.5f, y + 0.5f, 7f, 5f, 1.2f, 4.5f)))));
        Dash = Paint(10, 10, (x, y) => Shade(Color.white, Coverage(RectDistance(x + 0.5f, y + 0.5f, 5f, 5f, 4f, 1.2f))));
        // The same shapes for state pills, painted at 20 px and shown at 10 so they stay crisp and bold at any UI scale.
        IconDot = Paint(20, 20, (x, y) => Shade(Color.white, Coverage(CircleDistance(x + 0.5f, y + 0.5f, 10f, 10f, 8f))));
        IconRing = Paint(20, 20, (x, y) => Shade(Color.white, Mathf.Clamp01(Coverage(CircleDistance(x + 0.5f, y + 0.5f, 10f, 10f, 8.5f)) - Coverage(CircleDistance(x + 0.5f, y + 0.5f, 10f, 10f, 4.5f)))));
        IconSquare = Paint(20, 20, (x, y) => Shade(Color.white, Coverage(RectDistance(x + 0.5f, y + 0.5f, 10f, 10f, 7f, 7f))));
        IconBars = Paint(20, 20, (x, y) => Shade(Color.white, Coverage(Mathf.Min(RectDistance(x + 0.5f, y + 0.5f, 6f, 10f, 2.5f, 8f), RectDistance(x + 0.5f, y + 0.5f, 14f, 10f, 2.5f, 8f)))));
        IconDash = Paint(20, 20, (x, y) => Shade(Color.white, Coverage(RectDistance(x + 0.5f, y + 0.5f, 10f, 10f, 8f, 2.5f))));
        // A warning triangle with its "!" cut out (rows count up from the bottom).
        IconWarning = Paint(20, 20, (x, y) =>
        {
            float px = x + 0.5f, py = y + 0.5f;
            float mark = Mathf.Min(RectDistance(px, py, 10f, 10.75f, 1.25f, 3.25f), CircleDistance(px, py, 10f, 5f, 1.35f));
            return Shade(Color.white, Coverage(TriangleDistance(px, py)) * (1f - Coverage(mark)));
        });
        FocusRing = Rounded(rControl + 2, rControl + 2, Color.clear, v.Focus, 2f);
        // Colour swatches: white, tinted when drawn; the ring marks the chosen one.
        Swatch = Rounded(rChip, rChip, Color.white, Color.white, 0f);

        BadgeActive = Rounded(rBadge, rBadge, v.BadgeActive, v.BadgeActive, 0f);
        BadgeWaiting = Rounded(rBadge, rBadge, v.BadgeWaiting, v.BadgeWaiting, 0f);
        BadgeHolding = Rounded(rBadge, rBadge, v.BadgeHolding, v.BadgeHolding, 0f);
        BadgePaused = Rounded(rBadge, rBadge, v.BadgePaused, v.BadgePaused, 0f);
        BadgeOff = Rounded(rBadge, rBadge, v.BadgeOff, v.BadgeOff, 0f);
        BadgeTint = Rounded(rBadge, rBadge, Color.white, Color.white, 0f);

        int rPill = PillRadius;
        Pill = new GUIStyle { border = Offset(Slice(rPill)) };
        // The mini HUD and its panel share the menu's darkest surface.
        Pill.normal.background = Rounded(rPill, rPill, v.Sidebar, v.WindowEdge, 1f);
        Pill.hover.background = Rounded(rPill, rPill, v.Card, v.InputEdge, 1f);
        PillLabel = new GUIStyle(skin.label) { fontSize = v.FontSmall, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
        Pip = new GUIStyle(skin.label) { fontSize = v.FontCaption, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, padding = Offset(0) };
        int rPip = PipRadius;
        PipOn = Rounded(rPip, rPip, v.Accent, v.Accent, 0f);
        PipOff = Rounded(rPip, rPip, Color.clear, v.PipEdge, 1f);
        PipPaused = Rounded(rPip, rPip, v.ChipPaused, v.StatePaused, 1f);
        Panel = new GUIStyle { border = Offset(Slice(rPanel)), padding = new RectOffset(12, 12, 10, 12) };
        Panel.normal.background = Rounded(rPanel, rPanel, v.Sidebar, v.WindowEdge, 1f);
        // A hover hint's card: the panel with a thin edge in the next surface up (Carbonfox #161616), so it stands off dark
        // backgrounds.
        HintCard = new GUIStyle(Panel);
        HintCard.normal.background = Rounded(rPanel, rPanel, v.Sidebar, v.Window, 1f);
        HintStripe = new GUIStyle { border = new RectOffset(0, 0, Slice(rPanel), Slice(rPanel)) };
        HintStripe.normal.background = AccentStripe(rPanel);
        ToastText = new GUIStyle(skin.label) { wordWrap = true, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(28, 14, 9, 10) };
        // World labels: white text tinted by the caller's colour, so the theme gives the font and size.
        var worldLabel = new GUIStyle(skin.label) { fontSize = v.FontSmall, alignment = TextAnchor.MiddleCenter, font = skin.font };
        TextColor(worldLabel, Color.white);
        Draw.LabelStyle = worldLabel;

        BuildMenu(skin, v);
        LoadLogo();
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
        s_builtAccent = null;
        s_builtIdentity = null;
        ColorPicker.Release();
        Draw.LabelStyle = null;
        foreach (var texture in s_textures)
        {
            if (texture != null) UnityEngine.Object.Destroy(texture);
        }
        s_textures.Clear();
        s_logo = null;
        s_logoSize = null;
        if (s_skin != null) UnityEngine.Object.Destroy(s_skin);
        s_skin = null;
        s_failed = false;
        s_sliced.Clear();
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

    /// <summary>A state's colour for text: lightened enough to read on the window, on badges and on tinted pills.</summary>
    public static Color StateText(StatusState state) => LightText(StateColor(state));

    /// <summary>A colour lightened for text on the dark menu (the state and warning colours are made for shapes).</summary>
    public static Color LightText(Color colour) => Color.Lerp(colour, Color.white, 0.55f);

    /// <summary>The selected theme's own accent, before the player's Accent setting.</summary>
    public static Color ThemeAccent { get; private set; } = ThemeValues.Presets[0].Values.Accent;

    // The three accent settings as one value, so a change to any of them rebuilds the skin.
    private static string AccentKey => $"{LookSettings.Accent.Value}|{LookSettings.AccentSecondary.Value}|{LookSettings.AccentTertiary.Value}";

    /// <summary>The selected theme's own highlight and accent text, before the player's secondary and tertiary accents.</summary>
    public static Color ThemeHighlight { get; private set; } = ThemeValues.Presets[0].Values.Highlight;
    public static Color ThemeAccentText { get; private set; } = ThemeValues.Presets[0].Values.AccentText;

    // As in an editor's theme settings, each accent role is the theme's own until the player picks one: the accent
    // (controls), the highlight (where you are: the tree's selected row, list highlights, search matches, slot chips
    // that are on) and the accent text. A picked colour replaces that role and what is made from it.
    private static void ApplyAccents(ThemeValues v)
    {
        var window = new Color(v.Window.r, v.Window.g, v.Window.b, 1f);
        if (LookSettings.AccentSecondary.TryGetColor(out Color secondary))
        {
            v.Highlight = secondary;
            v.Selection = Color.Lerp(new Color(v.Sidebar.r, v.Sidebar.g, v.Sidebar.b, 1f), secondary, 0.35f);
            v.ChipOn = Color.Lerp(window, secondary, 0.25f);
        }
        v.HighlightText = Readable(v.Highlight);
        if (LookSettings.AccentTertiary.TryGetColor(out Color tertiary)) v.AccentText = Readable(tertiary);
    }

    // A switch that is on: the accent toned toward the bars' colour, with a dark knob, so it reads as on at a glance
    // without being the brightest thing on the page.
    private static Color SwitchTrack(ThemeValues v, float amount) =>
        Color.Lerp(new Color(v.Sidebar.r, v.Sidebar.g, v.Sidebar.b, 1f), v.Accent, amount);

    // Dark colours lightened so they read as text on the dark surfaces.
    private static Color Readable(Color c) => Luminance(c) < 0.3f ? Color.Lerp(c, Color.white, 0.3f) : c;

    // The player's accent replaces the theme's for the controls (switches, checks, main buttons, the focus ring). Status
    // colours stay the theme's, so states keep their lightness rules. Text on the accent turns dark on light accents
    // (Catppuccin's are pastels).
    private static void ApplyAccent(ThemeValues v, Color accent)
    {
        v.Accent = accent;
        v.AccentHover = Color.Lerp(accent, Color.white, 0.15f);
        v.Focus = accent;
        var window = new Color(v.Window.r, v.Window.g, v.Window.b, 1f);
        v.OnAccent = Contrast(Color.white, accent) >= Contrast(window, accent) ? Color.white : window;
    }

    private static float Luminance(Color c)
    {
        static float Linear(float x) => x <= 0.04045f ? x / 12.92f : Mathf.Pow((x + 0.055f) / 1.055f, 2.4f);
        return 0.2126f * Linear(c.r) + 0.7152f * Linear(c.g) + 0.0722f * Linear(c.b);
    }

    private static float Contrast(Color a, Color b)
    {
        float la = Luminance(a) + 0.05f, lb = Luminance(b) + 0.05f;
        return Mathf.Max(la, lb) / Mathf.Min(la, lb);
    }

    /// <summary>The dot for a state: Active filled circle, Waiting ring, Holding square, Paused two bars, Off a dash.</summary>
    public static Texture2D StateShape(StatusState state) => state switch
    {
        StatusState.Active => Dot,
        StatusState.Waiting => Ring,
        StatusState.Holding => Square,
        StatusState.Paused => Bars,
        _ => Dash,
    };

    /// <summary>A state's shape for a pill: the same shapes as StateShape, larger and bolder.</summary>
    public static Texture2D StateIcon(StatusState state) => state switch
    {
        StatusState.Active => IconDot,
        StatusState.Waiting => IconRing,
        StatusState.Holding => IconSquare,
        StatusState.Paused => IconBars,
        _ => IconDash,
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
}
