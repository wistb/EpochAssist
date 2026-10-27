using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Dev;

/// <summary>
/// The game's own input switch (EpochInputManager.forceDisableInput): the game turns it on when the window loses focus,
/// and the mod's menu and HUD lock and the bridge's batches turn it too. The game writes the field directly, so it is
/// polled once a frame and each change is kept with what was going on, for the `inputblock` verb.
/// </summary>
internal static class InputBlockWatch
{
    private const int MaxEntries = 30;

    private static readonly List<string> s_log = new();
    private static bool? s_last;

    /// <summary>The bridge command that ran last, for the log.</summary>
    public static string LastCommand = "none";

    public static void Update()
    {
        var input = Game.Input;
        if (!Game.Alive(input)) return;
        bool on = input!.forceDisableInput;
        if (s_last == on) return;
        if (s_last != null)
        {
            if (s_log.Count == MaxEntries) s_log.RemoveAt(0);
            s_log.Add($"f{Time.frameCount} {(on ? "on" : "off")}: scene {GameState.ActiveSceneName}, focus real {InputState.RealFocused}, "
                + $"bridge input {(VirtualInput.Active ? "active" : "idle")}, mod lock {(InputGate.OwnsLock ? "ours" : InputGate.Blocking ? "wanted" : "none")}, "
                + $"after '{LastCommand}'");
        }
        s_last = on;
    }

    public static string Describe()
    {
        var input = Game.Input;
        string now = Game.Alive(input) ? (input!.forceDisableInput ? "on" : "off") : "no input manager";
        return $"inputblock: {now}, mod lock {(InputGate.OwnsLock ? "ours" : "none")}; " + (s_log.Count == 0 ? "no changes seen" : string.Join(" | ", s_log));
    }
}
