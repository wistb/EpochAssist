using Il2Cpp;
using UnityEngine;

namespace EpochAssist.Core;

/// <summary>
/// Warnings for a binding, shown under its row: another of the mod's bindings (or a slot-toggle chord) whose keys would
/// trigger it or be triggered by it, and game actions whose first keyboard or mouse binding is its main key. Worked out
/// when a binding changes or the menu opens, not every frame.
/// </summary>
internal static class HotkeyConflicts
{
    private static readonly Dictionary<Hotkey, (string Signature, List<string> Warnings)> s_cache = new();
    private static int s_generation;

    /// <summary>Forget every result, so the game's bindings are looked up again (call when the menu opens).</summary>
    public static void Invalidate()
    {
        s_generation++;
        Hotkeys.SetSlotChordsStale();
    }

    public static IReadOnlyList<string> For(Hotkey hotkey)
    {
        // Any binding's change can make or end a conflict with this one.
        string signature = $"{s_generation}|{Hotkeys.SlotModifier}|{string.Join("|", Hotkeys.Bindings.Select(b => b.Value))}|{string.Join("|", ConditionKeys.All.Select(k => k.Chord))}";
        if (s_cache.TryGetValue(hotkey, out var cached) && cached.Signature == signature) return cached.Warnings;

        List<string> warnings;
        try
        {
            warnings = Compute(hotkey);
        }
        catch (Exception e)
        {
            warnings = new List<string>();
            Log.ErrorThrottled("hotkeys:conflicts", $"Hotkey conflict check failed: {e.Message}", 30);
        }
        s_cache[hotkey] = (signature, warnings);
        return warnings;
    }

    private static readonly Dictionary<(string Chord, bool Toggle), (string Signature, List<string> Warnings)> s_conditionCache = new();

    /// <summary>Warnings for a key a condition uses: the mod's bindings, slot chords and game actions it overlaps.
    /// Worked out again when a binding changes or the menu opens.</summary>
    public static IReadOnlyList<string> ForCondition(string chordText, bool toggle)
    {
        string signature = $"{s_generation}|{Hotkeys.SlotModifier}|{string.Join("|", Hotkeys.Bindings.Select(b => b.Value))}";
        if (s_conditionCache.TryGetValue((chordText, toggle), out var cached) && cached.Signature == signature) return cached.Warnings;
        var warnings = new List<string>();
        s_conditionCache[(chordText, toggle)] = (signature, warnings);
        Chord chord = ConditionKinds.ChordOf(chordText);
        if (chord.IsEmpty) return warnings;
        var kind = toggle ? HotkeyKind.Press : HotkeyKind.Hold;
        try
        {
            foreach (var other in Hotkeys.Bindings)
            {
                if (Overlaps(kind, chord, other.Kind, other.Chord)) warnings.Add($"Also {other.Label.ToLowerInvariant()} ({other.Chord.Display})");
            }
            foreach (var (slot, slotChord) in Hotkeys.SlotChords())
            {
                if (Overlaps(kind, chord, HotkeyKind.Press, slotChord)) warnings.Add($"Also toggles slot {slot + 1} ({slotChord.Display})");
            }
            var maps = Hotkeys.GameMaps();
            if (maps != null && !chord.ModifiersOnly)
            {
                foreach (EpochInputManager.InputActions action in Enum.GetValues(typeof(EpochInputManager.InputActions)))
                {
                    if (Hotkeys.FindBoundKey(maps, (int)action) == chord.Main) warnings.Add($"The game uses {chord.Main} for {action}");
                }
            }
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("hotkeys:conflicts", $"Hotkey conflict check failed: {e.Message}", 30);
        }
        return warnings;
    }

    private static List<string> Compute(Hotkey hotkey)
    {
        var warnings = new List<string>();
        Chord chord = hotkey.Chord;
        if (chord.IsEmpty) return warnings;

        foreach (var other in Hotkeys.Bindings)
        {
            if (other != hotkey && Overlaps(hotkey.Kind, chord, other.Kind, other.Chord)) warnings.Add($"Also {other.Label.ToLowerInvariant()} ({other.Chord.Display})");
        }
        foreach (var (slot, slotChord) in Hotkeys.SlotChords())
        {
            if (Overlaps(hotkey.Kind, chord, HotkeyKind.Press, slotChord)) warnings.Add($"Also toggles slot {slot + 1} ({slotChord.Display})");
        }
        foreach (var use in ConditionKeys.All)
        {
            Chord used = ConditionKinds.ChordOf(use.Chord);
            if (Overlaps(hotkey.Kind, chord, use.Toggle ? HotkeyKind.Press : HotkeyKind.Hold, used)) warnings.Add($"Also a {use.Label} ({used.Display})");
        }

        var maps = Hotkeys.GameMaps();
        if (maps == null) return warnings;
        // A chord of modifiers alone is compared key by key.
        var keys = chord.ModifiersOnly ? chord.Keys.ToList() : new List<KeyCode> { chord.Main };
        foreach (EpochInputManager.InputActions action in Enum.GetValues(typeof(EpochInputManager.InputActions)))
        {
            KeyCode bound = Hotkeys.FindBoundKey(maps, (int)action);
            if (bound != KeyCode.None && keys.Contains(bound)) warnings.Add($"The game uses {bound} for {action}");
        }
        return warnings;
    }

    // Press with press: the same chord. Hold with anything: its keys are all part of the other's (a hold allows extra keys).
    private static bool Overlaps(HotkeyKind kindA, Chord a, HotkeyKind kindB, Chord b)
    {
        if (a.IsEmpty || b.IsEmpty) return false;
        if (kindA == HotkeyKind.Press && kindB == HotkeyKind.Press) return a.Equals(b);
        if (kindA == HotkeyKind.Hold && kindB == HotkeyKind.Hold) return Within(a, b) || Within(b, a);
        return kindA == HotkeyKind.Hold ? Within(a, b) : Within(b, a);
    }

    private static bool Within(Chord inner, Chord outer) =>
        (inner.Modifiers & ~outer.Modifiers) == 0 && (inner.Main == KeyCode.None || inner.Main == outer.Main);
}
