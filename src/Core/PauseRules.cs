using UnityEngine;

namespace EpochAssist.Core;

// Runtime only. Global: shared by every automation feature; Momentary: the feature's own, ends when its trigger does
// (a key held); Latched: the feature's own, stays until its clear condition (a manual skill press after a zone change).
internal enum RuleKind { Global, Momentary, Latched }

/// <summary>
/// One reason automation must not act. Active answers true, false, or null when its signal can't be read (an optional
/// game member is missing): an unknown rule is skipped and shown as unknown by the bridge.
/// </summary>
internal sealed class PauseRule
{
    private readonly Func<string> _reason;
    private readonly Func<DispatchClient, bool?> _active;

    public PauseRule(string name, string reason, RuleKind kind, Func<bool> active) : this(name, () => reason, kind, active) { }

    public PauseRule(string name, Func<string> reason, RuleKind kind, Func<bool> active)
    {
        Name = name;
        _reason = reason;
        Kind = kind;
        _active = _ => active();
    }

    internal PauseRule(string name, string reason, RuleKind kind, Func<DispatchClient, bool?> active)
    {
        Name = name;
        _reason = () => reason;
        Kind = kind;
        _active = active;
    }

    public string Name { get; }
    public RuleKind Kind { get; }
    public string Reason => _reason();

    /// <summary>A rule that throws counts as unknown, so one broken signal can't stop the others.</summary>
    public bool? Active(DispatchClient client)
    {
        try
        {
            return _active(client);
        }
        catch (Exception e)
        {
            Log.ErrorThrottled($"rule:{Name}", $"Pause rule {Name} failed: {e.Message}", 30);
            return null;
        }
    }
}

/// <summary>
/// The ranked reasons automation stops: the global rules below, in this order, then each client's own in the order it
/// registered them. The first active one wins and is what the status shows.
/// </summary>
internal static class PauseRules
{
    /// <summary>A key capture is open (set by the hotkey layer).</summary>
    public static Func<bool> CaptureActive = () => false;

    public static readonly PauseRule[] Global =
    {
        new("disabled", "off", RuleKind.Global, c => !c.MasterToggle.Value),
        new("no-player", "no player", RuleKind.Global, _ => !GameState.InGame),
        new("no-input", "game input not ready", RuleKind.Global, _ => Game.Input == null || Game.AbilityProcessor == null),
        new("loading", "loading", RuleKind.Global, _ => GameState.CanReadLoading ? GameState.Loading : null),
        new("dead", "dead", RuleKind.Global, _ => !GameState.PlayerAlive),
        new("game-menu", "game menu open", RuleKind.Global, _ => GameState.CanReadPauseMenu ? GameState.GamePaused : null),
        new("time-stopped", "game paused", RuleKind.Global, _ => Time.timeScale == 0f),
        new("ui", "menu open", RuleKind.Global, _ => InputGate.Menu || InputGate.Hud),
        // Without the chat flag, typing in a game field or ours is still seen; only "not typing" is unsure.
        new("typing", "typing", RuleKind.Global, _ => GameState.Typing ? true : GameState.CanReadChat ? false : null),
        new("capture", "setting a key", RuleKind.Global, _ => CaptureActive()),
        new("unfocused", "window not focused", RuleKind.Global, _ => !Application.isFocused),
    };

    /// <summary>Rules a status shows as off rather than waiting.</summary>
    public static bool IsOff(PauseRule rule) => rule == Global[0];

    /// <summary>The first active rule for the client, or null when it may act. skip leaves out one kind of the client's own.</summary>
    public static PauseRule? FirstActive(DispatchClient client, RuleKind? skip = null)
    {
        foreach (var rule in Global)
        {
            if (rule.Active(client) == true) return rule;
        }
        foreach (var rule in client.Rules)
        {
            if (rule.Kind != skip && rule.Active(client) == true) return rule;
        }
        return null;
    }

    /// <summary>Every rule with its state, for the bridge.</summary>
    public static IEnumerable<(PauseRule Rule, bool? Active)> States(DispatchClient client) =>
        Global.Concat(client.Rules).Select(r => (r, r.Active(client)));
}
