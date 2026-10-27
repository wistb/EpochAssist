using System.Text.Json.Nodes;
using MelonLoader;

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
    /// <summary>What makes a hidden setting show ("Run macros is on"), for search. Code only, nothing stored.</summary>
    public string? ShownWhen { get; init; }
    /// <summary>Also listed on General › Keybinds with the keys (a choice that goes with a key). Keys always are.</summary>
    public bool WithKeybinds { get; init; }
    /// <summary>Also listed on General › Diagnostics › Debug. A feature's "debug" section always is.</summary>
    public bool WithDebug { get; init; }

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

    /// <summary>The HUD quick toggles this setting offers, in order (a Toggle with a QuickLabel offers itself).</summary>
    internal virtual IEnumerable<IQuickSwitch> QuickSwitches => Array.Empty<IQuickSwitch>();

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
            if (!Profiles.InProfile || Section.Group.Shared)
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
        if (Section.Group.Shared) return false;
        if (!TryReadNode(node, out T value)) return false;
        _override = value;
        _hasOverride = true;
        return true;
    }

    internal override JsonNode? ToJson(bool global) => Write(global || !_hasOverride ? GlobalValue : _override);

    public override string? Describe(JsonNode? node) => TryReadNode(node, out T value) ? Show(value) : null;

    /// <summary>A value as the menu shows it.</summary>
    protected virtual string Show(T value) => value?.ToString() ?? string.Empty;

    internal override bool TrySet(JsonNode? node)
    {
        if (!TryReadNode(node, out T value)) return false;
        Value = value;
        return true;
    }

    /// <summary>A value as a profile stores it.</summary>
    protected virtual JsonNode? Write(T value) => JsonValue.Create(value);

    /// <summary>A profile's stored value as this kind of setting, when it fits.</summary>
    protected abstract bool TryRead(JsonValue json, out T value);

    /// <summary>A stored node as this kind of setting, when it fits; a kind stored as an object or array overrides it.</summary>
    protected virtual bool TryReadNode(JsonNode? node, out T value)
    {
        value = default!;
        return node is JsonValue json && TryRead(json, out value);
    }

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
