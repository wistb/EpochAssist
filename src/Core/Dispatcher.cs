using HarmonyLib;
using Il2Cpp;
using UnityEngine;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

// Survival clients (potions) get the frame's press before skills.
internal enum PriorityClass { Survival, Skills }

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

    public bool Holds(int slot) => Held[slot];
}

/// <summary>
/// The one place automated game input is sent from. Each frame it checks every client's pause rules (in Update, before
/// the clients run), takes their slot requests (in their Update), then arbitrates and sends (in LateUpdate): at most
/// one new press per frame, survival before skills, no press for a slot pressed less than 0.1 s ago or at or after a
/// skill the player is holding, and never more than MaxPressesPerSecond in any one second. A client that may not act has its held slots let go in the same frame.
/// It also owns the bar command patch, so the player's own skill presses are seen here.
/// </summary>
internal static class Dispatcher
{
    public const int SlotCount = 5;
    private const float PressSpacingSeconds = 0.1f;

    /// <summary>The most automated presses in any one second: AutoCast's highest second measured on
    /// five slots (43) plus 25%, so it never holds AutoCast back; it bounds every client together.</summary>
    public const int MaxPressesPerSecond = 54;

    // The game's input action for each bar slot (Ability1..5), for the manual hold check.
    private static readonly int[] s_slotActionIds =
    {
        (int)EpochInputManager.InputActions.Ability1,
        (int)EpochInputManager.InputActions.Ability2,
        (int)EpochInputManager.InputActions.Ability3,
        (int)EpochInputManager.InputActions.Ability4,
        (int)EpochInputManager.InputActions.Ability5,
    };

    private static readonly List<DispatchClient> s_clients = new();
    private static bool s_sending;
    private static UsingAbilityPlayer? s_abilityState;
    private static Il2CppRewired.Player? s_rewiredPlayer;

    /// <summary>Off when the game lacks the input system's button read (set by DispatcherFeature).</summary>
    public static bool CanReadManualHolds = true;

    /// <summary>The player pressed a skill themselves since the last frame's send.</summary>
    public static bool PlainPressThisFrame { get; private set; }

    /// <summary>When the dispatcher last sent a key-down per slot (unscaled seconds, frame).</summary>
    public static readonly float[] LastPressAt = new float[SlotCount];
    public static readonly int[] LastPressFrame = new int[SlotCount];
    /// <summary>When the player last pressed each slot themselves (unscaled seconds).</summary>
    public static readonly float[] LastManualPressAt = new float[SlotCount];

    public static IReadOnlyList<DispatchClient> Clients => s_clients;

    // The times of the last key-downs sent, for the send rate; more than any second can hold (one per slot per 0.1 s).
    private const int PressRing = 64;
    private static readonly float[] s_pressTimes = new float[PressRing];
    private static int s_pressNext, s_pressStored;

    /// <summary>Key-downs sent in the last second (key-ups, channel stops, retargets and manual presses aren't counted).</summary>
    public static int PressesLastSecond => PressesSince(Clock.Now - 1f);

    /// <summary>The most key-downs sent within any one second since ResetPressCounts.</summary>
    public static int PeakPressesPerSecond { get; private set; }

    /// <summary>Key-downs sent since ResetPressCounts.</summary>
    public static int PressesSinceReset { get; private set; }

    /// <summary>Clears the peak and the total (dev `jobs reset`, for a baseline run).</summary>
    public static void ResetPressCounts()
    {
        PeakPressesPerSecond = 0;
        PressesSinceReset = 0;
    }

    private static int PressesSince(float since)
    {
        int count = 0;
        for (int i = 0; i < s_pressStored; i++)
        {
            if (s_pressTimes[i] > since) count++;
        }
        return count;
    }

    private static void CountPress()
    {
        float now = Clock.Now;
        s_pressTimes[s_pressNext] = now;
        s_pressNext = (s_pressNext + 1) % PressRing;
        if (s_pressStored < PressRing) s_pressStored++;
        PressesSinceReset++;
        PeakPressesPerSecond = Math.Max(PeakPressesPerSecond, PressesSince(now - 1f));
    }

    public static DispatchClient Register(DispatchClient client)
    {
        s_clients.Add(client);
        // Survival first; the sort is stable, so clients of one class keep registration order.
        var ordered = s_clients.OrderBy(c => c.Priority).ToList();
        s_clients.Clear();
        s_clients.AddRange(ordered);
        return client;
    }

    public static void Unregister(DispatchClient client)
    {
        if (!s_clients.Remove(client)) return;
        Release(client);
    }

    public static bool Allowed(DispatchClient client) => client.Blocking == null;

    public static void Request(DispatchClient client, int slot, PressIntent intent, Vector3 target, Transform? hit)
    {
        if (slot < 0 || slot >= SlotCount) return;
        client.Intents[slot] = intent;
        client.Target = target;
        client.Hit = hit;
    }

    /// <summary>Points the player's channel at a new target; the slot is the channelling one.</summary>
    public static void Retarget(DispatchClient client, int slot, Vector3 target)
    {
        client.RetargetTo = target;
        client.RetargetSlot = slot;
    }

    public static void StopChannel(DispatchClient client, int slot)
    {
        if (slot >= 0 && slot < SlotCount) client.StopRequested[slot] = true;
    }

    /// <summary>Lets go of every slot held for the client now: the key-up, then the channel stop for channelled ones.</summary>
    public static void Release(DispatchClient client)
    {
        for (int i = 0; i < SlotCount; i++) ReleaseSlot(client, i);
    }

    public static FeatureStatus StatusOf(DispatchClient client) => StatusFor(client, client.Blocking);

    /// <summary>The status with one kind of the client's own rules left out (for logs that skip momentary ones).</summary>
    public static FeatureStatus StatusOf(DispatchClient client, RuleKind skip) => StatusFor(client, PauseRules.FirstActive(client, skip));

    private static FeatureStatus StatusFor(DispatchClient client, PauseRule? rule)
    {
        bool[]? slots = client.Slots?.Invoke();
        if (rule == null) return new FeatureStatus(StatusState.Active, string.Empty, slots);
        StatusState state = PauseRules.IsOff(rule) ? StatusState.Off
            : rule.Kind switch { RuleKind.Momentary => StatusState.Holding, RuleKind.Latched => StatusState.Paused, _ => StatusState.Waiting };
        return new FeatureStatus(state, rule.Reason, slots);
    }

    // Before the clients' Update: who may act this frame. A client that may not is let go of at once.
    internal static void Update()
    {
        foreach (var client in s_clients)
        {
            client.Blocking = PauseRules.FirstActive(client);
            if (client.Blocking != null) Release(client);
        }
    }

    // After the clients' Update: arbitrate the frame's requests and send.
    internal static void LateUpdate()
    {
        int manualHold = FirstManuallyHeldSlot();
        bool pressed = false;
        // At the cap a press is held back like one inside the slot spacing; clients are in survival-first order.
        bool capped = PressesLastSecond >= MaxPressesPerSecond;
        float now = Time.unscaledTime;

        foreach (var client in s_clients)
        {
            if (client.Blocking != null)
            {
                ClearRequests(client);
                continue;
            }

            for (int i = 0; i < SlotCount; i++)
            {
                if (client.StopRequested[i]) SendStopChannel(i);

                switch (client.Intents[i])
                {
                    case PressIntent.None:
                        ReleaseSlot(client, i);
                        break;
                    // Nothing is sent for a slot at or after one the player holds, not even a key-up.
                    case PressIntent.Lift when i < manualHold:
                        SendBarCommand(i, keyDown: false, client.Target, client.Hit);
                        break;
                    case PressIntent.Press when i < manualHold:
                        if (capped || now - LastPressAt[i] < PressSpacingSeconds)
                        {
                            // Too soon for this slot, or the cap: the key-up, as a slot that isn't ready gets.
                            SendBarCommand(i, keyDown: false, client.Target, client.Hit);
                        }
                        else if (!pressed)
                        {
                            SendBarCommand(i, keyDown: true, client.Target, client.Hit);
                            pressed = true;
                            client.Held[i] = true;
                            LastPressAt[i] = now;
                            LastPressFrame[i] = Time.frameCount;
                        }
                        break;
                }
            }

            if (client.RetargetTo is Vector3 target && client.RetargetSlot < manualHold) SendRetarget(target);
            ClearRequests(client);
        }
        PlainPressThisFrame = false;
    }

    private static void ClearRequests(DispatchClient client)
    {
        Array.Clear(client.Intents);
        Array.Clear(client.StopRequested);
        client.RetargetTo = null;
    }

    private static void ReleaseSlot(DispatchClient client, int slot)
    {
        if (!client.Held[slot]) return;
        client.Held[slot] = false;
        SendBarCommand(slot, keyDown: false, client.Target, client.Hit);
        bool channelled;
        try
        {
            channelled = client.Channelled?.Invoke(slot) ?? false;
        }
        catch (Exception)
        {
            channelled = true;
        }
        if (channelled) SendStopChannel(slot);
    }

    /// <summary>Lets go of everything every client holds; the dispatcher's Shutdown.</summary>
    internal static void Shutdown()
    {
        foreach (var client in s_clients) Release(client);
        s_clients.Clear();
        PlainPressThisFrame = false;
        s_abilityState = null;
        s_rewiredPlayer = null;
    }

    internal static void OnSceneChanged()
    {
        s_abilityState = null;
        s_rewiredPlayer = null;
    }

    private static UsingAbilityPlayer? AbilityState()
    {
        if (Game.Alive(s_abilityState)) return s_abilityState;
        var actor = Player.Actor;
        s_abilityState = actor != null ? actor.GetComponent<UsingAbilityPlayer>() : null;
        return s_abilityState;
    }

    // The first slot whose skill button the player is holding right now, or SlotCount if none.
    // Asked of the input system each frame, so a missed key-up can't leave a slot looking held.
    private static int FirstManuallyHeldSlot()
    {
        if (!CanReadManualHolds) return SlotCount;
        try
        {
            if (!Game.Alive(s_rewiredPlayer)) s_rewiredPlayer = Il2CppRewired.ReInput.isReady ? Il2CppRewired.ReInput.players.GetPlayer(0) : null;
            if (s_rewiredPlayer == null) return SlotCount;
            for (int i = 0; i < SlotCount; i++)
            {
                // With a toggle chord held, the skill key toggles instead of casting.
                if (ChordHeld(i)) continue;
                if (s_rewiredPlayer.GetButton(s_slotActionIds[i])) return i;
            }
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("dispatch:held", $"Dispatcher held-skill check failed: {e.Message}", 30);
        }
        return SlotCount;
    }

    private static bool ChordHeld(int slot)
    {
        foreach (var client in s_clients)
        {
            if (client.SlotChordHeld?.Invoke(slot) == true) return true;
        }
        return false;
    }

    private static void SendBarCommand(int slot, bool keyDown, Vector3 target, Transform? hit)
    {
        var processor = Game.AbilityProcessor;
        if (processor == null) return;
        s_sending = true;
        try
        {
            processor.UseBarAbilityCommand(keyDown, slot + 1, -1, target, hit, false);
            if (keyDown) CountPress();
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("dispatch:send", $"Dispatcher bar command failed: {e.Message}", 30);
        }
        finally
        {
            s_sending = false;
        }
    }

    private static void SendStopChannel(int slot)
    {
        try
        {
            var clientState = Game.AbilityProcessor?.PlayerAbilityState?.TryCast<ClientUsingAbilityState>();
            if (clientState != null)
            {
                clientState.AttemptToStopAbility(slot + 1, true);
                return;
            }
        }
        catch (Exception)
        {
            // Fall through to the player state fallback.
        }

        try
        {
            AbilityState()?.stopUsingAbility(true);
        }
        catch (Exception)
        {
            // Nothing else to try; the channel ends on its own when the key state settles.
        }
    }

    private static void SendRetarget(Vector3 target)
    {
        try
        {
            AbilityState()?.TryUpdateTargetLocationIfChannelling(target);
        }
        catch (Exception)
        {
            // Target refresh is best-effort.
        }
    }

    // Every skill key press reaches the game through this method (__0 = keyDown, __1 = ability number).
    private static bool OnRealBarCommand(bool keyDown, int abilityNumber)
    {
        int slot = abilityNumber - 1;
        if (slot < 0 || slot >= SlotCount) return true;

        foreach (var client in s_clients)
        {
            if (client.SwallowRealPress?.Invoke(slot, keyDown) == true) return false;
        }
        if (keyDown)
        {
            LastManualPressAt[slot] = Time.unscaledTime;
            PlainPressThisFrame = true;
        }
        return true;
    }

    [HarmonyPatch(typeof(UseAbilityProcessor), nameof(UseAbilityProcessor.UseBarAbilityCommand))]
    private static class UseBarAbilityCommand_Patch
    {
        private static bool Prefix(bool __0, int __1)
        {
            if (s_sending) return true;
            try
            {
                return OnRealBarCommand(__0, __1);
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("dispatch:prefix", $"Dispatcher key handling failed: {e.Message}", 30);
                return true;
            }
        }
    }

    // Automated casts are attempted constantly; don't let them spam the "cannot use skill" voice line.
    [HarmonyPatch(typeof(PlayerVoiceEventManager), nameof(PlayerVoiceEventManager.playVoiceLine))]
    private static class PlayVoiceLine_Patch
    {
        private static bool Prefix(PlayerVoiceEventManager.VoiceLineEvent __1) =>
            !s_sending || __1 != PlayerVoiceEventManager.VoiceLineEvent.CannotUseSkill;
    }
}

internal sealed class DispatcherFeature : Feature
{
    public override string Id => "dispatch";
    public override string Name => "Dispatcher";
    // Without it a skill the player holds no longer stops automation on that slot and later ones.
    private const string ManualHolds = "manual holds";

    public override string[] Needs => new[] { "game", "input" };
    public override Type[] PatchClasses => NestedPatches(typeof(Dispatcher));
    public override Requirement[] Requirements => new[]
    {
        Need<EpochInputManager>(nameof(EpochInputManager.useAbilityProcessor)),
        Need<UseAbilityProcessor>(nameof(UseAbilityProcessor.UseBarAbilityCommand)),
        Need<UseAbilityProcessor>(nameof(UseAbilityProcessor.PlayerAbilityState)),
        Need<ClientUsingAbilityState>(nameof(ClientUsingAbilityState.AttemptToStopAbility)),
        Need<UsingAbilityPlayer>(nameof(UsingAbilityPlayer.TryUpdateTargetLocationIfChannelling)),
        Need<UsingAbilityPlayer>(nameof(UsingAbilityPlayer.stopUsingAbility)),
        Need(typeof(Il2CppRewired.ReInput), nameof(Il2CppRewired.ReInput.isReady), ManualHolds),
        Need(typeof(Il2CppRewired.ReInput), nameof(Il2CppRewired.ReInput.players), ManualHolds),
        Need<Il2CppRewired.Player>(nameof(Il2CppRewired.Player.GetButton), ManualHolds),
    };

    public override void Start()
    {
        Dispatcher.CanReadManualHolds = Has(ManualHolds);
    }
    public override void Update() => Dispatcher.Update();
    public override void LateUpdate() => Dispatcher.LateUpdate();
    public override void OnSceneChanged() => Dispatcher.OnSceneChanged();
    public override void Shutdown() => Dispatcher.Shutdown();
}
