using UnityEngine;

namespace EpochAssist.Ui;

internal enum ThemeValueKind { Colour, Opacity, Radius, Space, Font }

/// <summary>A theme value's name as files write it, its kind and range, and how to read or set it.</summary>
internal sealed record ThemeValueSpec(string Name, ThemeValueKind Kind, float Min, float Max, Func<ThemeValues, object> Get, Action<ThemeValues, object> Set);

/// <summary>
/// Every value a theme sets (contracts/themes.md): colours, base window opacity, corner radii, card spacing and font
/// sizes. Value names are a compatibility contract: renaming one raises the theme file version, with a migration.
/// </summary>
internal sealed class ThemeValues
{
    public Color Text, Dim, Accent, AccentText, OnAccent;
    public Color Window, WindowEdge, Sidebar, Card, CardEdge;
    public Color Control, ControlHover, ControlActive, NavHover, AccentHover;
    public Color Track, Thumb, ThumbHover, PipEdge, InputBg, InputEdge, Focus;
    public Color StateActive, StateWaiting, StateHolding, StatePaused, StateOff;
    public Color BadgeActive, BadgeWaiting, BadgeHolding, BadgePaused, BadgeOff;
    public Color ChipOn, ChipPaused;
    public float Opacity = 1f;
    public int RadiusWindow, RadiusCard, RadiusControl, RadiusChip;
    public int CardPad, CardGap;
    public int FontBody = 13, FontSmall = 12, FontCaption = 11, FontTitle = 18, FontNav = 15;

    public ThemeValues Clone() => (ThemeValues)MemberwiseClone();

    /// <summary>Every value by its file name, in the contract's order.</summary>
    public static readonly IReadOnlyList<ThemeValueSpec> Specs = new[]
    {
        Colour("text", v => v.Text, (v, c) => v.Text = c),
        Colour("dim", v => v.Dim, (v, c) => v.Dim = c),
        Colour("accent", v => v.Accent, (v, c) => v.Accent = c),
        Colour("accentText", v => v.AccentText, (v, c) => v.AccentText = c),
        Colour("onAccent", v => v.OnAccent, (v, c) => v.OnAccent = c),
        Colour("window", v => v.Window, (v, c) => v.Window = c),
        Colour("windowEdge", v => v.WindowEdge, (v, c) => v.WindowEdge = c),
        Colour("sidebar", v => v.Sidebar, (v, c) => v.Sidebar = c),
        Colour("card", v => v.Card, (v, c) => v.Card = c),
        Colour("cardEdge", v => v.CardEdge, (v, c) => v.CardEdge = c),
        Colour("control", v => v.Control, (v, c) => v.Control = c),
        Colour("controlHover", v => v.ControlHover, (v, c) => v.ControlHover = c),
        Colour("controlActive", v => v.ControlActive, (v, c) => v.ControlActive = c),
        Colour("navHover", v => v.NavHover, (v, c) => v.NavHover = c),
        Colour("accentHover", v => v.AccentHover, (v, c) => v.AccentHover = c),
        Colour("track", v => v.Track, (v, c) => v.Track = c),
        Colour("thumb", v => v.Thumb, (v, c) => v.Thumb = c),
        Colour("thumbHover", v => v.ThumbHover, (v, c) => v.ThumbHover = c),
        Colour("pipEdge", v => v.PipEdge, (v, c) => v.PipEdge = c),
        Colour("inputBg", v => v.InputBg, (v, c) => v.InputBg = c),
        Colour("inputEdge", v => v.InputEdge, (v, c) => v.InputEdge = c),
        Colour("focus", v => v.Focus, (v, c) => v.Focus = c),
        Colour("state.active", v => v.StateActive, (v, c) => v.StateActive = c),
        Colour("state.waiting", v => v.StateWaiting, (v, c) => v.StateWaiting = c),
        Colour("state.holding", v => v.StateHolding, (v, c) => v.StateHolding = c),
        Colour("state.paused", v => v.StatePaused, (v, c) => v.StatePaused = c),
        Colour("state.off", v => v.StateOff, (v, c) => v.StateOff = c),
        Colour("badge.active", v => v.BadgeActive, (v, c) => v.BadgeActive = c),
        Colour("badge.waiting", v => v.BadgeWaiting, (v, c) => v.BadgeWaiting = c),
        Colour("badge.holding", v => v.BadgeHolding, (v, c) => v.BadgeHolding = c),
        Colour("badge.paused", v => v.BadgePaused, (v, c) => v.BadgePaused = c),
        Colour("badge.off", v => v.BadgeOff, (v, c) => v.BadgeOff = c),
        Colour("chip.on", v => v.ChipOn, (v, c) => v.ChipOn = c),
        Colour("chip.paused", v => v.ChipPaused, (v, c) => v.ChipPaused = c),
        new ThemeValueSpec("opacity", ThemeValueKind.Opacity, 0.2f, 1f, v => v.Opacity, (v, x) => v.Opacity = (float)x),
        Whole("radius.window", ThemeValueKind.Radius, 16, v => v.RadiusWindow, (v, n) => v.RadiusWindow = n),
        Whole("radius.card", ThemeValueKind.Radius, 16, v => v.RadiusCard, (v, n) => v.RadiusCard = n),
        Whole("radius.control", ThemeValueKind.Radius, 16, v => v.RadiusControl, (v, n) => v.RadiusControl = n),
        Whole("radius.chip", ThemeValueKind.Radius, 16, v => v.RadiusChip, (v, n) => v.RadiusChip = n),
        Whole("space.cardPad", ThemeValueKind.Space, 24, v => v.CardPad, (v, n) => v.CardPad = n),
        Whole("space.cardGap", ThemeValueKind.Space, 24, v => v.CardGap, (v, n) => v.CardGap = n),
        Whole("font.body", ThemeValueKind.Font, 28, v => v.FontBody, (v, n) => v.FontBody = n, min: 8),
        Whole("font.small", ThemeValueKind.Font, 28, v => v.FontSmall, (v, n) => v.FontSmall = n, min: 8),
        Whole("font.caption", ThemeValueKind.Font, 28, v => v.FontCaption, (v, n) => v.FontCaption = n, min: 8),
        Whole("font.title", ThemeValueKind.Font, 28, v => v.FontTitle, (v, n) => v.FontTitle = n, min: 8),
        Whole("font.nav", ThemeValueKind.Font, 28, v => v.FontNav, (v, n) => v.FontNav = n, min: 8),
    };

    private static ThemeValueSpec Colour(string name, Func<ThemeValues, Color> get, Action<ThemeValues, Color> set) =>
        new(name, ThemeValueKind.Colour, 0f, 0f, v => get(v), (v, x) => set(v, (Color)x));

    private static ThemeValueSpec Whole(string name, ThemeValueKind kind, int max, Func<ThemeValues, int> get, Action<ThemeValues, int> set, int min = 0) =>
        new(name, kind, min, max, v => get(v), (v, x) => set(v, Mathf.RoundToInt((float)x)));

    /// <summary>A file's colour: #RRGGBB or #RRGGBBAA.</summary>
    public static bool TryParseColour(string text, out Color color)
    {
        color = default;
        if (text.Length is not (7 or 9) || text[0] != '#') return false;
        if (!uint.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out uint value)) return false;
        if (text.Length == 7) value = (value << 8) | 0xFF;
        color = new Color(((value >> 24) & 0xFF) / 255f, ((value >> 16) & 0xFF) / 255f, ((value >> 8) & 0xFF) / 255f, (value & 0xFF) / 255f);
        return true;
    }

    private static Color Hex(string text) => TryParseColour(text, out var color) ? color : throw new ArgumentException(text);

    /// <summary>The built-in themes, Slate first (the default and the fallback).</summary>
    public static readonly IReadOnlyList<(string Name, ThemeValues Values)> Presets = new[]
    {
        ("Slate", Slate()), ("Ember", Ember()), ("Epoch", Epoch()), ("Glass", Glass()),
    };

    // The menu as built before themes, except the Active and Off colours (darker, so Active stands apart in lightness).
    private static ThemeValues Slate() => new()
    {
        Text = Hex("#E4E7EC"), Dim = Hex("#8E96A4"), Accent = Hex("#4C8DFF"), AccentText = Hex("#7DAAFF"), OnAccent = Hex("#FFFFFF"),
        Window = Hex("#0F1115"), WindowEdge = Hex("#232833"), Sidebar = Hex("#12151B"), Card = Hex("#161A21"), CardEdge = Hex("#222833"),
        Control = Hex("#232935"), ControlHover = Hex("#2B3240"), ControlActive = Hex("#343C4C"), NavHover = Hex("#1C2029"), AccentHover = Hex("#6AA1FF"),
        Track = Hex("#2A303B"), Thumb = Hex("#FFFFFF"), ThumbHover = Hex("#DCE6FF"), PipEdge = Hex("#3A4150"),
        InputBg = Hex("#0F1115"), InputEdge = Hex("#2F3644"), Focus = Hex("#4C8DFF"),
        StateActive = Hex("#259361"), StateWaiting = Hex("#7DAAFF"), StateHolding = Hex("#F2B84B"), StatePaused = Hex("#F2B84B"), StateOff = Hex("#545B68"),
        BadgeActive = Hex("#12301F"), BadgeWaiting = Hex("#16264A"), BadgeHolding = Hex("#3A2E14"), BadgePaused = Hex("#3A2E14"), BadgeOff = Hex("#232935"),
        ChipOn = Hex("#16264A"), ChipPaused = Hex("#2B2414"),
        Opacity = 1f, RadiusWindow = 14, RadiusCard = 10, RadiusControl = 8, RadiusChip = 9, CardPad = 14, CardGap = 10,
    };

    // Board A: warm amber accent.
    private static ThemeValues Ember() => Designed(new ThemeValues
    {
        Window = Hex("#121418"), WindowEdge = Hex("#2A2E36"), Card = Hex("#1B1E24"), CardEdge = Hex("#262A32"), Control = Hex("#262A32"),
        Text = Hex("#D7DBE2"), Dim = Hex("#8E95A2"), Accent = Hex("#E0A84A"), AccentText = Hex("#E0A84A"), OnAccent = Hex("#15171C"),
        Track = Hex("#343943"), Thumb = Hex("#D7DBE2"), InputBg = Hex("#101216"), InputEdge = Hex("#3A404C"),
        ChipOn = Hex("#3A2E19"), ChipPaused = Hex("#18222F"), Sidebar = Hex("#15171C"), StatePaused = Hex("#6AA0E6"),
        StateActive = Hex("#2E8546"), StateOff = Hex("#4A4F5A"),
        Opacity = 1f, RadiusWindow = 10, RadiusCard = 8, RadiusControl = 5, RadiusChip = 6, CardPad = 12, CardGap = 8,
    });

    // Board B: gilded, square corners. The design's spaced capitals can't be drawn by IMGUI and are left out.
    private static ThemeValues Epoch() => Designed(new ThemeValues
    {
        Window = Hex("#17120D"), WindowEdge = Hex("#6B532E"), Card = Hex("#201911"), CardEdge = Hex("#3D2F1C"), Control = Hex("#2D2317"),
        Text = Hex("#E8DCC2"), Dim = Hex("#A3927A"), Accent = Hex("#C9A24A"), AccentText = Hex("#D9B865"), OnAccent = Hex("#17120D"),
        Track = Hex("#3D2F1C"), Thumb = Hex("#E8DCC2"), InputBg = Hex("#140F0A"), InputEdge = Hex("#6B532E"),
        ChipOn = Hex("#33280F"), ChipPaused = Hex("#17202A"), Sidebar = Hex("#18130D"), StatePaused = Hex("#86AED0"),
        StateActive = Hex("#5F8D3C"), StateOff = Hex("#655846"),
        Opacity = 1f, RadiusWindow = 3, RadiusCard = 2, RadiusControl = 2, RadiusChip = 2, CardPad = 12, CardGap = 8,
    });

    // Board D: see-through, white accent, flat cards.
    private static ThemeValues Glass() => Designed(new ThemeValues
    {
        Window = Hex("#08090B"), WindowEdge = Hex("#FFFFFF1A"), Card = Hex("#00000000"), CardEdge = Hex("#FFFFFF1A"), Control = Hex("#FFFFFF14"),
        Text = Hex("#F2F2F2"), Dim = Hex("#A3A3A3"), Accent = Hex("#F2F2F2"), AccentText = Hex("#F2F2F2"), OnAccent = Hex("#111111"),
        Track = Hex("#FFFFFF2E"), Thumb = Hex("#FFFFFF"), InputBg = Hex("#FFFFFF0F"), InputEdge = Hex("#FFFFFF2E"),
        ChipOn = Hex("#FFFFFF24"), ChipPaused = Hex("#8AB4F81F"), Sidebar = Hex("#FFFFFF08"), StatePaused = Hex("#8AB4F8"),
        StateActive = Hex("#1A9D50"), StateOff = Hex("#616161"),
        Opacity = 0.82f, RadiusWindow = 8, RadiusCard = 0, RadiusControl = 4, RadiusChip = 4, CardPad = 2, CardGap = 4,
    });

    // Values the designs don't give, derived from the ones they do.
    private static ThemeValues Designed(ThemeValues v)
    {
        v.ControlHover = Color.Lerp(v.Control, v.Text, 0.08f);
        v.ControlActive = Color.Lerp(v.Control, v.Text, 0.16f);
        v.NavHover = v.ControlHover;
        v.AccentHover = Color.Lerp(v.Accent, Color.white, 0.15f);
        v.ThumbHover = Color.Lerp(v.Thumb, v.AccentText, 0.25f);
        v.PipEdge = v.InputEdge;
        v.Focus = v.Accent;
        v.StateWaiting = v.AccentText;
        v.StateHolding = v.StatePaused;
        v.BadgeActive = Color.Lerp(v.Card, v.StateActive, 0.2f);
        v.BadgeWaiting = Color.Lerp(v.Card, v.StateWaiting, 0.2f);
        v.BadgeHolding = Color.Lerp(v.Card, v.StateHolding, 0.2f);
        v.BadgePaused = Color.Lerp(v.Card, v.StatePaused, 0.2f);
        v.BadgeOff = Color.Lerp(v.Card, v.StateOff, 0.2f);
        return v;
    }
}
