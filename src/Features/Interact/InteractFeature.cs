using System.Globalization;
using EpochAssist.Core;
using Il2Cpp;
using Feature = EpochAssist.Core.Feature;
using S = EpochAssist.Features.InteractSettings;

namespace EpochAssist.Features;

// The feature class wires the host to the logic in Interact, which keeps everything a run changes.
internal sealed class InteractFeature : Feature
{
    public override string Id => "interact";
    public override string Name => "Proximity Interact";
    public override string[] Needs => new[] { "game", "dispatch", "world" };
    // Reads its settings each scan, after the character's values are in place.
    public override string[] RunsAfter => new[] { "profiles" };
    public override MenuEntry? Menu => s_menu;
    public override string? StatusName => "Interact";
    // Show reach draws rings in the world.
    public override GuiLayer Layer => GuiLayer.World;
    // The session's uses on the pill, when the player asks for it.
    public override string? StatusBadge => S.HudCount.Value && State == FeatureState.Running ? Interact.SessionTotal.ToString(s_inv) : null;

    // Shrines and chests come from the world model; the rest is read here, each way into the game in its own group so a
    // game change makes only its kinds unavailable.
    public override PartNeed[] Parts => new[]
    {
        Part("world", "shrines", Interact.ObjectsGroup),
        Part("world", "chests", Interact.ObjectsGroup),
    };

    public override Requirement[] Requirements => new[]
    {
        Need<WorldObjectClickListener>(nameof(WorldObjectClickListener.ObjectClick), Interact.ObjectsGroup),
        Need<WorldObjectClickListener>(nameof(WorldObjectClickListener.interactionRange), Interact.ObjectsGroup),
        Need<WorldObjectClickListener>(nameof(WorldObjectClickListener.CanInteract), Interact.ObjectsGroup),
        Need<WorldObjectClickListener>(nameof(WorldObjectClickListener.allListener), Interact.ObjectsGroup),
        Need<InteractableListener>(nameof(InteractableListener.handler), Interact.ObjectsGroup),
        Need<ConditionHandler>(nameof(ConditionHandler.triggered), Interact.ObjectsGroup),
        Need<ConditionHandler>(nameof(ConditionHandler.CheckConditionReadiness), Interact.ObjectsGroup),
        Need<GroundItemVisuals>(nameof(GroundItemVisuals.all), Interact.ItemsGroup),
        Need<GroundItemVisuals>(nameof(GroundItemVisuals.label), Interact.ItemsGroup),
        Need<GroundItemVisuals>(nameof(GroundItemVisuals.itemData), Interact.ItemsGroup),
        Need<GroundItemVisuals>(nameof(GroundItemVisuals.id), Interact.ItemsGroup),
        Need<GroundItemLabel>(nameof(GroundItemLabel.requestPickup), Interact.ItemsGroup),
        Need<GroundItemLabel>(nameof(GroundItemLabel.pickupDistance), Interact.ItemsGroup),
        Need<GroundItemLabel>(nameof(GroundItemLabel.isAddedToAll), Interact.ItemsGroup),
        Need<GroundItemLabel>(nameof(GroundItemLabel.CurrentRuleNumber), Interact.ItemsGroup),
        Need<GroundItemLabel>(nameof(GroundItemLabel.lastFilterResult), Interact.ItemsGroup),
        Need<ItemData>(nameof(ItemData.itemType), Interact.ItemsGroup),
        Need(typeof(Il2CppItemFiltering.ItemFilterManager), nameof(Il2CppItemFiltering.ItemFilterManager.Instance), Interact.FilterGroup),
        Need<Il2CppItemFiltering.ItemFilterManager>(nameof(Il2CppItemFiltering.ItemFilterManager.Filter), Interact.FilterGroup),
        Need<Il2CppItemFiltering.ItemFilterManager>(nameof(Il2CppItemFiltering.ItemFilterManager.ManualFilterDisable), Interact.FilterGroup),
        Need<GroundItemLabel>(nameof(GroundItemLabel.requestPickup), Interact.FilterGroup),
        Need(typeof(ItemContainersManager), nameof(ItemContainersManager.Instance), Interact.InventoryGroup),
        Need<ItemContainersManager>(nameof(ItemContainersManager.inventory), Interact.InventoryGroup),
        Need<ItemContainer>(nameof(ItemContainer.ContentCount), Interact.InventoryGroup),
    };

    public override FeatureStatus Status => State == FeatureState.Running && Interact.Client is { } client ? Interact.StatusOf(client) : base.Status;

    public override void RegisterSettings() => S.Register();
    public override void Start() => Interact.Start(this);
    public override void OnGUI() => Interact.OnGUI();
    public override void Shutdown() => Interact.Shutdown();

    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;

    private static readonly MenuEntry s_menu = new(MenuCategory.Combat, order: 30)
    {
        Title = "Interact",
        Switch = S.Enabled,
        Pages =
        {
            new MenuPage(S.Main)
            {
                Rows =
                {
                    new SettingRow(S.Enabled),
                    new SettingRow(S.HoldKey),
                    new SettingRow(S.HoldMode),
                    new LiveText("Status", () => Interact.StatusText()),
                    new LiveText("Game reach", () => (Interact.ReachText(), null)),
                    new HeadingRow("Use"),
                    KindRow(S.Shrines, Interact.Shrines),
                    KindRow(S.Chests, Interact.Chests),
                    KindRow(S.RunePrisons, Interact.RunePrisons),
                    KindRow(S.Forges, Interact.Forges),
                    new HeadingRow("Pick up"),
                    KindRow(S.Keys, Interact.Keys),
                    KindRow(S.Materials, Interact.Materials),
                    KindRow(S.FilterShown, Interact.Filtered),
                    new HintRow(() => "No loot filter is active, so this picks up nothing.")
                    {
                        Key = "hint.nofilter", VisibleWhen = () => S.FilterShown.Value && Interact.Available(Interact.Filtered) && !GroundItems.FilterActive,
                    },
                },
            },
            new MenuPage(S.Pace),
            new MenuPage(S.Feedback)
            {
                Rows =
                {
                    new SettingRow(S.HudCount),
                    new SettingRow(S.NotifyEach),
                    new HeadingRow("Counts"),
                    new HintRow(Interact.CountsText) { Key = "hint.counts" },
                },
            },
            new MenuPage(S.Debug) { ConflictPill = true },
        },
    };

    // A kind's switch, with the game's reach once known or why it can't be used.
    private static SettingRow KindRow(Toggle toggle, InteractKind kind) =>
        new(toggle, null) { EnabledWhen = () => Interact.Available(kind) };

}
