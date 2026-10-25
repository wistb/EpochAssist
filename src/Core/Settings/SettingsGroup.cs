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
