using Il2Cpp;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

internal sealed class GameStateFeature : Feature
{
    public override string Id => "game";
    public override string Name => "Game state";
    // Chat focus is optional: without it, typing in chat is missed but everything else keeps working.
    private const string ChatTyping = "chat typing";
    // Optional too: without them automation doesn't pause for that one condition.
    private const string LoadingGroup = "loading";
    private const string PauseMenu = "pause menu";

    public override Type[] PatchClasses => NestedPatches(typeof(GameState));
    public override Requirement[] Requirements => new[]
    {
        Need<SceneList>(nameof(SceneList.IsNonCombatZone)),
        Need(typeof(PlayerFinder), nameof(PlayerFinder.getPlayerActor)),
        Need(typeof(PlayerFinder), nameof(PlayerFinder.getLocalTreeData)),
        Need<PlayerHealth>(nameof(PlayerHealth.currentHealth)),
        Need<EpochInputManager>(nameof(EpochInputManager.IsInputFieldActive)),
        Need<UIBase>(nameof(UIBase.instance), ChatTyping),
        Need<UIBase>(nameof(UIBase.IsChatInputFocused), ChatTyping),
        Need(typeof(Il2CppLE.UI.LoadingScreen), nameof(Il2CppLE.UI.LoadingScreen.IsVisible), LoadingGroup),
        Need<UIBase>(nameof(UIBase.instance), PauseMenu),
        Need<UIBase>(nameof(UIBase.menuOpen), PauseMenu),
    };

    public override void Start()
    {
        GameState.CanReadChat = Has(ChatTyping);
        GameState.CanReadLoading = Has(LoadingGroup);
        GameState.CanReadPauseMenu = Has(PauseMenu);
        GameState.OnSceneChanged();
    }

    public override void Update() => GameState.Update();
    public override void OnSceneChanged() => GameState.OnSceneChanged();
    public override void Shutdown() => GameState.Reset();
}
