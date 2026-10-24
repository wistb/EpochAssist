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
internal static class AutoCast
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

    private const string HostileAlignmentName = "Evil";
    private const string HostileNeutralAlignmentName = "HostileNeutral";

    private static readonly bool[] s_noToggles = new bool[SlotCount];

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
        public bool Active;
        // Set by a zone change; cleared by a skill press of the player's own or by switching a slot on.
        public bool ZonePaused;
        public bool IconsTinted;
        public int FormKey = HumanFormKey;
        public float NextSlotRefreshAt;
        public float NextStatusLogAt;
        public string LastLoggedStatus = string.Empty;

        // Hostile actors near the player, refreshed a few times a second while the enemy gate is on.
        public float NextEnemyScanAt;
        public int EnemiesInRange;
        public float EnemyWeightInRange;
        public bool EnemyGateOpen = true;
        // Closest hostile within aim range as of the last scan; null when there is none.
        public ActorVisuals? ClosestEnemy;

        public Actor? PlayerActor;
        public Transform? PlayerTransform;
        public PlayerChargeManager? ChargeManager;
        public UsingAbilityPlayer? AbilityState;
        public LocalTreeData? TreeData;
    }

    private static Run? s_run;

    public static int EnemiesInRange => s_run?.EnemiesInRange ?? 0;
    public static float EnemyWeightInRange => s_run?.EnemyWeightInRange ?? 0f;
    public static bool EnemyGateOpen => s_run?.EnemyGateOpen ?? true;

    public static string ModifierKeyName => S.ModifierKey.ValueLabel;

    public const int Slots = SlotCount;

    /// <summary>The dispatcher's view of autocast while it runs.</summary>
    public static DispatchClient? Client => s_run?.Client;

    public static bool IsSlotToggled(int slot) => CurrentToggles()[slot];

    public static string? SlotAbilityName(int slot) => s_run?.Slots[slot].Ability?.abilityName;

    // The toggles of the current form; read only.
    private static bool[] CurrentToggles()
    {
        var run = s_run;
        return run != null && run.TogglesByForm.TryGetValue(run.FormKey, out var toggles) ? toggles : s_noToggles;
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
        if (!S.Enabled.Value)
        {
            if (run.Active) Deactivate(run);
            return;
        }

        run.Active = true;
        UpdateState(run);
        LogStatusChanges(run);
    }

    private static void UpdateState(Run run)
    {
        for (int i = 0; i < SlotCount; i++)
        {
            if (!Hotkeys.SlotChordEngaged(i)) run.ToggleKeyDown[i] = false;
        }

        if (!Player.Exists)
        {
            ClearRefs(run);
            return;
        }

        // Ability details only refine timing (cooldowns, channelling); casting works without them.
        PlayerAbilityList? abilityList = ResolveRefs(run);
        if (abilityList != null) CheckFormChange(run, abilityList);
        if (Time.unscaledTime >= run.NextSlotRefreshAt)
        {
            RefreshSlots(run, abilityList);
        }

        // After a zone change, wait for the player to press any skill themselves before resuming.
        if (run.ZonePaused && Dispatcher.PlainPressThisFrame)
        {
            run.ZonePaused = false;
            Log.Info("Autocast: resumed");
        }

        UpdateEnemyGate(run);
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

    /// <summary>Draws the enemy-gate range as a ring on the ground around the player.</summary>
    public static void OnGUI()
    {
        var run = s_run;
        if (run == null || !run.Active || !S.RequireEnemies.Value || !S.ShowRangeRing.Value) return;
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        if (run.PlayerTransform == null) return;

        Vector3 center = run.PlayerTransform.position;
        float radius = S.EnemyRange.Value;
        Color color = run.EnemyGateOpen ? s_rangeRingCastingColor : s_rangeRingHoldingColor;

        Draw.WorldCircle(center, radius, color, RangeRingWidth, RangeRingSegments);
    }

    private static void LogStatusChanges(Run run)
    {
        if (run.Client == null || Time.unscaledTime < run.NextStatusLogAt) return;
        run.NextStatusLogAt = Time.unscaledTime + StatusLogIntervalSeconds;

        string status = BuildStatus(run.Client);
        if (status == run.LastLoggedStatus) return;

        run.LastLoggedStatus = status;
        Log.Info($"Autocast status: {status}");
    }

    /// <summary>Starts a clean run and registers with the dispatcher, with autocast's own pause rules in rank order.</summary>
    public static void Start(Feature feature)
    {
        var run = new Run();
        run.Client = Dispatcher.Register(new DispatchClient(feature, PriorityClass.Skills, S.Enabled, new[]
        {
            new PauseRule("non-combat", "non-combat zone", RuleKind.Latched,
                () => S.PauseInNonCombatZones.Value && InNonCombatZone()),
            new PauseRule("zone-pause", "press any skill to resume", RuleKind.Latched,
                () => S.PauseOnZoneChange.Value && run.ZonePaused),
            new PauseRule("hold-key", () => S.HoldToPauseKey.Display + " held", RuleKind.Momentary, () => Hotkeys.Held(S.HoldToPauseKey)),
            new PauseRule("enemy-gate", "not enough enemies in range", RuleKind.Momentary,
                () => S.RequireEnemies.Value && !run.EnemyGateOpen),
        })
        {
            Slots = CurrentToggles,
            Channelled = slot => run.Slots[slot].Channelled,
        });
        run.Client.SwallowRealPress = OnRealBarCommand;
        run.Client.SlotChordHeld = Hotkeys.SlotChordEngaged;
        s_run = run;
        GameState.ZoneChanged += OnZoneChanged;

        Actions.Register(feature.Id, "autocast.toggle", "Autocast", () => S.Enabled.Value = !S.Enabled.Value, () => S.Enabled.Value);
        for (int i = 0; i < SlotCount; i++)
        {
            int slot = i;
            Actions.Register(feature.Id, $"autocast.slot.{slot + 1}", $"Toggle slot {slot + 1}", () => ToggleSlot(slot), () => IsSlotToggled(slot));
        }
        // Holding the modifier and pressing a slot's key runs autocast.slot.N.
        Hotkeys.SetSlotChords(() => S.ModifierKey.Value, "autocast.slot.");
    }

    // Slots toggle only while autocast is on; switching it off forgets them.
    private static void ToggleSlot(int slot)
    {
        if (s_run is { } run && S.Enabled.Value) Toggle(run, slot);
    }

    private static void OnZoneChanged(ZoneChange change)
    {
        var run = s_run;
        if (run == null) return;

        // An echo transition keeps the scene, so references stay valid; only the bar may change.
        if (change.Monolith)
        {
            if (!S.Enabled.Value) return;
            if (S.PauseOnZoneChange.Value) PauseForZone(run);
            run.NextSlotRefreshAt = 0f;
            return;
        }

        ClearRefs(run);
        if (change.Kind == SceneKind.Menu)
        {
            ClearToggles(run);
        }
        else if (run.Active && S.PauseOnZoneChange.Value)
        {
            PauseForZone(run);
        }
    }

    // The dispatcher lets go of held slots as soon as the zone-pause rule is active. With no slot on there is nothing to
    // tell the player beyond the status.
    private static void PauseForZone(Run run)
    {
        if (!run.ZonePaused)
        {
            Log.Info($"Autocast: paused (scene '{GameState.Scene}')");
            if (CurrentToggles().Any(on => on)) Toasts.Show("autocast", "Autocast paused. Use a skill to resume.", ToastKind.Info);
        }
        run.ZonePaused = true;
    }

    /// <summary>
    /// Called by the dispatcher for every bar command the game itself issues (the player's real key presses).
    /// Returns true to swallow the command: while the slot-toggle chord is engaged the key toggles, and must not cast.
    /// </summary>
    private static bool OnRealBarCommand(int slot, bool keyDown)
    {
        var run = s_run;
        if (run == null || !S.Enabled.Value) return false;

        if (!keyDown)
        {
            run.ToggleKeyDown[slot] = false;
            return false;
        }

        if (!Hotkeys.SlotChordEngaged(slot)) return false;

        // The hotkey layer toggles from the slot's key; when that key couldn't be looked up, toggle here, once per press.
        if (!Hotkeys.SlotKeyKnown(slot) && !run.ToggleKeyDown[slot])
        {
            run.ToggleKeyDown[slot] = true;
            Toggle(run, slot);
        }
        return true;
    }

    /// <summary>Lets go of everything autocast holds: channels (through the dispatcher), toggles, tinted icons.</summary>
    public static void Shutdown()
    {
        GameState.ZoneChanged -= OnZoneChanged;
        Hotkeys.SetSlotChords(null, null);
        var run = s_run;
        if (run == null) return;
        if (run.Client != null) Dispatcher.Unregister(run.Client);
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
        run.Active = false;
        run.LastLoggedStatus = string.Empty;
        ClearToggles(run);
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
        run.PlayerActor = null;
        run.PlayerTransform = null;
        run.ChargeManager = null;
        run.AbilityState = null;
        run.TreeData = null;
        run.NextSlotRefreshAt = 0f;
        run.NextEnemyScanAt = 0f;
        run.EnemiesInRange = 0;
        run.EnemyWeightInRange = 0f;
        run.ClosestEnemy = null;
        Array.Clear(run.ToggleKeyDown);
        for (int i = 0; i < SlotCount; i++)
        {
            run.Slots[i].Ability = null;
            run.Slots[i].Channelled = false;
        }
    }

    private static PlayerAbilityList? ResolveRefs(Run run)
    {
        try
        {
            if (run.PlayerActor == null)
            {
                run.PlayerActor = Player.Actor;
                run.PlayerTransform = null;
                run.ChargeManager = null;
                run.AbilityState = null;
            }

            if (run.PlayerActor != null)
            {
                if (run.PlayerTransform == null) run.PlayerTransform = run.PlayerActor.transform;
                if (run.ChargeManager == null) run.ChargeManager = run.PlayerActor.GetComponent<PlayerChargeManager>();
                if (run.AbilityState == null) run.AbilityState = run.PlayerActor.GetComponent<UsingAbilityPlayer>();
            }

            if (run.PlayerTransform == null) run.PlayerTransform = Player.Transform;

            if (run.TreeData == null) run.TreeData = Game.LocalTree;
            if (run.TreeData == null) return null;

            PlayerAbilityList? abilityList = run.TreeData.playerAbilityList;
            return abilityList == null ? null : abilityList;
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("autocast:refs", $"Autocast reference lookup failed: {e.Message}", 30);
            return null;
        }
    }

    private static void RefreshSlots(Run run, PlayerAbilityList? abilityList)
    {
        run.NextSlotRefreshAt = Time.unscaledTime + SlotRefreshIntervalSeconds;

        for (int i = 0; i < SlotCount; i++)
        {
            Ability? ability = FindSlotAbility(abilityList, i);
            bool channelled = ability != null && ability.channelled;

            // A slot that stops being a channel must not be left held.
            if (run.Client != null && run.Client.Holds(i) && run.Slots[i].Channelled && !channelled) Dispatcher.StopChannel(run.Client, i);

            run.Slots[i].Ability = ability;
            run.Slots[i].Channelled = channelled;
        }
    }

    private static Ability? FindSlotAbility(PlayerAbilityList? abilityList, int slot)
    {
        // The bar icon knows exactly which ability its slot shows, online and offline.
        try
        {
            var icons = Game.BarIcons;
            if (icons != null)
            {
                for (int i = 0; i < icons.Count; i++)
                {
                    AbilityBarIcon? barIcon = icons[i];
                    if (barIcon == null || barIcon.abilityNumber - 1 != slot) continue;

                    Ability? ability = barIcon.ability;
                    if (ability != null) return ability;
                }
            }
        }
        catch (Exception)
        {
            // Fall back to the ability list.
        }

        if (abilityList == null) return null;

        try
        {
            Ability? ability = abilityList.getAbility(slot);
            return ability == null ? null : ability;
        }
        catch (Exception)
        {
            // Slot lookup can throw while the bar is rebuilding; treat as unknown this pass.
            return null;
        }
    }

    private static void CheckFormChange(Run run, PlayerAbilityList abilityList)
    {
        int formKey = HumanFormKey;
        try
        {
            var transformId = abilityList.TransformAbilityId;
            if (transformId.HasValue) formKey = transformId.Value;
        }
        catch (Exception)
        {
            // Keep the human-form key if the transform id is unreadable.
        }

        if (formKey == run.FormKey) return;

        if (run.Client != null) Dispatcher.Release(run.Client);
        run.FormKey = formKey;
        run.NextSlotRefreshAt = 0f;
    }

    private static void Toggle(Run run, int slot)
    {
        if (!run.TogglesByForm.TryGetValue(run.FormKey, out var toggles))
        {
            toggles = new bool[SlotCount];
            run.TogglesByForm[run.FormKey] = toggles;
        }

        toggles[slot] = !toggles[slot];

        // Switching a slot on is explicit intent: lift a zone-change pause (the non-combat pause stays).
        if (toggles[slot] && run.ZonePaused)
        {
            run.ZonePaused = false;
            Log.Info("Autocast: resumed");
        }

        bool paused = run.Client != null && Dispatcher.StatusOf(run.Client, RuleKind.Momentary).State == StatusState.Paused;
        Log.Info($"Autocast: slot {slot + 1} ({run.Slots[slot].Ability?.abilityName ?? "?"}) {(toggles[slot] ? "ON" : "off")}{(paused ? " (paused)" : string.Empty)}");
    }

    private static void ClearToggles(Run run)
    {
        run.TogglesByForm.Clear();
        run.ZonePaused = false;
        run.FormKey = HumanFormKey;
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

        Vector3 targetPosition = GetTargetPosition(run, out Transform? hitTransform);
        for (int i = 0; i < SlotCount; i++)
        {
            ref Slot slot = ref run.Slots[i];
            if (!toggles[i])
            {
                Dispatcher.Request(client, i, PressIntent.None, targetPosition, hitTransform);
                continue;
            }

            bool channelling = slot.Channelled && run.AbilityState != null && run.AbilityState.channelling;
            bool ready = !channelling && !IsOnCooldown(run, slot.Ability);
            Dispatcher.Request(client, i, ready ? PressIntent.Press : PressIntent.Lift, targetPosition, hitTransform);
            if (channelling) Dispatcher.Retarget(client, i, targetPosition);
        }
    }

    private static void UpdateEnemyGate(Run run)
    {
        bool gated = S.RequireEnemies.Value;
        if (!gated) run.EnemyGateOpen = true;
        if (!gated && !S.AimAtClosestEnemy.Value)
        {
            run.ClosestEnemy = null;
            return;
        }

        if (Time.unscaledTime < run.NextEnemyScanAt) return;
        run.NextEnemyScanAt = Time.unscaledTime + EnemyScanIntervalSeconds;

        try
        {
            ScanEnemies(run);
        }
        catch (Exception e)
        {
            // If the scan breaks, fail open so autocast keeps working ungated.
            run.EnemiesInRange = 0;
            run.EnemyWeightInRange = 0f;
            run.ClosestEnemy = null;
            run.EnemyGateOpen = true;
            Log.ErrorThrottled("autocast:enemies", $"Autocast enemy scan failed: {e.Message}", 30);
            return;
        }

        run.EnemyGateOpen = !gated || run.EnemyWeightInRange >= S.EnemyMinWeight.Value;
    }

    private static void ScanEnemies(Run run)
    {
        int count = 0;
        float weight = 0f;
        ActorVisuals? closest = null;

        ActorManager? manager = Game.Actors;
        if (manager != null && run.PlayerTransform != null)
        {
            Vector3 playerPosition = run.PlayerTransform.position;
            float rangeSquared = S.RequireEnemies.Value ? S.EnemyRange.Value * S.EnemyRange.Value : -1f;
            float closestSquared = S.AimAtClosestEnemy.Value ? S.AimRange.Value * S.AimRange.Value : -1f;

            foreach (var visual in manager.visuals)
            {
                string? alignmentName = visual.alignment?.name;
                if (alignmentName != HostileAlignmentName && alignmentName != HostileNeutralAlignmentName) continue;

                foreach (var actor in visual.visuals._list)
                {
                    if (actor == null || actor.dead) continue;

                    GameObject actorObject = actor.gameObject;
                    if (actorObject == null || !actorObject.activeInHierarchy) continue;

                    float distanceSquared = (actor.transform.position - playerPosition).sqrMagnitude;
                    if (distanceSquared <= closestSquared)
                    {
                        closestSquared = distanceSquared;
                        closest = actor;
                    }
                    if (distanceSquared > rangeSquared) continue;

                    count++;
                    weight += EnemyWeight(actor.GetComponent<ActorDisplayInformation>());
                }
            }
        }

        run.EnemiesInRange = count;
        run.EnemyWeightInRange = weight;
        run.ClosestEnemy = closest;
    }

    private static float EnemyWeight(ActorDisplayInformation? info)
    {
        if (info == null) return S.WeightNormal.Value;

        return info.actorClass switch
        {
            DisplayActorClass.Boss => S.WeightBoss.Value,
            DisplayActorClass.Rare => S.WeightRare.Value,
            DisplayActorClass.Magic => S.WeightMagic.Value,
            _ => S.WeightNormal.Value
        };
    }

    private static bool IsOnCooldown(Run run, Ability? ability)
    {
        if (ability == null || run.ChargeManager == null) return false;

        try
        {
            return run.ChargeManager.onCoooldown(ability);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Vector3 GetTargetPosition(Run run, out Transform? hitTransform)
    {
        hitTransform = null;
        if (run.PlayerTransform == null) return Vector3.zero;

        // The scan runs a few times a second, so make sure the target is still there and alive.
        try
        {
            if (S.AimAtClosestEnemy.Value && run.ClosestEnemy != null && !run.ClosestEnemy.dead
                && run.ClosestEnemy.gameObject.activeInHierarchy)
            {
                return run.ClosestEnemy.transform.position;
            }
        }
        catch (Exception)
        {
            run.ClosestEnemy = null;
        }

        try
        {
            return MouseManager.AbilityUseMousePoint(out hitTransform, out _, false, run.PlayerTransform);
        }
        catch (Exception)
        {
            return run.PlayerTransform.position;
        }
    }

    // Toggled slots pulse while autocast may cast (the enemy gate holding it counts); they turn blue while paused
    // or while the hold-to-pause key is held.
    private static void UpdateIcons(Run run)
    {
        bool[] toggles = CurrentToggles();
        bool anyToggled = false;
        for (int i = 0; i < SlotCount; i++) anyToggled |= toggles[i];

        if (!anyToggled)
        {
            if (run.IconsTinted) RestoreIcons(run);
            return;
        }

        var icons = Game.BarIcons;
        if (icons == null) return;

        PauseRule? rule = run.Client?.Blocking;
        bool held = rule != null && (rule.Kind == RuleKind.Latched || rule.Name == "hold-key");

        float pulse = (Mathf.Sin(Time.unscaledTime * 5f) + 1f) * 0.5f;
        float scale = Mathf.Lerp(0.92f, 1.07f, pulse);
        var activeColor = new Color(1f, Mathf.Lerp(1f, 0.75f, pulse), Mathf.Lerp(1f, 0.4f, pulse), 1f);
        var suspendedColor = new Color(0.55f, 0.75f, 1f, Mathf.Lerp(0.55f, 0.85f, pulse));

        for (int i = 0; i < icons.Count; i++)
        {
            AbilityBarIcon? barIcon = icons[i];
            if (barIcon == null) continue;

            int slot = barIcon.abilityNumber - 1;
            bool toggled = slot >= 0 && slot < SlotCount && toggles[slot];
            bool on = toggled && !held;
            bool paused = toggled && held;

            barIcon.transform.localScale = on ? new Vector3(scale, scale, 1f) : Vector3.one;

            var image = barIcon.icon;
            if (image != null)
            {
                image.color = on ? activeColor : paused ? suspendedColor : Color.white;
            }
        }

        run.IconsTinted = true;
    }

    private static void RestoreIcons(Run run)
    {
        run.IconsTinted = false;

        var icons = Game.BarIcons;
        if (icons == null) return;

        for (int i = 0; i < icons.Count; i++)
        {
            AbilityBarIcon? barIcon = icons[i];
            if (barIcon == null) continue;

            barIcon.transform.localScale = Vector3.one;
            var image = barIcon.icon;
            if (image != null) image.color = Color.white;
        }
    }
}
