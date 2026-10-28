using System.Text.Json;
using MelonLoader.Utils;

namespace EpochAssist.Core;

/// <summary>
/// Past sessions left out of trends and best marks: UserData/EpochAssist/session-trends.json,
/// `{ "version": 1, "leftOut": [ "&lt;session id&gt;", … ] }`, beside the sessions folder so it isn't a problem file there.
/// Loaded on first use; saved beside the target and moved into place on each change. A missing or unreadable file is
/// an empty set; an unreadable one is logged once and left alone until the next change.
/// </summary>
internal static class SessionTrends
{
    private const int Version = 1;

    private static HashSet<string>? s_leftOut;

    public static string PathName => Path.Combine(MelonEnvironment.UserDataDirectory, "EpochAssist", "session-trends.json");

    private static HashSet<string> LeftOut => s_leftOut ??= Load();

    public static bool IsLeftOut(string id) => LeftOut.Contains(id);

    public static IReadOnlyCollection<string> All => LeftOut;

    /// <summary>Leaves a session out of trends, or takes it back in.</summary>
    public static void Set(string id, bool leftOut)
    {
        if (leftOut ? LeftOut.Add(id) : LeftOut.Remove(id)) Save();
    }

    /// <summary>Drops ids no longer in the index (rotated or deleted).</summary>
    public static void Prune(IEnumerable<string> ids)
    {
        if (s_leftOut is not { Count: > 0 } set) return;
        var keep = new HashSet<string>(ids, StringComparer.Ordinal);
        if (set.RemoveWhere(id => !keep.Contains(id)) > 0) Save();
    }

    /// <summary>Forgets the loaded set (shutdown); read again on the next use.</summary>
    public static void Reset() => s_leftOut = null;

    private static HashSet<string> Load()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(PathName)) return set;
            using var doc = JsonDocument.Parse(File.ReadAllText(PathName));
            if (doc.RootElement.TryGetProperty("leftOut", out var ids) && ids.ValueKind == JsonValueKind.Array)
            {
                foreach (var id in ids.EnumerateArray())
                    if (id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } text) set.Add(text);
            }
        }
        catch (Exception e)
        {
            Log.Warn($"Sessions: session-trends.json can't be read ({e.Message}); no session is left out until it changes");
        }
        return set;
    }

    private static void Save()
    {
        try
        {
            string text = SessionFiles.Line(json =>
            {
                json.WriteStartObject();
                json.WriteNumber("version", Version);
                json.WriteStartArray("leftOut");
                foreach (string id in LeftOut.OrderBy(i => i, StringComparer.Ordinal)) json.WriteStringValue(id);
                json.WriteEndArray();
                json.WriteEndObject();
            });
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            string tmp = PathName + ".tmp";
            File.WriteAllText(tmp, text + "\n");
            File.Move(tmp, PathName, true);
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("sessions:trends", $"Sessions: session-trends.json couldn't be saved: {e.Message}", 30);
        }
    }
}
