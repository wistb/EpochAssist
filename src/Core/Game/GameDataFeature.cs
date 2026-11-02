using Il2Cpp;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

internal sealed class GameDataFeature : Feature
{
    public override string Id => "data";
    public override string Name => "Game data";

    // Each kind is its own group, so a renamed table loses only its lookups.
    public override Requirement[] Requirements => new[]
    {
        Need(typeof(CharacterClassList), nameof(CharacterClassList.get), GameData.ClassSkills),
        Need<CharacterClassList>(nameof(CharacterClassList.classes), GameData.ClassSkills),
        Need<CharacterClass>(nameof(CharacterClass.classID), GameData.ClassSkills),
        Need<CharacterClass>(nameof(CharacterClass.getAllAbilities), GameData.ClassSkills),
        Need(typeof(AffixList), nameof(AffixList.get), GameData.Affixes),
        Need<AffixList>(nameof(AffixList.GetAffix), GameData.Affixes),
        Need<AffixList.Affix>(nameof(AffixList.Affix.affixName), GameData.Affixes),
        Need<AffixList.Affix>(nameof(AffixList.Affix.getAffixDisplayName), GameData.Affixes),
        Need<AffixList.Affix>(nameof(AffixList.Affix.affixTitle), GameData.Affixes),
        Need<AffixList.Affix>(nameof(AffixList.Affix.tiers), GameData.Affixes),
        Need<AffixList>(nameof(AffixList.GetSingleAffixValue), GameData.Affixes),
        Need<AffixList>(nameof(AffixList.GetMultiAffixPropertyValue), GameData.Affixes),
        Need(typeof(ItemList), nameof(ItemList.get), GameData.Affixes),
        Need<ItemList>(nameof(ItemList.getAffixEffectModifier), GameData.Affixes),
        Need(typeof(ItemList), nameof(ItemList.GetItemImplicits), GameData.Values),
        Need(typeof(ItemList), nameof(ItemList.GetItemImplicit), GameData.Values),
        Need<ItemList.EquipmentImplicit>(nameof(ItemList.EquipmentImplicit.implicitValue), GameData.Values),
        Need<ItemList.EquipmentImplicit>(nameof(ItemList.EquipmentImplicit.GetValue), GameData.Values),
        Need(typeof(PropertyList), nameof(PropertyList.get), GameData.Values),
        Need<PropertyList>(nameof(PropertyList.GetPropertyInfo), GameData.Values),
        Need<AffixList.Affix>(nameof(AffixList.Affix.GetPropertyInfo), GameData.Values),
        Need<AffixList.MultiAffix>(nameof(AffixList.MultiAffix.affixProperties), GameData.Values),
        Need<AffixList.AffixProperty>(nameof(AffixList.AffixProperty.getModDisplayName), GameData.Values),
        Need<BasePropertyInfo>(nameof(BasePropertyInfo.getPropertyName), GameData.Values),
        Need<BasePropertyInfo>(nameof(BasePropertyInfo.GetValueAfterRounding), GameData.Values),
        Need<BasePropertyInfo>(nameof(BasePropertyInfo.displayAddedAsPercentage), GameData.Values),
        Need<BasePropertyInfo>(nameof(BasePropertyInfo.displayAddedAsTenthOfValue), GameData.Values),
        Need(typeof(UniqueList), nameof(UniqueList.getUnique), GameData.Values),
        Need<UniqueList.Entry>(nameof(UniqueList.Entry.mods), GameData.Values),
        Need<UniqueItemMod>(nameof(UniqueItemMod.getValue), GameData.Values),
        Need<UniqueItemMod>(nameof(UniqueItemMod.rollID), GameData.Values),
        Need<UniqueItemMod>(nameof(UniqueItemMod.canRoll), GameData.Values),
        Need<ItemData>(nameof(ItemData.getUniqueRoll), GameData.Values),
        Need(typeof(TooltipItemManager), nameof(TooltipItemManager.ImplicitFormatter), GameData.Values),
        Need(typeof(TooltipItemManager), nameof(TooltipItemManager.UniqueBasicModFormatter), GameData.Values),
        Need(typeof(MonsterRarityManager), nameof(MonsterRarityManager.monsterModByKey), GameData.MonsterMods),
        Need<Il2Cpp.MonsterMod>(nameof(Il2Cpp.MonsterMod.modKey), GameData.MonsterMods),
        Need<Il2Cpp.MonsterMod>(nameof(Il2Cpp.MonsterMod.description), GameData.MonsterMods),
        Need(typeof(AbilityManager), nameof(AbilityManager.getPlayerAbility), GameData.Abilities),
        Need<Ability>(nameof(Ability.abilityName), GameData.Abilities),
        Need<ItemData>(nameof(ItemData.id), GameData.Items),
        Need(typeof(SceneList), nameof(SceneList.instance), GameData.SceneNames),
        Need(typeof(SceneList), nameof(SceneList.GetSceneDisplayName), GameData.SceneNames),
        Need(typeof(SceneList), nameof(SceneList.GetEraEchoName), GameData.SceneNames),
        Need<ItemDataUnpacked>(nameof(ItemDataUnpacked.FullName), GameData.Items),
        Need<ItemDataUnpacked>(nameof(ItemDataUnpacked.LocalisedBaseTypeName), GameData.Items),
        Need<ItemData>(nameof(ItemData.uniqueID), GameData.Items),
        Need<ItemData>(nameof(ItemData.isSet), GameData.Items),
        Need<ItemData>(nameof(ItemData.getSetItemUniqueId), GameData.Items),
        Need<ItemData>(nameof(ItemData.isUniqueOrLegendary), GameData.Items),
        Need(typeof(UniqueList), nameof(UniqueList.getUniqueName), GameData.Items),
        Need(typeof(AilmentList), nameof(AilmentList.getAilment), GameData.Buffs),
        Need<Ailment>(nameof(Ailment.id), GameData.Buffs),
        Need<Ailment>(nameof(Ailment.displayName), GameData.Buffs),
    };

    public override void Shutdown() => GameData.Clear();
}
