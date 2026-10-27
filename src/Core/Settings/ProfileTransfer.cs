using System.Text.Json;
using System.Text.Json.Nodes;
using MelonLoader.Utils;

namespace EpochAssist.Core;

/// <summary>A part of a profile that can be exported or imported on its own: one settings section of one group.</summary>
internal sealed record ProfileSegment(SettingsGroup Group, Section Section)
{
    public string Id => $"{Group.Id}/{Section.Id}";
}

/// <summary>An export read back: its profile name, when it was made, the segments it holds, and its stored values.</summary>
internal sealed record ProfileExport(string Name, string Exported, JsonObject Root, IReadOnlyList<string> Segments)
{
    /// <summary>Groups made by an older version, brought up to date when read: group id → the version they were made at
    /// and their values from every section, migrated.</summary>
    public Dictionary<string, (int From, JsonObject Values)> Migrated { get; } = new();
    /// <summary>Groups made by an older version whose update failed; their values are skipped.</summary>
    public HashSet<string> Failed { get; } = new();

    /// <summary>The stored value for a setting (migrated when the export is older), or null when the export doesn't hold it.</summary>
    public JsonNode? ValueOf(Setting setting)
    {
        string group = setting.Section.Group.Id;
        if (Failed.Contains(group)) return null;
        var values = Migrated.TryGetValue(group, out var migrated) ? migrated.Values
            : Root["groups"]?[group]?["sections"]?[setting.Section.Id] as JsonObject;
        return values != null && values.TryGetPropertyValue(setting.Id, out var value) ? value ?? JsonValue.Create(string.Empty) : null;
    }

    /// <summary>After Apply: condition lists that kept entries this version can't read ("Shared Conditions: 1").</summary>
    public List<string> Unreadable { get; } = new();

    /// <summary>"Macros (from version 2)" for each migrated group, for the import result.</summary>
    public IEnumerable<string> MigratedNames =>
        SettingsRegistry.Groups.Where(g => Migrated.ContainsKey(g.Id)).Select(g => $"{g.Name} (from version {Migrated[g.Id].From})");
}

/// <summary>
/// Exports and imports profiles, whole, by segment (a settings section such as Mini HUD, Keys or the Macros' Enemy gate)
/// or setting by setting (chosen by key, group/section/setting). An export is a JSON text, written to UserData/EpochAssist/exports/ and copied to the clipboard; an import reads
/// one from there or the clipboard and sets the chosen segments' values in the profile in use.
/// </summary>
internal static class ProfileTransfer
{
    private const string Format = "epoch-assist-profile";
    private const int FormatVersion = 1;

    private static readonly JsonDocumentOptions s_readOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions s_writeOptions = new() { WriteIndented = true };

    public static string Dir => Path.Combine(MelonEnvironment.UserDataDirectory, "EpochAssist", "exports");

    /// <summary>Every segment, by group then section, in the order the menu shows them.</summary>
    public static IReadOnlyList<ProfileSegment> Segments =>
        SettingsRegistry.Groups.Where(g => !g.Shared).SelectMany(g => g.Sections.Where(s => s.Settings.Count > 0).Select(s => new ProfileSegment(g, s))).ToList();

    /// <summary>A setting's key for choosing it: group/section/setting.</summary>
    public static string Key(Setting setting) => $"{setting.Section.Group.Id}/{setting.Section.Id}/{setting.Id}";

    /// <summary>Every setting's key in the given segments.</summary>
    public static IEnumerable<string> KeysOf(IEnumerable<string> segments)
    {
        var chosen = new HashSet<string>(segments);
        return Segments.Where(s => chosen.Contains(s.Id)).SelectMany(s => s.Section.Settings).Select(Key);
    }

    /// <summary>The profile in use's values for the chosen settings (by key) as export text.</summary>
    public static string ExportText(IEnumerable<string> settings)
    {
        var chosen = new HashSet<string>(settings);
        var groups = new JsonObject();
        foreach (var segment in Segments)
        {
            var picked = segment.Section.Settings.Where(x => chosen.Contains(Key(x))).ToList();
            if (picked.Count == 0) continue;
            if (groups[segment.Group.Id] is not JsonObject group)
            {
                groups[segment.Group.Id] = group = new JsonObject { ["version"] = segment.Group.Version, ["sections"] = new JsonObject() };
            }
            var values = new JsonObject();
            foreach (var setting in picked) values[setting.Id] = setting.ToJson(global: false);
            group["sections"]![segment.Section.Id] = values;
        }
        var root = new JsonObject
        {
            ["format"] = Format,
            ["version"] = FormatVersion,
            ["name"] = Profiles.Active.Name,
            ["exported"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            ["groups"] = groups,
        };
        return root.ToJsonString(s_writeOptions);
    }

    /// <summary>Writes an export file and returns its name; null with the reason when it can't.</summary>
    public static string? WriteFile(string text, out string reason)
    {
        reason = string.Empty;
        try
        {
            Directory.CreateDirectory(Dir);
            string stem = $"{Profiles.Slug(Profiles.Active.Name, 24, "profile")}-{DateTime.Now:yyyyMMdd-HHmmss}";
            string name = stem + ".json";
            for (int n = 2; File.Exists(Path.Combine(Dir, name)); n++) name = $"{stem}-{n}.json";
            string path = Path.Combine(Dir, name), tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, true);
            return name;
        }
        catch (Exception e)
        {
            reason = e.Message;
            return null;
        }
    }

    /// <summary>Export files, newest first.</summary>
    public static IReadOnlyList<string> Files()
    {
        try
        {
            return Directory.Exists(Dir)
                ? Directory.GetFiles(Dir, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Select(Path.GetFileName).ToList()!
                : Array.Empty<string>();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    public static ProfileExport? ReadFile(string file, out string reason)
    {
        try
        {
            return Read(File.ReadAllText(Path.Combine(Dir, Path.GetFileName(file))), out reason);
        }
        catch (Exception e)
        {
            reason = e.Message;
            return null;
        }
    }

    /// <summary>Reads export text; null with the reason when it isn't one this build can read.</summary>
    public static ProfileExport? Read(string text, out string reason)
    {
        reason = string.Empty;
        JsonObject root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: s_readOptions) as JsonObject ?? throw new JsonException("not a JSON object");
        }
        catch (Exception e)
        {
            reason = $"it isn't a profile export ({e.Message})";
            return null;
        }
        if (root["format"]?.GetValue<string>() != Format)
        {
            reason = "it isn't a profile export";
            return null;
        }
        if (root["version"] is JsonValue v && v.TryGetValue(out int version) && version > FormatVersion)
        {
            reason = $"it was made by a newer version (format {version})";
            return null;
        }
        var segments = Segments.Where(s => root["groups"]?[s.Group.Id]?["sections"]?[s.Section.Id] is JsonObject).Select(s => s.Id).ToList();
        var export = new ProfileExport(root["name"]?.GetValue<string>() ?? "Imported", root["exported"]?.GetValue<string>() ?? "?", root, segments);
        foreach (var group in SettingsRegistry.Groups.Where(g => !g.Shared))
        {
            if (root["groups"]?[group.Id] is not JsonObject stored || stored["sections"] is not JsonObject sections) continue;
            if (stored["version"] is not JsonValue sv || !sv.TryGetValue(out int from) || from >= group.Version) continue;
            // The steps work on values by setting id, as profiles store them, so the sections are put together first.
            var values = new JsonObject();
            foreach (var (_, section) in sections)
            {
                if (section is not JsonObject sectionValues) continue;
                foreach (var (id, value) in sectionValues) values[id] = value == null ? null : JsonNode.Parse(value.ToJsonString());
            }
            if (group.RunStoredMigrations(from, values) < group.Version) export.Failed.Add(group.Id);
            else export.Migrated[group.Id] = (from, values);
        }
        return export;
    }

    /// <summary>
    /// Sets the chosen segments' values in the profile in use. A group made by a newer version of the mod is skipped,
    /// and so is a value that doesn't fit its setting. Returns how many were set and how many skipped.
    /// </summary>
    public static (int Set, int Skipped) Apply(ProfileExport export, IEnumerable<string> settings)
    {
        var chosen = new HashSet<string>(settings);
        int set = 0, skipped = 0;
        export.Unreadable.Clear();
        foreach (var segment in Segments.Where(s => export.Segments.Contains(s.Id)))
        {
            var group = export.Root["groups"]![segment.Group.Id]!;
            bool newer = group["version"] is JsonValue v && v.TryGetValue(out int version) && version > segment.Group.Version;
            bool failed = export.Failed.Contains(segment.Group.Id);
            foreach (var setting in segment.Section.Settings.Where(x => chosen.Contains(Key(x))))
            {
                // An older export's values come migrated; a failed group's are all skipped.
                var value = failed ? group["sections"]?[segment.Section.Id]?[setting.Id] : export.ValueOf(setting);
                if (value == null) continue;
                if (!newer && !failed && setting.TrySet(value))
                {
                    set++;
                    // Entries a newer version made are kept; the result says how many.
                    if (setting is ConditionSetting conditions)
                    {
                        int unreadable = conditions.Doc.InvalidCounts().Sum(c => c.Count);
                        if (unreadable > 0) export.Unreadable.Add($"{setting.Label}: {unreadable}");
                    }
                    else if (setting is MacroLibrarySetting library && library.UnreadableParts is int parts and > 0)
                    {
                        export.Unreadable.Add($"{setting.Label}: {parts}");
                    }
                }
                else skipped++;
            }
        }
        Profiles.Save();
        return (set, skipped);
    }
}
