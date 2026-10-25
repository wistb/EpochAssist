using Il2Cpp;
using Il2CppEHG.Multiplayer;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

internal sealed class PlayerModelFeature : Feature
{
    public override string Id => "player";
    public override string Name => "Player model";
    public override string[] Needs => new[] { "game" };

    // One group per part, or one per mode where the source differs; a missing one turns off only that part.
    public override Requirement[] Requirements => new[]
    {
        Need<PlayerHealth>(nameof(PlayerHealth.currentHealth), PlayerModel.VitalsGroup),
        Need<PlayerHealth>(nameof(PlayerHealth.maxHealth), PlayerModel.VitalsGroup),
        Need<PlayerHealth>(nameof(PlayerHealth.getHealthPercent), PlayerModel.VitalsGroup),
        Need<PlayerMana>(nameof(PlayerMana.currentMana), PlayerModel.VitalsGroup),
        Need<PlayerMana>(nameof(PlayerMana.maxMana), PlayerModel.VitalsGroup),

        Need<Actor>(nameof(Actor.protection), PlayerModel.WardOffline),
        Need<ProtectionClass>(nameof(ProtectionClass.currentWard), PlayerModel.WardOffline),
        Need<ClientWardHolder>(nameof(ClientWardHolder.currentWard), PlayerModel.WardOnline),

        Need(typeof(GameplayEnvironment), nameof(GameplayEnvironment.IsOnlinePlay), PlayerModel.ModeFlag),

        Need<Actor>(nameof(Actor.ailmentReceiver), PlayerModel.BuffsOffline),
        Need<AilmentReceiver>(nameof(AilmentReceiver.ailments), PlayerModel.BuffsOffline),
        Need<AilmentReceiver>(nameof(AilmentReceiver.ailmentStacks), PlayerModel.BuffsOffline),
        Need<AilmentReceiver.ActiveAilment>(nameof(AilmentReceiver.ActiveAilment.ailment), PlayerModel.BuffsOffline),
        Need<AilmentReceiver.ActiveAilment>(nameof(AilmentReceiver.ActiveAilment.remainingDuration), PlayerModel.BuffsOffline),
        Need<AilmentReceiver.ActiveAilment>(nameof(AilmentReceiver.ActiveAilment.totalDuration), PlayerModel.BuffsOffline),
        Need<Ailment>(nameof(Ailment.id), PlayerModel.BuffsOffline),
        Need<Ailment>(nameof(Ailment.positive), PlayerModel.BuffsOffline),
        Need<Ailment>(nameof(Ailment.showsInBuffUI), PlayerModel.BuffsOffline),

        Need<BuffsUIManager>(nameof(BuffsUIManager.buffs), PlayerModel.BuffsOnline),
        Need<BuffsUIManager>(nameof(BuffsUIManager.debuffs), PlayerModel.BuffsOnline),
        Need<BuffUIEntry>(nameof(BuffUIEntry.id), PlayerModel.BuffsOnline),
        Need<BuffUIEntry>(nameof(BuffUIEntry.maxRemainingDuration), PlayerModel.BuffsOnline),
        Need<BuffUIEntry>(nameof(BuffUIEntry.stacks), PlayerModel.BuffsOnline),

        Need<AbilityBarIcon>(nameof(AbilityBarIcon.all), PlayerModel.SkillBar),
        Need<AbilityBarIcon>(nameof(AbilityBarIcon.abilityNumber), PlayerModel.SkillBar),
        Need<AbilityBarIcon>(nameof(AbilityBarIcon.ability), PlayerModel.SkillBar),
        Need<Ability>(nameof(Ability.playerAbilityID), PlayerModel.SkillBar),
        Need<Ability>(nameof(Ability.abilityName), PlayerModel.SkillBar),
        Need<Ability>(nameof(Ability.channelled), PlayerModel.SkillBar),
        Need<UsingAbilityPlayer>(nameof(UsingAbilityPlayer.channelling), PlayerModel.SkillBar),
        Need<ClientUsingAbilityState>(nameof(ClientUsingAbilityState.channelling), PlayerModel.SkillBar),
        Need<LocalTreeData>(nameof(LocalTreeData.playerAbilityList), PlayerModel.SkillBar),
        Need<PlayerAbilityList>(nameof(PlayerAbilityList.getAbility), PlayerModel.SkillBar),

        Need<PlayerChargeManager>(nameof(PlayerChargeManager.onCoooldown), PlayerModel.CooldownReady),

        Need<OfflinePlayerChargeManager>(nameof(OfflinePlayerChargeManager.abilities), PlayerModel.ChargesOffline),
        Need<OfflinePlayerChargeManager>(nameof(OfflinePlayerChargeManager.charges), PlayerModel.ChargesOffline),
        Need<OfflinePlayerChargeManager>(nameof(OfflinePlayerChargeManager.maxCharges), PlayerModel.ChargesOffline),
        Need<OfflinePlayerChargeManager>(nameof(OfflinePlayerChargeManager.chargeRegen), PlayerModel.ChargesOffline),

        Need<ClientChargeManager>(nameof(ClientChargeManager.icons), PlayerModel.ChargesOnline),
        Need<ClientChargeManager>(nameof(ClientChargeManager.charges), PlayerModel.ChargesOnline),
        Need<ClientChargeManager>(nameof(ClientChargeManager.maxCharges), PlayerModel.ChargesOnline),
        Need<ClientChargeManager>(nameof(ClientChargeManager.chargeRegen), PlayerModel.ChargesOnline),

        Need<LocalTreeData>(nameof(LocalTreeData.playerAbilityList), PlayerModel.FormGroup),
        Need<PlayerAbilityList>(nameof(PlayerAbilityList.TransformAbilityId), PlayerModel.FormGroup),
        Need<ClientAbilityList>(nameof(ClientAbilityList.transformed), PlayerModel.FormGroup),

        Need<ChangeHealthMaterialDuringLifetime>(nameof(ChangeHealthMaterialDuringLifetime.materialToChangeTo), PlayerModel.ReaperForm),
        Need(typeof(UIGlobeHealth.AlternateMaterial), nameof(UIGlobeHealth.AlternateMaterial.ReaperForm), PlayerModel.ReaperForm),

        Need<HealthPotion>(nameof(HealthPotion.currentCharges), PlayerModel.PotionsOffline),
        Need<HealthPotion>(nameof(HealthPotion.maxCharges), PlayerModel.PotionsOffline),
        Need<HealthPotionCharges>(nameof(HealthPotionCharges.currentCharges), PlayerModel.PotionsOnline),
        Need<HealthPotionCharges>(nameof(HealthPotionCharges.maxCharges), PlayerModel.PotionsOnline),
    };

    public override void Start() => PlayerModel.Start(this);
    public override void Shutdown() => PlayerModel.Stop();
}
