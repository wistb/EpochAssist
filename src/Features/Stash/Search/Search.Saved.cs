using System.Text.Json;
using System.Text.Json.Nodes;
using EpochAssist.Core;
using MelonLoader.Utils;
using UnityEngine;

namespace EpochAssist.Features;

internal static partial class StashSearch
{
    public const string CodeTag = "EASEARCH";
    public const int CodeVersion = 1;
    public const int MaxSaved = 50, MaxRecent = 10;
    private const int FileVersion = 1;
    private const float RecentAfterSeconds = 3f;

    private static List<SearchDoc>? s_saved;
    private static List<SearchDoc>? s_recent;
    // A file from a newer build: read for what's known, not overwritten unless the player says so.
    private static int s_fileVersion;
    private static bool s_overwriteNewer;
    // When the active search's results first showed, for the recent list.
    private static (int Generation, float Since)? s_shownSince;

    private static string SavedPath => Path.Combine(MelonEnvironment.UserDataDirectory, "EpochAssist", "stash", "searches.json");

    /// <summary>Saved searches, in the order saved (account-wide, like stash presets).</summary>
    public static IReadOnlyList<SearchDoc> Saved => LoadSaved().Saved;

    /// <summary>The most recent searches, newest first.</summary>
    public static IReadOnlyList<SearchDoc> Recent => LoadSaved().Recent;

    /// <summary>The file was written by a newer build: saving would drop its newer parts.</summary>
    public static bool SavedFileNewer => LoadSaved().Saved != null && s_fileVersion > FileVersion && !s_overwriteNewer;

    private static (List<SearchDoc> Saved, List<SearchDoc> Recent) LoadSaved()
    {
        if (s_saved != null && s_recent != null) return (s_saved, s_recent);
        s_saved = new List<SearchDoc>();
        s_recent = new List<SearchDoc>();
        s_fileVersion = 0;
        try
        {
            if (File.Exists(SavedPath) && JsonNode.Parse(File.ReadAllText(SavedPath)) is JsonObject root)
            {
                s_fileVersion = root["v"] is JsonValue v && v.TryGetValue(out int version) ? version : 1;
                Read(root["saved"], s_saved, named: true);
                Read(root["recent"], s_recent, named: false);
            }
        }
        catch (Exception e)
        {
            Log.Warn($"Stash search: saved searches unreadable, starting empty: {e.Message}");
        }
        return (s_saved, s_recent);
    }

    private static void Read(JsonNode? node, List<SearchDoc> into, bool named)
    {
        if (node is not JsonArray list) return;
        foreach (var item in list)
        {
            if (item is not JsonObject o || Parse(o.ToJsonString(), out _) is not { } doc) continue;
            if (named && string.IsNullOrWhiteSpace(doc.Name)) continue;
            into.Add(doc);
        }
    }

    private static bool WriteSaved()
    {
        if (SavedFileNewer)
        {
            Log.Warn("Stash search: searches.json is from a newer Epoch Assist; not overwritten");
            return false;
        }
        var (saved, recent) = LoadSaved();
        var root = new JsonObject
        {
            ["v"] = FileVersion,
            ["saved"] = new JsonArray(saved.Select(d => JsonNode.Parse(d.ToJson())).ToArray()),
            ["recent"] = new JsonArray(recent.Select(d => JsonNode.Parse(d.ToJson())).ToArray()),
        };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SavedPath)!);
            string tmp = SavedPath + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, SavedPath, true);
            s_fileVersion = FileVersion;
            return true;
        }
        catch (Exception e)
        {
            Log.WarnThrottled("search:saved", $"Stash search: saved searches not written: {e.Message}");
            return false;
        }
    }

    /// <summary>The player chose to replace a newer build's file with this build's.</summary>
    public static void OverwriteNewerFile()
    {
        s_overwriteNewer = true;
        WriteSaved();
    }

    /// <summary>A name not taken yet: the name itself, else "Name 2", "Name 3"…</summary>
    public static string FreeName(string name, SearchDoc? except = null)
    {
        name = name.Trim();
        if (name.Length > MaxNameLength) name = name[..MaxNameLength].TrimEnd();
        if (name.Length == 0) name = "Search";
        var taken = new HashSet<string>(Saved.Where(d => d != except).Select(d => d.Name ?? string.Empty), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (int n = 2; ; n++)
        {
            string suffix = " " + n;
            string candidate = (name.Length + suffix.Length > MaxNameLength ? name[..(MaxNameLength - suffix.Length)].TrimEnd() : name) + suffix;
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>Saves a copy of a search under a name ("Name 2" when taken); the reason when it can't.</summary>
    public static string? SaveAs(SearchDoc doc, string name, out SearchDoc? saved)
    {
        saved = null;
        if (Saved.Count >= MaxSaved) return $"At most {MaxSaved} saved searches";
        if (SavedFileNewer) return "Saved by a newer Epoch Assist";
        var copy = doc.Clone();
        copy.Name = FreeName(name);
        s_saved!.Add(copy);
        if (!WriteSaved())
        {
            s_saved.Remove(copy);
            return "Couldn't write the file (see the log)";
        }
        saved = copy;
        return null;
    }

    /// <summary>Replaces a saved search's content with a search, keeping its name.</summary>
    public static string? Overwrite(SearchDoc saved, SearchDoc doc)
    {
        if (SavedFileNewer) return "Saved by a newer Epoch Assist";
        int i = s_saved!.IndexOf(saved);
        if (i < 0) return "No such saved search";
        var copy = doc.Clone();
        copy.Name = saved.Name;
        s_saved[i] = copy;
        return WriteSaved() ? null : "Couldn't write the file (see the log)";
    }

    public static string? Rename(SearchDoc saved, string name)
    {
        if (SavedFileNewer) return "Saved by a newer Epoch Assist";
        if (!s_saved!.Contains(saved)) return "No such saved search";
        saved.Name = FreeName(name, saved);
        return WriteSaved() ? null : "Couldn't write the file (see the log)";
    }

    public static string? Delete(SearchDoc saved)
    {
        if (SavedFileNewer) return "Saved by a newer Epoch Assist";
        if (!s_saved!.Remove(saved)) return "No such saved search";
        return WriteSaved() ? null : "Couldn't write the file (see the log)";
    }

    public static SearchDoc? FindSaved(string name) => Saved.FirstOrDefault(d => string.Equals(d.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    // A search becomes recent once its results showed for 3 s, or when a result is clicked; a duplicate moves up.
    private static void RecentFrame()
    {
        if (s_active == null || s_active.IsEmpty || Matching || ResultCount == 0)
        {
            s_shownSince = null;
            return;
        }
        if (s_shownSince is not { } shown || shown.Generation != s_generation)
        {
            s_shownSince = (s_generation, Time.unscaledTime);
            return;
        }
        if (shown.Since < 0f || Time.unscaledTime - shown.Since < RecentAfterSeconds) return;
        s_shownSince = (s_generation, -1f);
        AddRecent(s_active);
    }

    /// <summary>Puts a search at the top of the recent list (a result was clicked, or it showed for a while).</summary>
    internal static void AddRecent(SearchDoc doc)
    {
        if (doc.IsEmpty || SavedFileNewer) return;
        var (_, recent) = LoadSaved();
        var copy = doc.Clone();
        copy.Name = null;
        string json = copy.ToJson();
        recent.RemoveAll(d => d.ToJson() == json);
        recent.Insert(0, copy);
        if (recent.Count > MaxRecent) recent.RemoveRange(MaxRecent, recent.Count - MaxRecent);
        WriteSaved();
    }

    /// <summary>A short line for a recent search: its first rows' stats and its filters.</summary>
    internal static string Describe(SearchDoc doc)
    {
        if (doc.Name is { Length: > 0 } name) return name;
        var parts = doc.Groups.SelectMany(g => g.Rows).Take(3).Select(r => EntryOf(r.Stat)?.Text ?? RowName(r)).ToList();
        if (doc.Filters.Any) parts.Add($"{FilterCountOf(doc.Filters)} filter{(FilterCountOf(doc.Filters) == 1 ? string.Empty : "s")}");
        int more = doc.RowCount - Math.Min(3, doc.RowCount);
        return (parts.Count == 0 ? "Empty search" : string.Join(", ", parts)) + (more > 0 ? $" +{more}" : string.Empty);
    }

    private static int FilterCountOf(ItemFilters f) => new[]
    {
        f.Types.Count > 0, f.Rarities.Count > 0, f.Uniques.Count > 0, !f.Lp.IsEmpty, !f.Ww.IsEmpty, !f.Wt.IsEmpty, !f.Fp.IsEmpty,
        !f.LevelReq.IsEmpty, !f.Prefixes.IsEmpty, !f.Suffixes.IsEmpty, f.Corrupted != Tri.Any, f.Sealed != Tri.Any, f.Class != null,
    }.Count(x => x);

    private static void ResetSaved()
    {
        s_saved = null;
        s_recent = null;
        s_shownSince = null;
        s_overwriteNewer = false;
    }

    // ---- share codes ----

    /// <summary>The search's share code, or why it can't be shared. Carries only the search.</summary>
    public static (string? Code, string? Error) CodeOf(SearchDoc doc)
    {
        string code = ShareCode.Pack(CodeTag, CodeVersion, doc.ToJson());
        return code.Length > ShareCode.MaxLength ? (null, $"search too large to share ({code.Length} characters)") : (code, null);
    }

    /// <summary>Reads a share code: the search, or not a share code, made by a newer Epoch Assist, damaged or incomplete.</summary>
    public static SearchDoc? FromCode(string text, out string? error)
    {
        text = text.Trim();
        if (text.Length > ShareCode.MaxLength + 64)
        {
            error = $"search too large to share ({text.Length} characters)";
            return null;
        }
        var (_, json, unpackError) = ShareCode.Unpack(CodeTag, CodeVersion, text);
        if (unpackError != null)
        {
            error = unpackError;
            return null;
        }
        var doc = Parse(json!, out _);
        error = doc == null ? ShareCode.Damaged : null;
        return doc;
    }
}
