using System.Text.Json;
using System.Text.Json.Nodes;
using MelonLoader.Utils;
using UnityEngine;

namespace EpochAssist.Core;

// Runtime only, never stored.
internal enum CharacterState { NoCharacter, Reading, NotReadable, Loaded, Unreadable }

/// <summary>
/// Which character is playing: the mode, the game's character slot and the character's name offline; online, where the
/// player has no slot, the game's character id instead. The name and id stay in memory for the file name and scrubbing
/// only; they are never written to the log, diagnostics or reports.
/// </summary>
internal sealed record CharacterKey(string Mode, int Slot, string Name, string Id = "")
{
    private const int MaxSlug = 40;

    /// <summary>
    /// The character's file: mode-slot-name.json offline, the name as lowercase letters, digits and dashes; online
    /// mode-id.json, so a renamed character keeps its file.
    /// </summary>
    public string FileName => Id.Length > 0
        ? $"{Mode}-{Profiles.Slug(Id, MaxSlug, "unknown")}.json"
        : $"{Mode}-{Slot}-{Profiles.Slug(Name, MaxSlug, "unnamed")}.json";

    /// <summary>For the log: the mode, and the slot where there is one.</summary>
    public string Label => Id.Length > 0 ? Mode : $"{Mode}, slot {Slot}";
}

/// <summary>
/// The game's own record of the character in play (its gameplay service): id, name and mode, set when a character
/// starts playing. Read by reflection, as the service's interface uses UniTask, which the project doesn't reference.
/// Online it is the only place the character is named: the online player has no actor or character data tracker.
/// </summary>
internal static class GameplayCharacter
{
    private const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
        | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;

    /// <summary>The service's id, name and offline flag; false with the reason when it isn't there yet.</summary>
    public static bool TryRead(out string id, out string name, out bool offline, out string reason)
    {
        id = name = string.Empty;
        offline = false;
        try
        {
            if (Service() is not { } service)
            {
                reason = "no gameplay service";
                return false;
            }
            object? Get(string member) => service.GetType().GetProperty(member, Any)?.GetValue(service);
            id = Get("CharacterId")?.ToString() ?? string.Empty;
            name = Get("CharacterName") as string ?? string.Empty;
            offline = Get("CharacterIsOffline") is true;
            reason = id.Length == 0 ? "no character id" : string.Empty;
            return id.Length > 0;
        }
        catch (Exception e)
        {
            reason = $"gameplay service: {(e.InnerException ?? e).GetType().Name}";
            return false;
        }
    }

    // The stored service first; GameplayService itself hands out a UniTask, read only once it has finished.
    private static object? Service()
    {
        var provider = typeof(Il2CppLE.Services.ServiceProvider);
        var property = provider.GetProperty("_GameplayService", Any) ?? provider.GetProperty("GameplayService", Any);
        if (property?.GetMethod == null) return null;
        object? owner = property.GetMethod.IsStatic ? null : provider.GetProperty("Instance", Any)?.GetValue(null);
        object? service = property.GetValue(owner);
        if (service == null || !service.GetType().Name.StartsWith("UniTask")) return service;
        if (service.GetType().GetProperty("Status", Any)?.GetValue(service)?.ToString() != "Succeeded") return null;
        object? awaiter = service.GetType().GetMethod("GetAwaiter", Any)?.Invoke(service, null);
        return awaiter?.GetType().GetMethod("GetResult", Any)?.Invoke(awaiter, null);
    }
}

/// <summary>A settings profile: its id (the file name, kept on rename) and the name the player gave it.</summary>
internal sealed record ProfileInfo(string Id, string Name)
{
    public bool IsDefault => Id == Profiles.DefaultId;
}

/// <summary>
/// Settings profiles: Default is the global settings in MelonPreferences; any other
/// profile is a full set of every setting in UserData/EpochAssist/profiles/&lt;id&gt;.json. Each character uses one
/// profile (Default until it picks another), remembered in UserData/EpochAssist/characters/&lt;key&gt;.json with the
/// character's own data (autocast's slot toggles). While a character on a profile other than Default plays, every
/// setting reads and writes that profile; out of game the Default profile is in use. A file this build can't read is
/// left alone and Default is used instead.
/// </summary>
internal static class Profiles
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

    // ---- Managing profiles ----

    /// <summary>
    /// Puts a profile (by id) in use. With a character in game it becomes that character's profile; out of game it is
    /// the one being edited until a character enters (which brings its own). False with the reason when it can't.
    /// </summary>
    public static bool Use(string id, out string reason)
    {
        reason = string.Empty;
        if (All.All(p => p.Id != id))
        {
            reason = "no such profile";
            return false;
        }
        if (HasCharacter && s_character != null)
        {
            s_character["profile"] = id;
            s_characterDirty = true;
            MarkDirtyNow();
        }
        Activate(id);
        s_users = null;
        Save();
        // Logged here as well: the toast saying so shows only once per 10 s.
        Log.Info(HasCharacter ? $"profile '{s_active.Name}' picked for this character" : $"profile '{s_active.Name}' picked for editing");
        return true;
    }

    /// <summary>
    /// A new profile with a unique name, a copy of the Default profile's values, or with copy of the profile in use.
    /// Returns it, or null with the reason.
    /// </summary>
    public static ProfileInfo? Create(string name, bool copy, out string reason)
    {
        if (!ValidName(name, null, out reason)) return null;
        name = name.Trim();
        string id = Slug(name, 24, "profile");
        for (int n = 2; id == DefaultId || File.Exists(Path.Combine(ProfileDir, id + ".json")); n++) id = $"{Slug(name, 24, "profile")}-{n}";
        var info = new ProfileInfo(id, name);
        // Values the profile in use couldn't read stay with it; the copy takes Default's for those.
        var root = new JsonObject();
        if (copy && InProfile) SaveProfile(info, root, current: true);
        else SaveProfile(info, root, current: false);
        s_list = null;
        if (All.All(p => p.Id != id))
        {
            reason = "it could not be saved";
            return null;
        }
        return info;
    }

    public static bool Rename(string id, string name, out string reason)
    {
        if (id == DefaultId)
        {
            reason = "Default can't be renamed";
            return false;
        }
        if (!ValidName(name, id, out reason)) return false;
        string path = Path.Combine(ProfileDir, id + ".json");
        try
        {
            var root = id == s_active.Id && s_profile != null ? s_profile : (JsonObject)ParseFile(path);
            root["name"] = name.Trim();
            if (!TryWrite(path, root, $"Profile '{name.Trim()}'"))
            {
                reason = "it could not be saved";
                return false;
            }
        }
        catch (Exception e)
        {
            reason = e.Message;
            return false;
        }
        if (id == s_active.Id) s_active = new ProfileInfo(id, name.Trim());
        s_list = null;
        return true;
    }

    /// <summary>Deletes a profile's file; characters on it go back to Default.</summary>
    public static bool Delete(string id, out string reason)
    {
        reason = string.Empty;
        if (id == DefaultId)
        {
            reason = "Default can't be deleted";
            return false;
        }
        if (id == s_active.Id) Use(DefaultId, out _);
        try
        {
            File.Delete(Path.Combine(ProfileDir, id + ".json"));
        }
        catch (Exception e)
        {
            reason = e.Message;
            return false;
        }
        s_list = null;
        s_users = null;
        return true;
    }

    private static bool ValidName(string name, string? renaming, out string reason)
    {
        reason = string.Empty;
        string trimmed = name.Trim();
        if (trimmed.Length == 0) reason = "the name is empty";
        else if (trimmed.Length > MaxName) reason = $"the name is longer than {MaxName} characters";
        else if (All.Any(p => p.Id != renaming && p.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase))) reason = "another profile has that name";
        return reason.Length == 0;
    }

    /// <summary>Lowercase letters, digits and dashes, at most max long; fallback when nothing is left.</summary>
    public static string Slug(string text, int max, string fallback)
    {
        string slug = new(text.ToLowerInvariant().Select(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') ? c : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        if (slug.Length > max) slug = slug.Substring(0, max).TrimEnd('-');
        return slug.Length > 0 ? slug : fallback;
    }

    private static List<ProfileInfo> Scan()
    {
        var list = new List<ProfileInfo>();
        try
        {
            if (Directory.Exists(ProfileDir))
            {
                foreach (string path in Directory.GetFiles(ProfileDir, "*.json"))
                {
                    string id = Path.GetFileNameWithoutExtension(path);
                    string name;
                    try
                    {
                        name = ParseFile(path)["name"]?.GetValue<string>() ?? id;
                    }
                    catch (Exception)
                    {
                        // Listed under its file name, so it can still be picked (and fails to load) or deleted.
                        name = id;
                    }
                    if (id != DefaultId) list.Add(new ProfileInfo(id, name));
                }
            }
        }
        catch (Exception e)
        {
            Log.WarnThrottled("profiles:scan", $"Profiles could not be listed: {e.Message}");
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, new ProfileInfo(DefaultId, DefaultName));
        return list;
    }

    private static Dictionary<string, int> CountUsers()
    {
        var users = new Dictionary<string, int>();
        try
        {
            if (!Directory.Exists(CharacterDir)) return users;
            foreach (string path in Directory.GetFiles(CharacterDir, "*.json"))
            {
                string id;
                try
                {
                    id = ParseFile(path)["profile"]?.GetValue<string>() ?? DefaultId;
                }
                catch (Exception)
                {
                    continue;
                }
                if (All.All(p => p.Id != id)) id = DefaultId;
                users[id] = users.TryGetValue(id, out int n) ? n + 1 : 1;
            }
        }
        catch (Exception e)
        {
            Log.WarnThrottled("profiles:users", $"Character files could not be read: {e.Message}");
        }
        return users;
    }

    private static JsonNode ParseFile(string path) =>
        JsonNode.Parse(File.ReadAllText(path), documentOptions: s_readOptions) ?? throw new JsonException("empty file");

    // ---- Following the character ----

    /// <summary>The Diagnostics line; never the character's name.</summary>
    public static string StatusText()
    {
        string profile = s_profileProblem.Length > 0 ? $"; profile can't be read, so Default is used: {s_profileProblem}" : string.Empty;
        return State switch
        {
            CharacterState.NoCharacter => "no character in game (Default)",
            CharacterState.Reading => "reading the character (Default)",
            CharacterState.NotReadable => "character not readable (Default)",
            CharacterState.Unreadable => $"this character's file can't be read, so it uses Default: {s_characterProblem}",
            _ => $"this character uses '{s_active.Name}'{profile}",
        };
    }

    /// <summary>Once per update: follows the playing character, loading its file and the profile it uses.</summary>
    internal static void Update()
    {
        if (GameState.Kind == SceneKind.Menu)
        {
            UnloadCharacter();
            s_hadPlayer = false;
            State = CharacterState.NoCharacter;
            return;
        }
        bool hasPlayer = Player.Exists;
        if (hasPlayer && !s_hadPlayer) s_spawnedAt = Time.unscaledTime;
        s_hadPlayer = hasPlayer;
        if (!hasPlayer)
        {
            if (s_key == null) State = CharacterState.NoCharacter;
            return;
        }
        // With a character loaded it is checked again now and then; while reading, every update.
        if (s_key != null && !s_recheck.Ready) return;
        if (State == CharacterState.NotReadable) return;
        s_recheck.Start(RecheckSeconds);

        if (!TryReadKey(out var key, out string reason))
        {
            if (s_key != null) return;
            State = Time.unscaledTime - s_spawnedAt < ReadingSeconds ? CharacterState.Reading : CharacterState.NotReadable;
            if (State == CharacterState.NotReadable) Log.Warn($"Profiles: the character couldn't be read ({reason}); Default is used until the next zone");
            return;
        }
        if (key == s_key) return;
        UnloadCharacter();
        LoadCharacter(key);
    }

    /// <summary>A new zone starts a new attempt for a character that couldn't be read.</summary>
    internal static void OnSceneChanged()
    {
        if (State == CharacterState.NotReadable) State = CharacterState.NoCharacter;
        s_hadPlayer = false;
    }

    private static void LoadCharacter(CharacterKey key)
    {
        s_key = key;
        s_characterDirty = false;
        Diagnostics.CharacterName = key.Name;
        string path = Path.Combine(CharacterDir, key.FileName);
        try
        {
            s_character = File.Exists(path) ? ParseFile(path) as JsonObject ?? throw new JsonException("not a JSON object") : new JsonObject();
            int version = s_character["version"] is JsonValue v && v.TryGetValue(out int n) ? n : FormatVersion;
            if (version > FormatVersion) throw new JsonException($"format version {version} is newer than this build reads ({FormatVersion})");
        }
        catch (Exception e)
        {
            // Left alone: never written this session; the character plays on Default.
            s_character = null;
            s_characterProblem = e.Message;
            State = CharacterState.Unreadable;
            Log.Warn($"Character file ({key.Label}) can't be read, so this character uses Default: {e.Message}");
            Generation++;
            return;
        }
        State = CharacterState.Loaded;
        Generation++;
        string id = s_character["profile"] is JsonValue p && p.TryGetValue(out string? stored) ? stored : DefaultId;
        Activate(All.Any(x => x.Id == id) ? id : DefaultId);
        Log.Info($"character loaded ({key.Label}), profile '{s_active.Name}'");
    }

    private static void UnloadCharacter()
    {
        if (s_key == null) return;
        Save();
        Activate(DefaultId);
        bool wasLoaded = HasCharacter;
        s_key = null;
        s_character = null;
        s_characterDirty = false;
        State = CharacterState.NoCharacter;
        Diagnostics.CharacterName = null;
        Generation++;
        if (wasLoaded) Log.Info("character unloaded; profile Default");
    }

    // Puts the profile in use away and takes up another: its values go into every setting's override slot.
    private static void Activate(string id)
    {
        if (InProfile && s_profile != null) Save();
        foreach (var setting in SettingsRegistry.Groups.SelectMany(g => g.All)) setting.ClearOverride();
        InProfile = false;
        s_profile = null;
        s_profileProblem = string.Empty;
        s_rejected.Clear();
        s_newerGroups.Clear();
        s_active = All.First(p => p.IsDefault);
        if (id == DefaultId) return;

        var info = All.First(p => p.Id == id);
        try
        {
            var root = ParseFile(Path.Combine(ProfileDir, id + ".json")) as JsonObject ?? throw new JsonException("not a JSON object");
            int version = root["version"] is JsonValue v && v.TryGetValue(out int n) ? n : FormatVersion;
            if (version > FormatVersion) throw new JsonException($"format version {version} is newer than this build reads ({FormatVersion})");
            s_profile = root;
        }
        catch (Exception e)
        {
            s_profileProblem = e.Message;
            Log.Warn($"Profile '{info.Name}' can't be read, so Default is used: {e.Message}");
            return;
        }

        foreach (var group in SettingsRegistry.Groups)
        {
            if (s_profile["groups"]?[group.Id] is not JsonObject stored) continue;
            int version = stored["version"] is JsonValue v && v.TryGetValue(out int n) ? n : group.Version;
            // Written by a newer build: its values may mean something else here. Older ones would migrate (none yet).
            if (version > group.Version)
            {
                s_newerGroups.Add(group.Id);
                continue;
            }
            if (stored["values"] is not JsonObject values) continue;
            foreach (var setting in group.All)
            {
                if (values.TryGetPropertyValue(setting.Id, out var value) && !setting.TryApplyOverride(value)) s_rejected.Add((group.Id, setting.Id));
            }
        }
        if (s_rejected.Count > 0) Log.Warn($"Profile '{info.Name}': {s_rejected.Count} stored values don't fit their settings and use Default's");
        s_active = info;
        InProfile = true;
    }

    internal static void Reset()
    {
        UnloadCharacter();
        Activate(DefaultId);
        State = CharacterState.NoCharacter;
        s_hadPlayer = false;
        s_spawnedAt = -1f;
        s_recheck.Clear();
        s_list = null;
        s_users = null;
    }

    /// <summary>
    /// Reads the current character's key. Offline from the player: its character data tracker's slot and character
    /// name. Online, where the player object has no actor, from the game's gameplay service: its character id. The mode
    /// is the one the player object shows. False with the reason when any part is missing (before spawn, or right after).
    /// </summary>
    public static bool TryReadKey(out CharacterKey key, out string reason)
    {
        key = null!;
        if (Player.Online == true) return TryReadOnlineKey(out key, out reason);
        var tracker = Player.Actor?.characterDataTracker;
        if (tracker == null)
        {
            reason = Player.Actor == null ? "no player" : "no character data tracker";
            return false;
        }
        var data = tracker.charData;
        if (data == null)
        {
            reason = "no character data";
            return false;
        }
        if (Player.Online is not bool online)
        {
            reason = "mode not known";
            return false;
        }
        key = new CharacterKey(online ? "online" : "offline", tracker.characterSlotId, data.CharacterName ?? string.Empty);
        reason = string.Empty;
        return true;
    }

    private static bool TryReadOnlineKey(out CharacterKey key, out string reason)
    {
        key = null!;
        if (!Player.Exists)
        {
            reason = "no player";
            return false;
        }
        if (!GameplayCharacter.TryRead(out string id, out string name, out bool offline, out reason)) return false;
        // Still the offline character's record (it changes as the character starts playing): read again shortly.
        if (offline)
        {
            reason = "the gameplay service shows an offline character";
            return false;
        }
        key = new CharacterKey("online", 0, name, id);
        return true;
    }

    /// <summary>For the dev profile command: the key's parts without the name itself, and the profile in use.</summary>
    public static string Describe()
    {
        string profile = $"profile '{s_active.Name}' ({s_active.Id}), state {State.ToString().ToLowerInvariant()}, generation {Generation}";
        if (!TryReadKey(out var key, out string reason)) return $"{profile}; key not readable ({reason})";
        string name = $"name read {(key.Name.Length > 0 ? "yes" : "no")} ({key.Name.Length} chars)";
        // Online: the id's length only, never the id.
        if (key.Id.Length > 0) return $"{profile}; mode {key.Mode}, id read ({key.Id.Length} chars), {name}";
        var data = Player.Actor!.characterDataTracker!.charData!;
        string offline;
        try { offline = data.IsOffline.ToString(); } catch (Exception e) { offline = $"? ({e.GetType().Name})"; }
        return $"{profile}; mode {key.Mode}, slot {key.Slot}, {name}, isOffline {offline}";
    }
}

/// <summary>
/// Follows the playing character and keeps the profile it uses in effect. Without it (the game lacks one of the
/// character members) the Default profile is always in use and slot toggles behave as before profiles.
/// </summary>
internal sealed class ProfilesFeature : Feature
{
    public override string Id => "profiles";
    public override string Name => "Profiles";
    public override string[] Needs => new[] { "game" };
    public override Requirement[] Requirements => new[]
    {
        Need<Il2Cpp.Actor>(nameof(Il2Cpp.Actor.characterDataTracker)),
        Need<Il2Cpp.CharacterDataTracker>(nameof(Il2Cpp.CharacterDataTracker.characterSlotId)),
        Need<Il2Cpp.CharacterDataTracker>(nameof(Il2Cpp.CharacterDataTracker.charData)),
        Need<Il2CppLE.Data.CharacterData>(nameof(Il2CppLE.Data.CharacterData.CharacterName)),
    };

    public override void Start()
    {
        Diagnostics.ProfileStatus = Profiles.StatusText;
        Actions.Register(Id, "profiles.new", "New profile", () => Profiles.Create(NextName("Profile"), copy: false, out _));
        Actions.Register(Id, "profiles.duplicate", "Duplicate profile", () => Profiles.Create(NextName(Profiles.Active.Name + " copy"), copy: true, out _));
    }

    /// <summary>The name, or the name with the first free number after it.</summary>
    public static string NextName(string name)
    {
        if (Profiles.Find(name) == null && !name.Equals(Profiles.DefaultName, StringComparison.OrdinalIgnoreCase)) return name;
        for (int n = 2; ; n++)
        {
            if (Profiles.Find($"{name} {n}") == null) return $"{name} {n}";
        }
    }

    public override void Update() => Profiles.Update();
    public override void OnSceneChanged() => Profiles.OnSceneChanged();

    public override void Shutdown()
    {
        Diagnostics.ProfileStatus = null;
        Profiles.Reset();
    }
}
