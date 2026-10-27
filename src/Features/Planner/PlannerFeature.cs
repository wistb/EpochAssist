using EpochAssist.Core;
using Il2Cpp;
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
        Need<SkillsPanelManager>(nameof(SkillsPanelManager.masteryClass1PanelIcons), Planner.SkillsPageGroup),
        Need<SkillsPanelManager>(nameof(SkillsPanelManager.masteryClass2PanelIcons), Planner.SkillsPageGroup),
        Need<SkillsPanelManager>(nameof(SkillsPanelManager.masteryClass3PanelIcons), Planner.SkillsPageGroup),
        Need<SkillsPanelManager>(nameof(SkillsPanelManager.masteryAbilityPanelIcon1), Planner.SkillsPageGroup),
        Need<SkillsPanelManager>(nameof(SkillsPanelManager.masteryAbilityPanelIcon2), Planner.SkillsPageGroup),
        Need<SkillsPanelManager>(nameof(SkillsPanelManager.masteryAbilityPanelIcon3), Planner.SkillsPageGroup),
        Need<AbilityPanelIcon>(nameof(AbilityPanelIcon.ability), Planner.SkillsPageGroup),
        Need<SpecializedSkillIcon>(nameof(SpecializedSkillIcon.Ability), Planner.SkillsPageGroup),

        Need(typeof(GlobalTreeData), nameof(GlobalTreeData.get), Planner.GameDataGroup),
        Need<GlobalTreeData>(nameof(GlobalTreeData.getTreeData), Planner.GameDataGroup),
        Need<GlobalTreeData.TreeData>(nameof(GlobalTreeData.TreeData.nodes), Planner.GameDataGroup),
        Need<GlobalTreeData.NodeData>(nameof(GlobalTreeData.NodeData.requiredMastery), Planner.GameDataGroup),
        Need<GlobalTreeData.NodeData>(nameof(GlobalTreeData.NodeData.maxPoints), Planner.GameDataGroup),
        Need(typeof(AbilityManager), nameof(AbilityManager.getPlayerAbility), Planner.GameDataGroup),
        Need(typeof(CharacterClassList), nameof(CharacterClassList.getClass), Planner.GameDataGroup),
        Need(typeof(CharacterClassList), nameof(CharacterClassList.getClassName), Planner.GameDataGroup),
    };

    public override FeatureStatus Status => State == FeatureState.Running ? Planner.StatusOf(this) : base.Status;

    public override void RegisterSettings() => S.Register();
    public override void Start() => Planner.Start(this);
    public override void Update() => Planner.Frame();
    public override void OnSceneChanged() => Planner.OnSceneChanged();
    public override void Shutdown() => Planner.Shutdown();
}
