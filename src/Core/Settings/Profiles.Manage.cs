using System.Text.Json;
using System.Text.Json.Nodes;

namespace EpochAssist.Core;

internal static partial class Profiles
{
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
}
