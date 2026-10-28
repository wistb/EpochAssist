using System.Text.Json.Nodes;
using UnityEngine;

namespace EpochAssist.Core;

internal sealed class Toggle : Setting<bool>, IQuickSwitch
{
    public Toggle(Section section, string id, string label, bool defaultValue) : base(section, id, label, defaultValue) { }

    string IQuickSwitch.Label => QuickLabel ?? Label;
    string? IQuickSwitch.Tip => Help ?? Label;
    void IQuickSwitch.Set(bool on) => Value = on;
    string IQuickSwitch.ActionId => SettingsRegistry.ToggleAction(this);
    internal override IEnumerable<IQuickSwitch> QuickSwitches => QuickLabel != null ? new IQuickSwitch[] { this } : Array.Empty<IQuickSwitch>();
    internal override void Clamp() { }
    protected override bool TryRead(JsonValue json, out bool value) => json.TryGetValue(out value);
    protected override string Show(bool value) => value ? "on" : "off";
}

internal sealed class Number : Setting<float>
{
    public Number(Section section, string id, string label, float defaultValue, float min, float max) : base(section, id, label, defaultValue)
    {
        Min = min;
        Max = max;
    }

    public float Min { get; }
    public float Max { get; }
    /// <summary>Values snap to multiples of this.</summary>
    public float Step { get; init; } = 1f;
    /// <summary>Drawn as a slider across its range instead of a field with steppers.</summary>
    public bool Slider { get; init; }
    public string Unit { get; init; } = string.Empty;
    public bool Percent { get; init; }
    /// <summary>For settings that move the UI itself (scale): the value applies when the slider is let go.</summary>
    public bool ApplyOnRelease { get; init; }

    public float Snap(float value) => Mathf.Clamp(Step > 0f ? Mathf.Round(value / Step) * Step : value, Min, Max);

    // The stored value comes back through the preferences file a hair off (0.85 reads as 0.8499999642), so it is the
    // default when it is within half a step of it.
    public override bool IsDefault => Mathf.Abs(Value - Default) < (Step > 0f ? Step * 0.5f : 1e-4f);

    public string Format(float value)
    {
        if (Percent) return $"{value * 100f:F0}%";
        string digits = Step >= 1f ? "F0" : Step >= 0.1f ? "F1" : "F2";
        return value.ToString(digits) + Unit;
    }

    internal override void Clamp()
    {
        float value = Value;
        if (float.IsNaN(value) || float.IsInfinity(value)) Reset();
        else Value = Snap(value);
    }

    protected override string Show(float value) => Format(value);

    // In range and on a step, or it is skipped (and kept in the file).
    protected override bool TryRead(JsonValue json, out float value)
    {
        value = 0f;
        // Parsed text holds any number; a value made in memory holds the float it was made from.
        double number = json.TryGetValue(out double d) ? d : json.TryGetValue(out float f) ? f : double.NaN;
        if (double.IsNaN(number) || number < Min || number > Max) return false;
        value = (float)number;
        return Mathf.Approximately(Snap(value), value);
    }
}

internal sealed class Key : Setting<KeyCode>
{
    public Key(Section section, string id, string label, KeyCode defaultValue) : base(section, id, label, defaultValue) { }

    /// <summary>May be left unbound (KeyCode.None).</summary>
    public bool Clearable { get; init; }

    internal override void Clamp()
    {
        if (!Enum.IsDefined(typeof(KeyCode), Value) || (!Clearable && Value == KeyCode.None)) Reset();
    }

    protected override JsonNode? Write(KeyCode value) => JsonValue.Create(value.ToString());
    protected override string Show(KeyCode value) => value == KeyCode.None ? "Not set" : value.ToString();
    protected override bool TryRead(JsonValue json, out KeyCode value)
    {
        value = KeyCode.None;
        return json.TryGetValue(out string? name) && Enum.TryParse(name, out value) && Enum.IsDefined(typeof(KeyCode), value) && (Clearable || value != KeyCode.None);
    }
}

/// <summary>One of a fixed list of values, shown as a dropdown. T must be storable: an int or a game enum, never one of ours.</summary>
internal sealed class Choice<T> : Setting<T> where T : notnull
{
    public Choice(Section section, string id, string label, T defaultValue, params (T Value, string Label)[] options)
        : base(section, id, label, defaultValue) => Options = options;

    public (T Value, string Label)[] Options { get; }

    /// <summary>Headings for a long option list: each group starts at option index First. Code only, nothing stored.</summary>
    public (string Label, int First)[]? Groups { get; init; }
    /// <summary>An image shown before an option in the dropdown (a texture and its part of it); null for none. Code only.</summary>
    public Func<T, (Texture Texture, Rect Uv)?>? Preview { get; init; }
    /// <summary>An example of an option, drawn by the owner (IMGUI) before its label in the dropdown and its list, in a rect
    /// SampleWidth wide; null for none. Code only.</summary>
    public Action<T, Rect>? Sample { get; init; }
    public float SampleWidth { get; init; } = 56f;

    public int Index
    {
        get => Array.FindIndex(Options, o => EqualityComparer<T>.Default.Equals(o.Value, Value));
        set => Value = Options[Math.Clamp(value, 0, Options.Length - 1)].Value;
    }

    public string ValueLabel => Index >= 0 ? Options[Index].Label : Value.ToString() ?? string.Empty;

    internal override void Clamp()
    {
        if (Index < 0) Reset();
    }

    protected override string Show(T value)
    {
        int index = Array.FindIndex(Options, o => EqualityComparer<T>.Default.Equals(o.Value, value));
        return index >= 0 ? Options[index].Label : value.ToString() ?? string.Empty;
    }

    // An int as a number, a game enum by name; only one of the options.
    protected override JsonNode? Write(T value) => value is Enum ? JsonValue.Create(value.ToString()) : JsonValue.Create(value);

    protected override bool TryRead(JsonValue json, out T value)
    {
        value = default!;
        object? read = typeof(T) == typeof(int) ? (json.TryGetValue(out int number) ? number : null)
            : typeof(T).IsEnum && json.TryGetValue(out string? name) && Enum.TryParse(typeof(T), name, out object? parsed) ? parsed : null;
        if (read is not T candidate || !Options.Any(o => EqualityComparer<T>.Default.Equals(o.Value, candidate))) return false;
        value = candidate;
        return true;
    }
}

/// <summary>
/// A colour stored as #RRGGBB, or empty for none (the caller's own colour). Shown as a dropdown of swatches (the empty
/// one first, then the named ones) and a picker for any other colour.
/// </summary>
internal sealed class ColorSetting : Setting<string>
{
    public ColorSetting(Section section, string id, string label, string defaultValue = "") : base(section, id, label, defaultValue) { }

    /// <summary>Named colours offered as swatches, in order, as #RRGGBB.</summary>
    public (string Name, string Hex)[] Swatches { get; init; } = Array.Empty<(string, string)>();
    /// <summary>What the empty value means, for its swatch ("Theme").</summary>
    public string EmptyLabel { get; init; } = "Default";
    /// <summary>The colour the empty value stands for (#RRGGBB), shown on its swatch; null shows the theme accent.</summary>
    public string? EmptyHex { get; init; }
    /// <summary>Headings for the swatches: each group starts at swatch index First. Code only, nothing stored.</summary>
    public (string Label, int First)[]? Groups { get; init; }

    /// <summary>The stored colour; false when empty or unreadable.</summary>
    public bool TryGetColor(out Color color) => TryParse(Value, out color);

    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        if (text == null || text.Length != 7 || text[0] != '#'
            || !int.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out int rgb)) return false;
        color = new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
        return true;
    }

    public static string Format(Color color) => $"#{ColorByte(color.r):X2}{ColorByte(color.g):X2}{ColorByte(color.b):X2}";

    private static int ColorByte(float channel) => Mathf.Clamp(Mathf.RoundToInt(channel * 255f), 0, 255);

    internal override void Clamp()
    {
        if (Value.Length > 0 && !TryParse(Value.ToUpperInvariant(), out _)) Reset();
        else Value = Value.ToUpperInvariant();
    }

    protected override bool TryRead(JsonValue json, out string value) => json.TryGetValue(out value!) && (value.Length == 0 || TryParse(value, out _));
    protected override string Show(string value) => value.Length == 0 ? EmptyLabel : value;
}

/// <summary>
/// A name chosen from a list that can change while the game runs (themes from files), shown as a dropdown. The
/// list and any notes come from hooks the UI sets; a stored name missing from the list is kept, so it comes back with it.
/// </summary>
internal sealed class Pick : Setting<string>
{
    public Pick(Section section, string id, string label, string defaultValue) : base(section, id, label, defaultValue) { }

    /// <summary>The names to offer; null until the UI sets it.</summary>
    public Func<IReadOnlyList<string>>? Options { get; set; }
    /// <summary>Lines shown under the row (problems with the options); null for none.</summary>
    public Func<IEnumerable<string>>? Notes { get; set; }

    internal override void Clamp()
    {
        if (string.IsNullOrWhiteSpace(Value)) Reset();
    }

    protected override bool TryRead(JsonValue json, out string value) => json.TryGetValue(out value!) && value.Trim().Length > 0;
}
