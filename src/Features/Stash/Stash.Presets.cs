using System.Text.Json;
using System.Text.Json.Nodes;
using EpochAssist.Core;
using MelonLoader.Utils;

namespace EpochAssist.Features;

internal static partial class Stash
{
    /// <summary>Where a tab's unique list comes from: stored in the rules, or the player's planner builds with edits.</summary>
    internal sealed class UniqueSource
    {
        public const string Fixed = "fixed", Builds = "builds";
        public string Kind { get; set; } = Fixed;
        public List<int> Added { get; } = new();
        public List<int> Removed { get; } = new();
    }

    internal sealed class PresetTab
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int IconId { get; set; }
        public int ColourId { get; set; }
        public TabRules Rules { get; set; } = TabRules.Empty;
        /// <summary>Usefulness, lower first.</summary>
        public int Rank { get; set; }
        /// <summary>For a merged tab: the section keys it stands for while they aren't all bought.</summary>
        public List<string>? MergeOf { get; set; }
        public UniqueSource? Uniques { get; set; }
        /// <summary>Saved presets: how many tabs this section had when captured.</summary>
        public int? SavedCount { get; set; }
        public bool IsMerged => MergeOf is { Count: > 0 };
    }

    internal sealed class PresetFolder
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int IconId { get; set; }
        public int ColourId { get; set; }
        public List<PresetTab> Tabs { get; } = new();
    }

    internal sealed class Preset
    {
        public const int FileVersion = 1;
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool BuiltIn { get; set; }
        public List<PresetFolder> Folders { get; } = new();

        /// <summary>Every tab in preset order with its folder.</summary>
        public IEnumerable<(PresetFolder Folder, PresetTab Tab)> AllTabs() =>
            Folders.SelectMany(f => f.Tabs.Select(t => (f, t)));
    }

    internal static class Presets
    {
        public const string BuiltInId = "default";
        private static List<Preset>? s_all;

        private static string Dir => Path.Combine(MelonEnvironment.UserDataDirectory, "EpochAssist", "stash", "presets");

        /// <summary>The built-in preset first, then the player's in name order; loaded once until <see cref="Reload"/>.</summary>
        public static IReadOnlyList<Preset> All => s_all ??= Load();

        public static void Reload() => s_all = null;

        public static Preset? Find(string? id) => All.FirstOrDefault(p => p.Id == id);

        public static Preset Default => Find(BuiltInId) ?? All.FirstOrDefault() ?? new Preset { Id = BuiltInId, Name = "Epoch Assist", BuiltIn = true };

        private static List<Preset> Load()
        {
            var list = new List<Preset>();
            try
            {
                using var stream = typeof(Stash).Assembly.GetManifestResourceStream($"EpochAssist.Stash.{BuiltInId}.json");
                if (stream != null && Parse(JsonNode.Parse(stream)) is { } builtIn)
                {
                    builtIn.BuiltIn = true;
                    list.Add(builtIn);
                }
            }
            catch (Exception e)
            {
                Log.Error($"Stash: built-in preset unreadable: {e.Message}");
            }
            var own = new List<Preset>();
            if (Directory.Exists(Dir))
                foreach (string path in Directory.GetFiles(Dir, "*.json"))
                {
                    try
                    {
                        if (Parse(JsonNode.Parse(File.ReadAllText(path))) is { } p && p.Id.Length > 0 && list.All(x => x.Id != p.Id)) own.Add(p);
                        else Log.Warn($"Stash: preset {Path.GetFileName(path)} skipped (no id, or the id is taken)");
                    }
                    catch (Exception e)
                    {
                        Log.Warn($"Stash: preset {Path.GetFileName(path)} skipped: {e.Message}");
                    }
                }
            list.AddRange(own.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase));
            return list;
        }

        public static Preset? Parse(JsonNode? node)
        {
            if (node is not JsonObject o) return null;
            if ((int?)o["fileVersion"] is { } v && v > Preset.FileVersion) throw new InvalidDataException($"file version {v} is newer than {Preset.FileVersion}");
            var preset = new Preset { Id = (string?)o["id"] ?? string.Empty, Name = (string?)o["name"] ?? "Preset" };
            if (o["folders"] is JsonArray folders)
                foreach (var fn in folders.OfType<JsonObject>())
                {
                    var folder = new PresetFolder
                    {
                        Key = (string?)fn["key"] ?? $"f{preset.Folders.Count}",
                        Name = (string?)fn["name"] ?? string.Empty,
                        IconId = Clamp((int?)fn["icon"] ?? 0, 23),
                        ColourId = Clamp((int?)fn["colour"] ?? 0, 17),
                    };
                    if (fn["tabs"] is JsonArray tabs)
                        foreach (var tn in tabs.OfType<JsonObject>())
                        {
                            var tab = new PresetTab
                            {
                                Key = (string?)tn["key"] ?? $"{folder.Key}.{folder.Tabs.Count}",
                                Name = (string?)tn["name"] ?? folder.Name,
                                IconId = Clamp((int?)tn["icon"] ?? folder.IconId, 23),
                                ColourId = Clamp((int?)tn["colour"] ?? folder.ColourId, 17),
                                Rules = Rules.FromJson(tn["rules"]),
                                Rank = (int?)tn["rank"] ?? 1000,
                                MergeOf = (tn["mergeOf"] as JsonArray)?.Select(n => (string?)n).OfType<string>().ToList(),
                                SavedCount = (int?)tn["savedCount"],
                            };
                            if (tn["uniques"] is JsonObject u)
                            {
                                tab.Uniques = new UniqueSource { Kind = (string?)u["source"] == UniqueSource.Builds ? UniqueSource.Builds : UniqueSource.Fixed };
                                tab.Uniques.Added.AddRange(Ints(u["added"]));
                                tab.Uniques.Removed.AddRange(Ints(u["removed"]));
                            }
                            folder.Tabs.Add(tab);
                        }
                    preset.Folders.Add(folder);
                }
            return preset;
        }

        private static int Clamp(int v, int max) => v < 0 || v > max ? 0 : v;

        private static IEnumerable<int> Ints(JsonNode? node) =>
            (node as JsonArray)?.Select(n => n is JsonValue j && j.TryGetValue(out int i) ? i : -1).Where(i => i >= 0) ?? Enumerable.Empty<int>();

        public static JsonObject ToJson(Preset preset)
        {
            var folders = new JsonArray();
            foreach (var f in preset.Folders)
            {
                var tabs = new JsonArray();
                foreach (var t in f.Tabs)
                {
                    var tn = new JsonObject { ["key"] = t.Key, ["name"] = t.Name, ["icon"] = t.IconId, ["colour"] = t.ColourId, ["rank"] = t.Rank, ["rules"] = Rules.ToJson(t.Rules) };
                    if (t.MergeOf is { Count: > 0 }) tn["mergeOf"] = new JsonArray(t.MergeOf.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray());
                    if (t.Uniques is { } u)
                        tn["uniques"] = new JsonObject
                        {
                            ["source"] = u.Kind,
                            ["added"] = new JsonArray(u.Added.Select(i => (JsonNode?)i).ToArray()),
                            ["removed"] = new JsonArray(u.Removed.Select(i => (JsonNode?)i).ToArray()),
                        };
                    if (t.SavedCount is { } n) tn["savedCount"] = n;
                    tabs.Add(tn);
                }
                folders.Add(new JsonObject { ["key"] = f.Key, ["name"] = f.Name, ["icon"] = f.IconId, ["colour"] = f.ColourId, ["tabs"] = tabs });
            }
            return new JsonObject { ["fileVersion"] = Preset.FileVersion, ["id"] = preset.Id, ["name"] = preset.Name, ["folders"] = folders };
        }

        /// <summary>Writes a player preset (never the built-in) and reloads the list.</summary>
        public static bool Save(Preset preset)
        {
            if (preset.BuiltIn) return false;
            try
            {
                Directory.CreateDirectory(Dir);
                string path = Path.Combine(Dir, preset.Id + ".json");
                File.WriteAllText(path + ".tmp", ToJson(preset).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(path + ".tmp", path, overwrite: true);
                Reload();
                return true;
            }
            catch (Exception e)
            {
                Log.Error($"Stash: preset {preset.Name} not saved: {e.Message}");
                return false;
            }
        }

        /// <summary>Deletes a player preset's file (never the built-in) and reloads the list.</summary>
        public static bool Delete(Preset preset)
        {
            if (preset.BuiltIn) return false;
            try
            {
                string path = Path.Combine(Dir, preset.Id + ".json");
                if (File.Exists(path)) File.Delete(path);
                Reload();
                return true;
            }
            catch (Exception e)
            {
                Log.Error($"Stash: preset {preset.Name} not deleted: {e.Message}");
                return false;
            }
        }

        /// <summary>The name with the next free number ("Epoch Assist 2", "Epoch Assist 3"), any number it ends in left off
        /// first; the name itself when it is free and carries no number.</summary>
        public static string NextName(string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(name.Trim(), @"^(.*?)\s+\d+$");
            string stem = m.Success ? m.Groups[1].Value : name.Trim();
            bool Taken(string n) => All.Any(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase));
            for (int n = 2; ; n++)
                if (!Taken($"{stem} {n}")) return $"{stem} {n}";
        }

        /// <summary>A fresh id for a new player preset.</summary>
        public static string NewId() => "p" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");

        /// <summary>A deep copy under a new id and name, editable.</summary>
        public static Preset Copy(Preset source, string name)
        {
            var copy = Parse(ToJson(source))!;
            copy.Id = NewId();
            copy.Name = name;
            copy.BuiltIn = false;
            return copy;
        }
    }

    /// <summary>
    /// Saves the stash as a player preset: every folder and tab in game order with its name, icon, colour and rules; one
    /// section per distinct rule set in a folder, keeping how many tabs it had; ranks in stash order; no merges.
    /// </summary>
    public static Preset? Capture(StashSnapshot snap, string name)
    {
        var preset = new Preset { Id = Presets.NewId(), Name = name };
        int rank = 0;
        foreach (var f in snap.Folders)
        {
            var folder = new PresetFolder { Key = "f" + f.Id, Name = f.Name, IconId = f.IconId, ColourId = f.ColourId };
            foreach (var group in snap.TabsIn(f.Id).GroupBy(t => t.Rules))
            {
                var t = group.First();
                folder.Tabs.Add(new PresetTab
                {
                    Key = $"t{t.Id}", Name = t.Name, IconId = t.IconId, ColourId = t.ColourId, Rules = t.Rules, Rank = ++rank,
                    SavedCount = group.Count(),
                    Uniques = t.Rules.Uniques.Length > 0 ? new UniqueSource { Kind = UniqueSource.Fixed } : null,
                });
            }
            preset.Folders.Add(folder);
        }
        return Presets.Save(preset) ? Presets.Find(preset.Id) : null;
    }
}
