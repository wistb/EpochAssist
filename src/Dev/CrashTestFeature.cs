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

    public static void ArmFault() => s_updateThrows = 5;

    public static void ArmStart(string message) => s_startThrow = message;

    public static void ArmHandler(int calls) => s_handlerThrows = calls;

    public override void Start()
    {
        // An empty repeat job, so `jobs` can show a fault dropping it.
        Scheduler.Every(this, "tick", 1f, () => { });
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

    public override void Shutdown() => s_updateThrows = s_handlerThrows = 0;
}
