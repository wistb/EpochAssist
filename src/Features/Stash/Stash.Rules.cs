using System.Text;
using System.Text.Json.Nodes;
using EpochAssist.Core;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppItemFiltering;
using Il2CppLE.Data;

namespace EpochAssist.Features;

internal static partial class Stash
{
    /// <summary>A min-max pair of a rule; both null when the rule doesn't set it.</summary>
    internal readonly record struct Span(int? Min, int? Max)
    {
        public bool IsSet => Min != null || Max != null;
        public override string ToString() => Min == Max ? $"{Min}" : $"{Min?.ToString() ?? string.Empty}–{Max?.ToString() ?? string.Empty}";
    }

    /// <summary>
    /// A tab's or folder's sorting rules as plain values, comparable and stored in presets; made into the game's rule object
    /// only when a step is sent or the game's rule test runs. Values are the game's own: rarity and class flags, equipment
    /// type ids, unique ids, faction ids.
    /// </summary>
    internal sealed class TabRules : IEquatable<TabRules>
    {
        public int Rarity { get; init; }
        public int[] Types { get; init; } = Array.Empty<int>();
        public int[] SubTypes { get; init; } = Array.Empty<int>();
        public int Class { get; init; }
        public Span Lp { get; init; }
        public Span Ww { get; init; }
        public Span Wt { get; init; }
        public Span Fp { get; init; }
        public Span Level { get; init; }
        public int[] Factions { get; init; } = Array.Empty<int>();
        public int[] Uniques { get; init; } = Array.Empty<int>();
        /// <summary>The game's corruption choice (1 corrupted, 2 clean); null for either (the game reads "any" as no condition).</summary>
        public int? Corruption { get; init; }

        public static readonly TabRules Empty = new();

        public bool IsEmpty => Rarity == 0 && Types.Length == 0 && Class == 0 && !Lp.IsSet && !Ww.IsSet && !Wt.IsSet && !Fp.IsSet
            && !Level.IsSet && Factions.Length == 0 && Uniques.Length == 0 && Corruption == null;

        public TabRules WithUniques(IEnumerable<int> ids) => Copy(uniques: ids.Distinct().OrderBy(i => i).ToArray());

        private TabRules Copy(int[]? uniques = null) => new()
        {
            Rarity = Rarity, Types = Types, SubTypes = SubTypes, Class = Class, Lp = Lp, Ww = Ww, Wt = Wt, Fp = Fp, Level = Level,
            Factions = Factions, Uniques = uniques ?? Uniques, Corruption = Corruption,
        };

        public bool Equals(TabRules? other) =>
            other != null && Rarity == other.Rarity && SameSet(Types, other.Types) && SameSet(SubTypes, other.SubTypes) && Class == other.Class
            && Lp == other.Lp && Ww == other.Ww && Wt == other.Wt && Fp == other.Fp && Level == other.Level
            && SameSet(Factions, other.Factions) && SameSet(Uniques, other.Uniques) && Corruption == other.Corruption;

        public override bool Equals(object? obj) => Equals(obj as TabRules);

        public override int GetHashCode()
        {
            var h = new HashCode();
            h.Add(Rarity);
            h.Add(Class);
            h.Add(Lp);
            h.Add(Ww);
            h.Add(Wt);
            h.Add(Fp);
            h.Add(Level);
            h.Add(Corruption);
            h.Add(SetHash(Types));
            h.Add(SetHash(SubTypes));
            h.Add(SetHash(Factions));
            h.Add(SetHash(Uniques));
            return h.ToHashCode();
        }

        private static bool SameSet(int[] a, int[] b)
        {
            if (a.Length != b.Length) return false;
            if (a.Length == 0) return true;
            var set = new HashSet<int>(a);
            return b.All(set.Contains) && set.Count == new HashSet<int>(b).Count;
        }

        private static int SetHash(int[] a)
        {
            int h = 0;
            foreach (int v in a.Distinct()) h ^= v * 397 + 17;
            return h;
        }
    }

    internal static class Rules
    {
        public const int Normal = 1, Magic = 2, Rare = 4, Unique = 8, Set = 16, Legendary = 32, Exalted = 64;
        public const int Any = 0, Corrupted = 1, Clean = 2;

        private static readonly (int Flag, string Name)[] s_rarities =
        {
            (Normal, "Normal"), (Magic, "Magic"), (Rare, "Rare"), (Exalted, "Exalted"), (Unique, "Unique"), (Set, "Set"), (Legendary, "Legendary"),
        };

        private static readonly (int Flag, string Name)[] s_classes =
        {
            (1, "Primalist"), (2, "Mage"), (4, "Sentinel"), (8, "Acolyte"), (16, "Rogue"),
        };

        // ---- From and to the game's rule object ----

        /// <summary>The game's rule object as plain values; Empty for none.</summary>
        public static TabRules FromGame(StashPriority? p)
        {
            if (p == null || !p.HasValue()) return TabRules.Empty;
            int none = PotentialStashPriorityCondition.NetworkNull;
            int? N(int v) => v == none ? null : v;
            Span lp = default, ww = default, wt = default, fp = default, level = default;
            if (p.PotentialStashPriority is { } pot && pot.HasValue())
            {
                var m = pot.Serialize();
                lp = new Span(N(m.MinLegendaryPotential), N(m.MaxLegendaryPotential));
                ww = new Span(N(m.MinWeaversWill), N(m.MaxWeaversWill));
                wt = new Span(N(m.MinWeaversTouch), N(m.MaxWeaversTouch));
                fp = new Span(N(m.MinForgingPotential), N(m.MaxForgingPotential));
            }
            if (p.LevelStashPriority is { } lvl && lvl.HasValue())
            {
                int lnone = LevelStashPriorityCondition.NetworkNull;
                var m = lvl.Serialize();
                level = new Span(m.MinLevel == lnone ? null : m.MinLevel, m.MaxLevel == lnone ? null : m.MaxLevel);
            }
            return new TabRules
            {
                Rarity = p.RarityStashPriority is { } r && r.HasValue() ? r.Serialize().Rarity : 0,
                Types = p.TypeStashPriority is { } t && t.HasValue() ? Ints(t.SerializeTypes()) : Array.Empty<int>(),
                SubTypes = p.TypeStashPriority is { } st && st.HasValue() ? Ints(st.SerializeSubtypes()) : Array.Empty<int>(),
                Class = p.ClassStashPriority is { } c && c.HasValue() ? c.Serialize().Requirement : 0,
                Lp = lp, Ww = ww, Wt = wt, Fp = fp, Level = level,
                Factions = p.FactionStashPriority is { } f && f.HasValue() ? Ints(f.SerializeFactions()) : Array.Empty<int>(),
                Uniques = p.UniquesStashPriority is { } u && u.HasValue() ? Ints(u.SerializeUniqueIds()) : Array.Empty<int>(),
                Corruption = p.CorruptionStashPriority is { } k && k.HasValue() ? k.Serialize().Corruption : null,
            };
        }

        private static int[] Ints(Il2CppStructArray<int>? a)
        {
            if (a == null) return Array.Empty<int>();
            var result = new int[a.Length];
            for (int i = 0; i < a.Length; i++) result[i] = a[i];
            return result;
        }

        /// <summary>The game's rule object for plain rules, null for none. Every condition is made, empty ones too (the
        /// game's test reads all eight), and each is filled with its own network form as the game fills one it receives;
        /// nullable ints never cross the interop. Empty rules give null for the game's test, and an object with no
        /// conditions for a send (null there keeps the tab's old rules).</summary>
        public static StashPriority? ToGame(TabRules rules, bool forSend = false)
        {
            if (rules.IsEmpty && !forSend) return null;
            int none = PotentialStashPriorityCondition.NetworkNull;
            int W(int? v) => v ?? none;
            var potential = new PotentialStashPriorityCondition();
            potential.Deserialize(new PotentialStashPriorityMessage
            {
                MinLegendaryPotential = W(rules.Lp.Min), MaxLegendaryPotential = W(rules.Lp.Max),
                MinWeaversWill = W(rules.Ww.Min), MaxWeaversWill = W(rules.Ww.Max),
                MinWeaversTouch = W(rules.Wt.Min), MaxWeaversTouch = W(rules.Wt.Max),
                MinForgingPotential = W(rules.Fp.Min), MaxForgingPotential = W(rules.Fp.Max),
            });
            var rarity = new RarityStashPriorityCondition();
            rarity.Deserialize(new RarityStashPriorityMessage { Rarity = rules.Rarity });
            var type = new TypeStashPriorityCondition();
            type.Deserialize(new Il2CppStructArray<int>(rules.Types), new Il2CppStructArray<int>(rules.SubTypes));
            var cls = new ClassStashPriorityCondition();
            cls.Deserialize(new ClassStashPriorityMessage { Requirement = rules.Class });
            int lnone = LevelStashPriorityCondition.NetworkNull;
            var level = new LevelStashPriorityCondition();
            level.Deserialize(new LevelStashPriorityMessage { MinLevel = rules.Level.Min ?? lnone, MaxLevel = rules.Level.Max ?? lnone });
            var faction = new FactionStashPriorityCondition();
            faction.Deserialize(new Il2CppStructArray<int>(rules.Factions));
            var uniques = new UniquesStashPriorityCondition();
            uniques.Deserialize(new Il2CppStructArray<int>(rules.Uniques));
            var corruption = new CorruptionStashPriorityCondition();
            corruption.Deserialize(new CorruptionStashPriorityMessage { Corruption = rules.Corruption ?? Any });
            return new StashPriority(potential, rarity, type, cls, level, faction, uniques, corruption);
        }

        // ---- Words ----

        /// <summary>The rules in words, one part per condition ("Exalted · Helmet · not corrupted"); "No rules" for none.</summary>
        public static string Describe(TabRules rules)
        {
            if (rules.IsEmpty) return "No rules";
            var parts = new List<string>();
            if (rules.Rarity != 0) parts.Add(string.Join(", ", s_rarities.Where(r => (rules.Rarity & r.Flag) != 0).Select(r => r.Name)));
            if (rules.Types.Length > 0) parts.Add(TypesText(rules.Types));
            if (rules.Class != 0) parts.Add(string.Join(", ", s_classes.Where(c => (rules.Class & c.Flag) != 0).Select(c => c.Name)));
            if (rules.Lp.IsSet) parts.Add(SpanText("LP", rules.Lp));
            if (rules.Ww.IsSet) parts.Add(SpanText("Weaver's Will", rules.Ww));
            if (rules.Wt.IsSet) parts.Add(SpanText("Weaver's Touch", rules.Wt));
            if (rules.Fp.IsSet) parts.Add(SpanText("Forging potential", rules.Fp));
            if (rules.Level.IsSet) parts.Add(SpanText("Level", rules.Level));
            if (rules.Factions.Length > 0) parts.Add($"{rules.Factions.Length} faction{(rules.Factions.Length == 1 ? string.Empty : "s")}");
            if (rules.Uniques.Length > 0) parts.Add($"{rules.Uniques.Length} unique{(rules.Uniques.Length == 1 ? string.Empty : "s")}");
            if (rules.Corruption is { } k) parts.Add(k switch { Corrupted => "corrupted", Clean => "not corrupted", _ => "corrupted or not" });
            return string.Join(" · ", parts);
        }

        private static string SpanText(string name, Span s) =>
            s.Min != null && s.Max != null ? (s.Min == s.Max ? $"{name} {s.Min}" : $"{name} {s.Min}–{s.Max}")
            : s.Min != null ? $"{name} {s.Min}+" : $"{name} up to {s.Max}";

        private static string TypesText(int[] types)
        {
            var set = new HashSet<int>(types);
            if (Idols.All(set.Contains) && set.Count == Idols.Length) return "Idols";
            if (Weapons.All(set.Contains) && set.Count == Weapons.Length) return "Weapons";
            if (types.Length > 4) return $"{types.Length} types";
            return string.Join(", ", types.Select(TypeName));
        }

        /// <summary>An equipment type's name in words ("Body Armor", "1H Axe").</summary>
        public static string TypeName(int type)
        {
            string raw = Enum.IsDefined(typeof(EquipmentType), type) ? ((EquipmentType)type).ToString() : type.ToString();
            var sb = new StringBuilder();
            foreach (string word in raw.Replace("ONE_HANDED", "1H").Replace("TWO_HANDED", "2H").Split('_'))
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(word.Length <= 2 || word.Contains('x') ? word : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant());
            }
            return sb.ToString();
        }

        /// <summary>The unique's name from the game's list, or its id.</summary>
        public static string UniqueName(int id)
        {
            try
            {
                var unique = UniqueList.getUnique((ushort)id);
                if (unique != null) return !string.IsNullOrWhiteSpace(unique.displayName) ? unique.displayName : unique.name;
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("stash:unique", $"Stash: unique name read failed: {e.Message}", 30);
            }
            return $"Unique {id}";
        }

        // The game's idol and weapon types, as its loot filter lists them.
        public static readonly int[] Idols = { 25, 26, 27, 28, 29, 30, 31, 32, 33 };
        public static readonly int[] Weapons = { 5, 6, 7, 8, 9, 10, 12, 13, 14, 15, 16, 23 };

        // ---- JSON, in words a person can edit ----

        public static JsonObject ToJson(TabRules rules)
        {
            var o = new JsonObject();
            if (rules.Rarity != 0) o["rarity"] = Names(s_rarities, rules.Rarity);
            if (rules.Types.Length > 0) o["types"] = new JsonArray(rules.Types.Select(t => (JsonNode?)JsonValue.Create(TypeKey(t))).ToArray());
            if (rules.SubTypes.Length > 0) o["subTypes"] = new JsonArray(rules.SubTypes.Select(t => (JsonNode)t).ToArray());
            if (rules.Class != 0) o["class"] = Names(s_classes, rules.Class);
            WriteSpan(o, "lp", rules.Lp);
            WriteSpan(o, "ww", rules.Ww);
            WriteSpan(o, "wt", rules.Wt);
            WriteSpan(o, "fp", rules.Fp);
            WriteSpan(o, "level", rules.Level);
            if (rules.Factions.Length > 0) o["factions"] = new JsonArray(rules.Factions.Select(t => (JsonNode)t).ToArray());
            if (rules.Uniques.Length > 0) o["uniques"] = new JsonArray(rules.Uniques.Select(t => (JsonNode)t).ToArray());
            if (rules.Corruption is { } k) o["corruption"] = k switch { Corrupted => "corrupted", Clean => "clean", _ => "any" };
            return o;
        }

        public static TabRules FromJson(JsonNode? node)
        {
            if (node is not JsonObject o) return TabRules.Empty;
            return new TabRules
            {
                Rarity = Flags(s_rarities, o["rarity"]),
                Types = (o["types"] as JsonArray)?.Select(n => n == null ? -1 : TypeId(n)).Where(t => t >= 0).ToArray() ?? Array.Empty<int>(),
                SubTypes = IntList(o["subTypes"]),
                Class = Flags(s_classes, o["class"]),
                Lp = ReadSpan(o["lp"]), Ww = ReadSpan(o["ww"]), Wt = ReadSpan(o["wt"]), Fp = ReadSpan(o["fp"]), Level = ReadSpan(o["level"]),
                Factions = IntList(o["factions"]),
                Uniques = IntList(o["uniques"]),
                Corruption = (string?)o["corruption"] switch { "corrupted" => Corrupted, "clean" => Clean, _ => null },
            };
        }

        private static JsonArray Names((int Flag, string Name)[] table, int flags) =>
            new(table.Where(r => (flags & r.Flag) != 0).Select(r => (JsonNode?)JsonValue.Create(r.Name)).ToArray());

        private static int Flags((int Flag, string Name)[] table, JsonNode? node)
        {
            int flags = 0;
            if (node is JsonArray a)
                foreach (var n in a)
                    foreach (var (flag, name) in table)
                        if (string.Equals((string?)n, name, StringComparison.OrdinalIgnoreCase)) flags |= flag;
            return flags;
        }

        private static string TypeKey(int t) => Enum.IsDefined(typeof(EquipmentType), t) ? ((EquipmentType)t).ToString() : t.ToString();

        private static int TypeId(JsonNode n)
        {
            if (n is JsonValue v && v.TryGetValue(out int i)) return i;
            return Enum.TryParse<EquipmentType>((string?)n, true, out var e) ? (int)e : -1;
        }

        private static int[] IntList(JsonNode? node) =>
            (node as JsonArray)?.Select(n => n is JsonValue v && v.TryGetValue(out int i) ? i : int.MinValue).Where(i => i != int.MinValue).ToArray() ?? Array.Empty<int>();

        private static void WriteSpan(JsonObject o, string key, Span s)
        {
            if (!s.IsSet) return;
            o[key] = new JsonObject { ["min"] = s.Min, ["max"] = s.Max };
        }

        private static Span ReadSpan(JsonNode? node) =>
            node is JsonObject o ? new Span((int?)o["min"], (int?)o["max"]) : default;
    }
}
