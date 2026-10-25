using UnityEngine;

namespace EpochAssist.Core;

// Survival clients (potions) get the frame's press before skills.
internal enum PriorityClass { Survival, Skills }

/// <summary>A press of the game's own UI that a client asks the dispatcher for.</summary>
internal abstract record UiAction
{
    /// <summary>Yes on the open prompt, only when its kind is known, confirmable and one of these PromptKinds ids.</summary>
    public sealed record Confirm(IReadOnlyCollection<string> Kinds) : UiAction;
    /// <summary>No on the open prompt, whatever it is.</summary>
    public sealed record Decline : UiAction;
    /// <summary>The death screen's normal Respawn button.</summary>
    public sealed record Respawn : UiAction;
    /// <summary>The container UI's own quick move (as a Ctrl-click) of the item at a slot; the game picks where it goes.
    /// Container is a ContainerID name ("INVENTORY", "STASH").</summary>
    public sealed record QuickMove(string Container, Vector2Int Position) : UiAction;
}

/// <summary>How a UI request ended: pressed, or refused with the reason.</summary>
internal readonly record struct UiResult(bool Pressed, string? Refused)
{
    public static UiResult Done => new(true, null);
    public static UiResult No(string reason) => new(false, reason);
    public override string ToString() => Pressed ? "pressed" : $"refused: {Refused}";
}

// Per slot, per frame. Press: a new key-down if allowed; Lift: the key-up (not ready yet); None: the slot isn't wanted,
// so a slot the dispatcher holds for the client is let go.
internal enum PressIntent { None, Press, Lift }

/// <summary>
/// An automation feature registered with the dispatcher: its pause rules, its master switch, and the slots the
/// dispatcher holds down for it. Made in the feature's Start, removed in its Shutdown.
/// </summary>
internal sealed class DispatchClient
{
    public DispatchClient(Feature feature, PriorityClass priority, Toggle masterToggle, PauseRule[] rules)
    {
        Feature = feature;
        Priority = priority;
        MasterToggle = masterToggle;
        Rules = rules;
    }

    public Feature Feature { get; }
    public PriorityClass Priority { get; }
    public Toggle MasterToggle { get; }
    public PauseRule[] Rules { get; }

    /// <summary>Toggled slots for the status pips; null for a client without slots.</summary>
    public Func<bool[]?>? Slots { get; init; }
    /// <summary>Whether a slot holds a channelled skill, so letting go of it also ends the channel.</summary>
    public Func<int, bool>? Channelled { get; init; }
    /// <summary>Asked for each real bar command (slot, key down); true swallows it. A slot toggle chord uses it.</summary>
    public Func<int, bool, bool>? SwallowRealPress { get; set; }
    /// <summary>True while the slot's key toggles instead of casting, so holding it isn't a manual cast.</summary>
    public Func<int, bool>? SlotChordHeld { get; set; }

    /// <summary>The rule that stopped the client at the last check, or null.</summary>
    public PauseRule? Blocking { get; internal set; }

    internal readonly bool[] Held = new bool[Dispatcher.SlotCount];
    internal readonly PressIntent[] Intents = new PressIntent[Dispatcher.SlotCount];
    internal readonly bool[] StopRequested = new bool[Dispatcher.SlotCount];
    internal Vector3 Target;
    internal Transform? Hit;
    internal Vector3? RetargetTo;
    internal int RetargetSlot;
    internal UiAction? PendingUi;
    internal float PendingUiAt;
    internal UiResult? LastUi;

    public bool Holds(int slot) => Held[slot];
}
