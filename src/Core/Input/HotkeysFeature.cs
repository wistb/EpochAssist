namespace EpochAssist.Core;

internal sealed class HotkeysFeature : Feature
{
    public override string Id => "hotkeys";
    public override string Name => "Hotkeys";
    // Optional: without the game's binding lookup there are no slot-toggle chords (the bar command still toggles)
    // and no warnings about keys the game uses.
    private const string GameBindings = "game bindings";

    public override string[] Needs => new[] { "game" };
    // Reads its bindings each frame, after the character's values are in place.
    public override string[] RunsAfter => new[] { "profiles" };
    public override Requirement[] Requirements => new[]
    {
        Need(typeof(Il2CppRewired.ReInput), nameof(Il2CppRewired.ReInput.isReady), GameBindings),
        Need(typeof(Il2CppRewired.ReInput), nameof(Il2CppRewired.ReInput.players), GameBindings),
        Need<Il2CppRewired.Player.ControllerHelper.MapHelper>(nameof(Il2CppRewired.Player.ControllerHelper.MapHelper.GetFirstElementMapWithAction), GameBindings),
        Need<Il2CppRewired.ActionElementMap>(nameof(Il2CppRewired.ActionElementMap.keyCode), GameBindings),
        Need<Il2CppRewired.ActionElementMap>(nameof(Il2CppRewired.ActionElementMap.elementIdentifierId), GameBindings),
    };

    public override void Start()
    {
        Hotkeys.CanReadGameBindings = Has(GameBindings);
        Hotkeys.Start();
        PauseRules.CaptureActive = () => Hotkeys.CaptureActive;
    }

    public override void Update() => Hotkeys.Update();
    public override void OnSceneChanged() => Hotkeys.SetSlotChordsStale();

    public override void Shutdown()
    {
        PauseRules.CaptureActive = () => false;
        Hotkeys.Reset();
    }
}
