using EpochAssist.Core;
using Il2Cpp;
using Il2CppLE.Data;
using Feature = EpochAssist.Core.Feature;
using S = EpochAssist.Features.StashSettings;

namespace EpochAssist.Features;

// The feature class wires the host to the logic in Stash, which keeps everything a run changes.
internal sealed class StashFeature : Feature
{
    public override string Id => "stash";
    public override string Name => "Stash layouts";
    public override string[] Needs => new[] { "game", "player" };
    public override MenuEntry? Menu => s_menu;

    // Reading the stash is required. Each kind of step is its own group, so a game change hides only those steps; the
    // rule test (order check, Full layout) and the game's icons are optional too.
    public override Requirement[] Requirements => new[]
    {
        Need<LocalPlayer>(nameof(LocalPlayer.stashInfo)),
        Need<ClientStashInfo>(nameof(ClientStashInfo.categories)),
        Need<ClientStashInfo>(nameof(ClientStashInfo.tabs)),
        Need<GlobalDataTracker>(nameof(GlobalDataTracker.stash)),
        Need<Il2CppLE.Data.Stash>(nameof(Il2CppLE.Data.Stash.Tabs)),
        Need<Il2CppLE.Data.Stash>(nameof(Il2CppLE.Data.Stash.Categories)),
        Need<Il2CppLE.Data.Stash>(nameof(Il2CppLE.Data.Stash.StashType)),
        Need<Il2CppLE.Data.Stash>(nameof(Il2CppLE.Data.Stash.Cycle)),
        Need<StashTab>(nameof(StashTab.TabId)),
        Need<StashTab>(nameof(StashTab.CategoryId)),
        Need<StashTab>(nameof(StashTab.IconId)),
        Need<StashTab>(nameof(StashTab.ColorId)),
        Need<StashTab>(nameof(StashTab.DisplayName)),
        Need<StashTab>(nameof(StashTab.DisplayOrder)),
        Need<StashTab>(nameof(StashTab.TabPriority)),
        Need<StashTab>(nameof(StashTab.IgnoreFolderPriority)),
        Need<StashTab>(nameof(StashTab.SavedItems)),
        Need<StashCategory>(nameof(StashCategory.CategoryId)),
        Need<StashCategory>(nameof(StashCategory.IconId)),
        Need<StashCategory>(nameof(StashCategory.ColorId)),
        Need<StashCategory>(nameof(StashCategory.DisplayName)),
        Need<StashCategory>(nameof(StashCategory.DisplayOrder)),
        Need<StashCategory>(nameof(StashCategory.FolderPriority)),
        Need(typeof(StashHelpers), nameof(StashHelpers.NextTabCost)),
        Need(typeof(StashHelpers), nameof(StashHelpers.CanBuy)),
        Need(typeof(StashConstants), nameof(StashConstants.MaxTabs)),
        Need(typeof(StashManager), nameof(StashManager.MaxFolderCount)),
        Need(typeof(PlayerFinder), nameof(PlayerFinder.localGoldTracker)),
        Need(typeof(PlayerFinder), nameof(PlayerFinder.getLocalGoldTracker)),
        Need<GoldTracker>(nameof(GoldTracker.value)),

        Need(typeof(ItemContainersManager), nameof(ItemContainersManager.Instance), Stash.BuyGroup),
        Need<ItemContainersManager>(nameof(ItemContainersManager.PurchaseNewStashTab), Stash.BuyGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.HandleConfigureTabResult), Stash.ConfigureGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.HandleConfigureCategoryResult), Stash.ConfigureGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.AddNewCategory), Stash.FolderGroup),
        Need(typeof(ServerStashCommands), nameof(ServerStashCommands.ServerAddNewCategory), Stash.FolderGroup),
        Need(typeof(ServerStashCommands), nameof(ServerStashCommands.ServerSetCategoryInfo), Stash.ConfigureGroup),
        Need(typeof(ServerStashCommands), nameof(ServerStashCommands.ServerSetTabInfo), Stash.ConfigureGroup),
        Need(typeof(ServerStashCommands), nameof(ServerStashCommands.ServerMoveTab), Stash.MoveGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.OnDataUpdated), Stash.ConfigureGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.SetCategoryUIWithIconProperties), Stash.ConfigureGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.SetTabUIWithIconProperties), Stash.ConfigureGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.OnTabRemoved), Stash.MoveGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.OnTabAdded), Stash.MoveGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.OnCategoryRemoved), Stash.MoveGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.OnCategoryAdded), Stash.MoveGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.localCategories), Stash.MoveGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.reorderableCategories), Stash.MoveGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.categoryLayoutGroup), Stash.MoveGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.localTabs), Stash.MoveGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.reorderableTabs), Stash.MoveGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.OpenCategoryOnly), Stash.MoveGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.tabLayoutGroup), Stash.MoveGroup),
        Need<SimpleLayoutGroup>(nameof(SimpleLayoutGroup.SetItemOrder), Stash.MoveGroup),
        Need<SimpleLayoutGroup>(nameof(SimpleLayoutGroup.UpdateLayout), Stash.MoveGroup),
        Need<SimpleLayoutGroup>(nameof(SimpleLayoutGroup.AddElement), Stash.MoveGroup),
        Need<SimpleLayoutGroup>(nameof(SimpleLayoutGroup._elements), Stash.MoveGroup),
        Need<SimpleLayoutGroup.SimpleLayoutElement>(nameof(SimpleLayoutGroup.SimpleLayoutElement.RectTransformReference), Stash.MoveGroup),
        Need<StashPriority>(nameof(StashPriority.Match), Stash.TestGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.tabIcons), Stash.IconsGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.tabColors), Stash.IconsGroup),
        Need<UIGoldText>(nameof(UIGoldText.goldText), Stash.IconsGroup),
        Need(typeof(UITooltipItem), nameof(UITooltipItem.instance), Stash.IconsGroup),
        Need<UITooltipItem>(nameof(UITooltipItem.goldIcon), Stash.IconsGroup),
    };

    // While steps are in flight the stash is read every frame until the game answers; otherwise the menu's usual budget.
    // Checking where each item belongs takes its own slice of up to a millisecond a frame.
    public override double BudgetMs => Stash.StepsBusy ? 1.0 : Stash.Belonging ? 1.5 : 0.3;
    // The outlines and labels over the game's stash window, under the menu.
    public override GuiLayer Layer => GuiLayer.Hud;

    public override FeatureStatus Status => State == FeatureState.Running ? Stash.StatusOf(this) : base.Status;

    public override void RegisterSettings() => S.Register();
    public override void Start() => Stash.Start(this);
    public override void Update() => Stash.Frame();
    public override void OnGUI() => Stash.DrawOverlay();
    public override void OnSceneChanged() => Stash.OnSceneChanged();
    public override void Shutdown() => Stash.Shutdown();

    private static readonly MenuEntry s_menu = new(MenuCategory.Stash, order: 0)
    {
        Layout = MenuLayout.Wide,
        Title = "Layouts",
        Switch = S.Enabled,
        ContentEnabled = () => S.Enabled.Value,
        Pages = { new MenuPage("Layouts") { Block = Stash.PageBlock, Resettable = false, SharesInput = true } },
    };
}
