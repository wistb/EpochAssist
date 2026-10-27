using System.Text.Json.Nodes;
using EpochAssist.Core;
using S = EpochAssist.Features.MacroSettings;

namespace EpochAssist.Features;

internal static partial class SlotMacros
{
    public static bool IsSlotToggled(int slot) => CurrentToggles()[slot];

    // The toggles of the current form; read only.
    private static bool[] CurrentToggles()
    {
        var run = s_run;
        return run != null && run.TogglesByForm.TryGetValue(run.FormKey, out var toggles) ? toggles : s_noToggles;
    }

    // Slots toggle only while Run macros is on; switching it off forgets them.
    private static void ToggleSlot(int slot)
    {
        if (s_run is { } run && S.Enabled.Value) Toggle(run, slot);
    }

    // The character's slot toggles from its profile (none without one), replacing the current ones.
    private static void LoadToggles(Run run)
    {
        run.ProfileGeneration = Profiles.Generation;
        if (run.Client != null) Dispatcher.Release(run.Client);
        run.TogglesByForm.Clear();
        if (Profiles.Read(SlotsKey) is not JsonObject forms) return;
        foreach (var (form, node) in forms)
        {
            if (!int.TryParse(form, out int key) || node is not JsonArray slots || slots.Count != SlotCount) continue;
            var toggles = new bool[SlotCount];
            for (int i = 0; i < SlotCount; i++) toggles[i] = slots[i] is JsonValue v && v.TryGetValue(out bool on) && on;
            run.TogglesByForm[key] = toggles;
        }
        if (run.EntryPaused && CurrentToggles().Any(on => on)) Toasts.Show("macros", "Macros paused. Use a skill to resume.", ToastKind.Info);
    }

    // Kept in the character's profile while one is loaded.
    private static void SaveToggles(Run run)
    {
        if (!Profiles.HasCharacter) return;
        var forms = new JsonObject();
        foreach (var (form, toggles) in run.TogglesByForm)
        {
            var slots = new JsonArray();
            foreach (bool on in toggles) slots.Add(on);
            forms[form.ToString()] = slots;
        }
        Profiles.Write(SlotsKey, forms);
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

    private static void Toggle(Run run, int slot)
    {
        if (!run.TogglesByForm.TryGetValue(run.FormKey, out var toggles))
        {
            toggles = new bool[SlotCount];
            run.TogglesByForm[run.FormKey] = toggles;
        }

        toggles[slot] = !toggles[slot];

        // Switching a slot on is explicit intent: lift a zone-change or entry pause (the non-combat pause stays).
        if (toggles[slot] && (run.ZonePaused || run.EntryPaused))
        {
            run.ZonePaused = run.EntryPaused = false;
            Log.Info("Macros: resumed");
        }
        SaveToggles(run);

        bool paused = run.Client != null && Dispatcher.StatusOf(run.Client, RuleKind.Momentary).State == StatusState.Paused;
        Log.Info($"Macros: slot {slot + 1} ({run.Slots[slot].Ability?.abilityName ?? "?"}) {(toggles[slot] ? "ON" : "off")}{(paused ? " (paused)" : string.Empty)}");
    }

    private static void ClearToggles(Run run)
    {
        run.TogglesByForm.Clear();
        run.ZonePaused = run.EntryPaused = false;
        run.FormKey = HumanFormKey;
    }
}
