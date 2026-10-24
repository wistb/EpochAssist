using EpochAssist.Core;

namespace EpochAssist.Dev;

/// <summary>
/// Dev only: throws when armed by the crash command, so faults, failed starts and crash reports can be checked in game.
/// Fault: its next five updates throw the same error (the host faults it). Start: its next start throws.
/// </summary>
internal sealed class CrashTestFeature : Feature
{
    private static int s_updateThrows;
    private static string? s_startThrow;

    public override string Id => "crashtest";
    public override string Name => "Crash test";

    public static void ArmFault() => s_updateThrows = 5;

    public static void ArmStart(string message) => s_startThrow = message;

    public override void Start()
    {
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

    public override void Shutdown() => s_updateThrows = 0;
}
