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

    /// <summary>The most automated presses in any one second: the slot macros' highest second measured on
    /// five slots (43) plus 25%, so it never holds SlotMacros back; it bounds every client together.</summary>
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

    /// <summary>Dev only: told every request's outcome (the dev `dispatch log`); null otherwise, so nothing is built.</summary>
    internal static Action<string>? Trace { get; set; }

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

    /// <summary>Key-downs sent per slot since ResetPressCounts, with the spacing between a slot's presses (seconds).</summary>
    public static readonly int[] SlotPresses = new int[SlotCount];
    public static readonly float[] SlotGapSum = new float[SlotCount];
    public static readonly float[] SlotMinGap = Enumerable.Repeat(float.MaxValue, SlotCount).ToArray();

    /// <summary>Clears the peak and the totals (dev `jobs reset`, for a baseline run).</summary>
    public static void ResetPressCounts()
    {
        PeakPressesPerSecond = 0;
        PressesSinceReset = 0;
        Array.Clear(SlotPresses);
        Array.Clear(SlotGapSum);
        Array.Fill(SlotMinGap, float.MaxValue);
        foreach (var client in s_clients)
        {
            Array.Clear(client.Sent);
            Array.Clear(client.Refused);
            client.DrinksSent = 0;
        }
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

    private static void CountSlotPress(int slot, float now)
    {
        if (SlotPresses[slot] > 0)
        {
            float gap = now - LastPressAt[slot];
            SlotGapSum[slot] += gap;
            SlotMinGap[slot] = Math.Min(SlotMinGap[slot], gap);
        }
        SlotPresses[slot]++;
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
        client.SlotTarget[slot] = target;
        client.SlotHit[slot] = hit;
    }

    /// <summary>When the last drink was sent, by any client (unscaled seconds); negative infinity before the first.</summary>
    public static float LastDrinkAt { get; private set; } = float.NegativeInfinity;

    /// <summary>Asks for one potion drink this frame. It is sent in LateUpdate if the client may act, no press went this
    /// frame, the cap allows and no earlier drink is still landing; it counts in the send rate. The outcome is in
    /// DrinkOutcome next frame. Meant for survival clients, which get the frame's press first.</summary>
    public static void RequestDrink(DispatchClient client) => client.DrinkRequested = true;

    // A drink sent and not yet seen landing: the potion count it was sent at and the health then, and when the count
    // went down. No time limit between drinks otherwise: only a second drink for the same need is held back (online the
    // count and the heal come from the server a moment after the drink).
    private const float DrinkSettleSeconds = 0.25f, DrinkLandTimeout = 1.5f;
    private static bool s_drinkInFlight;
    private static int? s_flightCharges;
    private static float? s_flightHealth;
    private static float s_flightDroppedAt = float.NaN;

    /// <summary>Whether a drink sent earlier is still landing (its potion and heal not seen yet); checked when read, since
    /// a macro held back by it requests nothing that would check it.</summary>
    public static bool DrinkLanding
    {
        get
        {
            UpdateDrinkInFlight(Clock.Now);
            return s_drinkInFlight;
        }
    }

    // Clears the drink in flight once the count went down and the heal showed (or a short settle passed, for a heal at
    // full health), or when it never landed within the time-out.
    private static void UpdateDrinkInFlight(float now)
    {
        if (!s_drinkInFlight) return;
        if (now - LastDrinkAt >= DrinkLandTimeout)
        {
            s_drinkInFlight = false;
            return;
        }
        int? charges = PlayerModel.Potions?.Charges;
        if (charges is not int count || s_flightCharges is not int before || count >= before) return;
        if (float.IsNaN(s_flightDroppedAt)) s_flightDroppedAt = now;
        bool healed = PlayerModel.Vitals?.Health is float health && s_flightHealth is float was && health > was;
        if (healed || now - s_flightDroppedAt >= DrinkSettleSeconds) s_drinkInFlight = false;
    }

    /// <summary>Asks for one town portal this frame, as the portal key opens it (GF-76), at the target; the outcome is in
    /// PortalOutcome next frame.</summary>
    public static void RequestPortal(DispatchClient client, Vector3 target)
    {
        client.PortalRequested = true;
        client.Target = target;
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

    // After the clients' Update: arbitrate the frame's requests and send. Each Press and drink request's outcome is kept
    // on its client for the next frame.
    internal static void LateUpdate()
    {
        int manualHold = FirstManuallyHeldSlot();
        bool pressed = false;
        // At the cap a press is held back like one inside the slot spacing; clients are in survival-first order.
        bool capped = PressesLastSecond >= MaxPressesPerSecond;
        float now = Time.unscaledTime;

        foreach (var client in s_clients)
        {
            Array.Clear(client.Outcome);
            client.DrinkOutcome = SendOutcome.None;
            client.PortalOutcome = SendOutcome.None;
            if (client.Blocking != null)
            {
                client.PausedBy = client.Blocking.Name;
                for (int i = 0; i < SlotCount; i++)
                {
                    if (client.Intents[i] == PressIntent.Press) Record(client, i, SendOutcome.Paused, now);
                }
                if (client.DrinkRequested) RecordDrink(client, SendOutcome.Paused, now);
                if (client.PortalRequested) client.PortalOutcome = SendOutcome.Paused;
                ClearRequests(client);
                continue;
            }
            client.PausedBy = null;

            // A drink comes before the client's skill presses and takes the frame's press.
            if (client.DrinkRequested && Drink(client, now, capped, pressed)) pressed = true;
            if (client.PortalRequested)
            {
                // As a key press of the portal key: it takes the frame's press and counts in the send rate.
                client.PortalOutcome = capped ? SendOutcome.Cap : pressed ? SendOutcome.FrameTaken : SendOutcome.Sent;
                if (client.PortalOutcome == SendOutcome.Sent && SendPortal(client.Target))
                {
                    pressed = true;
                    CountPress();
                }
                Trace?.Invoke(FormattableString.Invariant($"{client.Name} portal {OutcomeWord(client, client.PortalOutcome)} f={Time.frameCount} t={now:F3}"));
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
                        SendBarCommand(i, keyDown: false, client.SlotTarget[i], client.SlotHit[i]);
                        break;
                    case PressIntent.Press when i < manualHold:
                        if (capped || now - LastPressAt[i] < PressSpacingSeconds)
                        {
                            // Too soon for this slot, or the cap: the key-up, as a slot that isn't ready gets.
                            SendBarCommand(i, keyDown: false, client.SlotTarget[i], client.SlotHit[i]);
                            Record(client, i, capped ? SendOutcome.Cap : SendOutcome.Spacing, now);
                        }
                        else if (!pressed)
                        {
                            SendBarCommand(i, keyDown: true, client.SlotTarget[i], client.SlotHit[i]);
                            pressed = true;
                            client.Held[i] = true;
                            CountSlotPress(i, now);
                            LastPressAt[i] = now;
                            LastPressFrame[i] = Time.frameCount;
                            Record(client, i, SendOutcome.Sent, now);
                        }
                        else
                        {
                            Record(client, i, SendOutcome.FrameTaken, now);
                        }
                        break;
                    case PressIntent.Press:
                        Record(client, i, SendOutcome.ManualHold, now);
                        break;
                }
            }

            if (client.RetargetTo is Vector3 target && client.RetargetSlot < manualHold) SendRetarget(target);
            ClearRequests(client);
        }
        PlainPressThisFrame = false;
        RunUi(pressed);
    }

    // A drink when no earlier drink of any client is still landing, the cap allows and no press went this frame; true
    // when it was sent.
    private static bool Drink(DispatchClient client, float now, bool capped, bool pressed)
    {
        UpdateDrinkInFlight(now);
        SendOutcome outcome = capped ? SendOutcome.Cap
            : pressed ? SendOutcome.FrameTaken
            : s_drinkInFlight ? SendOutcome.Spacing
            : SendOutcome.Sent;
        if (outcome == SendOutcome.Sent)
        {
            s_flightCharges = PlayerModel.Potions?.Charges;
            s_flightHealth = PlayerModel.Vitals?.Health;
            s_flightDroppedAt = float.NaN;
            s_drinkInFlight = true;
            SendDrink();
            LastDrinkAt = now;
            client.DrinksSent++;
        }
        RecordDrink(client, outcome, now);
        return outcome == SendOutcome.Sent;
    }

    private static void Record(DispatchClient client, int slot, SendOutcome outcome, float now)
    {
        client.Outcome[slot] = outcome;
        if (outcome == SendOutcome.Sent) client.Sent[slot]++;
        else client.Refused[(int)outcome]++;
        // A sent press says where it aimed, so send points can be compared across builds.
        Trace?.Invoke(outcome == SendOutcome.Sent
            ? FormattableString.Invariant($"{client.Name} slot {slot + 1} {OutcomeWord(client, outcome)} at {client.SlotTarget[slot].x:F1},{client.SlotTarget[slot].z:F1} f={Time.frameCount} t={now:F3}")
            : FormattableString.Invariant($"{client.Name} slot {slot + 1} {OutcomeWord(client, outcome)} f={Time.frameCount} t={now:F3}"));
    }

    private static void RecordDrink(DispatchClient client, SendOutcome outcome, float now)
    {
        client.DrinkOutcome = outcome;
        if (outcome != SendOutcome.Sent) client.Refused[(int)outcome]++;
        Trace?.Invoke(FormattableString.Invariant($"{client.Name} drink {OutcomeWord(client, outcome)} f={Time.frameCount} t={now:F3}"));
    }

    /// <summary>"sent", "spacing", "paused (hold-key)": an outcome as dev output shows it.</summary>
    public static string OutcomeWord(DispatchClient client, SendOutcome outcome) => outcome switch
    {
        SendOutcome.Sent => "sent",
        SendOutcome.Spacing => "spacing",
        SendOutcome.Cap => "cap",
        SendOutcome.FrameTaken => "frame taken",
        SendOutcome.ManualHold => "manual hold",
        SendOutcome.Paused => $"paused ({client.PausedBy})",
        _ => "none",
    };

    private static void ClearRequests(DispatchClient client)
    {
        client.DrinkRequested = false;
        client.PortalRequested = false;
        Array.Clear(client.Intents);
        Array.Clear(client.StopRequested);
        client.RetargetTo = null;
    }

    private static void ReleaseSlot(DispatchClient client, int slot)
    {
        if (!client.Held[slot]) return;
        client.Held[slot] = false;
        SendBarCommand(slot, keyDown: false, client.SlotTarget[slot], client.SlotHit[slot]);
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
        LastDrinkAt = float.NegativeInfinity;
        s_drinkInFlight = false;
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
