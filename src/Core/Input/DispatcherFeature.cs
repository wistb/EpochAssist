using Il2Cpp;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

internal sealed class DispatcherFeature : Feature
{
    public override string Id => "dispatch";
    public override string Name => "Dispatcher";
    // Without it a skill the player holds no longer stops automation on that slot and later ones.
    private const string ManualHolds = "manual holds";
    public const string Prompts = "dispatch: prompts";
    public const string RespawnGroup = "dispatch: respawn";
    public const string QuickMoveGroup = "dispatch: quick move";
    public const string PotionOnline = "dispatch: potion (online)";
    public const string PotionOffline = "dispatch: potion (offline)";
    public const string PortalOnline = "dispatch: portal (online)";
    public const string PortalOffline = "dispatch: portal (offline)";

    public override string[] Needs => new[] { "game", "input" };
    // Its pause rules read each client's switch, after the character's values are in place.
    public override string[] RunsAfter => new[] { "profiles" };
    public override Type[] PatchClasses => NestedPatches(typeof(Dispatcher));
    public override Requirement[] Requirements => new[]
    {
        Need<EpochInputManager>(nameof(EpochInputManager.useAbilityProcessor)),
        Need<UseAbilityProcessor>(nameof(UseAbilityProcessor.UseBarAbilityCommand)),
        Need<UseAbilityProcessor>(nameof(UseAbilityProcessor.PlayerAbilityState)),
        Need<ClientUsingAbilityState>(nameof(ClientUsingAbilityState.AttemptToStopAbility)),
        Need<UsingAbilityPlayer>(nameof(UsingAbilityPlayer.TryUpdateTargetLocationIfChannelling)),
        Need<UsingAbilityPlayer>(nameof(UsingAbilityPlayer.stopUsingAbility)),
        Need(typeof(Il2CppRewired.ReInput), nameof(Il2CppRewired.ReInput.isReady), ManualHolds),
        Need(typeof(Il2CppRewired.ReInput), nameof(Il2CppRewired.ReInput.players), ManualHolds),
        Need<Il2CppRewired.Player>(nameof(Il2CppRewired.Player.GetButton), ManualHolds),
        Need<Il2CppLE.UI.PanelSystem.ModalTemplate>("_yesButton", Prompts),
        Need<Il2CppLE.UI.PanelSystem.ModalTemplate>("_noButton", Prompts),
        Need<Il2CppLE.UI.PanelSystem.ModalTemplate>("_title", Prompts),
        Need<DeathScreen>(nameof(DeathScreen.normalRespawnButton), RespawnGroup),
        Need<ItemContainerUI>(nameof(ItemContainerUI.TryQuickMove), QuickMoveGroup),
        Need<ItemContainerUI>(nameof(ItemContainerUI.identifier), QuickMoveGroup),
        Need(typeof(LocalPlayer), nameof(LocalPlayer.instance), PotionOnline),
        Need<LocalPlayer>(nameof(LocalPlayer.PotionKeyPressed), PotionOnline),
        Need<EpochInputManager>(nameof(EpochInputManager.potion), PotionOffline),
        Need<HealthPotion>(nameof(HealthPotion.UsePotion), PotionOffline),
        Need<ClientUsingAbilityState>(nameof(ClientUsingAbilityState.UsePortalCommand), PortalOnline),
        Need<UsingAbilityPlayer>(nameof(UsingAbilityPlayer.UsePortalCommand), PortalOffline),
    };

    public override void Start()
    {
        Dispatcher.CanReadManualHolds = Has(ManualHolds);
        Dispatcher.CanDrinkOnline = Has(PotionOnline);
        Dispatcher.CanDrinkOffline = Has(PotionOffline);
        Dispatcher.CanPortalOnline = Has(PortalOnline);
        Dispatcher.CanPortalOffline = Has(PortalOffline);
    }
    public override void Update() => Dispatcher.Update();
    public override void LateUpdate() => Dispatcher.LateUpdate();
    public override void OnSceneChanged() => Dispatcher.OnSceneChanged();
    public override void Shutdown() => Dispatcher.Shutdown();
}
