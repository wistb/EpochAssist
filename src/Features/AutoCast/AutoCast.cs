using EpochAssist.Core;
using Il2Cpp;
using UnityEngine;
using Feature = EpochAssist.Core.Feature;
using S = EpochAssist.Features.AutoCastSettings;

namespace EpochAssist.Features;

/// <summary>
/// Per-slot skill autocast. Hold the modifier key and press a skill's own keybind to toggle
/// autocast for that slot. Casts are requested from the dispatcher, which checks the pause rules
/// and sends them through the game's UseAbilityProcessor, the entry point a real key press uses.
/// Toggles come from the hotkey layer, which reads the keys bound to each slot, because the game drops
/// the skill press when modifier+key is a binding of its own (Ctrl+F is its search field).
/// </summary>
internal static partial class AutoCast
{
    private struct Slot
    {
        public Ability? Ability;
        public bool Channelled;
    }

    private const int SlotCount = Dispatcher.SlotCount;
    private const int HumanFormKey = -1;
    private const float SlotRefreshIntervalSeconds = 1f;
    private const float StatusLogIntervalSeconds = 0.5f;
    private const float EnemyScanIntervalSeconds = 0.15f;
    private const int RangeRingSegments = 64;
    private const float RangeRingWidth = 2f;
    private static readonly Color s_rangeRingCastingColor = new(0.35f, 1f, 0.45f, 0.75f);
    private static readonly Color s_rangeRingHoldingColor = new(1f, 1f, 1f, 0.4f);

    private static readonly bool[] s_noToggles = new bool[SlotCount];
    private const string SlotsKey = "autocast.slots";

    /// <summary>
    /// Everything one run of the feature changes. Made in Start and dropped in Shutdown once its effects on the game
    /// are undone, so a restart begins with every slot off.
    /// </summary>
    private sealed class Run
    {
        public readonly Slot[] Slots = new Slot[SlotCount];
        // Toggles are remembered per form so a transformed skill bar keeps its own set.
        public readonly Dictionary<int, bool[]> TogglesByForm = new();
        // Slots whose current bar command has already toggled, so one press toggles once.
        public readonly bool[] ToggleKeyDown = new bool[SlotCount];
        public DispatchClient? Client;
        // Library macro runs (skills, registered before the slots client so a run's press goes first) and their drink
        // steps (survival); they share the slots client's feature rules but not the every-slot list.
        public DispatchClient? Library, Drinks;
        public bool Active;
        // Set by a zone change; cleared by a skill press of the player's own or by switching a slot on.
        public bool ZonePaused;
        // Set whenever a character enters the game, whatever the zone-change setting; cleared the same way.
        public bool EntryPaused;
        // A menu zone was seen, so the next game zone is an entry.
        public bool AwaitingEntry;
        // The profile generation whose slot toggles are loaded.
        public int ProfileGeneration = -1;
        public bool IconsTinted;
        public int FormKey = HumanFormKey;
        public string LastLoggedStatus = string.Empty;
        // The run's jobs, made in Start; the host drops them with the feature.
        public Job? SlotRefresh, EnemyScan;

        // Enemies near the player from the world model, counted a few times a second for the ranges conditions use.
        public readonly WorldTally Tally = new();
        public readonly List<TallyKey> Ranges = new();
        public readonly List<SubjectKey> Subjects = new();
        // Closest enemy within aim range as of the last count; checked again (not gone, not dead) before aiming.
        public WorldEntry? ClosestEnemy;
        public readonly Verdicts Verdicts = new();
    }

    private static Run? s_run;


    /// <summary>The shared slot target in words and what it picks now (for the dev gate verb): the scan's nearest enemy
    /// for the old switch's target, else the picker's.</summary>
    internal static (string Words, string Pick) SlotTargetNow
    {
        get
        {
            var shared = S.SharedSlotTarget;
            string words = shared.Words(TargetOwner.Slot);
            if (SharedAimedInScan(shared))
            {
                return (words, s_run?.ClosestEnemy is { Gone: false } closest ? Targets.EnemyWords(closest) : shared.Pick == TargetPick.Cursor ? "cursor" : "none (cursor)");
            }
            return (words, Targets.PickWords(shared) ?? "cursor");
        }
    }

    public static string ModifierKeyName => S.ModifierKey.ValueLabel;

    public const int Slots = SlotCount;

    /// <summary>The dispatcher's view of autocast while it runs.</summary>
    public static DispatchClient? Client => s_run?.Client;

    public static string? SlotAbilityName(int slot) => s_run?.Slots[slot].Ability?.abilityName;

    private static string? s_runningName, s_runningText;

    /// <summary>
    /// The feature's status: the slot macros' (as before) unless a library run is going ("running <macro>"), or the
    /// library is held by a rule while no slot is toggled (that rule's reason).
    /// </summary>
    public static FeatureStatus StatusOf(DispatchClient slots)
    {
        var status = Dispatcher.StatusOf(slots);
        if (Macros.RunningName is { } name)
        {
            // One string per running macro, not per read.
            if (!ReferenceEquals(name, s_runningName)) (s_runningName, s_runningText) = (name, "running " + name);
            return new FeatureStatus(StatusState.Active, s_runningText!, status.Slots);
        }
        if (s_run?.Library is { Blocking: not null } library && Macros.HasEnabled && !CurrentToggles().Any(on => on))
        {
            return Dispatcher.StatusOf(library) with { Slots = status.Slots };
        }
        return status;
    }

    // The enemy gate and the hold-to-pause key flip constantly in combat, so they are left out of the logged status.
    private static string BuildStatus(DispatchClient client)
    {
        FeatureStatus status = Dispatcher.StatusOf(client, RuleKind.Momentary);
        var text = new System.Text.StringBuilder(status.State.ToString().ToLowerInvariant());
        if (status.Detail.Length > 0) text.Append(" (").Append(status.Detail).Append(')');
        if (status.State == StatusState.Off || status.State == StatusState.Waiting) return text.ToString();

        text.Append("  ");
        for (int i = 0; i < SlotCount; i++)
        {
            text.Append("  ").Append(i + 1).Append(':').Append(IsSlotToggled(i) ? "ON" : "off");
            if (s_run?.Slots[i].Ability == null) text.Append('?');
        }
        return text.ToString();
    }

    public static void OnUpdate()
    {
        var run = s_run;
        if (run == null) return;
        if (Profiles.Generation != run.ProfileGeneration) LoadToggles(run);
        if (!S.Enabled.Value)
        {
            if (run.Active) Deactivate(run);
            return;
        }

        // Switched on: the bar is read in this frame, as it was before the slot refresh became a job.
        if (!run.Active && Player.Exists) run.SlotRefresh?.RunNow();
        run.Active = true;
        UpdateState(run);
    }

    private static void UpdateState(Run run)
    {
        for (int i = 0; i < SlotCount; i++)
        {
            if (!Hotkeys.SlotChordEngaged(i)) run.ToggleKeyDown[i] = false;
        }

        if (!Player.Exists)
        {
            Macros.Cancel("no player");
            ClearRefs(run);
            return;
        }

        // Ability details only refine timing (cooldowns, channelling); casting works without them. The bar itself is
        // read by the slot refresh job, at once on a form change.
        CheckFormChange(run);

        // After a zone change or entering the game, wait for the player to press any skill themselves before resuming.
        if ((run.ZonePaused || run.EntryPaused) && Dispatcher.PlainPressThisFrame)
        {
            run.ZonePaused = run.EntryPaused = false;
            Log.Info("Autocast: resumed");
        }

        UpdateEnemyGate(run);
        // Picks die with the hero; a refresh spread over frames goes on.
        if (!GameState.PlayerAlive) Targets.Clear();
        else Targets.Continue();
        CheckEvery(run);
        // Library macros ask first, so their press is in before the slot macros' in the same frame.
        Macros.Update();
        RequestCasts(run);

        // Cosmetic only: a failure here must never stop casting.
        try
        {
            UpdateIcons(run);
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("autocast:icons", $"Autocast icon update failed: {e.Message}", 30);
        }
    }

    /// <summary>Draws the enemy gate's range as a ring on the ground around the player, while the gate entry is on.</summary>
    public static void OnGUI()
    {
        Macros.OnGUI();
        var run = s_run;
        if (run == null || !run.Active || !S.ShowRangeRing.Value) return;
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        if (GateIndex(out var gate) is not int index || !gate!.On || gate.Invalid != null) return;
        if (Player.Transform is not Transform player) return;

        Vector3 center = player.position;
        float radius = gate.Values.Number("range");
        Color color = GateCounts(run, gate) ? s_rangeRingCastingColor : s_rangeRingHoldingColor;

        Draw.WorldCircle(center, radius, color, RangeRingWidth, RangeRingSegments);
    }

    private static void LogStatusChanges(Run run)
    {
        if (run.Client == null) return;
        string status = BuildStatus(run.Client);
        if (status == run.LastLoggedStatus) return;

        run.LastLoggedStatus = status;
        Log.Debug($"Autocast status: {status}");
    }

    /// <summary>
    /// Starts a clean run and registers with the dispatcher: drinks (survival), library and slots (skills, in that
    /// order), each with the feature's own pause rules in rank order; only the slots client has the every-slot list.
    /// </summary>
    public static void Start(Feature feature)
    {
        var run = new Run();
        // The same rule objects on every client, so they read and show alike.
        var featureRules = new[]
        {
            new PauseRule("non-combat", "non-combat zone", RuleKind.Latched,
                () => S.PauseInNonCombatZones.Value && InNonCombatZone()),
            new PauseRule("zone-pause", "press any skill to resume", RuleKind.Latched,
                () => S.PauseOnZoneChange.Value && run.ZonePaused),
            // Restoring a character's slots never starts casting by itself.
            new PauseRule("entry-pause", "press any skill to resume", RuleKind.Latched, () => run.EntryPaused),
            new PauseRule("hold-key", () => S.HoldToPauseKey.Display + " held", RuleKind.Momentary, () => Hotkeys.Held(S.HoldToPauseKey)),
        };
        // Drinks go on with the menu open (the menu pausing them once let a fight kill the player); every other rule holds.
        run.Drinks = Dispatcher.Register(new DispatchClient(feature, PriorityClass.Survival, S.Enabled, featureRules)
        {
            Name = "macros: drinks",
            Ignores = new[] { PauseRules.MenuOpen },
        });
        run.Library = Dispatcher.Register(new DispatchClient(feature, PriorityClass.Skills, S.Enabled, featureRules)
        {
            Name = "macros: library",
            Channelled = Macros.LibraryChannelled,
        });
        run.Client = Dispatcher.Register(new DispatchClient(feature, PriorityClass.Skills, S.Enabled, featureRules.Append(
            // The every-slot list in the old enemy gate's place; with the gate failing its reason reads as before.
            new PauseRule("every-slot", () => run.Verdicts.Reason, RuleKind.Momentary, () => run.Active && !run.Verdicts.EveryVerdict.Passed)).ToArray())
        {
            Name = "macros: slots",
            Slots = CurrentToggles,
            Channelled = slot => run.Slots[slot].Channelled,
        });
        Macros.Start(run.Library, run.Drinks);
        Targets.Cursor = s_cursorRead;
        Targets.Weight = s_enemyWeight;
        Targets.Clear();
        run.Client.SwallowRealPress = OnRealBarCommand;
        run.Client.SlotChordHeld = Hotkeys.SlotChordEngaged;
        s_run = run;
        Bus.Subscribe<ZoneChange>(feature, OnZoneChanged);
        // Each body keeps the checks the update made before calling it: this run, switched on, a player.
        run.SlotRefresh = Scheduler.Every(feature, "slot refresh", SlotRefreshIntervalSeconds, () =>
        {
            if (s_run == run && S.Enabled.Value && Player.Exists) RefreshSlots(run);
        }, startNow: true);
        run.EnemyScan = Scheduler.Every(feature, "enemy scan", EnemyScanIntervalSeconds, () =>
        {
            if (s_run == run && S.Enabled.Value && Player.Exists) ScanEnemyGate(run);
        }, startNow: true);
        Scheduler.Every(feature, "status log", StatusLogIntervalSeconds, () =>
        {
            if (s_run == run && S.Enabled.Value) LogStatusChanges(run);
        });

        RegisterConditionTargets(feature);
        // Toggle keys in conditions start off with every run.
        ConditionToggles.Clear();
        Actions.Register(feature.Id, "autocast.toggle", "Macros", () => S.Enabled.Value = !S.Enabled.Value, () => S.Enabled.Value);
        for (int i = 0; i < SlotCount; i++)
        {
            int slot = i;
            Actions.Register(feature.Id, $"autocast.slot.{slot + 1}", $"Toggle slot {slot + 1}", () => ToggleSlot(slot), () => IsSlotToggled(slot));
        }
        // Holding the modifier and pressing a slot's key runs autocast.slot.N.
        Hotkeys.SetSlotChords(() => S.ModifierKey.Value, "autocast.slot.");
        // Starting in game (a restart or a dev reload) is an entry too.
        if (GameState.InGame) PauseForEntry(run);
    }

    private static void OnZoneChanged(ZoneChange change)
    {
        var run = s_run;
        if (run == null) return;
        Macros.OnZoneChanged();

        // An echo transition keeps the scene, so references stay valid; only the bar may change.
        if (change.Monolith)
        {
            if (!S.Enabled.Value) return;
            if (S.PauseOnZoneChange.Value) PauseForZone(run);
            run.SlotRefresh?.Due();
            return;
        }

        ClearRefs(run);
        if (change.Kind == SceneKind.Menu)
        {
            // A loaded profile keeps the character's toggles; it unloads right after and hands back none.
            if (!Profiles.HasCharacter) ClearToggles(run);
            run.AwaitingEntry = true;
            return;
        }
        if (run.AwaitingEntry)
        {
            run.AwaitingEntry = false;
            PauseForEntry(run);
        }
        if (run.Active && S.PauseOnZoneChange.Value) PauseForZone(run);
    }

    private static void PauseForEntry(Run run)
    {
        if (!run.EntryPaused) Log.Info($"Autocast: paused on entering the game (scene '{GameState.Scene}')");
        run.EntryPaused = true;
        if (CurrentToggles().Any(on => on)) Toasts.Show("autocast", "Macros paused. Use a skill to resume.", ToastKind.Info);
    }

    // The dispatcher lets go of held slots as soon as the zone-pause rule is active. With no slot on there is nothing to
    // tell the player beyond the status.
    private static void PauseForZone(Run run)
    {
        if (!run.ZonePaused)
        {
            Log.Info($"Autocast: paused (scene '{GameState.Scene}')");
            if (CurrentToggles().Any(on => on)) Toasts.Show("autocast", "Macros paused. Use a skill to resume.", ToastKind.Info);
        }
        run.ZonePaused = true;
    }

    /// <summary>Lets go of everything autocast holds: channels (through the dispatcher), toggles, tinted icons.</summary>
    public static void Shutdown()
    {
        Hotkeys.SetSlotChords(null, null);
        Targets.Cursor = null;
        Targets.Weight = null;
        Targets.Clear();
        ConditionTargets.Remove("autocast");
        ConditionKeys.Remove("autocast");
        var run = s_run;
        if (run == null) return;
        Macros.Shutdown();
        if (run.Client != null) Dispatcher.Unregister(run.Client);
        if (run.Library != null) Dispatcher.Unregister(run.Library);
        if (run.Drinks != null) Dispatcher.Unregister(run.Drinks);
        try
        {
            RestoreIcons(run);
        }
        catch (Exception)
        {
            // Icons are recreated by the game; nothing to restore if they are gone.
        }
        s_run = null;
    }

    // Switched off: the dispatcher has let go of held slots (the "off" rule); forget toggles and restore the icons.
    private static void Deactivate(Run run)
    {
        Macros.Cancel("Run macros off");
        run.Active = false;
        run.LastLoggedStatus = string.Empty;
        ClearToggles(run);
        SaveToggles(run);
        ClearRefs(run);
        try
        {
            RestoreIcons(run);
        }
        catch (Exception)
        {
            // Icons are recreated by the game; nothing to restore if they are gone.
        }
    }

    private static void ClearRefs(Run run)
    {
        run.SlotRefresh?.Due();
        run.EnemyScan?.Due();
        run.Tally.Clear();
        Targets.Clear();
        run.Verdicts.Clear();
        run.ClosestEnemy = null;
        Array.Clear(run.ToggleKeyDown);
        for (int i = 0; i < SlotCount; i++)
        {
            run.Slots[i].Ability = null;
            run.Slots[i].Channelled = false;
        }
    }

    // The skill bar comes from the player model; a slot it can't read this frame is left empty until the next refresh.
    private static void RefreshSlots(Run run)
    {
        for (int i = 0; i < SlotCount; i++)
        {
            Core.Slot? modelSlot = PlayerModel.Slot(i + 1);
            Ability? ability = modelSlot?.Ability;
            bool channelled = ability != null && modelSlot!.Channelled;

            // A slot that stops being a channel must not be left held.
            if (run.Client != null && run.Client.Holds(i) && run.Slots[i].Channelled && !channelled) Dispatcher.StopChannel(run.Client, i);

            run.Slots[i].Ability = ability;
            run.Slots[i].Channelled = channelled;
        }
    }

    // The form part names the transform form, or none in human form. A frame it can't be read keeps the current form.
    private static void CheckFormChange(Run run)
    {
        var form = PlayerModel.Form;
        if (form == null) return;
        int formKey = form.TransformId ?? HumanFormKey;
        if (formKey == run.FormKey) return;

        if (run.Client != null) Dispatcher.Release(run.Client);
        run.FormKey = formKey;
        run.SlotRefresh?.RunNow();
    }

    private static bool InNonCombatZone() => GameState.Kind == SceneKind.NonCombat;

    // Per toggled slot each frame: a press when ready, else the key-up, as the game sees from a held key. The
    // dispatcher decides what is sent; a slot that isn't toggled is let go.
    private static void RequestCasts(Run run)
    {
        var client = run.Client;
        if (client == null || !Dispatcher.Allowed(client)) return;

        bool[] toggles = CurrentToggles();
        bool anyToggled = false;
        for (int i = 0; i < SlotCount; i++) anyToggled |= toggles[i];
        if (!anyToggled) return;

        // The point slots that send nothing are given (a let-go key-up): the shared target's, as before.
        Vector3 targetPosition = GetTargetPosition(run, out Transform? hitTransform);
        for (int i = 0; i < SlotCount; i++)
        {
            ref Slot slot = ref run.Slots[i];
            if (!toggles[i])
            {
                run.Verdicts.Waiting[i] = false;
                run.Verdicts.NoTarget[i] = false;
                Dispatcher.Request(client, i, PressIntent.None, targetPosition, hitTransform);
                continue;
            }

            // A library run casting on this slot has it: the slot macro leaves it alone until the run is done.
            if (Macros.SlotInUse(i))
            {
                run.Verdicts.InUse[i] = true;
                Dispatcher.Request(client, i, PressIntent.None, targetPosition, hitTransform);
                continue;
            }
            run.Verdicts.InUse[i] = false;

            // A slot whose own conditions fail is let go, as an untoggled one is: conditions only hold slots back.
            if (!CheckSlot(run, i))
            {
                Dispatcher.Request(client, i, PressIntent.None, targetPosition, hitTransform);
                continue;
            }

            // The slot's own target, or the shared one; one that finds nothing and doesn't cast holds the slot.
            var target = S.EffectiveSlotTarget(run.FormKey, i);
            Vector3 point = targetPosition;
            Transform? hit = hitTransform;
            if (!ReferenceEquals(target, S.SharedSlotTarget) || !SharedAimedInScan(target))
            {
                var aimed = Macros.AimAt(target);
                if (aimed.NoTarget)
                {
                    run.Verdicts.NoTarget[i] = true;
                    run.Verdicts.Waiting[i] = true;
                    Dispatcher.Request(client, i, PressIntent.None, targetPosition, hitTransform);
                    continue;
                }
                point = aimed.Point;
                hit = aimed.Hit;
            }
            run.Verdicts.NoTarget[i] = false;

            bool channelling = slot.Channelled && PlayerModel.Channelling == true;
            bool ready = !channelling && !IsOnCooldown(i, slot.Ability);
            Dispatcher.Request(client, i, ready ? PressIntent.Press : PressIntent.Lift, point, hit);
            if (channelling) Dispatcher.Retarget(client, i, point);
        }
    }

    /// <summary>Whether the slots client holds a channel on a slot (0-based): a library cast there waits for it.</summary>
    internal static bool SlotChannelHeld(int slot) =>
        s_run is { Client: { } client } run && client.Holds(slot) && run.Slots[slot].Channelled;

    // The player model's ready flag is the game's own cooldown check; unknown counts as ready, as before.
    private static bool IsOnCooldown(int slot, Ability? ability) =>
        ability != null && PlayerModel.Slot(slot + 1)?.Ready == false;

    // A shared target aimed from the scan, as the old switch did: nearest within its range, else at cursor (that is
    // GetTargetPosition), and at cursor itself.
    private static bool SharedAimedInScan(TargetSpec shared) => shared.Pick == TargetPick.Cursor || (shared.Legacy && shared.Pick == TargetPick.Nearest);

    // The shared target's point when the scan aims it (nearest from the scan, re-checked here) and else the cursor's.
    private static Vector3 GetTargetPosition(Run run, out Transform? hitTransform)
    {
        hitTransform = null;
        if (Player.Transform is not Transform player) return Vector3.zero;

        // The count runs a few times a second, so make sure the target is still there and alive; the world model keeps
        // its position current while it is active.
        if (run.ClosestEnemy is { Gone: false, Dead: false, Active: true } closest) return closest.Position;

        try
        {
            return MouseManager.AbilityUseMousePoint(out hitTransform, out _, false, player);
        }
        catch (Exception)
        {
            return player.position;
        }
    }
}
