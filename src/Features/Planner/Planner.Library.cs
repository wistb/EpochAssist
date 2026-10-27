using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EpochAssist.Core;
using EpochAssist.Features.PlannerFormats;
using MelonLoader.Utils;

namespace EpochAssist.Features;

internal static partial class Planner
{
    /// <summary>
    /// The build library and which build each character uses: UserData/EpochAssist/planner/builds/&lt;id&gt;.json, one
    /// per build, and planner/bindings.json. Files, not settings, so every settings profile shares them and no export or
    /// share code carries them. Written tmp-then-move, so a crash mid-write leaves the old file.
    /// </summary>
    internal static class Library
    {
        private const int FileVersion = 1;

        private static readonly List<Build> s_builds = new();
        private static readonly Dictionary<string, (string Build, int Stage)> s_bindings = new();
        // Build ids name files: the parsers' ids are letters, digits and dashes, and nothing else is accepted.
        private static readonly Regex s_id = new("^[a-z]+(?:-[A-Za-z0-9]+)+$", RegexOptions.CultureInvariant);
        private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true };

        private static string Dir => Path.Combine(MelonEnvironment.UserDataDirectory, "EpochAssist", "planner");
        private static string BuildsDir => Path.Combine(Dir, "builds");
        private static string BindingsPath => Path.Combine(Dir, "bindings.json");

        /// <summary>The builds, oldest import first.</summary>
        public static IReadOnlyList<Build> Builds => s_builds;

        /// <summary>Raised by every change, so readers can tell their copy is old.</summary>
        public static int Generation { get; private set; }

        private static bool s_loaded;

        /// <summary>Reads the library once, for the tab before the feature first runs.</summary>
        public static void EnsureLoaded()
        {
            if (!s_loaded) Load();
        }

        /// <summary>Reads every build file and the bindings; a file that can't be read is skipped and logged.</summary>
        public static void Load()
        {
            s_loaded = true;
            s_builds.Clear();
            s_bindings.Clear();
            Generation++;
            try
            {
                if (Directory.Exists(BuildsDir))
                {
                    foreach (string path in Directory.GetFiles(BuildsDir, "*.json"))
                    {
                        string id = Path.GetFileNameWithoutExtension(path);
                        try
                        {
                            if (s_id.IsMatch(id) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o && Build.FromJson(id, o) is { } build) s_builds.Add(build);
                            else Log.Warn($"Planner: {Path.GetFileName(path)} isn't a build this version reads; left as it is");
                        }
                        catch (Exception e) when (e is JsonException or IOException)
                        {
                            Log.ErrorThrottled($"planner:library:{id}", $"Planner: build file {Path.GetFileName(path)} could not be read: {e.Message}", 30);
                        }
                    }
                    s_builds.Sort((a, b) => a.ImportedAt.CompareTo(b.ImportedAt));
                }
                LoadBindings();
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("planner:library", $"Planner: the build library could not be read: {e.Message}", 30);
            }
        }

        private static void LoadBindings()
        {
            if (!File.Exists(BindingsPath)) return;
            if (JsonNode.Parse(File.ReadAllText(BindingsPath)) is not JsonObject root || Build.Int(root["version"]) != FileVersion
                || root["characters"] is not JsonObject characters)
            {
                Log.Warn("Planner: bindings.json isn't a version this one reads; left as it is");
                return;
            }
            bool dropped = false;
            foreach (var (character, node) in characters)
            {
                if (node is not JsonObject o || Build.Str(o["build"]) is not { } id) continue;
                // A binding to a build that's gone is dropped.
                if (Find(id) is not { } build)
                {
                    dropped = true;
                    continue;
                }
                s_bindings[character] = (id, Math.Clamp(Build.Int(o["stage"]) ?? build.FirstStage, 0, build.Stages.Count - 1));
            }
            if (dropped) SaveBindings();
        }

        public static Build? Find(string? id)
        {
            if (id == null) return null;
            foreach (var build in s_builds)
                if (build.Id == id) return build;
            return null;
        }

        /// <summary>Adds a build, or replaces the one with its id. False when it can't be stored.</summary>
        public static bool Add(Build build)
        {
            if (!s_id.IsMatch(build.Id) || build.Stages.Count == 0) return false;
            if (!WriteBuild(build)) return false;
            int at = s_builds.FindIndex(b => b.Id == build.Id);
            if (at >= 0)
            {
                s_builds[at] = build;
                ForgetResolution(build.Id);
                // The stage a character had may be past the new build's end.
                foreach (var (character, binding) in s_bindings.ToList())
                    if (binding.Build == build.Id) s_bindings[character] = (build.Id, Math.Clamp(binding.Stage, 0, build.Stages.Count - 1));
                SaveBindings();
            }
            else
            {
                s_builds.Add(build);
            }
            Generation++;
            return true;
        }

        public static bool Rename(string id, string name)
        {
            name = name.Trim();
            if (Find(id) is not { } build || name.Length == 0 || name == build.Name) return false;
            string old = build.Name;
            build.Name = name;
            if (!WriteBuild(build))
            {
                build.Name = old;
                return false;
            }
            Generation++;
            return true;
        }

        /// <summary>Deletes a build's file and drops every character's binding to it.</summary>
        public static bool Delete(string id)
        {
            if (Find(id) is not { } build) return false;
            try
            {
                string path = BuildPath(id);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("planner:library:delete", $"Planner: build file could not be deleted: {e.Message}", 30);
                return false;
            }
            s_builds.Remove(build);
            ForgetResolution(id);
            int before = s_bindings.Count;
            foreach (var (character, binding) in s_bindings.ToList())
                if (binding.Build == id) s_bindings.Remove(character);
            if (s_bindings.Count != before) SaveBindings();
            Generation++;
            return true;
        }

        /// <summary>The character's build id and stage, or null when it has none.</summary>
        public static (string Build, int Stage)? Binding(CharacterKey? character) =>
            character != null && s_bindings.TryGetValue(character.FileName, out var binding) ? binding : null;

        /// <summary>Uses a build for the character, from the build's first stage.</summary>
        public static bool Bind(CharacterKey character, string id)
        {
            if (Find(id) is not { } build) return false;
            s_bindings[character.FileName] = (id, build.FirstStage);
            SaveBindings();
            Generation++;
            return true;
        }

        /// <summary>Sets the character's stage; only the player changes it.</summary>
        public static bool SetStage(CharacterKey character, int stage)
        {
            if (!s_bindings.TryGetValue(character.FileName, out var binding) || Find(binding.Build) is not { } build) return false;
            stage = Math.Clamp(stage, 0, build.Stages.Count - 1);
            if (stage == binding.Stage) return false;
            s_bindings[character.FileName] = (binding.Build, stage);
            SaveBindings();
            Generation++;
            return true;
        }

        private static string BuildPath(string id) => Path.Combine(BuildsDir, id + ".json");

        private static bool WriteBuild(Build build) => Write(BuildPath(build.Id), build.ToJson(), "planner:library:write");

        private static void SaveBindings()
        {
            var characters = new JsonObject();
            foreach (var (character, binding) in s_bindings)
                characters[character] = new JsonObject { ["build"] = binding.Build, ["stage"] = binding.Stage };
            Write(BindingsPath, new JsonObject { ["version"] = FileVersion, ["characters"] = characters }, "planner:library:bindings");
        }

        private static bool Write(string path, JsonObject root, string key)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, root.ToJsonString(s_indented));
                File.Move(tmp, path, true);
                return true;
            }
            catch (Exception e)
            {
                Log.ErrorThrottled(key, $"Planner: {Path.GetFileName(path)} could not be written: {e.Message}", 30);
                return false;
            }
        }
    }
}
