using System.Text.Json.Nodes;
using MelonLoader;
using UnityEngine;

namespace EpochAssist.Core;

/// <summary>
/// One setting, declared once: its stored entry plus everything the menu, the HUD and the loader need to know about it.
/// Read and write it through Value, as with a preferences entry.
/// </summary>
internal abstract class Setting
{
    protected Setting(Section section, string id, string label)
    {
        Section = section;
        Id = id;
        Label = label;
        section.Settings.Add(this);
    }

    public Section Section { get; }
    public string Id { get; }
    public string Label { get; }
    public string? Help { get; init; }
    /// <summary>Label in the HUD's quick panel; a setting with one is offered there.</summary>
    public string? QuickLabel { get; init; }
    /// <summary>Settings sharing a fold are shown together under a collapsible header.</summary>
    public string? Fold { get; init; }
    public Func<bool>? VisibleWhen { get; init; }

    public bool Visible => VisibleWhen?.Invoke() ?? true;
    public abstract bool IsDefault { get; }
    public abstract void Reset();

    /// <summary>The profile in use holds a value for this setting (only while a profile other than Default is in use).</summary>
    public abstract bool HasOverride { get; }
    /// <summary>Forgets the profile's value (when the profile is put away); the Default profile's value shows again.</summary>
    public abstract void ClearOverride();
    /// <summary>Takes a profile's stored value when it fits the setting; false (nothing changed) when it doesn't.</summary>
    internal abstract bool TryApplyOverride(JsonNode? node);
    /// <summary>The value as a profile stores it: the profile's own, or with global the Default profile's.</summary>
    internal abstract JsonNode? ToJson(bool global);
    /// <summary>Sets the value (in the profile in use) from a stored one when it fits; false (nothing changed) when not.</summary>
    internal abstract bool TrySet(JsonNode? node);
    /// <summary>A stored value as the menu shows it ("Top", "85%", "Left Ctrl + F2"); null when it doesn't fit.</summary>
    public abstract string? Describe(JsonNode? node);

    /// <summary>Binds the stored entry; false when one of another type already holds the id in this game session.</summary>
    internal abstract bool Register(MelonPreferences_Category category);
    /// <summary>Puts a hand-edited or outdated stored value back in range.</summary>
    internal abstract void Clamp();
}

internal abstract class Setting<T> : Setting
{
    private MelonPreferences_Entry<T>? _entry;

    protected Setting(Section section, string id, string label, T defaultValue) : base(section, id, label) => Default = defaultValue;

    public T Default { get; }

    private bool _hasOverride;
    private T _override = default!;

    /// <summary>The stored global value: the Default profile's, whatever profile is in use.</summary>
    public T GlobalValue
    {
        get => _entry != null ? _entry.Value : Default;
        set
        {
            if (_entry == null || EqualityComparer<T>.Default.Equals(_entry.Value, value)) return;
            _entry.Value = value;
            Config.MarkDirty();
        }
    }

    /// <summary>
    /// The value in effect: the profile's while a profile other than Default is in use (a value it lacks falls back to
    /// Default's), else the global value. Setting it writes to the profile in use.
    /// </summary>
    public T Value
    {
        get => _hasOverride ? _override : GlobalValue;
        set
        {
            if (!Profiles.InProfile)
            {
                GlobalValue = value;
                return;
            }
            if (_hasOverride && EqualityComparer<T>.Default.Equals(value, _override)) return;
            _override = value;
            _hasOverride = true;
            Profiles.MarkDirty();
        }
    }

    public override bool IsDefault => EqualityComparer<T>.Default.Equals(Value, Default);
    public override void Reset() => Value = Default;

    public override bool HasOverride => _hasOverride;

    public override void ClearOverride() => _hasOverride = false;

    internal override bool TryApplyOverride(JsonNode? node)
    {
        if (node is not JsonValue json || !TryRead(json, out T value)) return false;
        _override = value;
        _hasOverride = true;
        return true;
    }

    internal override JsonNode? ToJson(bool global) => Write(global || !_hasOverride ? GlobalValue : _override);

    public override string? Describe(JsonNode? node) => node is JsonValue json && TryRead(json, out T value) ? Show(value) : null;

    /// <summary>A value as the menu shows it.</summary>
    protected virtual string Show(T value) => value?.ToString() ?? string.Empty;

    internal override bool TrySet(JsonNode? node)
    {
        if (node is not JsonValue json || !TryRead(json, out T value)) return false;
        Value = value;
        return true;
    }

    /// <summary>A value as a profile stores it.</summary>
    protected virtual JsonNode? Write(T value) => JsonValue.Create(value);

    /// <summary>A profile's stored value as this kind of setting, when it fits.</summary>
    protected abstract bool TryRead(JsonValue json, out T value);

    // MelonPreferences keeps an entry's type for the whole game session, so after a retype only a restart can rebind it.
    internal override bool Register(MelonPreferences_Category category)
    {
        try
        {
            _entry = Config.Entry(category, Id, Default, Help ?? Label);
            return true;
        }
        catch (Exception e)
        {
            _entry = null;
            Log.Warn($"{Section.Group.Name} setting {Id} has an older type in this game session; its default is used until the game restarts ({e.GetType().Name})");
            return false;
        }
    }
}

internal sealed class Toggle : Setting<bool>
{
    public Toggle(Section section, string id, string label, bool defaultValue) : base(section, id, label, defaultValue) { }
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
    public string Unit { get; init; } = string.Empty;
    public bool Percent { get; init; }
    /// <summary>For settings that move the UI itself (scale): the value applies when the slider is let go.</summary>
    public bool ApplyOnRelease { get; init; }

    public float Snap(float value) => Mathf.Clamp(Step > 0f ? Mathf.Round(value / Step) * Step : value, Min, Max);

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

/// <summary>One of a fixed list of values, shown as a row of buttons. T must be storable: an int or a game enum, never one of ours.</summary>
internal sealed class Choice<T> : Setting<T> where T : notnull
{
    public Choice(Section section, string id, string label, T defaultValue, params (T Value, string Label)[] options)
        : base(section, id, label, defaultValue) => Options = options;

    public (T Value, string Label)[] Options { get; }

    /// <summary>Lays the options out three to a row, each cell an index into Options or -1 for an empty cell; null for the usual rows.</summary>
    public int[]? Cells { get; init; }

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
/// A colour stored as #RRGGBB, or empty for none (the caller's own colour). Shown as swatches (the empty one first, then
/// the named ones) and a picker for any other colour.
/// </summary>
internal sealed class ColorSetting : Setting<string>
{
    public ColorSetting(Section section, string id, string label) : base(section, id, label, string.Empty) { }

    /// <summary>Named colours offered as swatches, in order, as #RRGGBB.</summary>
    public (string Name, string Hex)[] Swatches { get; init; } = Array.Empty<(string, string)>();
    /// <summary>What the empty value means, for its swatch ("Theme").</summary>
    public string EmptyLabel { get; init; } = "Default";

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
/// A name chosen from a list that can change while the game runs (themes from files), shown as a row of buttons. The
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

internal sealed class Section
{
    public Section(SettingsGroup group, string id, string title)
    {
        Group = group;
        Id = id;
        Title = title;
    }

    public SettingsGroup Group { get; }
    public string Id { get; }
    public string Title { get; }
    public List<Setting> Settings { get; } = new();

    public bool IsDefault => Settings.All(s => s.IsDefault);

    public void Reset()
    {
        foreach (var setting in Settings) setting.Reset();
    }
}

/// <summary>
/// A feature's settings: one preferences category, split into sections. Declare it as static fields, in order:
/// the group, then each section, then that section's settings. Register once at start.
/// </summary>
internal sealed class SettingsGroup
{
    private const string VersionKey = "_SettingsVersion";

    private readonly List<(int Version, Action<MelonPreferences_Category> Step)> _migrations = new();
    private MelonPreferences_Category? _category;

    public SettingsGroup(string id, string name, int version = 1)
    {
        Id = id;
        Name = name;
        Version = version;
    }

    public string Id { get; }
    public string Name { get; }
    /// <summary>The group's part of action names (settings.reset.[key].[section]); lowercase letters and digits.</summary>
    public string ActionKey { get; init; } = string.Empty;
    /// <summary>Raise when a setting is renamed or changes type, and add a migration for the new version.</summary>
    public int Version { get; }
    /// <summary>What went wrong bringing the stored settings up to Version at the last registration; shown in the menu.</summary>
    public List<string> Failures { get; } = new();
    public List<Section> Sections { get; } = new();
    public IEnumerable<Setting> All => Sections.SelectMany(s => s.Settings);

    public Section Section(string id, string title)
    {
        var section = new Section(this, id, title);
        Sections.Add(section);
        return section;
    }

    /// <summary>
    /// Runs once for users whose stored settings are older than toVersion. It runs after the entries are registered and
    /// before they are clamped, so it can read and write the typed settings (and the raw category).
    /// </summary>
    public void Migration(int toVersion, Action<MelonPreferences_Category> step) => _migrations.Add((toVersion, step));

    public void Register()
    {
        _category = Config.Category(Id, $"{BuildInfo.Name} - {Name}");
        var stored = Config.Entry(_category, VersionKey, 0, "Settings format version; do not edit");
        Failures.Clear();

        var unbound = All.Where(setting => !setting.Register(_category)).Select(setting => setting.Id).ToList();
        int reached = stored.Value;
        if (unbound.Count > 0)
        {
            // A retyped entry still has its old type in this session; migrate after a restart instead.
            Failures.Add($"Restart the game to finish updating: {string.Join(", ", unbound)}");
        }
        else
        {
            // 0 is both a first run and settings from before versioning, so a migration must cope with finding nothing.
            reached = Version;
            foreach (var (version, step) in _migrations.OrderBy(m => m.Version))
            {
                if (version <= stored.Value || version > Version) continue;
                try
                {
                    step(_category);
                    Log.Info($"{Name} settings migrated to version {version}");
                }
                catch (Exception e)
                {
                    // Later steps would build on this one; stop here and try again on the next start.
                    reached = _migrations.Where(m => m.Version < version).Select(m => m.Version).Prepend(stored.Value).Max();
                    Failures.Add($"migration to version {version} failed: {e.Message}");
                    Log.Error($"{Name} settings migration to version {version} failed: {e.Message}");
                    break;
                }
            }
        }

        foreach (var setting in All) setting.Clamp();
        stored.Value = reached;
        SettingsRegistry.Add(this);
    }

    public void Reset()
    {
        foreach (var section in Sections) section.Reset();
    }
}

/// <summary>Every registered group, for the settings search and the HUD's quick toggles.</summary>
internal static class SettingsRegistry
{
    private static readonly List<SettingsGroup> s_groups = new();

    public static IReadOnlyList<SettingsGroup> Groups => s_groups;

    public static IEnumerable<Toggle> QuickToggles =>
        s_groups.SelectMany(g => g.All).OfType<Toggle>().Where(t => t.QuickLabel != null);

    // Each group's sections get a reset action and its quick toggles a toggle action, owned by the settings layer.
    internal static void Add(SettingsGroup group)
    {
        if (s_groups.Contains(group)) return;
        s_groups.Add(group);
        foreach (var section in group.Sections)
        {
            Actions.Register("settings", ResetAction(section), $"Reset {section.Title}", () =>
            {
                section.Reset();
                Toasts.Show("settings", $"{section.Title} reset to defaults.", ToastKind.Info);
            });
        }
        foreach (var toggle in group.All.OfType<Toggle>().Where(t => t.QuickLabel != null))
        {
            Actions.Register("settings", ToggleAction(toggle), toggle.QuickLabel!, () => toggle.Value = !toggle.Value, () => toggle.Value);
        }
    }

    public static string ResetAction(Section section) => $"settings.reset.{Key(section.Group)}.{Slug(section.Id)}";

    public static string ToggleAction(Toggle toggle) => $"setting.toggle.{Key(toggle.Section.Group)}.{Slug(toggle.Id)}";

    private static string Key(SettingsGroup group) => Slug(group.ActionKey.Length > 0 ? group.ActionKey : group.Id);

    private static string Slug(string text) => new(text.ToLowerInvariant().Where(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9')).ToArray());

    public static IEnumerable<Setting> Search(string query)
    {
        string q = query.Trim();
        if (q.Length == 0) return Enumerable.Empty<Setting>();
        return s_groups.SelectMany(g => g.All).Where(s =>
            s.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (s.Help?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || s.Section.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || s.Section.Group.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
    }
}
