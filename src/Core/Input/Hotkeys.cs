using Il2Cpp;
using UnityEngine;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

/// <summary>
/// The menu's navigation keys pressed this frame (fixed keys, not actions): Move from Tab/Shift+Tab and Down/Up,
/// Activate from Enter and Space, Adjust from Left/Right, Page from PageUp/PageDown. Each is -1, 0 or +1.
/// </summary>
internal readonly record struct MenuNav(int Move, bool Activate, int Adjust, int Page)
{
    public bool Any => Move != 0 || Activate || Adjust != 0 || Page != 0;
}

/// <summary>
/// The one reader of the mod's keys. Once per frame it polls Unity's input and runs the action of every press binding
/// whose chord went down, answers whether hold bindings are held, fires the slot-toggle chords (a modifier plus the
/// key the game binds to a slot), drives key capture for the settings page, and handles Esc for the menu.
/// Nothing fires while a capture is open, while typing (a game field, the chat box, a mod field), while the window
/// lacks focus, or, for gameplay bindings, outside a playable zone. Keys already down when focus returns are ignored
/// until released, so clicking back into the game with a key held fires nothing.
/// </summary>
internal static class Hotkeys
{
    public const int SlotCount = Dispatcher.SlotCount;

    // The game's input action for each bar slot, for looking up the key bound to it.
    private static readonly int[] s_slotActionIds =
    {
        (int)EpochInputManager.InputActions.Ability1,
        (int)EpochInputManager.InputActions.Ability2,
        (int)EpochInputManager.InputActions.Ability3,
        (int)EpochInputManager.InputActions.Ability4,
        (int)EpochInputManager.InputActions.Ability5,
    };
    // Rewired numbers mouse buttons from 3 (left); 0-2 are the axes and the wheel.
    private const int FirstMouseButtonElementId = 3;
    private const int MouseButtonCount = 7;

    private static bool s_running;
    private static Hotkey[] s_bindings = Array.Empty<Hotkey>();
    private static int s_bindingGroups = -1;
    private static KeyCode[]? s_allKeys;
    private static readonly HashSet<KeyCode> s_ignored = new();
    private static bool s_wasFocused = true;
    private static int s_heldModifiers;

    // A modifiers-only press chord fires as its modifiers are let go, unless another key went down while they were held.
    private static int s_peakModifiers;
    private static bool s_modifierPressSpent;
    private static int s_releasedModifiers;

    // Slot-toggle chords, derived from the client's modifier and the game's slot bindings.
    private static Func<KeyCode>? s_slotModifier;
    private static string? s_slotActionPrefix;
    private static readonly KeyCode[] s_slotKeys = new KeyCode[SlotCount];
    private static bool s_slotKeysFresh;

    private static int s_capturePeak;

    /// <summary>Off when the game lacks the binding lookup (set by HotkeysFeature): no slot-toggle chords or game conflicts.</summary>
    public static bool CanReadGameBindings = true;

    /// <summary>Navigation keys for the menu this frame; none unless the menu is open and no capture is.</summary>
    public static MenuNav MenuNav { get; private set; }
    /// <summary>Counts frames with navigation keys, so the menu applies each exactly once.</summary>
    public static int MenuNavCount { get; private set; }

    /// <summary>The setting being rebound, or null.</summary>
    public static Setting? Capturing { get; private set; }
    public static bool CaptureActive => Capturing != null;

    /// <summary>Every Hotkey setting of every registered group.</summary>
    public static IReadOnlyList<Hotkey> Bindings
    {
        get
        {
            if (s_bindingGroups != SettingsRegistry.Groups.Count)
            {
                s_bindings = SettingsRegistry.Groups.SelectMany(g => g.All).OfType<Hotkey>().ToArray();
                s_bindingGroups = SettingsRegistry.Groups.Count;
            }
            return s_bindings;
        }
    }

    /// <summary>Keys ignored since focus came back, until released.</summary>
    public static IEnumerable<KeyCode> Ignored => s_ignored;

    private static KeyCode[] AllKeys => s_allKeys ??= Enum.GetValues<KeyCode>()
        .Where(k => k != KeyCode.None && k != KeyCode.Mouse0 && k != KeyCode.Mouse1 && k < KeyCode.JoystickButton0)
        .Distinct().ToArray();

    internal static void Start() => s_running = true;

    internal static void Reset()
    {
        s_running = false;
        Capturing = null;
        s_ignored.Clear();
        s_wasFocused = true;
        s_heldModifiers = s_peakModifiers = s_releasedModifiers = 0;
        s_modifierPressSpent = false;
        s_slotKeysFresh = false;
        Array.Clear(s_slotKeys);
    }

    internal static void Update()
    {
        MenuNav = default;
        bool focused = Application.isFocused;
        if (focused && !s_wasFocused) IgnoreKeysDown();
        s_wasFocused = focused;
        if (s_ignored.Count > 0) s_ignored.RemoveWhere(k => !Input.GetKey(k));
        if (!focused) return;

        s_heldModifiers = HeldModifiers();
        TrackModifierPress();

        // Esc closes the menu even from the mod's own fields, but never takes a key meant for a game field or chat.
        if (Input.GetKeyDown(KeyCode.Escape) && !GameState.GameTyping)
        {
            if (Capturing != null)
            {
                CancelCapture();
                return;
            }
            if (MenuOpen) Actions.Run("menu.close", "key Escape");
        }
        if (Capturing != null)
        {
            UpdateCapture();
            return;
        }
        // The menu's input lock keeps these from the game. In the menu's own field only Tab is ours (it leaves the field).
        if (MenuOpen && !GameState.GameTyping) MenuNav = ReadMenuNav(fieldFocused: GameState.UiTyping);
        if (MenuNav.Any) MenuNavCount++;
        if (GameState.Typing) return;

        bool inGame = GameState.InGame;
        UpdateSlotChords(inGame);
        foreach (var binding in Bindings)
        {
            if (binding.Kind != HotkeyKind.Press || binding.Action == null) continue;
            if (binding.Scope == HotkeyScope.Gameplay && !inGame) continue;
            Chord chord = binding.Chord;
            if (Pressed(chord)) Actions.Run(binding.Action, $"key {chord.Text}");
        }
    }

    /// <summary>Whether a hold binding is held: every key of its chord down (extra keys allowed), and its gates open.</summary>
    public static bool Held(Hotkey hotkey)
    {
        Chord chord = hotkey.Chord;
        if (chord.IsEmpty || !Open(hotkey.Scope)) return false;
        for (int i = 0; i < Chord.ModifierKeys.Length; i++)
        {
            if ((chord.Modifiers & (1 << i)) != 0 && !Down(Chord.ModifierKeys[i])) return false;
        }
        return chord.Main == KeyCode.None || Down(chord.Main);
    }

    private static bool Open(HotkeyScope scope) =>
        s_running && Capturing == null && !GameState.Typing && Application.isFocused && (scope == HotkeyScope.Global || GameState.InGame);

    private static bool Down(KeyCode key) => Input.GetKey(key) && !s_ignored.Contains(key);

    private static MenuNav ReadMenuNav(bool fieldFocused)
    {
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        int move = Input.GetKeyDown(KeyCode.Tab) ? (shift ? -1 : 1) : 0;
        if (fieldFocused) return new MenuNav(move, false, 0, 0);
        if (Input.GetKeyDown(KeyCode.DownArrow)) move = 1;
        else if (Input.GetKeyDown(KeyCode.UpArrow)) move = -1;
        bool activate = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space);
        int adjust = Input.GetKeyDown(KeyCode.RightArrow) ? 1 : Input.GetKeyDown(KeyCode.LeftArrow) ? -1 : 0;
        int page = Input.GetKeyDown(KeyCode.PageDown) ? 1 : Input.GetKeyDown(KeyCode.PageUp) ? -1 : 0;
        return new MenuNav(move, activate, adjust, page);
    }

    private static bool MenuOpen => Actions.Get("menu.toggle") is { } toggle && Actions.StateOf(toggle) == true;

    // A press fires on its main key's down with exactly its modifiers held (left and right apart).
    private static bool Pressed(Chord chord)
    {
        if (chord.IsEmpty) return false;
        if (chord.ModifiersOnly) return s_releasedModifiers == chord.Modifiers;
        return Input.GetKeyDown(chord.Main) && !s_ignored.Contains(chord.Main) && s_heldModifiers == chord.Modifiers;
    }

    private static int HeldModifiers()
    {
        int held = 0;
        for (int i = 0; i < Chord.ModifierKeys.Length; i++)
        {
            if (Down(Chord.ModifierKeys[i])) held |= 1 << i;
        }
        return held;
    }

    private static void TrackModifierPress()
    {
        s_releasedModifiers = 0;
        bool modifierWentDown = false;
        foreach (KeyCode key in Chord.ModifierKeys) modifierWentDown |= Input.GetKeyDown(key);
        // Any other key or a mouse button while modifiers are held makes it a chord, not a modifier press.
        if (s_peakModifiers != 0 && !modifierWentDown && !s_modifierPressSpent && OtherKeyWentDown()) s_modifierPressSpent = true;
        // Typing doesn't count as a modifier press either.
        if (GameState.Typing) s_modifierPressSpent = true;

        s_peakModifiers |= s_heldModifiers;
        if (s_peakModifiers != 0 && (s_heldModifiers & s_peakModifiers) != s_peakModifiers && !s_modifierPressSpent)
        {
            s_releasedModifiers = s_peakModifiers;
            s_modifierPressSpent = true;
        }
        if (s_heldModifiers == 0)
        {
            s_peakModifiers = 0;
            s_modifierPressSpent = false;
        }
    }

    // Input.anyKeyDown reads true on every frame in this game, so the keys are checked one by one (only while a
    // modifier is held).
    private static bool OtherKeyWentDown()
    {
        if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1)) return true;
        foreach (KeyCode key in AllKeys)
        {
            if (Chord.ModifierBit(key) == null && Input.GetKeyDown(key)) return true;
        }
        return false;
    }

    private static void IgnoreKeysDown()
    {
        foreach (KeyCode key in AllKeys)
        {
            if (Input.GetKey(key)) s_ignored.Add(key);
        }
        if (s_ignored.Count > 0) Log.Info($"hotkeys: ignoring {string.Join(", ", s_ignored)} until released (held when focus came back)");
    }

    // ---- Slot-toggle chords ----

    /// <summary>
    /// Sets where slot-toggle chords come from: the modifier to hold, and the action prefix (slot N runs prefix + N).
    /// Null turns them off.
    /// </summary>
    public static void SetSlotChords(Func<KeyCode>? modifier, string? actionPrefix)
    {
        s_slotModifier = modifier;
        s_slotActionPrefix = actionPrefix;
        s_slotKeysFresh = false;
    }

    /// <summary>The game's bindings may have changed (a new scene); look them up again on next use.</summary>
    internal static void SetSlotChordsStale() => s_slotKeysFresh = false;

    /// <summary>True while the slot-toggle modifier is held, so the slot's key toggles instead of casting.</summary>
    public static bool SlotChordEngaged(int slot)
    {
        KeyCode modifier = SlotModifier;
        return modifier != KeyCode.None && Down(modifier);
    }

    /// <summary>Whether the key the game binds to a slot is known; when it isn't, the bar command itself has to toggle.</summary>
    public static bool SlotKeyKnown(int slot)
    {
        if (!s_slotKeysFresh) RefreshSlotKeys();
        return slot >= 0 && slot < SlotCount && s_slotKeys[slot] != KeyCode.None;
    }

    /// <summary>The slot-toggle chord for each slot with a known key, for conflicts and the bridge.</summary>
    public static IEnumerable<(int Slot, Chord Chord)> SlotChords()
    {
        KeyCode modifier = SlotModifier;
        if (modifier == KeyCode.None || Chord.ModifierBit(modifier) is not int bit) yield break;
        if (!s_slotKeysFresh) RefreshSlotKeys();
        for (int i = 0; i < SlotCount; i++)
        {
            if (s_slotKeys[i] != KeyCode.None) yield return (i, new Chord(1 << bit, s_slotKeys[i]));
        }
    }

    internal static KeyCode SlotModifier
    {
        get
        {
            try
            {
                return s_slotModifier?.Invoke() ?? KeyCode.None;
            }
            catch (Exception)
            {
                return KeyCode.None;
            }
        }
    }

    private static void UpdateSlotChords(bool inGame)
    {
        KeyCode modifier = SlotModifier;
        bool held = modifier != KeyCode.None && Down(modifier);
        // Bindings are looked up as the modifier goes down, so a rebind is picked up on the next toggle.
        if (!held)
        {
            s_slotKeysFresh = false;
            return;
        }
        if (!s_slotKeysFresh) RefreshSlotKeys();
        if (!inGame || s_slotActionPrefix == null || Chord.ModifierBit(modifier) is not int bit || s_heldModifiers != 1 << bit) return;

        for (int i = 0; i < SlotCount; i++)
        {
            KeyCode key = s_slotKeys[i];
            if (key == KeyCode.None || s_ignored.Contains(key) || !Input.GetKeyDown(key)) continue;
            Actions.Run(s_slotActionPrefix + (i + 1), $"key {modifier}+{key}");
        }
    }

    private static void RefreshSlotKeys()
    {
        s_slotKeysFresh = true;
        try
        {
            var maps = GameMaps();
            for (int i = 0; i < SlotCount; i++)
            {
                s_slotKeys[i] = maps == null ? KeyCode.None : FindBoundKey(maps, s_slotActionIds[i]);
            }
        }
        catch (Exception e)
        {
            // Without the bindings, toggling still works through the game's own bar commands.
            Array.Clear(s_slotKeys);
            Log.ErrorThrottled("hotkeys:binds", $"Hotkeys keybind lookup failed: {e.Message}", 30);
        }
    }

    /// <summary>The game's control maps for its single input player, or null when they can't be read.</summary>
    internal static Il2CppRewired.Player.ControllerHelper.MapHelper? GameMaps()
    {
        if (!CanReadGameBindings || !Il2CppRewired.ReInput.isReady) return null;
        return Il2CppRewired.ReInput.players.GetPlayer(0)?.controllers?.maps;
    }

    /// <summary>The first key or mouse button the game binds to an input action, or None.</summary>
    internal static KeyCode FindBoundKey(Il2CppRewired.Player.ControllerHelper.MapHelper maps, int actionId)
    {
        var keyboard = maps.GetFirstElementMapWithAction(Il2CppRewired.ControllerType.Keyboard, actionId, true);
        if (keyboard != null && keyboard.keyCode != KeyCode.None) return keyboard.keyCode;

        var mouse = maps.GetFirstElementMapWithAction(Il2CppRewired.ControllerType.Mouse, actionId, true);
        if (mouse == null) return KeyCode.None;

        int button = mouse.elementIdentifierId - FirstMouseButtonElementId;
        return button >= 0 && button < MouseButtonCount ? KeyCode.Mouse0 + button : KeyCode.None;
    }

    // ---- Capture ----

    /// <summary>Starts rebinding a setting (a Hotkey, or a plain Key). One capture at a time.</summary>
    public static void BeginCapture(Setting setting)
    {
        Capturing = setting;
        s_capturePeak = 0;
    }

    public static void CancelCapture()
    {
        Capturing = null;
        s_capturePeak = 0;
    }

    // Modifiers held become the prefix; the first other key down commits; a modifier let go before any other key
    // commits the modifiers alone. Mouse buttons only for hold bindings; the menu closing cancels.
    private static void UpdateCapture()
    {
        var target = Capturing;
        if (!MenuOpen)
        {
            CancelCapture();
            return;
        }

        int held = s_heldModifiers;
        s_capturePeak |= held;
        foreach (KeyCode key in AllKeys)
        {
            if (key == KeyCode.Escape || !Input.GetKeyDown(key)) continue;
            switch (target)
            {
                case Hotkey hotkey when Chord.ModifierBit(key) == null && Chord.AllowedMain(key, hotkey.Kind):
                    Commit(hotkey, new Chord(held, key));
                    return;
                case Key plain when key < KeyCode.Mouse0:
                    plain.Value = key;
                    CancelCapture();
                    return;
            }
        }

        if (target is Hotkey modifiersOnly && s_capturePeak != 0 && (held & s_capturePeak) != s_capturePeak)
            Commit(modifiersOnly, new Chord(s_capturePeak, KeyCode.None));
    }

    private static void Commit(Hotkey hotkey, Chord chord)
    {
        CancelCapture();
        if (chord.IsEmpty && !hotkey.Clearable) return;
        hotkey.Value = chord.Text;
        // The keys just pressed shouldn't also fire the binding they were bound to.
        foreach (KeyCode key in chord.Keys)
        {
            if (Input.GetKey(key)) s_ignored.Add(key);
        }
        s_modifierPressSpent = true;
        Log.Info($"hotkeys: {hotkey.Section.Group.Name} {hotkey.Id} = {(chord.IsEmpty ? "(none)" : chord.Text)}");
    }

    // ---- While the layer isn't running ----

    /// <summary>For the menu when this layer is down: whether its key's main key went down, read directly as before.</summary>
    public static bool FallbackPressed(Hotkey hotkey) => hotkey.Chord.Main != KeyCode.None && Input.GetKeyDown(hotkey.Chord.Main);

    public static bool FallbackEscape() => Input.GetKeyDown(KeyCode.Escape);
}

internal sealed class HotkeysFeature : Feature
{
    public override string Id => "hotkeys";
    public override string Name => "Hotkeys";
    // Optional: without the game's binding lookup there are no slot-toggle chords (the bar command still toggles)
    // and no warnings about keys the game uses.
    private const string GameBindings = "game bindings";

    public override string[] Needs => new[] { "game" };
    public override Requirement[] Requirements => new[]
    {
        Need(typeof(Il2CppRewired.ReInput), nameof(Il2CppRewired.ReInput.isReady), GameBindings),
        Need(typeof(Il2CppRewired.ReInput), nameof(Il2CppRewired.ReInput.players), GameBindings),
        Need<Il2CppRewired.Player.ControllerHelper.MapHelper>(nameof(Il2CppRewired.Player.ControllerHelper.MapHelper.GetFirstElementMapWithAction), GameBindings),
        Need<Il2CppRewired.ActionElementMap>(nameof(Il2CppRewired.ActionElementMap.keyCode), GameBindings),
        Need<Il2CppRewired.ActionElementMap>(nameof(Il2CppRewired.ActionElementMap.elementIdentifierId), GameBindings),
    };

    public override void Start()
    {
        Hotkeys.CanReadGameBindings = Has(GameBindings);
        Hotkeys.Start();
        PauseRules.CaptureActive = () => Hotkeys.CaptureActive;
    }

    public override void Update() => Hotkeys.Update();
    public override void OnSceneChanged() => Hotkeys.SetSlotChordsStale();

    public override void Shutdown()
    {
        PauseRules.CaptureActive = () => false;
        Hotkeys.Reset();
    }
}
