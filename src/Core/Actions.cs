using System.Text.RegularExpressions;

namespace EpochAssist.Core;

/// <summary>
/// A named thing the player can make happen: open the menu, toggle a slot, restart a feature. Bindings, buttons and the
/// dev bridge all run actions by name, so each control has one path. A toggle has a state getter; a run action doesn't.
/// </summary>
internal sealed class GameAction
{
    public GameAction(string name, string label, string owner, Action run, Func<bool>? state)
    {
        Name = name;
        Label = label;
        Owner = owner;
        Run = run;
        State = state;
    }

    public string Name { get; }
    public string Label { get; }
    /// <summary>The feature id that registered it, or "host" / "settings" for ones the core keeps.</summary>
    public string Owner { get; }
    public Action Run { get; }
    public Func<bool>? State { get; }
    public bool IsToggle => State != null;
}

/// <summary>
/// Every registered action. Features register theirs in Start; the host removes a feature's actions when it stops,
/// faults or is unavailable. Names are stable (the bridge and bindings refer to them): owner.verb[.arg].
/// </summary>
internal static class Actions
{
    // Lowercase segments, camelCase allowed after the first letter (hud.togglePanel).
    private static readonly Regex s_name = new(@"^[a-z0-9][a-zA-Z0-9]*(\.[a-z0-9][a-zA-Z0-9]*)+$|^panic$", RegexOptions.CultureInvariant);
    private static readonly Dictionary<string, GameAction> s_actions = new(StringComparer.Ordinal);

    /// <summary>Sorted by name.</summary>
    public static IEnumerable<GameAction> All => s_actions.Values.OrderBy(a => a.Name, StringComparer.Ordinal);

    public static GameAction? Get(string name) => s_actions.TryGetValue(name, out var action) ? action : null;

    public static void Register(string owner, string name, string label, Action run, Func<bool>? state = null)
    {
        if (!s_name.IsMatch(name))
        {
            Log.Error($"Action '{name}' not registered: names are owner.verb[.arg]");
            return;
        }
        if (s_actions.ContainsKey(name)) Log.Warn($"Action {name} registered again; the newer one replaces it");
        s_actions[name] = new GameAction(name, label, owner, run, state);
    }

    public static void RemoveOwner(string owner)
    {
        foreach (string name in s_actions.Values.Where(a => a.Owner == owner).Select(a => a.Name).ToList()) s_actions.Remove(name);
    }

    /// <summary>
    /// Runs an action on the calling (main) thread. A trigger names itself so the log shows what ran it. Errors stay
    /// here, so a failing action can't count against the feature that triggered it.
    /// </summary>
    public static string Run(string name, string? trigger = null)
    {
        if (!s_actions.TryGetValue(name, out var action)) return $"error unknown action {name}";
        if (trigger != null) Log.Info($"{trigger}: {name}");
        try
        {
            action.Run();
        }
        catch (Exception e)
        {
            Log.ErrorThrottled($"action:{name}", $"Action {name} failed: {e}");
            return $"error action {name} failed: {e.Message}";
        }
        return $"ok run {name}";
    }

    /// <summary>A toggle's state, or null for a run action or a getter that throws.</summary>
    public static bool? StateOf(GameAction action)
    {
        if (action.State == null) return null;
        try
        {
            return action.State();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Clear() => s_actions.Clear();
}
