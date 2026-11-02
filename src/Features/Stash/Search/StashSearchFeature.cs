using EpochAssist.Core;
using Il2Cpp;
using Feature = EpochAssist.Core.Feature;
using S = EpochAssist.Features.StashSettings;

namespace EpochAssist.Features;

// The feature class wires the host to the logic in StashSearch. Built with the stash module, separate from Layouts so a
// game change that breaks a search member never stops Layouts.
internal sealed class StashSearchFeature : Feature
{
    public override string Id => "stashsearch";
    public override string Name => "Stash search";
    public override string[] Needs => new[] { "game", "player" };
    public override MenuEntry? Menu => s_menu;

    // Reading the stash's items is required; values, the panel, the tooltip, going to an item, the game's search box
    // and icons are each optional, so a game change takes out only that part.
    public override Requirement[] Requirements => new[]
    {
        Need(typeof(ItemContainersManager), nameof(ItemContainersManager.Instance)),
        Need<ItemContainersManager>(nameof(ItemContainersManager.stash)),
        Need<ItemContainer>(nameof(ItemContainer.content)),
        Need<ItemContainerEntry>(nameof(ItemContainerEntry.data)),
        Need<ItemContainerEntry>(nameof(ItemContainerEntry.size)),
        Need<ItemContainerEntry>(nameof(ItemContainerEntry.Position)),
        Need<ItemData>(nameof(ItemData.id)),
        Need<ItemData>(nameof(ItemData.getAsUnpacked)),
        Need<ItemData>(nameof(ItemData.affixes)),
        Need<ItemData>(nameof(ItemData.isExaltedItem)),
        Need<ItemData>(nameof(ItemData.isLegendary)),
        Need<ItemData>(nameof(ItemData.hasSealedRegularAffix)),
        Need<ItemData>(nameof(ItemData.hasSealedPrimordialAffix)),
        Need<ItemDataUnpacked>(nameof(ItemDataUnpacked.LvlReq)),
        Need<ItemDataUnpacked>(nameof(ItemDataUnpacked.classReq)),
        Need<ItemAffix>(nameof(ItemAffix.affixType)),
        Need<ItemAffix>(nameof(ItemAffix.specialAffixType)),
        Need<ItemAffix>(nameof(ItemAffix.sealedAffixType)),
        Need<ItemAffix>(nameof(ItemAffix.DisplayTier)),
        Need<ItemAffix>(nameof(ItemAffix.IsChampion)),
        Need(typeof(ItemList), nameof(ItemList.EquippableItems)),

        Need<ItemData>(nameof(ItemData.GetNonSealedPrefixAndSuffixCount), StashSearch.CountsGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.verticalTabWrapper), StashSearch.PanelGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.verticalSearchBar), StashSearch.PanelGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.primarySearchBar), StashSearch.PanelGroup),
        Need(typeof(UIBase), nameof(UIBase.instance), StashSearch.PanelGroup),
        Need<UIBase>(nameof(UIBase.IsStashOpen), StashSearch.PanelGroup),
        Need(typeof(TooltipItemManager), nameof(TooltipItemManager.OpenItemTooltip), StashSearch.TooltipGroup),
        Need<TooltipItemManager>(nameof(TooltipItemManager.CloseTooltip), StashSearch.TooltipGroup),
        Need<TooltipItemManager>(nameof(TooltipItemManager.ActiveItem), StashSearch.TooltipGroup),
        Need(typeof(UITooltipItem), nameof(UITooltipItem.instance), StashSearch.TooltipGroup),
        Need<UITooltipItem>(nameof(UITooltipItem._itemTooltipPrompts), StashSearch.TooltipGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.localVertTabs), StashSearch.GoGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.localVertCategories), StashSearch.GoGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.localTabs), StashSearch.GoGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.localCategories), StashSearch.GoGroup),
        Need<TabbedUITabWithIcon>(nameof(TabbedUITabWithIcon.OnPointerClick), StashSearch.GoGroup),
        Need<StashItemContainer>(nameof(StashItemContainer.currentlyActiveTab), StashSearch.GoGroup),
        Need<InventoryItemUI>(nameof(InventoryItemUI.EntryRef), StashSearch.GoGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.searchUI), StashSearch.GoGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.ClearSearchField), StashSearch.BoxGroup),
        Need<StashTabbedUIControls>(nameof(StashTabbedUIControls.SetStashWideSearch), StashSearch.BoxGroup),
        Need(typeof(ItemData), nameof(ItemData.GetItemSprite), StashSearch.IconsGroup),
    };

    // Reading items or checking a search takes a slice of up to a millisecond a frame; otherwise the usual budget.
    // The stash's own search box reads ~ searches through these; without them it is the game's alone.
    public override Type[] PatchClasses => new[] { typeof(StashSearch.BoxGridPatch), typeof(StashSearch.BoxQuickViewPatch), typeof(StashSearch.BoxMatchPatch), typeof(StashSearch.BoxMacroMatchPatch) };

    public override IReadOnlyDictionary<Type, string> PatchGroups { get; } = new Dictionary<Type, string>
    {
        [typeof(StashSearch.BoxGridPatch)] = StashSearch.SyntaxGroup,
        [typeof(StashSearch.BoxQuickViewPatch)] = StashSearch.SyntaxGroup,
        [typeof(StashSearch.BoxMatchPatch)] = StashSearch.SyntaxGroup,
        [typeof(StashSearch.BoxMacroMatchPatch)] = StashSearch.SyntaxGroup,
    };

    public override double BudgetMs => StashSearch.Busy ? 1.5 : 0.3;
    // Outlines over the game's stash window, under the menu.
    public override GuiLayer Layer => GuiLayer.Hud;

    public override FeatureStatus Status => State == FeatureState.Running ? StashSearch.StatusOf() : base.Status;

    public override void Start() => StashSearch.Start(this);
    public override void Update() => StashSearch.Frame();
    public override void OnGUI() => StashSearch.DrawOverlay();
    public override void OnSceneChanged() => StashSearch.OnSceneChanged();
    public override void Shutdown() => StashSearch.Shutdown();

    private static readonly MenuEntry s_menu = MakeMenu();

    private static MenuEntry MakeMenu()
    {
        var page = new MenuPage("Filter") { Block = StashSearch.PageBlock, Resettable = false, SharesInput = true };
        page.Actions.AddRange(StashSearch.PageButtons());
        return new MenuEntry(MenuCategory.Stash, order: 1)
        {
            Layout = MenuLayout.Wide,
            Title = "Filter",
            Switch = S.SearchEnabled,
            ContentEnabled = () => S.SearchEnabled.Value,
            Pages = { page },
        };
    }
}
