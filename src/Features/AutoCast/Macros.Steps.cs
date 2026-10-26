using EpochAssist.Core;
using UnityEngine;
using S = EpochAssist.Features.AutoCastSettings;

namespace EpochAssist.Features;

// Where a run's current step is: just begun, waiting (with a reason), a press or drink asked for (its outcome comes
// next frame), confirming a cast, holding a key, or channelling.
internal enum StepPhase { Start, Waiting, Pressing, Confirming, Holding, Channelling }

/// <summary>One run of a library macro: its place in the sequence, the current step's state, and each step's result.</summary>
internal sealed class MacroRun
{
    public MacroRun(MacroSet set, Macro macro, float now, bool testFire)
    {
        Set = set;
        Macro = macro;
        StartedAt = now;
        TestFire = testFire;
    }

    public MacroSet Set { get; }
    public Macro Macro { get; }
    public float StartedAt { get; }
    public bool TestFire { get; }
    /// <summary>The first sent key-down or drink of the run; null before.</summary>
    public float? FiredAt { get; internal set; }
    public int StepIndex { get; private set; }
    public MacroStep? Step => StepIndex < Macro.Actions.Count ? Macro.Actions[StepIndex] : null;
    public StepPhase Phase { get; internal set; }
    /// <summary>What the step waits on ("not ready", "refused: spacing", "wait 0.4 s"), or null.</summary>
    public string? Waiting { get; internal set; }
    /// <summary>Why the run ended or was cancelled ("done", "death", "taken over by …"); null while it runs.</summary>
    public string? Cause { get; internal set; }
    /// <summary>Each finished step's result: "cast", "sent", "skipped (not ready)", "off".</summary>
    public List<string> Results { get; } = new();
    public int Presses { get; internal set; }

    internal float StepStartedAt, PhaseUntil, SentAt;
    internal float LastSendAt = float.NegativeInfinity;
    // The bar slot (1–5) of the cast step under way, or 0; the slot (0–4) held for a hold or channel, or -1.
    internal int CastSlot;
    internal int ChannelSlot = -1;
    internal float? ChargesBefore;
    internal bool? ReadyBefore;
    internal readonly TreeState Until = new();

    /// <summary>A higher macro may take over at a step boundary, or while a wait or wait until waits.</summary>
    public bool CanBeTakenOver => Phase == StepPhase.Start || (Phase == StepPhase.Waiting && Step?.Step is "wait" or "waitUntil");

    internal void Next(string result)
    {
        Results.Add(result);
        StepIndex++;
        Phase = StepPhase.Start;
        Waiting = null;
        CastSlot = 0;
        ChannelSlot = -1;
        Presses = 0;
        ChargesBefore = null;
        ReadyBefore = null;
        Until.Reset();
    }
}

internal static partial class Macros
{
    // The run's current step goes on: at most one request a frame; steps that finish without sending (off, wait over,
    // message) let the next one go in the same frame.
    private static void Advance(Engine engine, MacroRun run, float now)
    {
        for (int guard = 0; guard <= Macro.MaxSteps; guard++)
        {
            if (engine.Run != run) return;
            var step = run.Step;
            if (step == null)
            {
                Finish(engine, run, "done");
                return;
            }
            if (!step.On)
            {
                run.Next("off");
                continue;
            }
            if (step.Invalid is { } invalid)
            {
                // A step this build doesn't know stops the run; a bad one is skipped.
                if (!step.Known && step.Raw == null)
                {
                    run.Results.Add("stopped (needs a newer version)");
                    Finish(engine, run, "needs a newer version");
                    return;
                }
                run.Next($"skipped ({invalid})");
                continue;
            }
            if (run.Phase == StepPhase.Start) run.StepStartedAt = now;
            bool finished = step.Step switch
            {
                "cast" => Cast(engine, run, step, now),
                "drink" => Drink(engine, run, step, now),
                "portal" => Portal(engine, run, now),
                "wait" => Wait(run, step, now),
                "waitUntil" => WaitUntil(engine, run, step, now),
                "message" => Message(engine, run, step, now),
                _ => Skip(run, "unknown step"),
            };
            if (!finished) return;
        }
    }

    private static bool Skip(MacroRun run, string reason)
    {
        run.Next($"skipped ({reason})");
        return true;
    }

    private static bool Done(MacroRun run, string result)
    {
        run.Next(result);
        return true;
    }

    // Waits on a reason until the step's time-out, then skips with it.
    private static bool WaitFor(MacroRun run, string reason, float now, float timeout)
    {
        run.Phase = StepPhase.Waiting;
        run.Waiting = reason;
        return now - run.StepStartedAt >= timeout && Skip(run, reason);
    }

    private static void Sent(Engine engine, MacroRun run, float now)
    {
        run.Presses++;
        run.SentAt = now;
        run.LastSendAt = now;
        if (run.FiredAt == null)
        {
            run.FiredAt = now;
            engine.Fired[run.Macro.Id] = now;
        }
    }

    // ---- cast ----

    private static bool Cast(Engine engine, MacroRun run, MacroStep step, float now)
    {
        var library = engine.Library;
        switch (run.Phase)
        {
            case StepPhase.Pressing:
            {
                int slot = run.CastSlot - 1;
                var outcome = slot >= 0 ? library.Outcome[slot] : SendOutcome.None;
                if (outcome == SendOutcome.Sent)
                {
                    Sent(engine, run, now);
                    switch (step.Mode)
                    {
                        case "hold":
                            run.Phase = StepPhase.Holding;
                            run.PhaseUntil = now + step.Ms / 1000f;
                            run.ChannelSlot = slot;
                            Keep(engine, run, step, slot);
                            return false;
                        case "channel":
                            run.Phase = StepPhase.Channelling;
                            run.PhaseUntil = now + step.Ms / 1000f;
                            run.ChannelSlot = slot;
                            Keep(engine, run, step, slot);
                            return false;
                        default:
                            // Tap: nothing asked this frame, so the key goes up.
                            run.Phase = StepPhase.Confirming;
                            return false;
                    }
                }
                run.Waiting = outcome == SendOutcome.None ? "not sent" : "refused: " + Dispatcher.OutcomeWord(library, outcome);
                run.Phase = StepPhase.Waiting;
                return TryPress(engine, run, step, now);
            }
            case StepPhase.Confirming:
                return Confirm(engine, run, step, now);
            case StepPhase.Holding:
                if (now < run.PhaseUntil)
                {
                    Keep(engine, run, step, run.ChannelSlot);
                    return false;
                }
                return LetGo(engine, run, "cast");
            case StepPhase.Channelling:
            {
                bool goOn = now < run.PhaseUntil;
                if (goOn && step.Until is { Entries.Count: > 0 } until) goOn = Conditions.Check(until, ContextOf(run.Macro), run.Until).Passed;
                if (!goOn) return LetGo(engine, run, "cast");
                Keep(engine, run, step, run.ChannelSlot);
                var (target, _) = Aim(step);
                Dispatcher.Retarget(library, run.ChannelSlot, target);
                return false;
            }
            default:
                return TryPress(engine, run, step, now);
        }
    }

    // Asks for the step's key-down if nothing holds it back; waits (until the time-out) on what does.
    private static bool TryPress(Engine engine, MacroRun run, MacroStep step, float now)
    {
        float timeout = step.TimeoutMs / 1000f;
        if (now - run.LastSendAt < SendGapSeconds) return WaitFor(run, "spacing", now, timeout);
        int number = PlayerModel.SlotOf(step.Skill);
        if (number == PlayerModel.NotOnBar) return Skip(run, "not on the bar");
        if (number == PlayerModel.BarUnknown) return Skip(run, "bar unknown");
        run.CastSlot = number;
        // The first holder keeps a slot: a slot macro channelling it isn't interrupted.
        if (AutoCast.SlotChannelHeld(number - 1)) return WaitFor(run, $"held by slot macro {number}", now, timeout);
        var slot = PlayerModel.Slot(number);
        if (slot?.Ready == false)
        {
            // A re-press of an unconfirmed cast doesn't wait for readiness that went away: it was the cast.
            if (run.Presses > 0) return Done(run, "cast");
            return WaitFor(run, "not ready", now, timeout);
        }
        if (now - run.StepStartedAt >= timeout && run.Presses == 0 && run.Waiting != null) return Skip(run, run.Waiting);
        var (target, hit) = Aim(step);
        Dispatcher.Request(engine.Library, number - 1, PressIntent.Press, target, hit);
        run.ChargesBefore = slot?.Charges;
        run.ReadyBefore = slot?.Ready;
        run.Phase = StepPhase.Pressing;
        return false;
    }

    // A tap is confirmed by the cast signal, else by the skill's own reads changing within 0.5 s; still ready after
    // that, it is pressed again (3 presses at most); with nothing to confirm it by, it counts as sent (R11).
    private static bool Confirm(Engine engine, MacroRun run, MacroStep step, float now)
    {
        var slot = PlayerModel.Slot(run.CastSlot);
        if (slot == null || slot.AbilityId != step.Skill) return Done(run, "sent");
        bool signal = PlayerModel.CastSignal;
        bool confirmed = (signal && PlayerModel.LastCastAt(run.CastSlot) >= run.SentAt - 0.05f)
            || (run.ChargesBefore is float before && slot.Charges is float after && (MathF.Floor(after + 1e-4f) <= MathF.Floor(before + 1e-4f) - 1f || (before >= 1f && after < 1f)))
            || (run.ReadyBefore == true && slot.Ready == false)
            || (slot.Channelled && PlayerModel.Channelling == true);
        if (confirmed) return Done(run, "cast");
        bool canConfirm = signal || slot.MaxCharges is > 0 || slot.Channelled;
        if (!canConfirm) return Done(run, "sent");
        if (now - run.SentAt < ConfirmSeconds) return false;
        if (run.Presses >= MaxPressesPerStep || slot.Ready == false) return Done(run, "sent");
        run.Phase = StepPhase.Waiting;
        run.Waiting = "not confirmed, pressing again";
        return TryPress(engine, run, step, now);
    }

    // Keeps a held or channelled key down: a key-up request (sent as the game sees from a held key) keeps the slot held.
    private static void Keep(Engine engine, MacroRun run, MacroStep step, int slot)
    {
        var (target, hit) = Aim(step);
        Dispatcher.Request(engine.Library, slot, PressIntent.Lift, target, hit);
    }

    // Lets go of a held or channelled key now (the channel stop goes with it), then the step is done.
    private static bool LetGo(Engine engine, MacroRun run, string result)
    {
        Dispatcher.Release(engine.Library);
        return Done(run, result);
    }

    // Where a cast aims: the cursor point (with what it hits), the nearest enemy within the step's range, or the enemy
    // under the cursor; with no enemy, the cursor.
    private static (Vector3 Target, Transform? Hit) Aim(MacroStep step)
    {
        if (step.Aim is "nearest" or "hovered")
        {
            var enemy = step.Aim == "hovered" ? AutoCast.HoveredEnemy() : World.Nearest(new WorldQuery { Kind = WorldKind.Enemy, Alive = true, Active = true, Range = step.AimRange });
            if (enemy is { Gone: false, Dead: false }) return (enemy.Position, null);
        }
        Vector3? point = AutoCast.CursorPoint(out var hit);
        if (point is Vector3 p) return (p, hit);
        return (Player.Transform is Transform t ? t.position : Vector3.zero, null);
    }

    // ---- drink ----

    private static bool Drink(Engine engine, MacroRun run, MacroStep step, float now)
    {
        float timeout = step.TimeoutMs / 1000f;
        if (run.Phase == StepPhase.Pressing)
        {
            var outcome = engine.Drinks.DrinkOutcome;
            if (outcome == SendOutcome.Sent)
            {
                Sent(engine, run, now);
                return Done(run, "drunk");
            }
            run.Phase = StepPhase.Waiting;
            if (outcome == SendOutcome.Spacing) run.Waiting = "last drink still landing";
            else run.Waiting = outcome == SendOutcome.None ? "not sent" : "refused: " + Dispatcher.OutcomeWord(engine.Drinks, outcome);
            if (now - run.StepStartedAt >= timeout) return Skip(run, run.Waiting);
        }
        if (!Dispatcher.CanDrink) return Skip(run, "drinking isn't available in this mode");
        if (PlayerModel.Potions?.Charges == 0) return Skip(run, "no potions");
        if (now - run.LastSendAt < SendGapSeconds) return WaitFor(run, "spacing", now, timeout);
        Dispatcher.RequestDrink(engine.Drinks);
        run.Phase = StepPhase.Pressing;
        return false;
    }

    // ---- portal ----

    // A town portal, only from a key-triggered macro (no unattended zone change) and where the game's command exists.
    private static bool Portal(Engine engine, MacroRun run, float now)
    {
        if (run.Phase == StepPhase.Pressing)
        {
            var outcome = engine.Library.PortalOutcome;
            if (outcome == SendOutcome.Sent)
            {
                Sent(engine, run, now);
                return Done(run, "portal opened");
            }
            run.Phase = StepPhase.Waiting;
            run.Waiting = "refused: " + Dispatcher.OutcomeWord(engine.Library, outcome);
            if (now - run.StepStartedAt >= 1f) return Skip(run, run.Waiting);
        }
        if (!run.Macro.Trigger.NeedsKey && !run.TestFire) return Skip(run, "only in key-triggered macros");
        if (!Dispatcher.CanPortal) return Skip(run, "town portal isn't available in this mode");
        if (now - run.LastSendAt < SendGapSeconds) return WaitFor(run, "spacing", now, 1f);
        var (target, _) = Aim(MacroStep.New("cast"));
        Dispatcher.RequestPortal(engine.Library, target);
        run.Phase = StepPhase.Pressing;
        return false;
    }

    // ---- wait, wait until, message ----

    private static bool Wait(MacroRun run, MacroStep step, float now)
    {
        if (run.Phase == StepPhase.Start)
        {
            run.Phase = StepPhase.Waiting;
            run.PhaseUntil = now + step.Ms / 1000f;
        }
        if (now >= run.PhaseUntil) return Done(run, "waited");
        run.Waiting = "wait";
        return false;
    }

    private static bool WaitUntil(Engine engine, MacroRun run, MacroStep step, float now)
    {
        if (run.Phase == StepPhase.Start)
        {
            run.Phase = StepPhase.Waiting;
            run.PhaseUntil = now + step.TimeoutMs / 1000f;
        }
        if (step.Until != null && Conditions.Check(step.Until, ContextOf(run.Macro), run.Until).Passed) return Done(run, "condition met");
        if (now < run.PhaseUntil)
        {
            run.Waiting = "until";
            return false;
        }
        if (step.OnTimeout == "go") return Done(run, "timed out, went on");
        run.Results.Add("timed out, stopped");
        Finish(engine, run, "wait until timed out");
        return false;
    }

    private static bool Message(Engine engine, MacroRun run, MacroStep step, float now)
    {
        // Once per 2 s per macro, however often it runs.
        if (engine.LastMessage.TryGetValue(run.Macro.Id, out float last) && now - last < MessageGapSeconds) return Done(run, "not shown (too soon)");
        engine.LastMessage[run.Macro.Id] = now;
        Toasts.Show("macros", step.Message, ToastKind.Info);
        return Done(run, "shown");
    }

    /// <summary>What the run's step waits on now, with numbers ("wait 0.4 s left", "until: 1.2 s to time-out"), for `why`.</summary>
    internal static string RunWaiting(MacroRun run)
    {
        float now = Clock.Now;
        return run.Phase switch
        {
            StepPhase.Pressing => "asked for a press",
            StepPhase.Confirming => $"confirming ({ArgSpec.Format(now - run.SentAt)} s)",
            StepPhase.Holding => $"holding, {ArgSpec.Format(Math.Max(0f, run.PhaseUntil - now))} s left",
            StepPhase.Channelling => $"channelling, {ArgSpec.Format(Math.Max(0f, run.PhaseUntil - now))} s left at most",
            StepPhase.Waiting when run.Waiting == "wait" => $"wait {ArgSpec.Format(Math.Max(0f, run.PhaseUntil - now))} s left",
            StepPhase.Waiting when run.Waiting == "until" => $"until: {ArgSpec.Format(Math.Max(0f, run.PhaseUntil - now))} s to time-out",
            StepPhase.Waiting => run.Waiting ?? "waiting",
            _ => "starting",
        };
    }
}
