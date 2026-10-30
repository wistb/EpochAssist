using System.Text.Json;
using System.Text.Json.Nodes;
using UnityEngine;

namespace EpochAssist.Core;

internal static partial class Profiles
{
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

        bool migrated = false;
        foreach (var group in SettingsRegistry.Groups)
        {
            if (group.Shared || s_profile["groups"]?[group.Id] is not JsonObject stored) continue;
            int version = stored["version"] is JsonValue v && v.TryGetValue(out int n) ? n : group.Version;
            // Written by a newer build: its values may mean something else here.
            if (version > group.Version)
            {
                s_newerGroups.Add(group.Id);
                continue;
            }
            if (stored["values"] is not JsonObject values) continue;
            // Written by an older build: its values are brought up to date in place, and the profile is saved at the new
            // version (ids no longer used stay in the file). A failed step leaves the group as a newer one is left.
            if (version < group.Version)
            {
                if (group.RunStoredMigrations(version, values) < group.Version)
                {
                    Log.Warn($"Profile '{info.Name}': {group.Name} settings couldn't be updated from version {version}, so they use Default's");
                    s_newerGroups.Add(group.Id);
                    continue;
                }
                Log.Info($"Profile '{info.Name}': {group.Name} settings migrated from version {version} to {group.Version}");
                migrated = true;
            }
            foreach (var setting in group.All)
            {
                if (values.TryGetPropertyValue(setting.Id, out var value) && !setting.TryApplyOverride(value)) s_rejected.Add((group.Id, setting.Id));
            }
        }
        if (s_rejected.Count > 0)
        {
            // Named (group.setting), so a report says which; the values stay in the file.
            var names = s_rejected.Take(12).Select(r => $"{r.Group}.{r.Id}");
            Log.Warn($"Profile '{info.Name}': {s_rejected.Count} stored values don't fit their settings and use Default's: {string.Join(", ", names)}{(s_rejected.Count > 12 ? " and more" : string.Empty)}");
        }
        s_active = info;
        InProfile = true;
        if (migrated) MarkDirtyNow();
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
