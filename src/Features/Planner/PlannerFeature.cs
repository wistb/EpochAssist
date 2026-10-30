using EpochAssist.Core;
using Il2Cpp;
using Il2CppItemFiltering;
using Feature = EpochAssist.Core.Feature;
using S = EpochAssist.Features.PlannerSettings;

namespace EpochAssist.Features;

// The feature class wires the host to the logic in Planner, which keeps everything a run changes.
internal sealed class PlannerFeature : Feature
{
    public override string Id => "planner";
    public override string Name => "Planner";
    public override string[] Needs => new[] { "game", "player" };
    // Reads its settings each tick, after the character's values are in place.
    public override string[] RunsAfter => new[] { "profiles" };
    public override MenuEntry? Menu => Planner.Entry;
    public override MenuEntry? ConfigMenu => Planner.ConfigEntry;

    // The bar's skills come from the player model.
    public override PartNeed[] Parts => new[] { Part("player", "skill bar") };

    // The character's trees are required; each mark kind's panel members are in their own group, so a game change
    // makes only that kind unavailable.
    public override Requirement[] Requirements => new[]
    {
        Need(typeof(PlayerFinder), nameof(PlayerFinder.getLocalTreeData)),
        Need<LocalTreeData>(nameof(LocalTreeData.characterClass)),
        Need<LocalTreeData>(nameof(LocalTreeData.chosenMastery)),
        Need<LocalTreeData>(nameof(LocalTreeData.characterLevel)),
        Need<LocalTreeData>(nameof(LocalTreeData.passiveTree)),
        Need<LocalTreeData>(nameof(LocalTreeData.specialisedSkillTrees)),
        Need<LocalTreeData.TreeData>(nameof(LocalTreeData.TreeData.treeID)),
        Need<LocalTreeData.TreeData>(nameof(LocalTreeData.TreeData.nodes)),
        Need<LocalTreeData.NodeData>(nameof(LocalTreeData.NodeData.id)),
        Need<LocalTreeData.NodeData>(nameof(LocalTreeData.NodeData.pointsAllocated)),
        Need<LocalTreeData.SkillTreeData>(nameof(LocalTreeData.SkillTreeData.slot)),
        Need<CharacterClass>(nameof(CharacterClass.classID)),

        Need<PassivePanelManager>(nameof(PassivePanelManager.instance), Planner.PassiveUiGroup),
        Need<PassivePanelManager>(nameof(PassivePanelManager.activeTree), Planner.PassiveUiGroup),
        Need<PassivePanelManager>(nameof(PassivePanelManager.activeMastery), Planner.PassiveUiGroup),
        Need<PassivePanelManager>(nameof(PassivePanelManager.openedBanner), Planner.PassiveUiGroup),
        Need<Il2Cpp.Tree>(nameof(Il2Cpp.Tree.nodeList), Planner.PassiveUiGroup),
        Need<SkillTreeNode>(nameof(SkillTreeNode.id), Planner.PassiveUiGroup),
        Need<SkillTreeNode>(nameof(SkillTreeNode.mastery), Planner.PassiveUiGroup),
        Need<SkillTreeNode>(nameof(SkillTreeNode.pointsAllocated), Planner.PassiveUiGroup),

        Need<SkillsPanelManager>(nameof(SkillsPanelManager.CurrentlyOpenTree), Planner.SkillUiGroup),
        Need<Il2Cpp.Tree>(nameof(Il2Cpp.Tree.treeID), Planner.SkillUiGroup),
        Need<Il2Cpp.Tree>(nameof(Il2Cpp.Tree.nodeList), Planner.SkillUiGroup),
        Need<SkillTreeNode>(nameof(SkillTreeNode.id), Planner.SkillUiGroup),
        Need<SkillsSpecialisationUIManager>(nameof(SkillsSpecialisationUIManager.Respeccing), Planner.SkillUiGroup),

        Need<AbilityBarSelectorManager>(nameof(AbilityBarSelectorManager.instance), Planner.SkillChoiceGroup),
        Need<AbilityBarSelectorManager>(nameof(AbilityBarSelectorManager.IsMenuOpened), Planner.SkillChoiceGroup),
        Need<AbilityBarSelectorManager>(nameof(AbilityBarSelectorManager.abilityClicked), Planner.SkillChoiceGroup),
        Need<AbilityBarSelectorManager>(nameof(AbilityBarSelectorManager._abilitySelectionIcons), Planner.SkillChoiceGroup),
        Need<AbilitySelectionIcon>(nameof(AbilitySelectionIcon.ability), Planner.SkillChoiceGroup),
        Need<AbilityBarIcon>(nameof(AbilityBarIcon.all), Planner.SkillChoiceGroup),
        Need<AbilityBarIcon>(nameof(AbilityBarIcon.abilityNumber), Planner.SkillChoiceGroup),

        Need<SkillsPanelManager>(nameof(SkillsPanelManager.baseClassPanelIcons), Planner.SkillsPageGroup),
        Need<SkillsPanelManager>(nameof(SkillsPanelManager.baseClassTreePanelIcons), Planner.SkillsPageGroup),
        Need<SkillsPanelManager>(nameof(SkillsPanelManager.masteryClass1PanelIcons), Planner.SkillsPageGroup),
        Need<SkillsPanelManager>(nameof(SkillsPanelManager.masteryClass2PanelIcons), Planner.SkillsPageGroup),
        Need<SkillsPanelManager>(nameof(SkillsPanelManager.masteryClass3PanelIcons), Planner.SkillsPageGroup),
        Need<SkillsPanelManager>(nameof(SkillsPanelManager.masteryAbilityPanelIcon1), Planner.SkillsPageGroup),
        Need<SkillsPanelManager>(nameof(SkillsPanelManager.masteryAbilityPanelIcon2), Planner.SkillsPageGroup),
        Need<SkillsPanelManager>(nameof(SkillsPanelManager.masteryAbilityPanelIcon3), Planner.SkillsPageGroup),
        Need<AbilityPanelIcon>(nameof(AbilityPanelIcon.ability), Planner.SkillsPageGroup),
        Need<SpecialisedAbilityIcon>(nameof(SpecialisedAbilityIcon.ability), Planner.SkillsPageGroup),
        Need<SpecialisedAbilityIcon>(nameof(SpecialisedAbilityIcon.abilityNumber), Planner.SkillsPageGroup),

        Need(typeof(GlobalTreeData), nameof(GlobalTreeData.get), Planner.GameDataGroup),
        Need<GlobalTreeData>(nameof(GlobalTreeData.getTreeData), Planner.GameDataGroup),
        Need<GlobalTreeData.TreeData>(nameof(GlobalTreeData.TreeData.nodes), Planner.GameDataGroup),
        Need<GlobalTreeData.TreeData>(nameof(GlobalTreeData.TreeData.getNode), Planner.GameDataGroup),
        Need<GlobalTreeData.NodeData>(nameof(GlobalTreeData.NodeData.name), Planner.GameDataGroup),
        Need<GlobalTreeData.NodeData>(nameof(GlobalTreeData.NodeData.requiredMastery), Planner.GameDataGroup),
        Need<GlobalTreeData.NodeData>(nameof(GlobalTreeData.NodeData.maxPoints), Planner.GameDataGroup),
        Need(typeof(AbilityManager), nameof(AbilityManager.getPlayerAbility), Planner.GameDataGroup),
        Need(typeof(UIBase), nameof(UIBase.instance), Planner.OpenUiGroup),
        Need<UIBase>(nameof(UIBase.OpenPassivePanel), Planner.OpenUiGroup),
        Need<UIBase>(nameof(UIBase.openSkills), Planner.OpenUiGroup),
        Need<AbilityBarSelectorManager>(nameof(AbilityBarSelectorManager.OpenMenu), Planner.OpenUiGroup),
        Need<AbilityBarSelectorManager>(nameof(AbilityBarSelectorManager.CloseMenu), Planner.OpenUiGroup),
        Need<UIBase>(nameof(UIBase.CloseAllPanels), Planner.OpenUiGroup),
        Need<LocalTreeData.SkillTreeData>(nameof(LocalTreeData.SkillTreeData.ability), Planner.OpenUiGroup),
        Need(typeof(CharacterClassList), nameof(CharacterClassList.getClass), Planner.GameDataGroup),
        Need(typeof(CharacterClassList), nameof(CharacterClassList.getClassName), Planner.GameDataGroup),
        Need(typeof(Localization), nameof(Localization.TryGetText), Planner.GameDataGroup),

        // Planned items made in memory from the game's own data.
        Need<ItemData>(nameof(ItemData.RefreshIDAndValues), Planner.ItemDataGroup),
        Need<ItemData>(nameof(ItemData.numberOfImplicitRolls), Planner.ItemDataGroup),
        Need<ItemData>(nameof(ItemData.IsTwoHanded), Planner.ItemDataGroup),
        Need<ItemData>(nameof(ItemData.hasSealedRegularAffix), Planner.ItemDataGroup),
        Need<ItemData>(nameof(ItemData.hasSealedPrimordialAffix), Planner.ItemDataGroup),
        Need<ItemData>(nameof(ItemData.hasSealedAffixFromCorruption), Planner.ItemDataGroup),
        Need<ItemAffix>(nameof(ItemAffix.sealedAffixType), Planner.ItemDataGroup),
        Need(typeof(UniqueList), nameof(UniqueList.getUnique), Planner.ItemDataGroup),
        Need(typeof(UniqueList), nameof(UniqueList.getUniqueItemType), Planner.ItemDataGroup),
        Need(typeof(ItemList), nameof(ItemList.get), Planner.ItemDataGroup),
        Need<ItemList>(nameof(ItemList.GetEquipmentBaseItem), Planner.ItemDataGroup),
        Need<ItemList>(nameof(ItemList.GetEquipmentSubItem), Planner.ItemDataGroup),
        Need<ItemList>(nameof(ItemList.GetNonEquipmentSubItem), Planner.ItemDataGroup),
        Need<ItemList>(nameof(ItemList.GetItemDisplayName), Planner.ItemDataGroup),
        Need<ItemList.BaseItem>(nameof(ItemList.BaseItem.gridSize), Planner.ItemDataGroup),
        Need(typeof(AffixList), nameof(AffixList.get), Planner.ItemDataGroup),
        Need<AffixList>(nameof(AffixList.GetAffix), Planner.ItemDataGroup),
        Need<AffixList>(nameof(AffixList.GetAffixName), Planner.ItemDataGroup),

        // The game's own item tooltip, opened for a planned item.
        Need<TooltipItemManager>(nameof(TooltipItemManager.OpenItemTooltip), Planner.ItemTooltipGroup),
        Need<TooltipItemManager>(nameof(TooltipItemManager.CloseTooltip), Planner.ItemTooltipGroup),
        Need(typeof(TooltipItemManager), nameof(TooltipItemManager.ForceClose), Planner.ItemTooltipGroup),
        Need<TooltipItemManager>(nameof(TooltipItemManager.ActiveItem), Planner.ItemTooltipGroup),
        Need<TooltipItemManager>(nameof(TooltipItemManager.activeParameters), Planner.ItemTooltipGroup),
        Need<TooltipItemManager>(nameof(TooltipItemManager.ShowComparison), Planner.ItemTooltipGroup),
        Need<TooltipItemManager>(nameof(TooltipItemManager.SetEquippedItemTextForComparison), Planner.ItemTooltipGroup),
        Need<UITooltipItem>(nameof(UITooltipItem.instance), Planner.ItemTooltipGroup),
        Need<UITooltipItem>(nameof(UITooltipItem._itemTooltipPrompts), Planner.ItemTooltipGroup),
        Need<TooltipItemManager>(nameof(TooltipItemManager.ActiveTooltipRect), Planner.ItemTooltipGroup),

        // The equipped side and the inventory's slot elements.
        Need(typeof(ItemContainersManager), nameof(ItemContainersManager.Instance), Planner.InventoryUiGroup),
        Need<ItemContainersManager>(nameof(ItemContainersManager.equipment), Planner.InventoryUiGroup),
        Need<ItemContainersManager>(nameof(ItemContainersManager.idols), Planner.InventoryUiGroup),
        Need<ItemContainersManager>(nameof(ItemContainersManager.cursor), Planner.InventoryUiGroup),
        Need<UIBase>(nameof(UIBase.IsInventoryOpen), Planner.InventoryUiGroup),
        Need<UIBase>(nameof(UIBase.openInventory), Planner.InventoryUiGroup),
        Need<ItemContainerUI>(nameof(ItemContainerUI.Container), Planner.InventoryUiGroup),
        Need<ItemContainerEntry>(nameof(ItemContainerEntry.Position), Planner.InventoryUiGroup),

        // The blessings panel.
        Need<ItemContainersManager>(nameof(ItemContainersManager.blessings), Planner.BlessingUiGroup),
        Need<ItemContainersManager.BlessingItemContainer>(nameof(ItemContainersManager.BlessingItemContainer.Containers), Planner.BlessingUiGroup),
        Need<InventoryBlessingSlotUI>(nameof(InventoryBlessingSlotUI.blessingUIContainer), Planner.BlessingUiGroup),
        Need<BlessingContainer>(nameof(BlessingContainer.TimelineID), Planner.BlessingUiGroup),
        Need<TabUIElement>(nameof(TabUIElement.controller), Planner.BlessingUiGroup),
        Need<TabUIController>(nameof(TabUIController.Select), Planner.BlessingUiGroup),

        // The weaver tree.
        Need<LocalTreeData>(nameof(LocalTreeData.weaverTree), Planner.WeaverUiGroup),
        Need<Il2CppLE.Factions.WeaverTree>(nameof(Il2CppLE.Factions.WeaverTree.nodeList), Planner.WeaverUiGroup),
        Need<UIBase>(nameof(UIBase.OpenWeaverRankPanel), Planner.WeaverUiGroup),
        Need<Il2CppLE.Factions.FactionRankUIWeaver>(nameof(Il2CppLE.Factions.FactionRankUIWeaver.IsWeaverTreeOpened), Planner.WeaverUiGroup),

        // The weaver tree's item sockets: the game's node id to socket map, the socketed items and the socket elements.
        Need<ItemContainersManager>(nameof(ItemContainersManager.weaverTreeItems), Planner.WeaverItemsGroup),
        Need(typeof(ItemContainersManager.WeaverTreeContainer), nameof(ItemContainersManager.WeaverTreeContainer.WeaverTreeLargeContainerNodeIDs), Planner.WeaverItemsGroup),
        Need(typeof(ItemContainersManager.WeaverTreeContainer), nameof(ItemContainersManager.WeaverTreeContainer.WeaverTreeSmallContainerNodeIDs), Planner.WeaverItemsGroup),
        Need<ItemContainersManager.WeaverTreeContainer>(nameof(ItemContainersManager.WeaverTreeContainer.TryGetContainer), Planner.WeaverItemsGroup),
        Need<Il2CppLE.Factions.WeaverTreeItemContainerUI>(nameof(Il2CppLE.Factions.WeaverTreeItemContainerUI.LinkedNodeID), Planner.WeaverItemsGroup),
        Need<TooltipItemManager.SlotType>(nameof(TooltipItemManager.SlotType.WEAVER_TREE_NODE), Planner.WeaverItemsGroup),

        // The guide's game icons: skill and item sprites, and node icons from the trees' prefabs, loaded as assets.
        Need<Ability>(nameof(Ability.abilitySpriteSoftRef), Planner.GuideIconsGroup),
        Need(typeof(ItemData), nameof(ItemData.GetItemSprite), Planner.GuideIconsGroup),
        Need(typeof(Il2CppLE.AssetBundles.SoftRefExtensions), nameof(Il2CppLE.AssetBundles.SoftRefExtensions.CreateLoadRefTypeless), Planner.GuideIconsGroup),
        Need<Il2CppLE.AssetBundles.LoadRef>(nameof(Il2CppLE.AssetBundles.LoadRef.Status), Planner.GuideIconsGroup),
        Need<Il2CppLE.AssetBundles.LoadRef>(nameof(Il2CppLE.AssetBundles.LoadRef.TypelessAssetOrNull), Planner.GuideIconsGroup),
        Need<Il2CppLE.AssetBundles.LoadRef>(nameof(Il2CppLE.AssetBundles.LoadRef.ExplicitDisposeInternal), Planner.GuideIconsGroup),
        Need<Il2CppLE.AssetBundles.LoadRef>(nameof(Il2CppLE.AssetBundles.LoadRef.AddSceneLifetimeReferenceTo), Planner.GuideIconsGroup),
        Need<PassivePanelManager>(nameof(PassivePanelManager.PassivePanelData), Planner.GuideIconsGroup),
        Need<PassivePanelData.ClassResources>(nameof(PassivePanelData.ClassResources.passiveTree), Planner.GuideIconsGroup),
        Need<SkillsTreesUIManager>(nameof(SkillsTreesUIManager.prefabSkillTreesHolder), Planner.GuideIconsGroup),
        Need<SkillTreesHolder>(nameof(SkillTreesHolder.Find), Planner.GuideIconsGroup),
        Need<SkillTreeNode>(nameof(SkillTreeNode.icon), Planner.GuideIconsGroup),

        // The game's own skill and node tooltips, opened for a guide's names.
        Need<UITooltipAbility>(nameof(UITooltipAbility.OpenTooltip), Planner.GuideTipsGroup),
        Need<UITooltipAbility>(nameof(UITooltipAbility.CloseTooltip), Planner.GuideTipsGroup),
        Need(typeof(NodeTooltipManager), nameof(NodeTooltipManager.instance), Planner.GuideTipsGroup),
        Need<NodeTooltipManager>(nameof(NodeTooltipManager.openForNode), Planner.GuideTipsGroup),
        Need<NodeTooltipManager>(nameof(NodeTooltipManager.close), Planner.GuideTipsGroup),
        Need<NodeTooltipManager>(nameof(NodeTooltipManager.rectTransform), Planner.GuideTipsGroup),
        Need<UITooltipAbility>(nameof(UITooltipAbility.rectTransform), Planner.GuideTipsGroup),

        // Loot filters made from the game's own filter classes and saved with its own serializer.
        Need(typeof(ItemFilterManager), nameof(ItemFilterManager.Instance), Planner.LootFilterGroup),
        Need<ItemFilterManager>(nameof(ItemFilterManager.Filter), Planner.LootFilterGroup),
        Need<ItemFilterManager>(nameof(ItemFilterManager.filtersPath), Planner.LootFilterGroup),
        Need<ItemFilterManager>(nameof(ItemFilterManager.serializer), Planner.LootFilterGroup),
        Need<ItemFilterManager>(nameof(ItemFilterManager.ns), Planner.LootFilterGroup),
        Need(typeof(ItemFilterManager), nameof(ItemFilterManager.LoadFilterFromPath), Planner.LootFilterGroup),
        Need<ItemFilterManager>(nameof(ItemFilterManager.GetFilterFileList), Planner.LootFilterGroup),
        Need<ItemFilterManager>(nameof(ItemFilterManager.LoadFilter), Planner.LootFilterGroup),
        Need<ItemFilterManager>(nameof(ItemFilterManager.UpdateBindingForActiveCharacter), Planner.LootFilterGroup),
        Need(typeof(ItemFilterManager), nameof(ItemFilterManager.InvokeLootFilterChanged), Planner.LootFilterGroup),
        Need(typeof(ItemFilter), nameof(ItemFilter.MAX_RULES_PER_FILTER), Planner.LootFilterGroup),
        Need(typeof(ItemFilter), nameof(ItemFilter.CURRENT_LF_VERSION), Planner.LootFilterGroup),
        Need<ItemFilter>(nameof(ItemFilter.lootFilterVersion), Planner.LootFilterGroup),
        Need<ItemFilter>(nameof(ItemFilter.rules), Planner.LootFilterGroup),
        Need<ItemFilter>(nameof(ItemFilter.AddRule), Planner.LootFilterGroup),
        Need<ItemFilter>(nameof(ItemFilter.UpdateRuleOrders), Planner.LootFilterGroup),
        Need<ItemFilter>(nameof(ItemFilter.UpdatedLastEditVersion), Planner.LootFilterGroup),
        Need<ItemFilter>(nameof(ItemFilter.PreSerialization), Planner.LootFilterGroup),
        Need<ItemFilter>(nameof(ItemFilter.Clone), Planner.LootFilterGroup),
        Need<ItemFilter>(nameof(ItemFilter.Match), Planner.LootFilterGroup),
        Need<ItemFilter>(nameof(ItemFilter.description), Planner.LootFilterGroup),
        Need<ItemFilter>(nameof(ItemFilter.filterIcon), Planner.LootFilterGroup),
        Need<ItemFilter>(nameof(ItemFilter.filterIconColor), Planner.LootFilterGroup),
        Need<Rule>(nameof(Rule.conditions), Planner.LootFilterGroup),
        Need<Rule>(nameof(Rule.isEnabled), Planner.LootFilterGroup),
        Need<Rule>(nameof(Rule.nameOverride), Planner.LootFilterGroup),
        Need<Rule>(nameof(Rule.recolor), Planner.LootFilterGroup),
        Need<Rule>(nameof(Rule.color), Planner.LootFilterGroup),
        Need<Rule>(nameof(Rule.emphasized), Planner.LootFilterGroup),
        Need<Rule>(nameof(Rule.SoundId), Planner.LootFilterGroup),
        Need<Rule>(nameof(Rule.MapIconId), Planner.LootFilterGroup),
        Need<Rule>(nameof(Rule.BeamOverride), Planner.LootFilterGroup),
        Need<Rule>(nameof(Rule.BeamSizeOverride), Planner.LootFilterGroup),
        Need<Rule>(nameof(Rule.BeamColorOverride), Planner.LootFilterGroup),
        Need<SubTypeCondition>(nameof(SubTypeCondition.SetTypes), Planner.LootFilterGroup),
        Need<SubTypeCondition>(nameof(SubTypeCondition.SetSubTypes), Planner.LootFilterGroup),
        Need<AffixCondition>(nameof(AffixCondition.SetAffixes), Planner.LootFilterGroup),
        Need<AffixCondition>(nameof(AffixCondition.comparsion), Planner.LootFilterGroup),
        Need<AffixCondition>(nameof(AffixCondition.comparsionValue), Planner.LootFilterGroup),
        Need<AffixCondition>(nameof(AffixCondition.minOnTheSameItem), Planner.LootFilterGroup),
        Need<AffixCondition>(nameof(AffixCondition.combinedComparsion), Planner.LootFilterGroup),
        Need<AffixCondition>(nameof(AffixCondition.combinedComparsionValue), Planner.LootFilterGroup),
        Need<AffixCondition>(nameof(AffixCondition.advanced), Planner.LootFilterGroup),
        Need<UniqueModifiersCondition>(nameof(UniqueModifiersCondition.SetUniqueIds), Planner.LootFilterGroup),
        Need<PotentialCondition>(nameof(PotentialCondition.MinLegendaryPotential), Planner.LootFilterGroup),
        Need<PotentialCondition>(nameof(PotentialCondition.MinWeaversWill), Planner.LootFilterGroup),
        Need<RarityCondition>(nameof(RarityCondition.rarity), Planner.LootFilterGroup),
        Need<CorruptionCondition>(nameof(CorruptionCondition.Corruption), Planner.LootFilterGroup),
        Need<CharacterLevelCondition>(nameof(CharacterLevelCondition.minimumLvl), Planner.LootFilterGroup),
        Need<CharacterLevelCondition>(nameof(CharacterLevelCondition.maximumLvl), Planner.LootFilterGroup),
        Need<ClassCondition>(nameof(ClassCondition.req), Planner.LootFilterGroup),

        // The game's loot filter window and settings panel, refreshed after Use now.
        Need<LootFilterUI>(nameof(LootFilterUI.ForcedInit), Planner.LootFilterPanelGroup),
        Need<LootFilterSettingsPanelUI>(nameof(LootFilterSettingsPanelUI.Init), Planner.LootFilterPanelGroup),
        Need<LootFilterUI>(nameof(LootFilterUI.filterSelector), Planner.LootFilterPanelGroup),
        Need<LootFilterUI>(nameof(LootFilterUI._isBuildingUi), Planner.LootFilterPanelGroup),
        Need<LootFilterUI>(nameof(LootFilterUI.autoScrollScrollRect), Planner.LootFilterPanelGroup),
        Need<LootFilterUI>(nameof(LootFilterUI.entries), Planner.LootFilterPanelGroup),
        Need<RuleEntryUI>(nameof(RuleEntryUI.rule), Planner.LootFilterPanelGroup),
        Need<RuleEntryUI>(nameof(RuleEntryUI.SetRuleTogleActive), Planner.LootFilterPanelGroup),
        Need<RuleEntryUI>(nameof(RuleEntryUI.Init), Planner.LootFilterPanelGroup),
        Need<LootFilterUI>(nameof(LootFilterUI._lastActiveFilterWithUiBuilt), Planner.LootFilterPanelGroup),
        Need<LootFilterSettingsPanelUI>(nameof(LootFilterSettingsPanelUI.filterSelector), Planner.LootFilterPanelGroup),
        Need<Il2CppLE.UI.Controls.ColoredIconDropdown>(nameof(Il2CppLE.UI.Controls.ColoredIconDropdown.SetValueWithoutNotify), Planner.LootFilterPanelGroup),
    };

    public override FeatureStatus Status => State == FeatureState.Running ? Planner.StatusOf(this) : base.Status;

    public override void RegisterSettings() => S.Register();
    public override void Start() => Planner.Start(this);
    public override void Update() => Planner.Frame();
    // The game's loot filter window is held in place after the game's own scripts moved it, before it's drawn.
    public override void LateUpdate() => Planner.LootFilterGame.LateFrame();
    public override void OnSceneChanged() => Planner.OnSceneChanged();
    public override void Shutdown() => Planner.Shutdown();
}
