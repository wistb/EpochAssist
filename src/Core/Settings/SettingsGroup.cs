using System.Text.Json.Nodes;
using MelonLoader;

namespace EpochAssist.Core;

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
    private readonly List<(int Version, Action<JsonObject> Step)> _storedMigrations = new();
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
    /// <summary>Values are always the global ones; profiles don't override, store, export or import them.</summary>
    public bool Shared { get; init; }
    /// <summary>Raise when a setting is renamed or changes type, and add a migration for the new version.</summary>
    public int Version { get; }
    /// <summary>What went wrong bringing the stored settings up to Version at the last registration; shown in the menu.</summary>
    public List<string> Failures { get; } = new();
    public List<Section> Sections { get; } = new();
    public IEnumerable<Setting> All => Sections.SelectMany(s => s.Settings);

    /// <summary>Section ids in the order they show and are stored, whichever class declares its section first (a group
    /// declared across layers); ids not listed go after them in declaration order. Null: declaration order.</summary>
    public string[]? SectionOrder { get; init; }

    public Section Section(string id, string title)
    {
        var section = new Section(this, id, title);
        int place = SectionOrder == null ? -1 : Array.IndexOf(SectionOrder, id);
        int before = place < 0 ? -1 : Sections.FindIndex(s => Array.IndexOf(SectionOrder!, s.Id) > place);
        if (before < 0) Sections.Add(section);
        else Sections.Insert(before, section);
        return section;
    }

    /// <summary>
    /// Runs once for users whose stored settings are older than toVersion. It runs after the entries are registered and
    /// before they are clamped, so it can read and write the typed settings (and the raw category).
    /// </summary>
    public void Migration(int toVersion, Action<MelonPreferences_Category> step) => _migrations.Add((toVersion, step));

    /// <summary>
    /// The same step for values stored outside the preferences file: a profile's values (when it is put in use) and an
    /// export's (when it is imported). It gets the stored values by setting id and changes them in place; ids it no
    /// longer needs can stay.
    /// </summary>
    public void StoredMigration(int toVersion, Action<JsonObject> step) => _storedMigrations.Add((toVersion, step));

    /// <summary>
    /// Runs the stored steps above fromVersion, up to Version, in order, on values. Returns the version reached: Version,
    /// or the last good one when a step threw (logged once; the caller skips the group).
    /// </summary>
    public int RunStoredMigrations(int fromVersion, JsonObject values)
    {
        int reached = fromVersion;
        foreach (var (version, step) in _storedMigrations.OrderBy(m => m.Version))
        {
            if (version <= fromVersion || version > Version) continue;
            try
            {
                step(values);
                reached = version;
            }
            catch (Exception e)
            {
                Log.Error($"{Name} stored settings migration to version {version} failed: {e.Message}");
                return reached;
            }
        }
        return Version;
    }

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
