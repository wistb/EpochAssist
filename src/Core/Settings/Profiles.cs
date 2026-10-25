using System.Text.Json;
using System.Text.Json.Nodes;
using MelonLoader.Utils;

namespace EpochAssist.Core;

/// <summary>
/// Settings profiles: Default is the global settings in MelonPreferences; any other
/// profile is a full set of every setting in UserData/EpochAssist/profiles/&lt;id&gt;.json. Each character uses one
/// profile (Default until it picks another), remembered in UserData/EpochAssist/characters/&lt;key&gt;.json with the
/// character's own data (autocast's slot toggles). While a character on a profile other than Default plays, every
/// setting reads and writes that profile; out of game the Default profile is in use. A file this build can't read is
/// left alone and Default is used instead.
/// </summary>
internal static partial class Profiles
{
    public const string DefaultId = "default";
    public const string DefaultName = "Default";
    private const int FormatVersion = 1;
    private const int MaxName = 32;
    private const float SaveDelaySeconds = 1f;
    private const float ReadingSeconds = 10f;
    private const float RecheckSeconds = 1f;

    private static readonly JsonDocumentOptions s_readOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions s_writeOptions = new() { WriteIndented = true };

    // The character playing and its file.
    private static CharacterKey? s_key;
    private static JsonObject? s_character;
    private static bool s_characterDirty;
    private static string s_characterProblem = string.Empty;

    // The profile in use (other than Default) and its file.
    private static ProfileInfo s_active = new(DefaultId, DefaultName);
    private static JsonObject? s_profile;
    private static string s_profileProblem = string.Empty;
    // Stored values that didn't fit their setting, and groups stored by a newer build: kept in the file as they are.
    private static readonly HashSet<(string Group, string Id)> s_rejected = new();
    private static readonly HashSet<string> s_newerGroups = new();

    private static List<ProfileInfo>? s_list;
    private static Dictionary<string, int>? s_users;
    // Unsaved changes, and the host job that writes them shortly after (it runs whether or not the feature does).
    private static bool s_dirty;
    private static Job? s_save;
    private static float s_spawnedAt = -1f;
    private static Cooldown s_recheck;
    private static bool s_hadPlayer;

    private static string ProfileDir => Path.Combine(MelonEnvironment.UserDataDirectory, "EpochAssist", "profiles");
    private static string CharacterDir => Path.Combine(MelonEnvironment.UserDataDirectory, "EpochAssist", "characters");

    public static CharacterState State { get; private set; }

    /// <summary>A character is playing and its file is loaded: per-character data reads and writes it.</summary>
    public static bool HasCharacter => State == CharacterState.Loaded;

    /// <summary>A profile other than Default is in use: every setting reads and writes it.</summary>
    public static bool InProfile { get; private set; }

    /// <summary>The profile in use (Default out of game).</summary>
    public static ProfileInfo Active => s_active;

    /// <summary>Raised whenever a character's file loads or unloads, so per-character data can be read again.</summary>
    public static int Generation { get; private set; }

    /// <summary>Every profile, Default first, then by name.</summary>
    public static IReadOnlyList<ProfileInfo> All => s_list ??= Scan();

    /// <summary>How many known characters use a profile (characters without a file use Default).</summary>
    public static int Users(string id) => (s_users ??= CountUsers()).TryGetValue(id, out int n) ? n : 0;

    public static ProfileInfo? Find(string nameOrId) =>
        All.FirstOrDefault(p => p.Id == nameOrId) ?? All.FirstOrDefault(p => p.Name.Equals(nameOrId.Trim(), StringComparison.OrdinalIgnoreCase));

    // ---- Per-character data ----

    /// <summary>Per-character data from the character's file, or null (none, or no character loaded).</summary>
    public static JsonNode? Read(string key) => HasCharacter ? s_character?["data"]?[key] : null;

    /// <summary>Stores per-character data; ignored without a loaded character.</summary>
    public static void Write(string key, JsonNode? node)
    {
        if (!HasCharacter || s_character == null) return;
        if (s_character["data"] is not JsonObject data) s_character["data"] = data = new JsonObject();
        data[key] = node;
        s_characterDirty = true;
        MarkDirtyNow();
    }

    // ---- Saving ----

    /// <summary>The profile in use changed; it is written shortly after.</summary>
    public static void MarkDirty()
    {
        if (InProfile) MarkDirtyNow();
    }

    private static void MarkDirtyNow()
    {
        s_dirty = true;
        if (s_save is not { Pending: true }) s_save = Scheduler.HostAfter("profiles save", SaveDelaySeconds, Save);
    }

    /// <summary>Writes what changed: the profile in use and the character's file.</summary>
    public static void Save()
    {
        s_save?.Cancel();
        s_save = null;
        if (!s_dirty) return;
        s_dirty = false;
        if (InProfile && s_profile != null) SaveProfile(s_active, s_profile, current: true);
        if (s_characterDirty && HasCharacter && s_character != null && s_key != null)
        {
            s_characterDirty = false;
            s_character["version"] = FormatVersion;
            s_character["mode"] = s_key.Mode;
            if (s_key.Id.Length == 0) s_character["slot"] = s_key.Slot;
            TryWrite(Path.Combine(CharacterDir, s_key.FileName), s_character, $"Character file ({s_key.Label})");
        }
    }

    // Every setting's value goes into the profile: its own, or with current false the Default profile's (a new copy).
    private static void SaveProfile(ProfileInfo info, JsonObject root, bool current)
    {
        root["version"] = FormatVersion;
        root["name"] = info.Name;
        if (root["groups"] is not JsonObject groups) root["groups"] = groups = new JsonObject();
        foreach (var group in SettingsRegistry.Groups.Where(g => !s_newerGroups.Contains(g.Id) || !current))
        {
            if (groups[group.Id] is not JsonObject stored) groups[group.Id] = stored = new JsonObject();
            stored["version"] = group.Version;
            if (stored["values"] is not JsonObject values) stored["values"] = values = new JsonObject();
            foreach (var setting in group.All)
            {
                if (current && !setting.HasOverride && s_rejected.Contains((group.Id, setting.Id))) continue;
                values[setting.Id] = setting.ToJson(global: !current);
            }
        }
        TryWrite(Path.Combine(ProfileDir, info.Id + ".json"), root, $"Profile '{info.Name}'");
    }

    // Written beside the target first, then moved over it; a failure is logged, never thrown.
    private static bool TryWrite(string path, JsonObject root, string what)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(s_writeOptions));
            File.Move(tmp, path, true);
            return true;
        }
        catch (Exception e)
        {
            Log.WarnThrottled("profiles:save", $"{what} not saved: {e.Message}");
            return false;
        }
    }
}
