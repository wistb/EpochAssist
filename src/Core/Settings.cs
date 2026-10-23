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

    internal abstract void Register(MelonPreferences_Category category);
    /// <summary>Puts a hand-edited or outdated stored value back in range.</summary>
    internal abstract void Clamp();
}

internal abstract class Setting<T> : Setting
{
    private MelonPreferences_Entry<T>? _entry;

    protected Setting(Section section, string id, string label, T defaultValue) : base(section, id, label) => Default = defaultValue;

    public T Default { get; }

    public T Value
    {
        get => _entry != null ? _entry.Value : Default;
        set
        {
            if (_entry == null || EqualityComparer<T>.Default.Equals(_entry.Value, value)) return;
            _entry.Value = value;
            Config.MarkDirty();
        }
    }

    public override bool IsDefault => EqualityComparer<T>.Default.Equals(Value, Default);
    public override void Reset() => Value = Default;

    internal override void Register(MelonPreferences_Category category) =>
        _entry = Config.Entry(category, Id, Default, Help ?? Label);
}

internal sealed class Toggle : Setting<bool>
{
    public Toggle(Section section, string id, string label, bool defaultValue) : base(section, id, label, defaultValue) { }
    internal override void Clamp() { }
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
}

/// <summary>One of a fixed list of values, shown as a row of buttons. T must be storable: an int or a game enum, never one of ours.</summary>
internal sealed class Choice<T> : Setting<T> where T : notnull
{
    public Choice(Section section, string id, string label, T defaultValue, params (T Value, string Label)[] options)
        : base(section, id, label, defaultValue) => Options = options;

    public (T Value, string Label)[] Options { get; }

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
    /// <summary>Raise when a setting is renamed or changes type, and add a migration for the new version.</summary>
    public int Version { get; }
    public List<Section> Sections { get; } = new();
    public IEnumerable<Setting> All => Sections.SelectMany(s => s.Settings);

    public Section Section(string id, string title)
    {
        var section = new Section(this, id, title);
        Sections.Add(section);
        return section;
    }

    /// <summary>Runs once for users whose stored settings are older than toVersion, before the settings are read.</summary>
    public void Migration(int toVersion, Action<MelonPreferences_Category> step) => _migrations.Add((toVersion, step));

    public void Register()
    {
        _category = Config.Category(Id, $"{BuildInfo.Name} - {Name}");
        var stored = Config.Entry(_category, VersionKey, 0, "Settings format version; do not edit");

        // 0 is both a first run and settings from before versioning, so a migration must cope with finding nothing.
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
                Log.Error($"{Name} settings migration to {version} failed: {e.Message}");
            }
        }

        foreach (var setting in All)
        {
            setting.Register(_category);
            setting.Clamp();
        }
        stored.Value = Version;
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

    internal static void Add(SettingsGroup group)
    {
        if (!s_groups.Contains(group)) s_groups.Add(group);
    }

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
