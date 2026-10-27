using UnityEngine;

namespace EpochAssist.Core;

// Survival clients (potions) get the frame's press before skills; interactions come after potions and casts, so they never
// take a frame or a press either of them wants.
internal enum PriorityClass { Survival, Skills, Interact }

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
    /// <summary>Uses a shrine, chest or ground item through the game's own click handler, only from within its reach;
    /// only a client of the Interact class may ask for it.</summary>
    public sealed record Interact(InteractTarget Target) : UiAction;
}

/// <summary>
/// What an interaction uses: the dispatcher checks at the send that the object is still there, not used yet and within
/// reach of the player's position that frame, then calls Use. Made by the feature; nothing here is kept between scans.
/// </summary>
internal sealed class InteractTarget
{
    public InteractTarget(string kind, string name, UnityEngine.Object obj, Func<Vector3> point, float reach, Func<bool> used, Action use)
    {
        Kind = kind;
        Name = name;
        Object = obj;
        Point = point;
        Reach = reach;
        Used = used;
        Use = use;
    }

    /// <summary>The kind's id, for the outcome log.</summary>
    public string Kind { get; }
    public string Name { get; }
    /// <summary>The Unity object to check with Game.Alive before the call.</summary>
    public UnityEngine.Object Object { get; }
    /// <summary>The point the game measures its reach to, this frame.</summary>
    public Func<Vector3> Point { get; }
    /// <summary>The effective reach in metres, already below the game's own.</summary>
    public float Reach { get; }
    public Func<bool> Used { get; }
    /// <summary>The kind's game call; it walks the character when made from out of reach, so only Press calls it.</summary>
    public Action Use { get; }
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

// What became of a Press (or a drink) last LateUpdate: sent, or why not. Never stored. Paused: a pause rule held the
// client (DispatchClient.PausedBy names it); ManualHold: the player holds this slot or one before it.
internal enum SendOutcome { None, Sent, Spacing, Cap, FrameTaken, ManualHold, Paused }

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
    /// <summary>The client's name in dev output and the outcome log; the feature's id unless a feature has several.</summary>
    public string Name { get => _name ?? Feature.Id; init => _name = value; }
    private readonly string? _name;
    public PriorityClass Priority { get; }
    public Toggle MasterToggle { get; }
    public PauseRule[] Rules { get; }
    /// <summary>Global rules this client acts through (by name), e.g. drinks while the menu is open.</summary>
    public string[] Ignores { get; init; } = Array.Empty<string>();

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

    /// <summary>Per slot, what became of last frame's Press request (None when it asked for none).</summary>
    public readonly SendOutcome[] Outcome = new SendOutcome[Dispatcher.SlotCount];
    /// <summary>The rule's name when an outcome is Paused.</summary>
    public string? PausedBy { get; internal set; }
    /// <summary>What became of last frame's drink request (None when it asked for none).</summary>
    public SendOutcome DrinkOutcome { get; internal set; }
    /// <summary>What became of last frame's town portal request (None when it asked for none).</summary>
    public SendOutcome PortalOutcome { get; internal set; }

    /// <summary>Since the dispatcher's press counts were reset: key-downs sent per slot, and refused Press requests by
    /// outcome (indexed by SendOutcome; drinks included).</summary>
    public readonly int[] Sent = new int[Dispatcher.SlotCount];
    public readonly int[] Refused = new int[(int)SendOutcome.Paused + 1];
    public int DrinksSent { get; internal set; }

    internal readonly bool[] Held = new bool[Dispatcher.SlotCount];
    internal readonly PressIntent[] Intents = new PressIntent[Dispatcher.SlotCount];
    internal readonly bool[] StopRequested = new bool[Dispatcher.SlotCount];
    internal Vector3 Target;
    internal Transform? Hit;
    // Each slot's own aim (slot macros can aim at different targets); Target and Hit are the last request's.
    internal readonly Vector3[] SlotTarget = new Vector3[Dispatcher.SlotCount];
    internal readonly Transform?[] SlotHit = new Transform?[Dispatcher.SlotCount];
    internal Vector3? RetargetTo;
    internal int RetargetSlot;
    internal UiAction? PendingUi;
    internal float PendingUiAt;
    internal UiResult? LastUi;
    internal bool DrinkRequested;
    internal bool PortalRequested;

    public bool Holds(int slot) => Held[slot];
}
