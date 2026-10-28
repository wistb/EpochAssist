using Il2Cpp;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

internal sealed class GameEventsFeature : Feature
{
    public override string Id => "events";
    public override string Name => "Game events";
    public override string[] Needs => new[] { "game", "player", "data" };

    public override PartNeed[] Parts => new[]
    {
        Part("player", "vitals", GameEvents.Hits),
        Part("data", "items", GameEvents.Drops),
        Part("data", "monster mods", GameEvents.Monolith),
    };

    // Every group has a member of its own, since Compat knows only groups that requirements or patch groups name.
    public override Requirement[] Requirements => new[]
    {
        Need<BaseHealth>(nameof(BaseHealth.currentHealth), GameEvents.Hits),
        Need<ExperienceTracker>(nameof(ExperienceTracker.CurrentExperience), GameEvents.Xp),
        Need<ExperienceTracker>(nameof(ExperienceTracker.CurrentLevel), GameEvents.Xp),
        Need<ExperienceTracker>(nameof(ExperienceTracker.NextLevelExperience), GameEvents.Xp),
        Need<GoldTracker>(nameof(GoldTracker.value), GameEvents.Gold),
        Need<GroundItemVisuals>(nameof(GroundItemVisuals.all), GameEvents.Drops),
        Need<GroundItemVisuals>(nameof(GroundItemVisuals.itemData), GameEvents.Drops),
        Need<GroundItemVisuals>(nameof(GroundItemVisuals.id), GameEvents.Drops),
        Need<ItemData>(nameof(ItemData.rarity), GameEvents.Drops),
        Need<ItemData>(nameof(ItemData.isLegendary), GameEvents.Drops),
        Need<ItemData>(nameof(ItemData.isSet), GameEvents.Drops),
        Need<ItemData>(nameof(ItemData.isUnique), GameEvents.Drops),
        Need<ItemData>(nameof(ItemData.isExaltedItem), GameEvents.Drops),
        Need<ItemData>(nameof(ItemData.isRare), GameEvents.Drops),
        Need<ItemData>(nameof(ItemData.isMagic), GameEvents.Drops),
        Need<ItemData>(nameof(ItemData.id), GameEvents.DropKey),
        Need<GroundItemVisuals>(nameof(GroundItemVisuals.label), GameEvents.DropFilter),
        Need<GroundItemLabel>(nameof(GroundItemLabel.lastFilterResult), GameEvents.DropFilter),
        Need<GroundItemLabel>(nameof(GroundItemLabel.CurrentRuleNumber), GameEvents.DropFilter),
        Need<GroundItemLabel>(nameof(GroundItemLabel.isAddedToAll), GameEvents.DropFilter),
        Need(typeof(Il2CppItemFiltering.ItemFilterManager), nameof(Il2CppItemFiltering.ItemFilterManager.Instance), GameEvents.DropFilter),
        Need<Il2CppItemFiltering.ItemFilterManager>(nameof(Il2CppItemFiltering.ItemFilterManager.Filter), GameEvents.DropFilter),
        Need<Il2CppItemFiltering.ItemFilterManager>(nameof(Il2CppItemFiltering.ItemFilterManager.ManualFilterDisable), GameEvents.DropFilter),
        Need(typeof(DeathInformationText), nameof(DeathInformationText.mostRecentDeathInfo), GameEvents.Death),
        Need(typeof(MonolithZoneManager), nameof(MonolithZoneManager.instance), GameEvents.Echo),
        Need<MonolithZoneManager>(nameof(MonolithZoneManager.objectiveComplete), GameEvents.Echo),
        Need<MonolithTimelinePanelManager>(nameof(MonolithTimelinePanelManager.LastSelectedIsland), GameEvents.Monolith),
        Need<MonolithTimelinePanelManager>(nameof(MonolithTimelinePanelManager.timelineID), GameEvents.Monolith),
        Need<EchoWebIsland>(nameof(EchoWebIsland.mod), GameEvents.Monolith),
        Need<EchoWebIsland>(nameof(EchoWebIsland.stabilityReward), GameEvents.Monolith),
        Need<EchoWebIsland>(nameof(EchoWebIsland.hexIndex), GameEvents.Monolith),
        Need<EchoWebIsland>(nameof(EchoWebIsland.hasDroppableReward), GameEvents.MonolithReward),
        Need<MonolithEchoWebUI>(nameof(MonolithEchoWebUI.activeIslands), GameEvents.MonolithReward),
        Need<EchoWebIslandUI>(nameof(EchoWebIslandUI.island), GameEvents.MonolithReward),
        Need<EchoWebIslandUI>(nameof(EchoWebIslandUI.rewards), GameEvents.MonolithReward),
        Need<DroppableReward>(nameof(DroppableReward.title), GameEvents.MonolithReward),
        Need(typeof(Il2CppLE.Gameplay.Monolith.MonolithGameplayManager), nameof(Il2CppLE.Gameplay.Monolith.MonolithGameplayManager.CurrentCorruption), GameEvents.Corruption),
        Need<ExperienceTracker>(nameof(ExperienceTracker.CurrentLevel), GameEvents.Level),
        Need<Il2CppLE.Factions.FactionTracker>(nameof(Il2CppLE.Factions.FactionTracker.factions), GameEvents.Factions),
        Need<Il2CppLE.Factions.ClientFactionInfo>(nameof(Il2CppLE.Factions.ClientFactionInfo.factions), GameEvents.Factions),
        Need<Il2CppLE.Factions.Faction>(nameof(Il2CppLE.Factions.Faction.Favor), GameEvents.Factions),
        Need<Il2CppLE.Factions.Faction>(nameof(Il2CppLE.Factions.Faction.Reputation), GameEvents.Factions),
        Need<Il2CppLE.Factions.Faction>(nameof(Il2CppLE.Factions.Faction.TotalReputation), GameEvents.Factions),
        Need<Il2CppLE.Factions.Faction>(nameof(Il2CppLE.Factions.Faction.Rank), GameEvents.Factions),
        Need<Il2CppLE.Factions.Faction>(nameof(Il2CppLE.Factions.Faction.ReputationToNextRankPercent), GameEvents.Factions),
        Need<Il2CppLE.Factions.Faction>(nameof(Il2CppLE.Factions.Faction.IsMember), GameEvents.Factions),
    };

    public override IEnumerable<string> DiagnosticNotes => GameEvents.Notes();

    public override void Start() => GameEvents.Start(this);
    public override void Update() => GameEvents.Update();
    public override void Shutdown() => GameEvents.Stop();
}
