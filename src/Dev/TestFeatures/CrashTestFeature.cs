using EpochAssist.Core;

namespace EpochAssist.Dev;

/// <summary>
/// Dev only: throws when armed by the crash command, so faults, failed starts and crash reports can be checked in game.
/// Fault: its next five updates throw the same error (the host faults it). Start: its next start throws. Handler: its
/// next n ZoneChange handler calls throw, so a subscriber's error can be seen to land on its owner.
/// </summary>
internal sealed class CrashTestFeature : Feature
{
    private static int s_updateThrows;
    private static string? s_startThrow;
    private static int s_handlerThrows;

    public override string Id => "crashtest";
    public override string Name => "Crash test";

    /// <summary>The members of the dev `edits` test targets; without them `edits test` refuses.</summary>
    public const string EditsGroup = "crashtest: edits";

    public override Requirement[] Requirements => new[]
    {
        Need<Il2CppLE.UI.Minimap.Minimap>(nameof(Il2CppLE.UI.Minimap.Minimap.RevealRadius), EditsGroup),
        Need<Il2CppLE.UI.Minimap.MinimapIconManager>(nameof(Il2CppLE.UI.Minimap.MinimapIconManager.minimap), EditsGroup),
        Need<Il2Cpp.DMMapZoom>(nameof(Il2Cpp.DMMapZoom.maxMinimapZoom), EditsGroup),
        Need<Il2Cpp.CameraManager>(nameof(Il2Cpp.CameraManager.instance), EditsGroup),
        Need<Il2Cpp.CameraManager>(nameof(Il2Cpp.CameraManager.zoomMin), EditsGroup),
    };

    // Always on, never saved: the dev UI client has no switch of its own.
    private static readonly Toggle s_uiOn = new(new Section(new SettingsGroup("crashtest", "Crash test"), "ui", "Dev UI actions"), "on", "On", true);

    /// <summary>The dev client for UI action requests (`ui`): skills class, no rules of its own.</summary>
    public static DispatchClient? UiClient
    {
        get
        {
            // A dispatcher restart drops its clients; this one registers again.
            if (s_uiClient != null && !Dispatcher.Clients.Contains(s_uiClient)) Dispatcher.Register(s_uiClient);
            return s_uiClient;
        }
    }
    private static DispatchClient? s_uiClient;

    public static void ArmFault() => s_updateThrows = 5;

    public static void ArmStart(string message) => s_startThrow = message;

    public static void ArmHandler(int calls) => s_handlerThrows = calls;

    public override void Start()
    {
        // An empty repeat job, so `jobs` can show a fault dropping it.
        Scheduler.Every(this, "tick", 1f, () => { });
        s_uiClient = Dispatcher.Register(new DispatchClient(this, PriorityClass.Skills, s_uiOn, Array.Empty<PauseRule>()));
        Bus.Subscribe<ZoneChange>(this, _ =>
        {
            if (s_handlerThrows <= 0) return;
            s_handlerThrows--;
            throw new InvalidOperationException("crash test handler");
        });
        if (s_startThrow is not { } message) return;
        s_startThrow = null;
        throw new InvalidOperationException(message);
    }

    public override void Update()
    {
        if (s_updateThrows <= 0) return;
        s_updateThrows--;
        throw new InvalidOperationException("crash test fault");
    }

    public override void Shutdown()
    {
        s_updateThrows = s_handlerThrows = 0;
        if (s_uiClient != null) Dispatcher.Unregister(s_uiClient);
        s_uiClient = null;
    }
}
