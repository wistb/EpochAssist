using EpochAssist.Core;
using Il2Cpp;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Features;

internal sealed class AutoCastFeature : Feature
{
    public override string Id => "autocast";
    public override string Name => "Auto-Cast";
    public override GuiLayer Layer => GuiLayer.World;
    public override string[] Needs => new[] { "game", "dispatch", "hotkeys" };

    // Sending, channel stops and retargeting are the dispatcher's requirements; these are what auto-cast reads itself.
    public override Requirement[] Requirements => new[]
    {
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
        Need<MouseManager>(nameof(MouseManager.AbilityUseMousePoint)),
        Need<ActorManager>(nameof(ActorManager.visuals)),
        Need<ActorDisplayInformation>(nameof(ActorDisplayInformation.actorClass)),
    };

    /// <summary>The dispatcher's status for auto-cast while it runs.</summary>
    public override FeatureStatus Status =>
        State == FeatureState.Running && AutoCast.Client is { } client ? Dispatcher.StatusOf(client) : base.Status;

    public override void RegisterSettings() => AutoCastSettings.Register();
    public override void Start() => AutoCast.Start(this);
    public override void Update() => AutoCast.OnUpdate();
    public override void OnGUI() => AutoCast.OnGUI();
    public override void Shutdown() => AutoCast.Shutdown();
}
