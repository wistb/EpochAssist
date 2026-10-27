using EpochAssist.Core;
using Il2Cpp;
using Il2CppLE.UI.Minimap;
using Feature = EpochAssist.Core.Feature;
using S = EpochAssist.Features.MapRevealSettings;

namespace EpochAssist.Features;

// The feature class wires the host to the logic in MapReveal, which keeps everything a run changes.
internal sealed class MapRevealFeature : Feature
{
    public override string Id => "mapreveal";
    public override string Name => "Map Reveal";
    public override string[] Needs => new[] { "game" };
    // Reads its settings each tick, after the character's values are in place.
    public override string[] RunsAfter => new[] { "profiles" };
    public override MenuEntry? Menu => s_menu;
    public override string? StatusName => "Map";

    // Each way into the game in its own group, so a game change takes out only what it breaks: the reveal, the icons
    // (omen windows have their own list), the zoom, or the look.
    public override Requirement[] Requirements => new[]
    {
        Need<Minimap>(nameof(Minimap.RevealRadius), MapReveal.FogGroup),
        Need<Minimap>(nameof(Minimap.minimapInformation), MapReveal.FogGroup),
        Need<MinimapInformation>(nameof(MinimapInformation.LiveSdf), MapReveal.FogGroup),
        Need<MinimapInformation>(nameof(MinimapInformation.LevelBounds), MapReveal.FogGroup),
        Need<WorldObjectClickListener>(nameof(WorldObjectClickListener.allListener), MapReveal.IconsGroup),
        Need<ConditionHandler>(nameof(ConditionHandler.triggered), MapReveal.IconsGroup),
        Need<ObserverWindowVisuals>(nameof(ObserverWindowVisuals.allVisuals), MapReveal.OmenGroup),
        Need<ConditionHandler>(nameof(ConditionHandler.triggered), MapReveal.OmenGroup),
        Need<Minimap>(nameof(Minimap.Update), MapReveal.ZoomGroup),
        Need<Minimap>(nameof(Minimap.mapParameters), MapReveal.ZoomGroup),
        Need<Minimap>(nameof(Minimap.Material), MapReveal.ZoomGroup),
        Need<Minimap>(nameof(Minimap.shaderMapParameters), MapReveal.ZoomGroup),
        Need<UIBase>(nameof(UIBase.instance), MapReveal.ZoomGroup),
        Need<UIBase>(nameof(UIBase.overlayMapOpen), MapReveal.ZoomGroup),
        Need<DMMapGlobalConfigs>(nameof(DMMapGlobalConfigs.instance), MapReveal.ZoomGroup),
        Need<DMMapGlobalConfigs>(nameof(DMMapGlobalConfigs.iconDistanceThreshold), MapReveal.ZoomGroup),
        Need(typeof(Il2CppLE.Settings.SettingsManager), nameof(Il2CppLE.Settings.SettingsManager.Gameplay), MapReveal.ZoomGroup),
        Need<Il2CppLE.Settings.GameplaySettings>(nameof(Il2CppLE.Settings.GameplaySettings.OverlayMapZoom), MapReveal.ZoomGroup),
        Need<UIBase>(nameof(UIBase.MoveOverlayMap), MapReveal.ZoomGroup),
        Need<UIBase>(nameof(UIBase.OpenOverlayMap), MapReveal.ZoomGroup),
        Need<UIBase>(nameof(UIBase.CloseOverlayMapOrRecenter), MapReveal.ZoomGroup),
        Need<UIBase>(nameof(UIBase.OverlayMapKeyDown), MapReveal.ZoomGroup),
        Need<Minimap>(nameof(Minimap.UpdateOverlayMapZoom), MapReveal.ZoomGroup),
        Need<MinimapIconManager>(nameof(MinimapIconManager.icons), MapReveal.PlayerGroup),
        Need<MinimapIconUI>(nameof(MinimapIconUI.MainImage), MapReveal.PlayerGroup),
        Need<MinimapIconManager>(nameof(MinimapIconManager.LateUpdate), MapReveal.PlayerTurnGroup),
        Need<Minimap>(nameof(Minimap.Material), MapReveal.LookGroup),
        Need<Minimap>(nameof(Minimap.material), MapReveal.LookGroup),
    };

    // The zoom postfix and the pan prefix are patched only while a zoom or pan speed is set and the switch is on.
    public override Type[] PatchClasses => new[] { typeof(MapReveal.ZoomPatch), typeof(MapReveal.ZoomPushPatch), typeof(MapReveal.OverlayOpenPatch), typeof(MapReveal.OverlayClosePatch), typeof(MapReveal.OverlayKeyPatch), typeof(MapReveal.PanPatch), typeof(MapReveal.PlayerTurnPatch) };
    public override IReadOnlyDictionary<Type, string> PatchGroups { get; } = new Dictionary<Type, string>
    {
        [typeof(MapReveal.ZoomPatch)] = MapReveal.ZoomGroup,
        [typeof(MapReveal.ZoomPushPatch)] = MapReveal.ZoomGroup,
        [typeof(MapReveal.OverlayOpenPatch)] = MapReveal.ZoomGroup,
        [typeof(MapReveal.OverlayClosePatch)] = MapReveal.ZoomGroup,
        [typeof(MapReveal.OverlayKeyPatch)] = MapReveal.ZoomGroup,
        [typeof(MapReveal.PanPatch)] = MapReveal.ZoomGroup,
        [typeof(MapReveal.PlayerTurnPatch)] = MapReveal.PlayerTurnGroup,
    };
    public override string[] DemandGroups => new[] { MapReveal.ZoomGroup, MapReveal.PlayerTurnGroup };

    public override FeatureStatus Status => State == FeatureState.Running ? MapReveal.StatusOf(this) : base.Status;

    public override void RegisterSettings() => S.Register();
    public override void Start() => MapReveal.Start(this);
    // The debug dot draws over the game's UI.
    public override GuiLayer Layer => GuiLayer.World;
    public override void OnGUI() => MapReveal.DrawCharacterPoint();
    public override void OnSceneChanged() => MapReveal.OnSceneChanged();
    public override void Update() => MapReveal.PanFrame();
    // After the game's own map update, so the outline copies carry this frame's scale, not the last one's (a switch
    // between the corner and the overlay drew them at the other map's scale for a frame).
    public override void LateUpdate() => MapReveal.OutlineFrame();
    public override void Shutdown() => MapReveal.Shutdown();

    private static readonly MenuEntry s_menu = new(MenuCategory.Combat, order: 40)
    {
        Title = "Map",
        Switch = S.Enabled,
        Pages =
        {
            new MenuPage(S.Main)
            {
                Rows =
                {
                    new SettingRow(S.Enabled),
                    new LiveText("Status", MapReveal.StatusText),
                    new HintRow(() => "The corner minimap shares the fog with the map.") { Key = "hint.bothmaps" },
                },
            },
            new MenuPage(S.Zoom)
            {
                Rows =
                {
                    new SettingRow(S.OverlayZoom) { EnabledWhen = ZoomAvailable },
                    new SettingRow(S.OverlayScale) { EnabledWhen = () => ZoomAvailable() && S.OverlayZoom.Value },
                    new SettingRow(S.PanSpeed) { EnabledWhen = () => ZoomAvailable() && !S.PanMatchZoom.Value },
                    new SettingRow(S.PanMatchZoom) { EnabledWhen = ZoomAvailable },
                    new SettingRow(S.CornerZoom) { EnabledWhen = ZoomAvailable },
                    new SettingRow(S.CornerScale) { EnabledWhen = () => ZoomAvailable() && S.CornerZoom.Value },
                },
            },
            new MenuPage(S.Look)
            {
                Actions = { new ActionButton("Game defaults", "mapreveal.look.ask") { Tip = "Every look value back to the game's own, after a confirmation" } },
                Rows =
                {
                    new BannerRow(() => MapReveal.ConfirmingGameDefaults ? "Set every look value to the game's own? Your colours and sliders here are replaced." : null,
                        new ActionButton("Set game defaults", "mapreveal.look.game", primary: true), new ActionButton("Keep my look", "mapreveal.look.keep")),
                    new SettingRow(S.LookEnabled) { EnabledWhen = LookAvailable },
                    LookRow(S.Style),
                    new HeadingRow("Fill") { VisibleWhen = () => S.LookEnabled.Value },
                    LookRow(S.FillColour), LookRow(S.FillStrength), LookRow(S.FillTexture), LookRow(S.FillTextureSize),
                    new HeadingRow("Outline") { VisibleWhen = () => S.LookEnabled.Value },
                    LookRow(S.OutlineColour), LookRow(S.OutlineStrength), LookRow(S.OutlineThickness), LookRow(S.OutlineSoftness),
                    new HeadingRow("Inner shadow") { VisibleWhen = () => S.LookEnabled.Value },
                    LookRow(S.ShadowColour), LookRow(S.ShadowStrength), LookRow(S.ShadowWidth),
                    new HeadingRow("Edge") { VisibleWhen = () => S.LookEnabled.Value },
                    LookRow(S.EdgeWobble), LookRow(S.EdgeWobbleSize), LookRow(S.EdgeSharpness), LookRow(S.GlowColour), LookRow(S.GlowStrength),
                },
            },
            new MenuPage(S.Icons)
            {
                Rows =
                {
                    new HeadingRow("Player"),
                    PlayerRow(S.PlayerIcon), PlayerRow(S.PlayerIconTurn), PlayerRow(S.PlayerIconColour), PlayerRow(S.PlayerIconSize),
                    PlayerRow(S.PlayerIconOffsetX), PlayerRow(S.PlayerIconOffsetY),
                    new HeadingRow("Shown from the start"),
                    new HintRow(() => "Shows these icons when you enter the zone.") { Key = "hint.icons" },
                    KindRow(MapReveal.Shrines), KindRow(MapReveal.RunePrisons), KindRow(MapReveal.Caches),
                    KindRow(MapReveal.TombChests), KindRow(MapReveal.TombEntrances), KindRow(MapReveal.OmenWindows),
                    KindRow(MapReveal.Nemesis), KindRow(MapReveal.Forges),
                },
            },
            new MenuPage(S.Debug),
        },
    };

    // A player icon row, usable while the game's icon members are there.
    private static SettingRow PlayerRow(Setting setting) =>
        new(setting, null) { EnabledWhen = () => FeatureHost.Find("mapreveal") is { } f && f.Has(MapReveal.PlayerGroup) };

    private static bool LookAvailable() => FeatureHost.Find("mapreveal") is { } f && f.Has(MapReveal.LookGroup);

    // A look value's row, shown while the look is on.
    private static SettingRow LookRow(Setting setting) => new(setting, null) { VisibleWhen = () => S.LookEnabled.Value, EnabledWhen = LookAvailable };

    private static bool ZoomAvailable() => FeatureHost.Find("mapreveal") is { } f && f.Has(MapReveal.ZoomGroup);

    // A kind's switch, usable while the game members it needs are there.
    private static SettingRow KindRow(MapReveal.IconKind kind) => new(kind.Toggle, null) { EnabledWhen = () => MapReveal.Available(kind) };
}
