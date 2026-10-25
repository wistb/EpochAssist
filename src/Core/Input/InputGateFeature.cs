using Il2Cpp;

namespace EpochAssist.Core;

internal sealed class InputGateFeature : Feature
{
    public override string Id => "input";
    public override string Name => "Input blocking";
    // The wheel-zoom hold is optional: if the camera changes in a game update, input blocking must keep working.
    private const string ZoomHold = "wheel zoom hold";
    private const string MovementStop = "movement stop";
    private const string HudClicks = "HUD click blocking";

    public override Type[] PatchClasses => NestedPatches(typeof(InputGate));
    public override IReadOnlyDictionary<Type, string> PatchGroups { get; } =
        new Dictionary<Type, string> { [typeof(InputGate.CameraUpdate_Patch)] = ZoomHold, [typeof(InputGate.InputUpdate_Patch)] = HudClicks };
    public override Requirement[] Requirements => new[]
    {
        Need<EpochInputManager>(nameof(EpochInputManager.forceDisableInput)),
        Need<CameraManager>(nameof(CameraManager.targetZoom), ZoomHold),
        Need<CameraManager>(nameof(CameraManager.currentZoom), ZoomHold),
        Need<EpochInputManager>(nameof(EpochInputManager.movementProcessor), MovementStop),
        Need<EpochInputManager>(nameof(EpochInputManager.isKeyboardMovementButtonHeld), MovementStop),
        Need<MovementProcessor>(nameof(MovementProcessor.StopActiveMovement), MovementStop),
        Need<MovementProcessor>(nameof(MovementProcessor.ProcessKeyboardMovement), MovementStop),
        Need<EpochInputManager>(nameof(EpochInputManager.blockMouseMovementInput), HudClicks),
    };

    public override void Start()
    {
        InputGate.CanStopMovement = Has(MovementStop);
        InputGate.CanBlockMouseMovement = Has(HudClicks);
    }

    // Runs after the menu and the HUD, which set what to block.
    public override void Update() => InputGate.Apply();
    public override void Shutdown() => InputGate.Release();
}
