using System.Text.Json;
using EpochAssist.Core;
using MelonLoader.Utils;

namespace EpochAssist.Ui;

/// <summary>A theme the player can pick: a preset, or a file's values over its base preset.</summary>
internal sealed record ThemeEntry(string Name, string? File, ThemeValues Values, string Identity, IReadOnlyList<string> Problems)
{
    public bool IsPreset => File == null;
}

/// <summary>The theme a name resolves to; Fallback when the name wasn't found and another theme stands in for it.</summary>
internal readonly record struct ResolvedTheme(string Name, ThemeValues Values, string Identity, bool Fallback);

/// <summary>
/// The presets plus player theme files from UserData/EpochAssist/themes/*.json (version 1, contracts/themes.md).
/// Files are read only: the mod never writes, renames or deletes them, and never creates the folder.
/// </summary>
internal static class Themes
{
    public const int FormatVersion = 1;
    private const string DefaultName = "Slate";

    private static List<ThemeEntry>? s_all;
    private static readonly List<string> s_problems = new();
    // A skipped file's theme name, to the preset that stands in for it while it can't be read.
    private static readonly Dictionary<string, string> s_skipped = new(StringComparer.OrdinalIgnoreCase);
    // File name plus content hash of every problem already logged, so each is logged once per file content.
    private static readonly HashSet<string> s_logged = new();

    public static string Folder => Path.Combine(MelonEnvironment.UserDataDirectory, "EpochAssist", "themes");

    /// <summary>Presets, then player themes in file name order.</summary>
    public static IReadOnlyList<ThemeEntry> All
    {
        get
        {
            if (s_all == null) Rescan();
            return s_all!;
        }
    }

    public static IReadOnlyList<string> Names => All.Select(t => t.Name).ToList();

    /// <summary>Every problem as "file: problem".</summary>
    public static IReadOnlyList<string> Problems
    {
        get
        {
            if (s_all == null) Rescan();
            return s_problems;
        }
    }

    public static int FileCount => All.Count(t => !t.IsPreset);

    public static ThemeEntry? Find(string name) => All.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The theme for a stored name. A name that no longer exists uses Slate (or a skipped file's base) and is kept.</summary>
    public static ResolvedTheme Resolve(string name)
    {
        if (Find(name) is { } entry) return new ResolvedTheme(entry.Name, entry.Values, entry.Identity, false);
        string stand = s_skipped.TryGetValue(name, out string? preset) ? preset : DefaultName;
        var fallback = Find(stand) ?? All[0];
        return new ResolvedTheme(fallback.Name, fallback.Values, fallback.Identity, true);
    }

    /// <summary>Lines for Settings under the theme choice: the file problems, and a missing selected theme.</summary>
    public static IEnumerable<string> NotesFor(string selected)
    {
        var resolved = Resolve(selected);
        if (resolved.Fallback) yield return $"{selected} isn't available, so {resolved.Name} is used until it is back.";
        foreach (string problem in Problems) yield return problem;
    }

    public static void Rescan()
    {
        var all = ThemeValues.Presets.Select(p => new ThemeEntry(p.Name, null, p.Values, p.Name, Array.Empty<string>())).ToList();
        s_problems.Clear();
        s_skipped.Clear();
        string[] files;
        try
        {
            files = Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*.json") : Array.Empty<string>();
        }
        catch (Exception e)
        {
            files = Array.Empty<string>();
            Log.Warn($"Themes folder unreadable: {e.Message}");
        }
        foreach (string path in files.OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
        {
            var problems = new List<string>();
            var entry = Read(path, all, problems);
            if (entry != null) all.Add(entry);
            string file = Path.GetFileName(path);
            foreach (string problem in problems) s_problems.Add($"{file}: {problem}");
        }
        s_all = all;
    }

    private static ThemeEntry? Read(string path, List<ThemeEntry> known, List<string> problems)
    {
        string file = Path.GetFileName(path);
        string fallbackName = Path.GetFileNameWithoutExtension(path);
        string logKey = file;
        string text;
        DateTime stamp;
        try
        {
            text = File.ReadAllText(path);
            stamp = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception e)
        {
            Skip(fallbackName, DefaultName, $"can't be read ({e.Message})");
            return null;
        }
        logKey = $"{file}:{text.GetHashCode()}";

        void Problem(string problem)
        {
            problems.Add(problem);
            if (s_logged.Add($"{logKey}:{problem}")) Log.Warn($"Theme {file}: {problem}");
        }
        void Skip(string name, string preset, string problem)
        {
            s_skipped.TryAdd(name, preset);
            Problem(problem);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException e)
        {
            Skip(fallbackName, DefaultName, $"unreadable JSON (line {(e.LineNumber ?? 0) + 1}, position {(e.BytePositionInLine ?? 0) + 1})");
            return null;
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                Skip(fallbackName, DefaultName, "unreadable: not a JSON object");
                return null;
            }

            string name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString()!.Trim().Length > 0
                ? n.GetString()!.Trim() : fallbackName;
            string? baseText = root.TryGetProperty("base", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            var basePreset = ThemeValues.Presets.FirstOrDefault(p => p.Name.Equals(baseText, StringComparison.OrdinalIgnoreCase));

            int version = FormatVersion;
            if (root.TryGetProperty("version", out var v) && !(v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out version)))
            {
                Skip(name, basePreset.Name ?? DefaultName, "version is not a whole number");
                return null;
            }
            if (version > FormatVersion)
            {
                Skip(name, basePreset.Name ?? DefaultName, $"made for a newer Epoch Assist (version {version})");
                return null;
            }
            if (known.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                Problem($"the name {name} is already taken, so this file is skipped");
                return null;
            }

            if (basePreset.Values == null)
            {
                Problem(baseText == null ? "no base, so Slate is used" : $"unknown base {baseText}, so Slate is used");
                basePreset = ThemeValues.Presets[0];
            }
            var values = basePreset.Values.Clone();
            if (root.TryGetProperty("values", out var map) && map.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in map.EnumerateObject())
                {
                    // Unknown names (and font.family, which follows the Font setting) are ignored and stay in the file.
                    var spec = ThemeValues.Specs.FirstOrDefault(s => s.Name == property.Name);
                    if (spec == null) continue;
                    string? problem = Apply(spec, property.Value, values);
                    if (problem != null) Problem($"{spec.Name}: {problem}");
                }
            }
            return new ThemeEntry(name, file, values, $"{name}@{stamp.Ticks}", problems.ToList());
        }
    }

    // Sets one value from the file; returns the problem, leaving the base's value, when it can't.
    private static string? Apply(ThemeValueSpec spec, JsonElement value, ThemeValues values)
    {
        if (spec.Kind == ThemeValueKind.Colour)
        {
            if (value.ValueKind != JsonValueKind.String || !ThemeValues.TryParseColour(value.GetString()!, out var color)) return "not a colour";
            spec.Set(values, color);
            return null;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number)) return "not a number";
        if (number < spec.Min || number > spec.Max) return "out of range";
        spec.Set(values, (float)number);
        return null;
    }
}
