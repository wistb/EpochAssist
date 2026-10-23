using EpochAssist.Core;
using Il2Cpp;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Features;

internal sealed class AutoCastFeature : Feature
{
    public override string Id => "autocast";
    public override string Name => "Auto-Cast";
    public override GuiLayer Layer => GuiLayer.World;
    public override Type[] PatchClasses => NestedPatches(typeof(AutoCast));

    public override Requirement[] Requirements => new[]
    {
        Need<EpochInputManager>(nameof(EpochInputManager.useAbilityProcessor)),
        Need<UseAbilityProcessor>(nameof(UseAbilityProcessor.UseBarAbilityCommand)),
        Need<UseAbilityProcessor>(nameof(UseAbilityProcessor.PlayerAbilityState)),
        Need<ClientUsingAbilityState>(nameof(ClientUsingAbilityState.AttemptToStopAbility)),
        Need<LocalTreeData>(nameof(LocalTreeData.playerAbilityList)),
        Need<PlayerAbilityList>(nameof(PlayerAbilityList.getAbility)),
        Need<PlayerAbilityList>(nameof(PlayerAbilityList.TransformAbilityId)),
        Need<AbilityBarIcon>(nameof(AbilityBarIcon.all)),
        Need<AbilityBarIcon>(nameof(AbilityBarIcon.abilityNumber)),
        Need<AbilityBarIcon>(nameof(AbilityBarIcon.ability)),
        Need<AbilityBarIcon>(nameof(AbilityBarIcon.icon)),
        Need<Ability>(nameof(Ability.channelled)),
        Need<PlayerChargeManager>(nameof(PlayerChargeManager.onCoooldown)),
        Need<UsingAbilityPlayer>(nameof(UsingAbilityPlayer.channelling)),
        Need<UsingAbilityPlayer>(nameof(UsingAbilityPlayer.TryUpdateTargetLocationIfChannelling)),
        Need<UsingAbilityPlayer>(nameof(UsingAbilityPlayer.stopUsingAbility)),
        Need<MouseManager>(nameof(MouseManager.AbilityUseMousePoint)),
        Need<ActorManager>(nameof(ActorManager.visuals)),
        Need<ActorDisplayInformation>(nameof(ActorDisplayInformation.actorClass)),
    };

    public override void RegisterSettings() => AutoCastSettings.Register();
    public override void Start() => AutoCast.Start();
    public override void Update() => AutoCast.OnUpdate();
    public override void OnGUI() => AutoCast.OnGUI();
    public override void Shutdown() => AutoCast.Shutdown();
}
