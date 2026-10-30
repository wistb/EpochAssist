using EpochAssist.Core;

namespace EpochAssist.Features;

internal static class MapRevealSettings
{
    // Stored ids are a contract. Renaming or retyping one raises the version and adds a migration for it.
    public static readonly SettingsGroup Group = new("EpochAssist_MapReveal", "Map Reveal", version: 1) { ActionKey = "mapreveal", Module = "mapreveal" };

    public static readonly Section Main = Group.Section("main", "Map Reveal");
    // Off until the player turns it on. The quick label offers it on the HUD panel.
    public static readonly Toggle Enabled = new(Main, "Enabled", "Map reveal", false)
    {
        QuickLabel = "Map", Help = "Shows the whole zone and its icons on the map.",
    };

    public static readonly Section Zoom = Group.Section("zoom", "Zoom");
    public static readonly Toggle OverlayZoom = new(Zoom, "OverlayZoom", "Overlay map zoom", true)
    {
        Help = "Overrides the game's overlay map zoom with the scale below.",
    };
    public static readonly Number OverlayScale = new(Zoom, "OverlayScale", "Overlay map scale", 400f, 120f, 1000f)
    {
        Step = 10f, Slider = true, Help = "Metres across the overlay map. Game setting is 120 to 300.",
    };
    public static readonly Number PanSpeed = new(Zoom, "PanSpeed", "Arrow pan speed", 3f, 0.5f, 8f)
    {
        Step = 0.25f, Slider = true, Unit = "×", Help = "How fast the arrow keys move the overlay map. Game setting is 1×.",
    };
    public static readonly Toggle PanMatchZoom = new(Zoom, "PanMatchZoom", "Match zoom", true)
    {
        Help = "Sets the arrow pan speed from the overlay scale, so the map moves across the screen as fast as at the game's zoom.",
    };
    public static readonly Toggle CornerZoom = new(Zoom, "CornerZoom", "Corner minimap zoom", false)
    {
        Help = "Overrides the game's minimap zoom and its + and - buttons with the scale below.",
    };
    public static readonly Number CornerScale = new(Zoom, "CornerScale", "Corner minimap scale", 60f, 10f, 300f)
    {
        Step = 5f, Slider = true, Help = "Metres across the corner minimap.",
    };

    // How the map's ground and edges are drawn. A colour left empty is the game's own; each strength is that colour's
    // opacity. Each colour also offers a blue that goes with the other blues, for a blue map.
    public static readonly Section Look = Group.Section("look", "Look");
    public static readonly Toggle LookEnabled = new(Look, "LookEnabled", "Map look", true)
    {
        Help = "Overrides how the game draws the map's ground and edges with the values below.",
    };
    public const string StyleSlate = "slate", StyleClear = "clear", StyleGame = "game", StyleCustom = "custom";
    public static readonly Choice<string> Style = new(Look, "Style", "Style", StyleSlate,
        (StyleSlate, "Slate"), (StyleClear, "Clear"), (StyleGame, "Game"), (StyleCustom, "Custom"))
    {
        Help = "Sets every value below at once. Changing one makes it Custom.",
    };
    public static readonly ColorSetting FillColour = new(Look, "FillColour", "Fill colour", "#2A3048") { EmptyLabel = "Game setting", EmptyHex = "#2E2B26", Swatches = new[] { ("Slate blue", "#2A3048") } };
    public static readonly Number FillStrength = new(Look, "FillStrength", "Fill strength", 0f, 0f, 1f)
    {
        Step = 0.01f, Slider = true, Percent = true, Help = "How solid the walkable ground is. Game setting is 1%.",
    };
    public static readonly Number FillTexture = new(Look, "FillTexture", "Fill texture", 0f, 0f, 1f)
    {
        Step = 0.05f, Slider = true, Percent = true, Help = "How much the ground's noise pattern shows. Game setting is 50%.",
    };
    public static readonly Number FillTextureSize = new(Look, "FillTextureSize", "Fill texture size", 2f, 0.25f, 8f)
    {
        Step = 0.25f, Slider = true, Help = "Game setting is 2.",
    };
    public static readonly ColorSetting OutlineColour = new(Look, "OutlineColour", "Outline colour", "#BCC6F2") { EmptyLabel = "Game setting", EmptyHex = "#B3B382", Swatches = new[] { ("Pale blue", "#BCC6F2") } };
    public static readonly Number OutlineStrength = new(Look, "OutlineStrength", "Outline strength", 1f, 0f, 1f)
    {
        Step = 0.05f, Slider = true, Percent = true, Help = "Game setting is 50%.",
    };
    public static readonly Number OutlineThickness = new(Look, "OutlineThickness", "Outline thickness", 2f, 0f, 3f)
    {
        Step = 1f, Slider = true, Help = "Pixels added on each side of the line; each step costs frame time. Game setting is 0 (a one-pixel line).",
    };
    public static readonly Number OutlineSoftness = new(Look, "OutlineSoftness", "Outline softness", 0.75f, 0f, 1f)
    {
        Step = 0.05f, Slider = true, Percent = true, Help = "How much the thicker line fades towards its edges.",
    };
    public static readonly ColorSetting ShadowColour = new(Look, "ShadowColour", "Inner shadow colour", "#151A2B") { EmptyLabel = "Game setting", EmptyHex = "#191924", Swatches = new[] { ("Deep slate", "#151A2B") } };
    public static readonly Number ShadowStrength = new(Look, "ShadowStrength", "Inner shadow strength", 0.4f, 0f, 1f)
    {
        Step = 0.05f, Slider = true, Percent = true, Help = "The band just inside the edges. Game setting is 80%.",
    };
    public static readonly Number ShadowWidth = new(Look, "ShadowWidth", "Inner shadow width", 1f, 0f, 1f)
    {
        Step = 0.05f, Slider = true, Help = "Game setting is 0.6.",
    };
    public static readonly Number EdgeWobble = new(Look, "EdgeWobble", "Edge wobble", 0f, 0f, 20f)
    {
        Step = 0.5f, Slider = true, Help = "How ragged the edges are. Game setting is 16.",
    };
    public static readonly Number EdgeWobbleSize = new(Look, "EdgeWobbleSize", "Edge wobble size", 0.25f, 0.25f, 8f)
    {
        Step = 0.25f, Slider = true, Help = "Game setting is 1.5.",
    };
    public static readonly Number EdgeSharpness = new(Look, "EdgeSharpness", "Edge sharpness", 0.05f, 0f, 1f)
    {
        Step = 0.01f, Slider = true, Help = "Game setting is 0.08.",
    };
    public static readonly ColorSetting GlowColour = new(Look, "GlowColour", "Edge glow colour", "#8E9AD0") { EmptyLabel = "Game setting", EmptyHex = "#A2986F", Swatches = new[] { ("Pale blue", "#8E9AD0") } };
    public static readonly Number GlowStrength = new(Look, "GlowStrength", "Edge glow strength", 0.05f, 0f, 1f)
    {
        Step = 0.05f, Slider = true, Percent = true, Help = "The soft band outside the edges. Game setting is 30%.",
    };

    public static readonly Section Icons = Group.Section("icons", "Icons");
    // The player's own icon: the game's loaded icon sprites by name; empty is the game's own arrow.
    public const string GamePlayerSprite = "player-minimap-arrow-v6";
    public static readonly (string Value, string Label)[] PlayerIcons =
    {
        (string.Empty, "Game setting"),
        ("player-minimap-arrow-v4", "Classic arrow"),
        ("PlayerArrow_Blue", "Blue arrow"),
        ("PlayerArrow_Gold", "Gold arrow"),
        ("PlayerArrow_Green", "Green arrow"),
        ("PlayerArrow_Purple", "Purple arrow"),
        ("T_UI_MapPointer_Generic", "Pointer"),
        ("T_UI_MapPointer_Green", "Green pointer"),
        ("T_UI_MapPointer_OrangeMajor", "Orange pointer"),
        ("T_UI_MapPointer_PurpleMajor", "Purple pointer"),
        ("T_UI_MapPointer_RedMajor", "Red pointer"),
        ("LocationMarker_PlayerPosition", "Position marker"),
        ("Player Location", "Location pin"),
    };
    public static readonly Choice<string> PlayerIcon = new(Icons, "PlayerIcon", "Icon", string.Empty, PlayerIcons)
    {
        Preview = MapReveal.SpritePreview, Help = "Your own marker on the map.",
    };
    public static readonly ColorSetting PlayerIconColour = new(Icons, "PlayerIconColour", "Icon tint") { EmptyLabel = "Game setting", EmptyHex = "#FFFFFF" };
    public const string TurnNone = "none";
    public static readonly Choice<string> PlayerIconTurn = new(Icons, "PlayerIconTurn", "Icon rotation", "0",
        ("0", "As drawn"), ("90", "90°"), ("180", "180°"), ("270", "270°"), (TurnNone, "Keep upright"))
    {
        Help = "For an icon that points the wrong way. Keep upright suits pins and markers.",
    };
    public static readonly Number PlayerIconOffsetX = new(Icons, "PlayerIconOffsetX", "Icon offset X", 0f, -40f, 40f)
    {
        Step = 1f, Slider = true, Help = "Moves the picture right (or left) within the icon; it turns with the icon, about your character's place.",
    };
    public static readonly Number PlayerIconOffsetY = new(Icons, "PlayerIconOffsetY", "Icon offset Y", 0f, -40f, 40f)
    {
        Step = 1f, Slider = true, Help = "Moves the picture up (or down) within the icon.",
    };
    public static readonly Number PlayerIconSize = new(Icons, "PlayerIconSize", "Icon scale", 1f, 0.5f, 3f)
    {
        Step = 0.1f, Slider = true, Unit = "×", Help = "Game setting is 1×.",
    };
    public static readonly Toggle Shrines = new(Icons, "Shrines", "Shrines", true);
    public static readonly Toggle RunePrisons = new(Icons, "RunePrisons", "Rune prisons", true);
    public static readonly Toggle Caches = new(Icons, "Caches", "Caches", true);
    public static readonly Toggle TombChests = new(Icons, "TombChests", "Tomb and cave chests", true);
    public static readonly Toggle TombEntrances = new(Icons, "TombEntrances", "Tomb entrances", true);
    public static readonly Toggle OmenWindows = new(Icons, "OmenWindows", "Omen windows", true);
    public static readonly Toggle Nemesis = new(Icons, "Nemesis", "Nemesis", true);
    public static readonly Toggle Forges = new(Icons, "Forges", "Forges", true);

    public static readonly Section Debug = Group.Section("debug", "Debug");
    public static readonly Toggle ShowCharacterPoint = new(Debug, "ShowCharacterPoint", "Show character point", false)
    {
        Help = "A dot on the map where your character is, the point the icon turns around.",
    };

    public static void Register() => Group.Register();
}
