using System.Text.Json.Nodes;
using UnityEngine;

namespace EpochAssist.Core;

// Never stored: a hotkey stores only its chord text.
internal enum HotkeyKind { Press, Hold }
internal enum HotkeyScope { Global, Gameplay }

/// <summary>
/// A key binding, stored as chord text: Unity KeyCode names joined by '+', modifiers first (LeftControl, RightControl,
/// LeftShift, RightShift, LeftAlt, RightAlt), then at most one main key; empty is unbound. A press binding runs its
/// action; a hold binding is asked whether it is held. The hotkey layer (Hotkeys) reads it.
/// </summary>
internal sealed class Hotkey : Setting<string>
{
    private string? _parsedText;
    private Chord _chord;

    public Hotkey(Section section, string id, string label, string defaultValue, HotkeyKind kind, HotkeyScope scope)
        : base(section, id, label, defaultValue)
    {
        Kind = kind;
        Scope = scope;
    }

    public HotkeyKind Kind { get; }
    public HotkeyScope Scope { get; }
    /// <summary>May be left unbound.</summary>
    public bool Clearable { get; init; }
    /// <summary>The action a press binding runs.</summary>
    public string? Action { get; init; }

    /// <summary>The parsed chord; empty when unbound or unparsable.</summary>
    public Chord Chord
    {
        get
        {
            string text = Value;
            if (!ReferenceEquals(text, _parsedText))
            {
                _chord = Chord.TryParse(text, Kind, out var chord) ? chord : default;
                _parsedText = text;
            }
            return _chord;
        }
    }

    public string Display => Chord.IsEmpty ? "Not set" : Chord.Display;

    internal override void Clamp()
    {
        string value = Value;
        if (Chord.TryParse(value, Kind, out var chord) && (Clearable || !chord.IsEmpty))
        {
            Value = chord.Text;
            return;
        }
        Log.Warn($"{Section.Group.Name} setting {Id}: '{value}' isn't a usable key, so it is back to '{Default}'");
        Reset();
    }

    protected override string Show(string value) => Chord.TryParse(value, Kind, out var chord) && !chord.IsEmpty ? chord.Display : "Not set";

    protected override bool TryRead(JsonValue json, out string value)
    {
        value = string.Empty;
        if (!json.TryGetValue(out string? text) || !Chord.TryParse(text, Kind, out var chord) || (!Clearable && chord.IsEmpty)) return false;
        value = chord.Text;
        return true;
    }
}

/// <summary>A parsed key chord: modifier keys (left and right apart) and at most one main key.</summary>
internal readonly struct Chord : IEquatable<Chord>
{
    /// <summary>Modifier keys in their canonical order; bit i of Modifiers is ModifierKeys[i].</summary>
    public static readonly KeyCode[] ModifierKeys =
    {
        KeyCode.LeftControl, KeyCode.RightControl, KeyCode.LeftShift, KeyCode.RightShift, KeyCode.LeftAlt, KeyCode.RightAlt,
    };
    private static readonly string[] s_modifierNames = { "Left Ctrl", "Right Ctrl", "Left Shift", "Right Shift", "Left Alt", "Right Alt" };

    public Chord(int modifiers, KeyCode main)
    {
        Modifiers = modifiers;
        Main = main;
    }

    public int Modifiers { get; }
    /// <summary>KeyCode.None for a chord of modifiers only.</summary>
    public KeyCode Main { get; }

    public bool IsEmpty => Modifiers == 0 && Main == KeyCode.None;
    public bool ModifiersOnly => Modifiers != 0 && Main == KeyCode.None;

    /// <summary>Every key of the chord, modifiers first.</summary>
    public IEnumerable<KeyCode> Keys
    {
        get
        {
            for (int i = 0; i < ModifierKeys.Length; i++)
            {
                if ((Modifiers & (1 << i)) != 0) yield return ModifierKeys[i];
            }
            if (Main != KeyCode.None) yield return Main;
        }
    }

    /// <summary>The stored form.</summary>
    public string Text => string.Join("+", Keys.Select(k => k.ToString()));

    /// <summary>For the menu: "Left Ctrl + F2".</summary>
    public string Display => string.Join(" + ", Keys.Select(k => ModifierBit(k) is int bit ? s_modifierNames[bit] : k.ToString()));

    public static int? ModifierBit(KeyCode key)
    {
        int index = Array.IndexOf(ModifierKeys, key);
        return index >= 0 ? index : null;
    }

    /// <summary>Whether a key may be a chord's main key: any keyboard key; mouse buttons 3 to 7 only for hold bindings.</summary>
    public static bool AllowedMain(KeyCode key, HotkeyKind kind)
    {
        if (key == KeyCode.None || key == KeyCode.Escape || key == KeyCode.Mouse0 || key == KeyCode.Mouse1) return false;
        if (key >= KeyCode.JoystickButton0) return false;
        if (key >= KeyCode.Mouse2 && key <= KeyCode.Mouse6) return kind == HotkeyKind.Hold;
        return Enum.IsDefined(typeof(KeyCode), key);
    }

    /// <summary>Parses chord text, ignoring case and order. Empty text is the unbound chord.</summary>
    public static bool TryParse(string? text, HotkeyKind kind, out Chord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text)) return true;

        int modifiers = 0;
        KeyCode main = KeyCode.None;
        foreach (string raw in text.Split('+'))
        {
            string part = raw.Trim();
            // Enum.TryParse also takes numbers, which aren't key names.
            if (part.Length == 0 || char.IsDigit(part[0]) || part[0] == '-' || !Enum.TryParse(part, true, out KeyCode key)) return false;
            if (ModifierBit(key) is int bit)
            {
                modifiers |= 1 << bit;
                continue;
            }
            if (main != KeyCode.None || !AllowedMain(key, kind)) return false;
            main = key;
        }
        chord = new Chord(modifiers, main);
        return true;
    }

    public bool Equals(Chord other) => Modifiers == other.Modifiers && Main == other.Main;
    public override bool Equals(object? obj) => obj is Chord other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Modifiers, Main);
}
