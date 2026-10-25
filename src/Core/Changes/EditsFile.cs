using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MelonLoader.Utils;

namespace EpochAssist.Core;

/// <summary>UserData/EpochAssist/edits.json: originals of saved values while our value is applied, and their recovery.</summary>
internal static class EditsFile
{
    private sealed record Saved(string Target, string Scene, string Original, string Written, string At);

    private static readonly List<Saved> s_entries = new();
    // Left from the last session: restored when the target's object exists and still holds our value.
    private static readonly List<Saved> s_recover = new();
    private static Cooldown s_nextTry;

    private static string FilePath => Path.Combine(MelonEnvironment.UserDataDirectory, "EpochAssist", "edits.json");

    public static bool Pending => s_recover.Count > 0;

    internal static void Load()
    {
        s_entries.Clear();
        s_recover.Clear();
        try
        {
            if (!File.Exists(FilePath)) return;
            var root = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject;
            if (root?["entries"] is not JsonArray entries) return;
            foreach (var node in entries)
            {
                if (node is not JsonObject o) continue;
                string? target = (string?)o["target"], original = (string?)o["original"], written = (string?)o["written"];
                if (target == null || original == null || written == null) continue;
                var saved = new Saved(target, (string?)o["scene"] ?? string.Empty, original, written, (string?)o["at"] ?? string.Empty);
                s_entries.Add(saved);
                s_recover.Add(saved);
            }
            if (s_recover.Count > 0) Log.Info($"edits: {s_recover.Count} saved value{(s_recover.Count == 1 ? string.Empty : "s")} left by the last session, restored when found");
        }
        catch (Exception e)
        {
            Log.Error($"edits.json could not be read: {e.Message}");
        }
    }

    internal static void Reset()
    {
        s_recover.Clear();
        s_entries.Clear();
    }

    public static void Record(string target, string original, string written)
    {
        s_entries.RemoveAll(e => e.Target == target);
        s_entries.Add(new Saved(target, GameState.Scene, original, written, DateTime.Now.ToString("s", CultureInfo.InvariantCulture)));
        Save();
    }

    public static void Remove(string target)
    {
        if (s_entries.RemoveAll(e => e.Target == target) > 0) Save();
    }

    // Twice a second while something is left: an entry goes once its object is found (restored if it still holds
    // our value); unknown targets are dropped, entries never found stay for the next session.
    internal static void Recover()
    {
        if (!s_nextTry.Ready || GameState.Loading) return;
        s_nextTry.Start(0.5f);
        foreach (var saved in s_recover.ToList()) TryRecover(saved);
    }

    public static void RecoverNow(string target)
    {
        foreach (var saved in s_recover.Where(s => s.Target == target).ToList()) TryRecover(saved, force: true);
    }

    private static void TryRecover(Saved saved, bool force = false)
    {
        bool? done;
        try
        {
            done = EditTargets.Recover(saved.Target, saved.Original, saved.Written, force);
        }
        catch (Exception e)
        {
            Log.ErrorThrottled($"edits:recover:{saved.Target}", $"edits: recovering '{saved.Target}' failed: {e.Message}", 30);
            done = false;
        }
        if (done == null) return;
        s_recover.Remove(saved);
        s_entries.Remove(saved);
        Save();
    }

    // Null while the object isn't there; true when restored, false when someone changed the value since.
    internal static bool? RecoverTarget<T>(EditTarget<T> target, string savedOriginal, string savedWritten, bool force)
    {
        var obj = target.Find().FirstOrDefault(o => Game.Alive(o));
        if (obj == null) return force ? false : null;
        T now = target.Read!(obj);
        T written = Parse<T>(savedWritten), original = Parse<T>(savedOriginal);
        bool ours = target.Same?.Invoke(now, written) ?? (now is float a && written is float b ? Math.Abs(a - b) <= 1e-4f : EqualityComparer<T>.Default.Equals(now, written));
        if (!ours)
        {
            Log.Info($"edits: '{target.Name}' was changed since the last session; left as it is");
            return false;
        }
        target.Write?.Invoke(obj, original);
        Log.Info($"edits: '{target.Name}' restored to {savedOriginal} (left at {savedWritten} by the last session)");
        return true;
    }

    private static T Parse<T>(string text) => (T)Convert.ChangeType(text, typeof(T), CultureInfo.InvariantCulture);

    private static void Save()
    {
        try
        {
            string path = FilePath;
            if (s_entries.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            var entries = new JsonArray();
            foreach (var e in s_entries)
                entries.Add(new JsonObject { ["target"] = e.Target, ["scene"] = e.Scene, ["original"] = e.Original, ["written"] = e.Written, ["at"] = e.At });
            var root = new JsonObject { ["version"] = 1, ["entries"] = entries };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, true);
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("edits:file", $"edits.json could not be written: {e.Message}", 30);
        }
    }
}
