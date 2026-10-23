using EpochAssist.Core;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;
using S = EpochAssist.Features.AutoCastSettings;

namespace EpochAssist.Features;

/// <summary>
/// Per-slot skill auto-cast. Hold the modifier key and press a skill's own keybind to toggle
/// auto-cast for that slot. Casts are sent through the game's UseAbilityProcessor, the same
/// entry point a real key press uses; the player's own presses are observed at that same point.
/// Toggle presses are also read straight from the keys bound to each slot, because the game drops
/// the skill press when modifier+key is a binding of its own (Ctrl+F is its search field).
/// </summary>
internal static class AutoCast
{
    private struct Slot
    {
        public Ability? Ability;
        public bool Channelled;
        public bool On;
        public bool Held;
        public float NextPressAt;
    }

    private const int SlotCount = 5;
    private const int HumanFormKey = -1;
    private const float PressThrottleSeconds = 0.1f;
    private const float SlotRefreshIntervalSeconds = 1f;
    private const float StatusLogIntervalSeconds = 0.5f;
    private const float EnemyScanIntervalSeconds = 0.15f;
    private const int RangeRingSegments = 64;
    private const float RangeRingWidth = 2f;
    private static readonly Color s_rangeRingCastingColor = new(0.35f, 1f, 0.45f, 0.75f);
    private static readonly Color s_rangeRingHoldingColor = new(1f, 1f, 1f, 0.4f);

    private const string HostileAlignmentName = "Evil";
    private const string HostileNeutralAlignmentName = "HostileNeutral";


    // The game's input action for each bar slot, and the key or mouse button currently bound to it.
    private static readonly int[] s_slotActionIds =
    {
        (int)EpochInputManager.InputActions.Ability1,
        (int)EpochInputManager.InputActions.Ability2,
        (int)EpochInputManager.InputActions.Ability3,
        (int)EpochInputManager.InputActions.Ability4,
        (int)EpochInputManager.InputActions.Ability5
    };
    private static readonly KeyCode[] s_slotKeys = new KeyCode[SlotCount];
    private static bool s_modifierWasHeld;
    // Rewired numbers mouse buttons from 3 (left); 0-2 are the axes and the wheel.
    private const int FirstMouseButtonElementId = 3;
    private const int MouseButtonCount = 7;

    private static readonly Slot[] s_slots = new Slot[SlotCount];
    // Toggles are remembered per form so a transformed skill bar keeps its own set.
    private static readonly Dictionary<int, bool[]> s_togglesByForm = new();

    // Slots whose current press has already toggled, so one press toggles once.
    private static readonly bool[] s_toggleKeyDown = new bool[SlotCount];
    private static bool s_plainPressSeen;

    private static bool s_active;
    private static bool s_suspended;
    private static bool s_holdToPauseHeld;
    private static bool s_fromAutoCast;
    private static bool s_iconsTinted;
    private static int s_formKey = HumanFormKey;
    private static float s_nextSlotRefreshAt;
    private static float s_nextStatusLogAt;
    private static string s_blockedReason = string.Empty;
    private static string s_lastLoggedStatus = string.Empty;

    // Hostile actors near the player, refreshed a few times a second while the enemy gate is on.
    private static float s_nextEnemyScanAt;
    private static int s_enemiesInRange;
    private static float s_enemyWeightInRange;
    private static bool s_enemyGateOpen = true;
    // Closest hostile within aim range as of the last scan; null when there is none.
    private static ActorVisuals? s_closestEnemy;

    public static int EnemiesInRange => s_enemiesInRange;
    public static float EnemyWeightInRange => s_enemyWeightInRange;
    public static bool EnemyGateOpen => s_enemyGateOpen;

    private static Actor? s_playerActor;
    private static Transform? s_playerTransform;
    private static PlayerChargeManager? s_chargeManager;
    private static UsingAbilityPlayer? s_abilityState;
    private static LocalTreeData? s_treeData;
    private static Il2CppRewired.Player? s_rewiredPlayer;

    public static string ModifierKeyName => S.ModifierKey.ValueLabel;

    public enum State { Off, Waiting, Active, Holding, Paused }

    public const int Slots = SlotCount;

    public static State CurrentState => Describe(includeMomentary: true, out _);

    /// <summary>Why auto-cast is in its current state, for the menu.</summary>
    public static string StateDetail
    {
        get
        {
            Describe(includeMomentary: true, out string detail);
            return detail;
        }
    }

    public static bool IsSlotToggled(int slot)
    {
        s_togglesByForm.TryGetValue(s_formKey, out var toggles);
        return toggles != null && toggles[slot];
    }

    public static string? SlotAbilityName(int slot) => s_slots[slot].Ability?.abilityName;

    // The enemy gate and the hold-to-pause key flip constantly in combat, so they are left out of the logged status.
    private static State Describe(bool includeMomentary, out string detail)
    {
        detail = string.Empty;
        if (!s_active) return State.Off;
        if (s_blockedReason.Length > 0)
        {
            detail = s_blockedReason;
            return State.Waiting;
        }

        if (!s_suspended)
        {
            if (includeMomentary && s_holdToPauseHeld)
            {
                detail = S.HoldToPauseKey.Value + " held";
                return State.Holding;
            }
            if (includeMomentary && S.RequireEnemies.Value && !s_enemyGateOpen)
            {
                detail = "not enough enemies in range";
                return State.Holding;
            }
            return State.Active;
        }
        detail = S.PauseInNonCombatZones.Value && InNonCombatZone() ? "non-combat zone" : "press any skill to resume";
        return State.Paused;
    }

    private static string BuildStatus(bool includeMomentary)
    {
        State state = Describe(includeMomentary, out string detail);
        var text = new System.Text.StringBuilder(state.ToString().ToLowerInvariant());
        if (detail.Length > 0) text.Append(" (").Append(detail).Append(')');
        if (state == State.Off || state == State.Waiting) return text.ToString();

        text.Append("  ");
        for (int i = 0; i < SlotCount; i++)
        {
            text.Append("  ").Append(i + 1).Append(':').Append(IsSlotToggled(i) ? "ON" : "off");
            if (s_slots[i].Ability == null) text.Append('?');
        }
        return text.ToString();
    }

    public static void OnUpdate()
    {
        if (!S.Enabled.Value)
        {
            if (s_active) Deactivate();
            return;
        }

        s_active = true;
        UpdateState();
        LogStatusChanges();
    }

    private static void UpdateState()
    {
        if (!IsModifierHeld()) Array.Clear(s_toggleKeyDown);
        s_holdToPauseHeld = IsHoldToPauseKeyHeld();

        if (!Player.Exists)
        {
            s_blockedReason = "no player";
            ClearRefs();
            return;
        }

        EpochInputManager? input = Game.Input;
        if (input == null)
        {
            s_blockedReason = "input manager not found";
            return;
        }

        UseAbilityProcessor? processor = input.useAbilityProcessor;
        if (processor == null)
        {
            s_blockedReason = "ability processor not found";
            return;
        }

        s_blockedReason = string.Empty;

        // Ability details only refine timing (cooldowns, channelling); casting works without them.
        PlayerAbilityList? abilityList = ResolveRefs();
        if (abilityList != null) CheckFormChange(abilityList, processor);
        if (Time.unscaledTime >= s_nextSlotRefreshAt)
        {
            RefreshSlots(abilityList, processor);
        }
        PollToggleKeys(input);

        if (!s_suspended && S.PauseInNonCombatZones.Value && InNonCombatZone())
        {
            Suspend(processor);
        }
        TryResume();
        s_plainPressSeen = false;

        bool anyActive = false;
        for (int i = 0; i < SlotCount; i++)
        {
            anyActive |= s_slots[i].On || s_slots[i].Held;
        }

        UpdateEnemyGate();
        if (s_holdToPauseHeld || !s_enemyGateOpen)
        {
            // Told to hold, or nothing worth casting at: let go of any channel and wait.
            ReleaseHeldChannels(processor);
        }
        else if (anyActive)
        {
            CastActiveSlots(processor);
        }

        // Cosmetic only: a failure here must never stop casting.
        try
        {
            UpdateIcons();
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("autocast:icons", $"AutoCast icon update failed: {e.Message}", 30);
        }
    }

    /// <summary>Draws the enemy-gate range as a ring on the ground around the player.</summary>
    public static void OnGUI()
    {
        if (!s_active || !S.RequireEnemies.Value || !S.ShowRangeRing.Value) return;
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        if (s_playerTransform == null) return;

        Vector3 center = s_playerTransform.position;
        float radius = S.EnemyRange.Value;
        Color color = s_enemyGateOpen ? s_rangeRingCastingColor : s_rangeRingHoldingColor;

        Draw.Begin();
        try
        {
            Vector3 previous = center + new Vector3(radius, 0f, 0f);
            for (int i = 1; i <= RangeRingSegments; i++)
            {
                float angle = i * (2f * Mathf.PI / RangeRingSegments);
                Vector3 next = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                Draw.WorldLine(previous, next, color, RangeRingWidth);
                previous = next;
            }
        }
        finally
        {
            Draw.End();
        }
    }

    private static void LogStatusChanges()
    {
        if (Time.unscaledTime < s_nextStatusLogAt) return;
        s_nextStatusLogAt = Time.unscaledTime + StatusLogIntervalSeconds;

        string status = BuildStatus(includeMomentary: false);
        if (status == s_lastLoggedStatus) return;

        s_lastLoggedStatus = status;
        Log.Info($"AutoCast status: {status}");
    }

    public static void Start() => GameState.ZoneChanged += OnZoneChanged;

    private static void OnZoneChanged(ZoneChange change)
    {
        // An echo transition keeps the scene, so references stay valid; only the bar may change.
        if (change.Monolith)
        {
            if (!S.Enabled.Value) return;
            if (S.PauseOnZoneChange.Value) Suspend(TryGetProcessor());
            s_nextSlotRefreshAt = 0f;
            return;
        }

        ClearRefs();
        if (change.Kind == SceneKind.Menu)
        {
            ClearToggles();
        }
        else if (s_active && S.PauseOnZoneChange.Value)
        {
            Suspend(null);
        }
    }

    /// <summary>
    /// Called for every bar command the game itself issues (the player's real key presses).
    /// Returns false to swallow the command.
    /// </summary>
    private static bool OnRealBarCommand(bool keyDown, int abilityNumber)
    {
        int slot = abilityNumber - 1;
        if (slot < 0 || slot >= SlotCount) return true;

        if (!keyDown)
        {
            s_toggleKeyDown[slot] = false;
            return true;
        }

        if (IsModifierHeld())
        {
            // Toggle once per press, and swallow it so toggling doesn't also cast.
            if (!s_toggleKeyDown[slot])
            {
                s_toggleKeyDown[slot] = true;
                Toggle(slot);
            }
            return false;
        }

        s_plainPressSeen = true;
        return true;
    }

    // The game has a single Rewired player.
    private static Il2CppRewired.Player? GetRewiredPlayer()
    {
        if (s_rewiredPlayer == null && Il2CppRewired.ReInput.isReady)
        {
            s_rewiredPlayer = Il2CppRewired.ReInput.players.GetPlayer(0);
        }
        return s_rewiredPlayer;
    }

    // The first slot whose skill button the player is holding right now, or SlotCount if none.
    // Asked of the input system each frame, so a missed key-up can't leave a slot looking held.
    private static int FirstManuallyHeldSlot()
    {
        // With the modifier down a skill key toggles instead of casting, so it isn't a manual cast.
        if (IsModifierHeld()) return SlotCount;

        try
        {
            Il2CppRewired.Player? player = GetRewiredPlayer();
            if (player != null)
            {
                for (int i = 0; i < SlotCount; i++)
                {
                    if (player.GetButton(s_slotActionIds[i])) return i;
                }
            }
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("autocast:held", $"AutoCast held-skill check failed: {e.Message}", 30);
        }

        return SlotCount;
    }

    private static void RefreshSlotKeys()
    {
        try
        {
            var maps = GetRewiredPlayer()?.controllers?.maps;
            for (int i = 0; i < SlotCount; i++)
            {
                s_slotKeys[i] = maps == null ? KeyCode.None : FindBoundKey(maps, s_slotActionIds[i]);
            }
        }
        catch (Exception e)
        {
            // Without the bindings, toggling still works through the game's own bar commands.
            Array.Clear(s_slotKeys);
            Log.ErrorThrottled("autocast:binds", $"AutoCast keybind lookup failed: {e.Message}", 30);
        }
    }

    private static KeyCode FindBoundKey(Il2CppRewired.Player.ControllerHelper.MapHelper maps, int actionId)
    {
        var keyboard = maps.GetFirstElementMapWithAction(Il2CppRewired.ControllerType.Keyboard, actionId, true);
        if (keyboard != null && keyboard.keyCode != KeyCode.None) return keyboard.keyCode;

        var mouse = maps.GetFirstElementMapWithAction(Il2CppRewired.ControllerType.Mouse, actionId, true);
        if (mouse == null) return KeyCode.None;

        int button = mouse.elementIdentifierId - FirstMouseButtonElementId;
        return button >= 0 && button < MouseButtonCount ? KeyCode.Mouse0 + button : KeyCode.None;
    }

    // Toggles from the bound keys themselves, so a game binding on modifier+key can't block one.
    // OnRealBarCommand sees the same press whenever the game does issue it; s_toggleKeyDown keeps
    // the two from toggling twice.
    private static void PollToggleKeys(EpochInputManager input)
    {
        bool modifierHeld = IsModifierHeld();
        // Bindings are looked up as the modifier goes down, so a rebind is picked up on the next toggle.
        if (modifierHeld && !s_modifierWasHeld) RefreshSlotKeys();
        s_modifierWasHeld = modifierHeld;

        if (!modifierHeld || input.IsInputFieldActive) return;

        for (int i = 0; i < SlotCount; i++)
        {
            KeyCode key = s_slotKeys[i];
            if (key == KeyCode.None) continue;

            if (!Input.GetKey(key))
            {
                s_toggleKeyDown[i] = false;
            }
            else if (Input.GetKeyDown(key) && !s_toggleKeyDown[i])
            {
                s_toggleKeyDown[i] = true;
                Toggle(i);
            }
        }
    }

    /// <summary>Lets go of everything auto-cast holds: channels, toggles, tinted icons.</summary>
    public static void Shutdown()
    {
        GameState.ZoneChanged -= OnZoneChanged;
        Deactivate();
    }

    private static void Deactivate()
    {
        s_active = false;
        s_holdToPauseHeld = false;
        s_modifierWasHeld = false;
        s_blockedReason = string.Empty;
        s_lastLoggedStatus = string.Empty;
        ReleaseHeldChannels(TryGetProcessor());
        ClearToggles();
        ClearRefs();
        try
        {
            RestoreIcons();
        }
        catch (Exception)
        {
            // Icons are recreated by the game; nothing to restore if they are gone.
        }
    }

    private static UseAbilityProcessor? TryGetProcessor()
    {
        return Game.AbilityProcessor;
    }

    private static void ClearRefs()
    {
        s_playerActor = null;
        s_playerTransform = null;
        s_chargeManager = null;
        s_abilityState = null;
        s_treeData = null;
        s_nextSlotRefreshAt = 0f;
        s_nextEnemyScanAt = 0f;
        s_enemiesInRange = 0;
        s_enemyWeightInRange = 0f;
        s_closestEnemy = null;
        s_rewiredPlayer = null;
        Array.Clear(s_toggleKeyDown);
        for (int i = 0; i < SlotCount; i++)
        {
            s_slots[i].Ability = null;
            s_slots[i].Channelled = false;
            s_slots[i].Held = false;
        }
    }

    private static PlayerAbilityList? ResolveRefs()
    {
        try
        {
            if (s_playerActor == null)
            {
                s_playerActor = Player.Actor;
                s_playerTransform = null;
                s_chargeManager = null;
                s_abilityState = null;
            }

            if (s_playerActor != null)
            {
                if (s_playerTransform == null) s_playerTransform = s_playerActor.transform;
                if (s_chargeManager == null) s_chargeManager = s_playerActor.GetComponent<PlayerChargeManager>();
                if (s_abilityState == null) s_abilityState = s_playerActor.GetComponent<UsingAbilityPlayer>();
            }

            if (s_playerTransform == null) s_playerTransform = Player.Transform;

            if (s_treeData == null) s_treeData = Game.LocalTree;
            if (s_treeData == null) return null;

            PlayerAbilityList? abilityList = s_treeData.playerAbilityList;
            return abilityList == null ? null : abilityList;
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("autocast:refs", $"AutoCast reference lookup failed: {e.Message}", 30);
            return null;
        }
    }

    private static void RefreshSlots(PlayerAbilityList? abilityList, UseAbilityProcessor processor)
    {
        s_nextSlotRefreshAt = Time.unscaledTime + SlotRefreshIntervalSeconds;

        for (int i = 0; i < SlotCount; i++)
        {
            Ability? ability = FindSlotAbility(abilityList, i);
            bool channelled = ability != null && ability.channelled;

            // A slot that stops being a channel must not be left held.
            if (s_slots[i].Held && s_slots[i].Channelled && !channelled) StopChannel(i, processor);

            s_slots[i].Ability = ability;
            s_slots[i].Channelled = channelled;
        }

        ApplyFormToggles();
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

    private static void CheckFormChange(PlayerAbilityList abilityList, UseAbilityProcessor processor)
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

        if (formKey == s_formKey) return;

        ReleaseHeldChannels(processor);
        s_formKey = formKey;
        s_nextSlotRefreshAt = 0f;
    }

    private static void Toggle(int slot)
    {
        if (!s_togglesByForm.TryGetValue(s_formKey, out var toggles))
        {
            toggles = new bool[SlotCount];
            s_togglesByForm[s_formKey] = toggles;
        }

        toggles[slot] = !toggles[slot];

        // Switching a slot on is explicit intent: lift a zone-change pause (but not the non-combat gate).
        if (toggles[slot] && s_suspended && !(S.PauseInNonCombatZones.Value && InNonCombatZone()))
        {
            s_suspended = false;
            ApplyFormToggles();
        }
        else if (!s_suspended)
        {
            s_slots[slot].On = toggles[slot];
        }

        Log.Info($"AutoCast: slot {slot + 1} ({s_slots[slot].Ability?.abilityName ?? "?"}) {(toggles[slot] ? "ON" : "off")}{(s_suspended ? " (paused)" : string.Empty)}");
    }

    private static void ApplyFormToggles()
    {
        if (s_suspended) return;

        s_togglesByForm.TryGetValue(s_formKey, out var toggles);
        for (int i = 0; i < SlotCount; i++)
        {
            s_slots[i].On = toggles != null && toggles[i];
        }
    }

    private static void ClearToggles()
    {
        s_togglesByForm.Clear();
        s_suspended = false;
        s_formKey = HumanFormKey;
        for (int i = 0; i < SlotCount; i++)
        {
            s_slots[i].On = false;
            s_slots[i].Held = false;
            s_slots[i].NextPressAt = 0f;
        }
    }

    private static void Suspend(UseAbilityProcessor? processor)
    {
        if (!s_suspended) Log.Info($"AutoCast: paused (scene '{GameState.Scene}')");
        s_suspended = true;
        ReleaseHeldChannels(processor);
        for (int i = 0; i < SlotCount; i++)
        {
            s_slots[i].On = false;
            s_slots[i].NextPressAt = 0f;
        }
    }

    private static void TryResume()
    {
        if (!s_suspended) return;
        if (S.PauseInNonCombatZones.Value && InNonCombatZone()) return;

        // After a zone change, wait for the player to press any skill themselves before resuming.
        if (S.PauseOnZoneChange.Value && !s_plainPressSeen) return;

        s_suspended = false;
        ApplyFormToggles();
        Log.Info("AutoCast: resumed");
    }

    private static bool InNonCombatZone() => GameState.Kind == SceneKind.NonCombat;

    private static bool IsModifierHeld()
    {
        var key = S.ModifierKey.Value;
        return key != KeyCode.None && Input.GetKey(key);
    }

    private static bool IsHoldToPauseKeyHeld()
    {
        var key = S.HoldToPauseKey.Value;
        return key != KeyCode.None && Input.GetKey(key);
    }

    private static void CastActiveSlots(UseAbilityProcessor processor)
    {
        Vector3 targetPosition = GetTargetPosition(out Transform? hitTransform);

        // A skill the player is holding manually takes priority over every slot after it.
        int manualHoldIndex = FirstManuallyHeldSlot();

        bool focused = Application.isFocused;
        bool pressedThisFrame = false;
        for (int i = 0; i < SlotCount; i++)
        {
            ref Slot slot = ref s_slots[i];

            if (!slot.On)
            {
                ReleaseSlot(i, processor);
                continue;
            }

            if (!focused || i >= manualHoldIndex) continue;

            bool channelling = slot.Channelled && s_abilityState != null && s_abilityState.channelling;
            bool ready = !channelling && !IsOnCooldown(slot.Ability) && Time.unscaledTime >= slot.NextPressAt;
            if (ready)
            {
                // Only one new press per frame; lower slots win.
                if (pressedThisFrame) continue;

                SendBarCommand(processor, i, keyDown: true, targetPosition, hitTransform);
                pressedThisFrame = true;
                slot.Held = true;
                slot.NextPressAt = Time.unscaledTime + PressThrottleSeconds;
            }
            else
            {
                SendBarCommand(processor, i, keyDown: false, targetPosition, hitTransform);
            }

            if (channelling)
            {
                try
                {
                    s_abilityState!.TryUpdateTargetLocationIfChannelling(targetPosition);
                }
                catch (Exception)
                {
                    // Target refresh is best-effort.
                }
            }
        }
    }

    private static void UpdateEnemyGate()
    {
        bool gated = S.RequireEnemies.Value;
        if (!gated) s_enemyGateOpen = true;
        if (!gated && !S.AimAtClosestEnemy.Value)
        {
            s_closestEnemy = null;
            return;
        }

        if (Time.unscaledTime < s_nextEnemyScanAt) return;
        s_nextEnemyScanAt = Time.unscaledTime + EnemyScanIntervalSeconds;

        try
        {
            ScanEnemies();
        }
        catch (Exception e)
        {
            // If the scan breaks, fail open so auto-cast keeps working ungated.
            s_enemiesInRange = 0;
            s_enemyWeightInRange = 0f;
            s_closestEnemy = null;
            s_enemyGateOpen = true;
            Log.ErrorThrottled("autocast:enemies", $"AutoCast enemy scan failed: {e.Message}", 30);
            return;
        }

        s_enemyGateOpen = !gated || s_enemyWeightInRange >= S.EnemyMinWeight.Value;
    }

    private static void ScanEnemies()
    {
        int count = 0;
        float weight = 0f;
        ActorVisuals? closest = null;

        ActorManager? manager = Game.Actors;
        if (manager != null && s_playerTransform != null)
        {
            Vector3 playerPosition = s_playerTransform.position;
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

        s_enemiesInRange = count;
        s_enemyWeightInRange = weight;
        s_closestEnemy = closest;
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

    private static bool IsOnCooldown(Ability? ability)
    {
        if (ability == null || s_chargeManager == null) return false;

        try
        {
            return s_chargeManager.onCoooldown(ability);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Vector3 GetTargetPosition(out Transform? hitTransform)
    {
        hitTransform = null;
        if (s_playerTransform == null) return Vector3.zero;

        // The scan runs a few times a second, so make sure the target is still there and alive.
        try
        {
            if (S.AimAtClosestEnemy.Value && s_closestEnemy != null && !s_closestEnemy.dead
                && s_closestEnemy.gameObject.activeInHierarchy)
            {
                return s_closestEnemy.transform.position;
            }
        }
        catch (Exception)
        {
            s_closestEnemy = null;
        }

        try
        {
            return MouseManager.AbilityUseMousePoint(out hitTransform, out _, false, s_playerTransform);
        }
        catch (Exception)
        {
            return s_playerTransform.position;
        }
    }

    private static void SendBarCommand(UseAbilityProcessor processor, int slot, bool keyDown, Vector3 targetPosition, Transform? hitTransform)
    {
        s_fromAutoCast = true;
        try
        {
            processor.UseBarAbilityCommand(keyDown, slot + 1, -1, targetPosition, hitTransform, false);
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("autocast:send", $"AutoCast bar command failed: {e.Message}", 30);
        }
        finally
        {
            s_fromAutoCast = false;
        }
    }

    private static void ReleaseHeldChannels(UseAbilityProcessor? processor)
    {
        for (int i = 0; i < SlotCount; i++)
        {
            ReleaseSlot(i, processor);
        }
    }

    // Let go of a slot auto-cast pressed: send the key-up so the game doesn't treat it as still held.
    private static void ReleaseSlot(int i, UseAbilityProcessor? processor)
    {
        if (!s_slots[i].Held) return;

        s_slots[i].Held = false;
        if (processor != null)
        {
            Vector3 targetPosition = GetTargetPosition(out Transform? hitTransform);
            SendBarCommand(processor, i, keyDown: false, targetPosition, hitTransform);
        }
        if (s_slots[i].Channelled) StopChannel(i, processor);
    }

    private static void StopChannel(int slot, UseAbilityProcessor? processor)
    {
        try
        {
            var clientState = processor?.PlayerAbilityState?.TryCast<ClientUsingAbilityState>();
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
            if (s_abilityState != null) s_abilityState.stopUsingAbility(true);
        }
        catch (Exception)
        {
            // Nothing else to try; the channel ends on its own when the key state settles.
        }
    }

    private static void UpdateIcons()
    {
        bool[]? suspendedToggles = null;
        if (s_suspended) s_togglesByForm.TryGetValue(s_formKey, out suspendedToggles);

        bool anyTint = false;
        for (int i = 0; i < SlotCount && !anyTint; i++)
        {
            anyTint = s_slots[i].On || (suspendedToggles != null && suspendedToggles[i]);
        }

        if (!anyTint)
        {
            if (s_iconsTinted) RestoreIcons();
            return;
        }

        var icons = Game.BarIcons;
        if (icons == null) return;

        float pulse = (Mathf.Sin(Time.unscaledTime * 5f) + 1f) * 0.5f;
        float scale = Mathf.Lerp(0.92f, 1.07f, pulse);
        var activeColor = new Color(1f, Mathf.Lerp(1f, 0.75f, pulse), Mathf.Lerp(1f, 0.4f, pulse), 1f);
        var suspendedColor = new Color(0.55f, 0.75f, 1f, Mathf.Lerp(0.55f, 0.85f, pulse));

        for (int i = 0; i < icons.Count; i++)
        {
            AbilityBarIcon? barIcon = icons[i];
            if (barIcon == null) continue;

            int slot = barIcon.abilityNumber - 1;
            bool inRange = slot >= 0 && slot < SlotCount;
            bool toggled = inRange && s_slots[slot].On;
            bool on = toggled && !s_holdToPauseHeld;
            bool paused = inRange && !on && (toggled || (suspendedToggles != null && suspendedToggles[slot]));

            barIcon.transform.localScale = on ? new Vector3(scale, scale, 1f) : Vector3.one;

            var image = barIcon.icon;
            if (image != null)
            {
                image.color = on ? activeColor : paused ? suspendedColor : Color.white;
            }
        }

        s_iconsTinted = true;
    }

    private static void RestoreIcons()
    {
        s_iconsTinted = false;

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

    // Every skill key press reaches the game through this method (__0 = keyDown, __1 = ability number).
    [HarmonyPatch(typeof(UseAbilityProcessor), nameof(UseAbilityProcessor.UseBarAbilityCommand))]
    private static class UseBarAbilityCommand_Patch
    {
        private static bool Prefix(bool __0, int __1)
        {
            if (s_fromAutoCast || !S.Enabled.Value) return true;

            try
            {
                return OnRealBarCommand(__0, __1);
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("autocast:prefix", $"AutoCast key handling failed: {e.Message}", 30);
                return true;
            }
        }
    }

    // Auto-casts are attempted constantly; don't let them spam the "cannot use skill" voice line.
    [HarmonyPatch(typeof(PlayerVoiceEventManager), nameof(PlayerVoiceEventManager.playVoiceLine))]
    private static class PlayVoiceLine_Patch
    {
        private static bool Prefix(PlayerVoiceEventManager.VoiceLineEvent __1)
        {
            return !s_fromAutoCast || __1 != PlayerVoiceEventManager.VoiceLineEvent.CannotUseSkill;
        }
    }
}
