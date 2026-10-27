using EpochAssist.Core;
using EpochAssist.Ui;

namespace EpochAssist.Dev;

internal static partial class DevCommands
{
    // The dev overlays and logs as switches on General › Diagnostics › Debug, bound to the same state as their verbs.
    private static IReadOnlyList<ValueToggle>? s_debugSwitches;

    public static IReadOnlyList<ValueToggle> DebugSwitches() => s_debugSwitches ??= new[]
    {
        new ValueToggle("Object names and reach", () => InteractCommands.Drawing, InteractCommands.SetDraw),
        new ValueToggle("Click trace (log)", () => InteractTraceFeature.On, on => InteractTraceFeature.On = on),
        new ValueToggle("Dispatch log", () => Dispatcher.Trace != null, on => Dispatcher.Trace = on ? line => Log.Info($"dispatch: {line}") : null),
        new ValueToggle("Minimap markers", () => s_drawMinimap, on => SetDraw(ref s_drawMinimap, on)),
        new ValueToggle("Edge arrows for rares", () => s_drawArrows, on => SetDraw(ref s_drawArrows, on)),
        new ValueToggle("Enemy health bars", () => s_drawBars, on => SetDraw(ref s_drawBars, on)),
    };

    private static void SetDraw(ref bool flag, bool on)
    {
        flag = on;
        if (on) World.WakeFor(600f);
    }
}
