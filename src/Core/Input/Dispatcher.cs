using HarmonyLib;
using Il2Cpp;
using UnityEngine;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

/// <summary>
/// The one place automated game input is sent from. Each frame it checks every client's pause rules (in Update, before
/// the clients run), takes their slot requests (in their Update), then arbitrates and sends (in LateUpdate): at most
/// one new press per frame, survival before skills, no press for a slot pressed less than 0.1 s ago or at or after a
/// skill the player is holding, and never more than MaxPressesPerSecond in any one second. A client that may not act has its held slots let go in the same frame.
/// It also owns the bar command patch, so the player's own skill presses are seen here.
/// </summary>
internal static partial class Dispatcher
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
        client.PendingUi = null;
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
        RunUi(pressed);
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
