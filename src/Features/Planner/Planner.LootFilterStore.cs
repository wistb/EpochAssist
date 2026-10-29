using System.Text.Json;
using System.Text.Json.Nodes;
using EpochAssist.Core;
using EpochAssist.Features.PlannerFormats;

namespace EpochAssist.Features;

/// <summary>A filter file the mod made: which build and stage it was made for, and the SHA-256 of the bytes written.</summary>
internal sealed class MadeRecord
{
    public string File = string.Empty;
    public string BuildId = string.Empty;
    /// <summary>A stage index as text, "all", or "base" for EA · Base.</summary>
    public string StageKey = string.Empty;
    public string Hash = string.Empty;
    /// <summary>The fingerprint of the page's plan it was made from (<see cref="Planner.PlanFingerprint"/>), so the page can
    /// tell whether anything changed since; empty for EA · Base.</summary>
    public string PlanHash = string.Empty;
    /// <summary>What it was made from, to list what changed since; null for records made before snapshots were kept.</summary>
    public PlanSnapshot? Snapshot;
    /// <summary>Edits made in the game's editor the page can't show (changed conditions, added rules); an Update undoes them.</summary>
    public List<string> GameNotes = new();
    public int Rules;
    public DateTime Made;
}

/// <summary>A made file now: as written, edited in the game's editor, or no longer there under its name.</summary>
internal enum MadeState { Unchanged, Edited, Gone }

internal static partial class Planner
{
    /// <summary>
    /// The loot filter's own files under UserData/EpochAssist/planner/filters/: made.json (a record per made filter file)
    /// and &lt;build id&gt;.json (the build's choices per stage). Written tmp-then-move. A file in the game's Filters
    /// folder with no record here is never written.
    /// </summary>
    internal static class LootFilterStore
    {
        private const int FileVersion = 1;
        private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true };

        private static string Dir => LootFilterGame.DataDir;
        private static string RecordsPath => Path.Combine(Dir, "made.json");
        private static string ChoicesPath(string buildId) => Path.Combine(Dir, buildId + ".json");

        private static List<MadeRecord>? s_records;
        // Choices per build, read on first use; changed ones are written once the frame they changed in is over.
        private static readonly Dictionary<string, Dictionary<string, StageChoices>> s_choices = new();
        private static readonly HashSet<string> s_dirty = new();
        private static int s_dirtyFrame = -1;

        // ---- made records ----

        public static IReadOnlyList<MadeRecord> Records => s_records ??= LoadRecords();

        /// <summary>The record of the filter made for a build and stage key ("base" for EA · Base), or null.</summary>
        public static MadeRecord? RecordFor(string buildId, string stageKey)
        {
            foreach (var record in Records)
                if (record.BuildId == buildId && record.StageKey == stageKey) return record;
            return null;
        }

        /// <summary>The record of a file by its file name, or null: a file without one isn't the mod's.</summary>
        public static MadeRecord? RecordOfFile(string file)
        {
            foreach (var record in Records)
                if (string.Equals(record.File, file, StringComparison.OrdinalIgnoreCase)) return record;
            return null;
        }

        /// <summary>The file as it is now against its record.</summary>
        public static MadeState StateOf(MadeRecord record)
        {
            if (LootFilterGame.FilterFolder is not { } folder) return MadeState.Gone;
            string? hash = LootFilterGame.HashFileCached(Path.Combine(folder, record.File));
            return hash == null ? MadeState.Gone : hash == record.Hash ? MadeState.Unchanged : MadeState.Edited;
        }

        /// <summary>Records a made file, replacing the build and stage's earlier record.</summary>
        public static void Record(string file, string buildId, string stageKey, string hash, PlanSnapshot? snapshot = null, int rules = 0)
        {
            var records = (List<MadeRecord>)Records;
            records.RemoveAll(r => r.BuildId == buildId && r.StageKey == stageKey || string.Equals(r.File, file, StringComparison.OrdinalIgnoreCase));
            records.Add(new MadeRecord { File = file, BuildId = buildId, StageKey = stageKey, Hash = hash, PlanHash = snapshot?.Fingerprint ?? string.Empty, Snapshot = snapshot, Rules = rules, Made = DateTime.UtcNow });
            SaveRecords();
        }

        /// <summary>A made file's edits were taken into the page: the record now holds the file as it is and the plan it
        /// matches, with the edits the page couldn't show.</summary>
        public static void TookIn(MadeRecord record, string hash, PlanSnapshot snapshot, List<string> notes)
        {
            record.Hash = hash;
            record.Snapshot = snapshot;
            record.PlanHash = snapshot.Fingerprint;
            record.GameNotes = notes;
            SaveRecords();
        }

        /// <summary>Drops one record (its file is gone).</summary>
        public static void Forget(MadeRecord record)
        {
            if (((List<MadeRecord>)Records).Remove(record)) SaveRecords();
        }

        private static List<MadeRecord> LoadRecords()
        {
            var list = new List<MadeRecord>();
            try
            {
                if (!File.Exists(RecordsPath)) return list;
                if (JsonNode.Parse(File.ReadAllText(RecordsPath)) is not JsonObject root || Build.Int(root["version"]) != FileVersion || root["made"] is not JsonArray made)
                {
                    Log.Warn("Planner: made.json isn't a version this one reads; left as it is");
                    return list;
                }
                foreach (var node in made)
                {
                    if (node is not JsonObject o || Build.Str(o["file"]) is not { Length: > 0 } file || Build.Str(o["hash"]) is not { Length: > 0 } hash) continue;
                    list.Add(new MadeRecord
                    {
                        File = file,
                        BuildId = Build.Str(o["build"]) ?? string.Empty,
                        StageKey = Build.Str(o["stage"]) ?? string.Empty,
                        Hash = hash,
                        PlanHash = Build.Str(o["plan"]) ?? string.Empty,
                        Snapshot = o["snapshot"] is JsonObject snapshot ? ReadSnapshot(snapshot) : null,
                        GameNotes = o["gameNotes"] is JsonArray notes ? notes.Select(n => Build.Str(n)).OfType<string>().ToList() : new List<string>(),
                        Rules = Build.Int(o["rules"]) ?? 0,
                        Made = DateTime.TryParse(Build.Str(o["made"]), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : DateTime.MinValue,
                    });
                }
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                Log.ErrorThrottled("planner:filter:records", $"Planner: made.json could not be read: {e.Message}", 30);
            }
            return list;
        }

        private static void SaveRecords()
        {
            var made = new JsonArray();
            foreach (var r in Records)
                made.Add(new JsonObject { ["file"] = r.File, ["build"] = r.BuildId, ["stage"] = r.StageKey, ["hash"] = r.Hash, ["plan"] = r.PlanHash, ["rules"] = r.Rules, ["made"] = r.Made.ToString("o"),
                    ["snapshot"] = r.Snapshot != null ? WriteSnapshot(r.Snapshot) : null,
                    ["gameNotes"] = r.GameNotes.Count > 0 ? new JsonArray(r.GameNotes.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()) : null });
            Write(RecordsPath, new JsonObject { ["version"] = FileVersion, ["made"] = made });
        }

        private static JsonObject WriteSnapshot(PlanSnapshot snapshot)
        {
            var rules = new JsonArray();
            foreach (var r in snapshot.Rules) rules.Add(new JsonArray(r.Key, r.Name, r.On, r.Hide, r.Look, r.Cond));
            return new JsonObject
            {
                ["name"] = snapshot.Name, ["description"] = snapshot.Description, ["base"] = snapshot.Base, ["baseHash"] = snapshot.BaseHash,
                ["strictness"] = snapshot.Strictness, ["rules"] = rules,
            };
        }

        private static PlanSnapshot ReadSnapshot(JsonObject o)
        {
            var snapshot = new PlanSnapshot
            {
                Name = Build.Str(o["name"]) ?? string.Empty, Description = Build.Str(o["description"]) ?? string.Empty,
                Base = Build.Str(o["base"]) ?? string.Empty, BaseHash = Build.Str(o["baseHash"]) ?? string.Empty, Strictness = Build.Int(o["strictness"]) ?? 1,
            };
            if (o["rules"] is JsonArray rules)
                foreach (var node in rules)
                    if (node is JsonArray r && r.Count == 6)
                        snapshot.Rules.Add(new SnapRule(Build.Str(r[0]) ?? string.Empty, Build.Str(r[1]) ?? string.Empty, Bool(r[2]) == true, Bool(r[3]) == true,
                            Build.Str(r[4]) ?? string.Empty, Build.Str(r[5]) ?? string.Empty));
            return snapshot;
        }

        // ---- choices ----

        /// <summary>The build's choices for a stage key, made empty on first use.</summary>
        public static StageChoices ChoicesFor(string buildId, string stageKey)
        {
            var stages = StagesOf(buildId);
            if (!stages.TryGetValue(stageKey, out var choices)) stages[stageKey] = choices = new StageChoices();
            return choices;
        }

        /// <summary>The stage keys of a build that have choices.</summary>
        public static IEnumerable<string> StageKeys(string buildId) => StagesOf(buildId).Keys;

        /// <summary>Marks a build's choices changed; they're written after the frame, so a drag writes once.</summary>
        public static void Changed(string buildId)
        {
            s_dirty.Add(buildId);
            s_dirtyFrame = UnityEngine.Time.frameCount;
        }

        /// <summary>Writes changed choices once a frame has passed without an edit (the feature's frame).</summary>
        public static void Flush(bool now = false)
        {
            if (s_dirty.Count == 0 || !now && UnityEngine.Time.frameCount == s_dirtyFrame) return;
            foreach (string id in s_dirty) SaveChoices(id);
            s_dirty.Clear();
        }

        /// <summary>Forgets a stage key's choices (Reset).</summary>
        public static void Reset(string buildId, string stageKey)
        {
            if (StagesOf(buildId).Remove(stageKey)) Changed(buildId);
        }

        /// <summary>Deletes a build's choices file; its made filters stay.</summary>
        public static void DeleteChoices(string buildId)
        {
            s_choices.Remove(buildId);
            s_dirty.Remove(buildId);
            try
            {
                if (File.Exists(ChoicesPath(buildId))) File.Delete(ChoicesPath(buildId));
            }
            catch (IOException e)
            {
                Log.ErrorThrottled("planner:filter:choices", $"Planner: loot filter choices could not be deleted: {e.Message}", 30);
            }
        }

        /// <summary>
        /// A build's stages were moved, removed or replaced: re-keys its choices and made-filter records by the full map of
        /// old stage index to new (null = removed). A removed stage's choices and record are dropped; its made file stays
        /// in the Filters folder as the player's. Stages not in the map and the "all" key are kept as they are.
        /// </summary>
        public static void StagesChanged(string buildId, IReadOnlyDictionary<int, int?> map)
        {
            var stages = StagesOf(buildId);
            var moved = new Dictionary<string, StageChoices>();
            foreach (var (key, choices) in stages)
            {
                if (!int.TryParse(key, out int old) || !map.TryGetValue(old, out int? now)) moved[key] = choices;
                else if (now is int index) moved[index.ToString()] = choices;
            }
            stages.Clear();
            foreach (var (key, choices) in moved) stages[key] = choices;
            Changed(buildId);
            Flush(now: true);

            var records = (List<MadeRecord>)Records;
            bool recordsChanged = false;
            foreach (var record in records.ToList())
            {
                if (record.BuildId != buildId || !int.TryParse(record.StageKey, out int old) || !map.TryGetValue(old, out int? now)) continue;
                if (now is int index) record.StageKey = index.ToString();
                else records.Remove(record);
                recordsChanged = true;
            }
            if (recordsChanged) SaveRecords();
            StagesMoved(buildId);
        }

        /// <summary>
        /// A saved build was merged into another as its later stages: moves its choices and made-filter records onto the
        /// target at stage index + <paramref name="offset"/>, then deletes its choices file. Its "all" choices and an All
        /// stages record are dropped (the target's "all" is kept); that made file stays as the player's.
        /// </summary>
        public static void MoveChoices(string fromBuild, string toBuild, int offset)
        {
            var target = StagesOf(toBuild);
            foreach (var (key, choices) in StagesOf(fromBuild))
                if (int.TryParse(key, out int index)) target[(index + offset).ToString()] = choices;
            Changed(toBuild);
            DeleteChoices(fromBuild);
            Flush(now: true);

            var records = (List<MadeRecord>)Records;
            bool recordsChanged = false;
            foreach (var record in records.ToList())
            {
                if (record.BuildId != fromBuild) continue;
                if (int.TryParse(record.StageKey, out int index))
                {
                    record.BuildId = toBuild;
                    record.StageKey = (index + offset).ToString();
                }
                else
                {
                    records.Remove(record);
                }
                recordsChanged = true;
            }
            if (recordsChanged) SaveRecords();
            StagesMoved(fromBuild);
            StagesMoved(toBuild);
        }

        /// <summary>Drops what's in memory, writing changes first (the feature stopping).</summary>
        public static void Forget()
        {
            Flush(now: true);
            s_choices.Clear();
            s_records = null;
        }

        /// <summary>
        /// Drops a stage's choices about things its plan no longer makes: rules, slots and uniques the build doesn't plan
        /// any more (a re-import). New planned things simply have no choice, so they get the defaults.
        /// </summary>
        public static void DropStale(string buildId, FilterPlan plan, IEnumerable<string> slotKeys, IEnumerable<ushort> uniques)
        {
            var choices = ChoicesFor(buildId, plan.StageKey);
            var keys = plan.Rules.Select(r => r.Key).ToHashSet();
            var slots = slotKeys.ToHashSet();
            var ids = uniques.ToHashSet();
            bool changed = false;
            foreach (string key in choices.Rules.Keys.ToList())
                if (!keys.Contains(key) && !plan.LeftOut.Any(l => l.Why == "over the limit")) changed |= choices.Rules.Remove(key);
            foreach (string key in choices.Slots.Keys.ToList())
                if (!slots.Contains(key)) changed |= choices.Slots.Remove(key);
            changed |= choices.DroppedUniques.RemoveAll(id => !ids.Contains(id)) > 0;
            if (changed) Changed(buildId);
        }

        private static Dictionary<string, StageChoices> StagesOf(string buildId)
        {
            if (!s_choices.TryGetValue(buildId, out var stages)) s_choices[buildId] = stages = LoadChoices(buildId);
            return stages;
        }

        private static Dictionary<string, StageChoices> LoadChoices(string buildId)
        {
            var stages = new Dictionary<string, StageChoices>();
            try
            {
                string path = ChoicesPath(buildId);
                if (!File.Exists(path)) return stages;
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root || Build.Int(root["version"]) != FileVersion || root["stages"] is not JsonObject all)
                {
                    Log.Warn($"Planner: {Path.GetFileName(path)} isn't a version this one reads; left as it is");
                    return stages;
                }
                foreach (var (key, node) in all)
                    if (node is JsonObject o) stages[key] = ReadStage(o);
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                Log.ErrorThrottled("planner:filter:choices", $"Planner: loot filter choices could not be read: {e.Message}", 30);
            }
            return stages;
        }

        private static StageChoices ReadStage(JsonObject o)
        {
            var c = new StageChoices
            {
                Base = Build.Str(o["base"]) is { } b && b != "ea" ? b : null,
                Strictness = Build.Int(o["strictness"]),
                Name = Build.Str(o["name"]),
                HideRest = Bool(o["hideRest"]),
                HideOtherClasses = Bool(o["hideOtherClasses"]),
                HideClassesFirst = Bool(o["hideClassesFirst"]),
                HideAll = Bool(o["hideAll"]),
                LowShards = Build.Int(o["lowShards"]),
                ShatterTier = Build.Int(o["shatterTier"]),
            };
            if (o["utility"] is JsonObject utility)
                foreach (var (k, v) in utility)
                    if (Enum.TryParse<UtilityGroup>(k, true, out var group) && Bool(v) is bool on) c.Utility[group] = on;
            if (o.ContainsKey("levelBand"))
            {
                c.BandSet = true;
                c.Band = o["levelBand"] is JsonArray band && band.Count == 2 && Build.Int(band[0]) is int min && Build.Int(band[1]) is int max ? new LevelBand(min, max) : null;
            }
            if (o["rules"] is JsonObject rules)
                foreach (var (key, node) in rules)
                {
                    if (node is not JsonObject r) continue;
                    c.Rules[key] = new RuleChoice
                    {
                        Enabled = Bool(r["enabled"]), Hide = Bool(r["hide"]), Look = r["look"] is JsonObject look ? ReadLook(look) : null,
                        Conditions = r["conditions"] is JsonObject conditions ? ReadConditions(conditions) : null,
                    };
                }
            if (o["slots"] is JsonObject slots)
                foreach (var (key, node) in slots)
                {
                    if (node is not JsonObject s) continue;
                    c.Slots[key] = new SlotChoice { Affixes = Ints(s["affixes"]), Min = Build.Int(s["min"]), TierBar = Build.Int(s["tierBar"]), Types = Ints(s["types"]), SubTypes = Ints(s["subTypes"]) };
                }
            if (o["uniques"] is JsonObject uniques)
            {
                foreach (int id in Ints(uniques["dropped"]) ?? new List<int>())
                    if (id is > 0 and <= ushort.MaxValue) c.DroppedUniques.Add((ushort)id);
                c.Potential = Bool(uniques["potential"]);
            }
            return c;
        }

        private static RuleLook ReadLook(JsonObject o) => new()
        {
            Colour = Build.Int(o["colour"]),
            Emphasized = Bool(o["emphasized"]) == true,
            Sound = Build.Int(o["sound"]) ?? 0,
            MapIcon = Build.Int(o["mapIcon"]) ?? 0,
            Beam = o["beam"] is JsonArray beam && beam.Count == 2 && Build.Int(beam[0]) is int size && Build.Int(beam[1]) is int colour ? (size, colour) : null,
        };

        // A rule's conditions as kept from the game's editor.
        private static JsonObject WriteConditions(RuleConditions c)
        {
            var affixes = new JsonArray();
            foreach (var a in c.Affixes)
            {
                var o = new JsonObject { ["ids"] = Array(a.Ids), ["min"] = a.Min, ["tierBar"] = a.TierBar, ["sum"] = a.Sum };
                if (a.Raw is { } raw) o["raw"] = new JsonArray(raw.Cmp, raw.Value, raw.CombinedCmp, raw.CombinedValue, raw.Advanced);
                affixes.Add(o);
            }
            return new JsonObject
            {
                ["types"] = c.Types != null ? Array(c.Types) : null,
                ["subTypes"] = c.SubTypes != null ? Array(c.SubTypes) : null,
                ["affixes"] = affixes,
                ["uniques"] = c.Uniques != null ? Array(c.Uniques.Select(u => (int)u)) : null,
                ["potential"] = c.Potential is { } p ? new JsonArray(p.Lp, p.Ww) : null,
                ["rarity"] = c.Rarity,
                ["corruption"] = c.Corruption,
                ["levels"] = c.Levels is { } l ? new JsonArray(l.Min, l.Max) : null,
                ["classes"] = c.Classes,
                ["factions"] = c.Factions != null ? Array(c.Factions) : null,
                ["forging"] = c.Forging,
                ["resource"] = c.Resource?.ToString(),
            };
        }

        private static RuleConditions ReadConditions(JsonObject o)
        {
            var c = new RuleConditions
            {
                Types = Ints(o["types"]),
                SubTypes = Ints(o["subTypes"]),
                Uniques = Ints(o["uniques"])?.Where(u => u is > 0 and <= ushort.MaxValue).Select(u => (ushort)u).ToList(),
                Potential = o["potential"] is JsonArray p && p.Count == 2 ? (Build.Int(p[0]), Build.Int(p[1])) : null,
                Rarity = Build.Int(o["rarity"]) ?? 0,
                Corruption = Build.Int(o["corruption"]) ?? 0,
                Levels = o["levels"] is JsonArray l && l.Count == 2 && Build.Int(l[0]) is int min && Build.Int(l[1]) is int max ? (min, max) : null,
                Classes = Build.Int(o["classes"]) ?? 0,
                Factions = Ints(o["factions"]),
                Forging = Build.Int(o["forging"]),
                Resource = Enum.TryParse<ResourceKind>(Build.Str(o["resource"]), out var resource) ? resource : null,
            };
            if (o["affixes"] is JsonArray affixes)
                foreach (var node in affixes)
                {
                    if (node is not JsonObject a) continue;
                    var need = new AffixNeed { Ids = Ints(a["ids"]) ?? new List<int>(), Min = Build.Int(a["min"]) ?? 1, TierBar = Build.Int(a["tierBar"]) ?? 0, Sum = Build.Int(a["sum"]) };
                    if (a["raw"] is JsonArray raw && raw.Count == 5 && Build.Int(raw[0]) is int cmp && Build.Int(raw[1]) is int value
                        && Build.Int(raw[2]) is int ccmp && Build.Int(raw[3]) is int cvalue)
                        need.Raw = (cmp, value, ccmp, cvalue, Bool(raw[4]) == true);
                    c.Affixes.Add(need);
                }
            return c;
        }

        private static void SaveChoices(string buildId)
        {
            if (!s_choices.TryGetValue(buildId, out var stages)) return;
            var all = new JsonObject();
            foreach (var (key, c) in stages)
            {
                if (c.Empty) continue;
                var o = new JsonObject();
                if (c.Base != null) o["base"] = c.Base;
                if (c.Strictness != null) o["strictness"] = c.Strictness;
                if (c.Name != null) o["name"] = c.Name;
                if (c.BandSet) o["levelBand"] = c.Band is { } band ? new JsonArray(band.Min, band.Max) : null;
                if (c.HideRest != null) o["hideRest"] = c.HideRest;
                if (c.HideOtherClasses != null) o["hideOtherClasses"] = c.HideOtherClasses;
                if (c.HideClassesFirst != null) o["hideClassesFirst"] = c.HideClassesFirst;
                if (c.HideAll != null) o["hideAll"] = c.HideAll;
                if (c.LowShards != null) o["lowShards"] = c.LowShards;
                if (c.ShatterTier != null) o["shatterTier"] = c.ShatterTier;
                if (c.Utility.Count > 0)
                {
                    var utility = new JsonObject();
                    foreach (var (group, on) in c.Utility) utility[group.ToString().ToLowerInvariant()] = on;
                    o["utility"] = utility;
                }
                if (c.Rules.Count > 0)
                {
                    var rules = new JsonObject();
                    foreach (var (k, r) in c.Rules)
                    {
                        var ro = new JsonObject();
                        if (r.Enabled != null) ro["enabled"] = r.Enabled;
                        if (r.Hide != null) ro["hide"] = r.Hide;
                        if (r.Look is { } look)
                            ro["look"] = new JsonObject
                            {
                                ["colour"] = look.Colour, ["emphasized"] = look.Emphasized, ["sound"] = look.Sound, ["mapIcon"] = look.MapIcon,
                                ["beam"] = look.Beam is { } beam ? new JsonArray(beam.Size, beam.Colour) : null,
                            };
                        if (r.Conditions is { } conditions) ro["conditions"] = WriteConditions(conditions);
                        rules[k] = ro;
                    }
                    o["rules"] = rules;
                }
                if (c.Slots.Count > 0)
                {
                    var slots = new JsonObject();
                    foreach (var (k, s) in c.Slots)
                    {
                        var so = new JsonObject();
                        if (s.Affixes != null) so["affixes"] = Array(s.Affixes);
                        if (s.Min != null) so["min"] = s.Min;
                        if (s.TierBar != null) so["tierBar"] = s.TierBar;
                        if (s.Types != null) so["types"] = Array(s.Types);
                        if (s.SubTypes != null) so["subTypes"] = Array(s.SubTypes);
                        slots[k] = so;
                    }
                    o["slots"] = slots;
                }
                if (c.DroppedUniques.Count > 0 || c.Potential != null)
                    o["uniques"] = new JsonObject { ["dropped"] = Array(c.DroppedUniques.Select(u => (int)u)), ["potential"] = c.Potential };
                all[key] = o;
            }
            if (all.Count == 0)
            {
                DeleteChoices(buildId);
                s_choices[buildId] = stages;
                return;
            }
            Write(ChoicesPath(buildId), new JsonObject { ["version"] = FileVersion, ["stages"] = all });
        }

        private static JsonArray Array(IEnumerable<int> values)
        {
            var array = new JsonArray();
            foreach (int v in values) array.Add(v);
            return array;
        }

        private static List<int>? Ints(JsonNode? node)
        {
            if (node is not JsonArray array) return null;
            var list = new List<int>();
            foreach (var item in array)
                if (Build.Int(item) is int v) list.Add(v);
            return list;
        }

        private static bool? Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue(out bool b) ? b : null;

        private static void Write(string path, JsonObject root)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, root.ToJsonString(s_indented));
                File.Move(tmp, path, true);
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("planner:filter:write", $"Planner: {Path.GetFileName(path)} could not be written: {e.Message}", 30);
            }
        }
    }
}
