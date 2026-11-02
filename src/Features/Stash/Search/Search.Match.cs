using System.Diagnostics;
using System.Globalization;
using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Features;

internal static partial class StashSearch
{
    /// <summary>One result: where the item is, its key, and its badge text (null for none).</summary>
    internal sealed record Result(ItemPlace Place, string? Badge);

    /// <summary>Results grouped as the game orders them: folders, then tabs, then items by cell.</summary>
    internal sealed record ResultTab(Stash.TabSnap Tab, List<Result> Items);
    internal sealed record ResultFolder(Stash.FolderSnap Folder, List<ResultTab> Tabs)
    {
        public int Count => Tabs.Sum(t => t.Items.Count);
    }

    private static SearchDoc? s_active;
    private static SearchDoc? s_pending;
    private static float s_pendingAt;
    private static int s_generation;
    // Per item key: whether it matches the active search and its badge.
    private static readonly Dictionary<string, (bool Match, string? Badge)> s_matches = new();
    private static readonly Queue<string> s_matchQueue = new();
    private static int s_matchedFactsGeneration = -1;
    private static List<ResultFolder>? s_grouped;
    private static (int Generation, int Facts, int Version) s_groupedFor;
    // Counts match slices that wrote results, so grouped results know when to rebuild.
    private static int s_matchVersion;
    private static float s_groupedAt;
    private static double s_matchMsTotal, s_matchMsMax;
    private static int s_matchFrames, s_matchChecks;

    private const double MatchMsPerFrame = 1.0;
    // Typing in a number field waits this long before the search runs again.
    private const float QuietSeconds = 0.15f;

    /// <summary>The search being shown; null for none.</summary>
    public static SearchDoc? Active => s_active;

    /// <summary>The active search's edit generation.</summary>
    public static int Generation => s_generation;

    /// <summary>Items are still being checked against the active search.</summary>
    public static bool Matching => s_active != null && (s_matchQueue.Count > 0 || s_pending != null || s_matchedFactsGeneration != s_factsGeneration);

    /// <summary>Matches so far (places, so two copies of one item count twice).</summary>
    public static int ResultCount => Grouped().Sum(f => f.Count);

    /// <summary>The match check's cost: checks, total and largest ms in a frame, frames.</summary>
    public static (int Checks, double TotalMs, double MaxMs, int Frames) MatchCost => (s_matchChecks, s_matchMsTotal, s_matchMsMax, s_matchFrames);

    private static void ResetMatch()
    {
        s_active = null;
        s_pending = null;
        s_matches.Clear();
        s_matchQueue.Clear();
        s_matchedFactsGeneration = -1;
        s_grouped = null;
        s_generation++;
    }

    /// <summary>Makes a search the active one (null clears it). With quiet, it waits for typing to stop first.</summary>
    public static void SetActive(SearchDoc? doc, bool quiet = false)
    {
        if (quiet && doc != null)
        {
            s_pending = doc;
            s_pendingAt = Time.unscaledTime + QuietSeconds;
            return;
        }
        s_pending = null;
        s_active = doc;
        s_generation++;
        s_matches.Clear();
        s_matchQueue.Clear();
        s_grouped = null;
        s_matchedFactsGeneration = -1;
        s_matchMsTotal = s_matchMsMax = 0;
        s_matchFrames = s_matchChecks = 0;
    }

    // Checks cached items against the active search for a slice of the frame; new or changed items only after the first run.
    private static void MatchSlice()
    {
        if (s_pending != null && Time.unscaledTime >= s_pendingAt) SetActive(s_pending);
        var doc = s_active;
        if (doc == null) return;
        if (s_matchedFactsGeneration != s_factsGeneration)
        {
            if (s_matchedFactsGeneration < 0)
            {
                foreach (var key in s_facts.Keys) s_matchQueue.Enqueue(key);
            }
            else
            {
                foreach (var key in s_changedKeys)
                {
                    if (s_facts.ContainsKey(key)) s_matchQueue.Enqueue(key);
                    else s_matches.Remove(key);
                }
            }
            s_changedKeys.Clear();
            s_matchedFactsGeneration = s_factsGeneration;
        }
        if (s_matchQueue.Count == 0) return;
        s_matchVersion++;
        long start = Stopwatch.GetTimestamp();
        long until = start + (long)(MatchMsPerFrame * Stopwatch.Frequency / 1000d);
        while (s_matchQueue.Count > 0)
        {
            string key = s_matchQueue.Dequeue();
            if (!s_facts.TryGetValue(key, out var facts)) continue;
            var (match, badge) = Match(doc, facts);
            s_matches[key] = (match, badge);
            s_matchChecks++;
            if (Stopwatch.GetTimestamp() > until) break;
        }
        double ms = (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
        s_matchMsTotal += ms;
        s_matchMsMax = Math.Max(s_matchMsMax, ms);
        s_matchFrames++;
    }

    /// <summary>The results, grouped by folder then tab in the game's order; rebuilt only when matches or places change.</summary>
    public static IReadOnlyList<ResultFolder> Grouped()
    {
        var snap = s_snap;
        if (s_active == null || snap == null || s_active.IsEmpty) return Array.Empty<ResultFolder>();
        var stamp = (s_generation, s_factsGeneration, s_matchVersion);
        // While matching, partial results regroup at most four times a second.
        if (s_grouped != null && (s_groupedFor == stamp || Matching && s_groupedFor.Generation == s_generation && Time.unscaledTime < s_groupedAt)) return s_grouped;
        s_groupedAt = Time.unscaledTime + 0.25f;
        var folders = GroupMatches(s_matches, snap);
        s_grouped = folders;
        s_groupedFor = stamp;
        return folders;
    }

    /// <summary>Matching places grouped by folder, then tab in the game's order, then cell.</summary>
    private static List<ResultFolder> GroupMatches(Dictionary<string, (bool Match, string? Badge)> matches, Stash.StashSnapshot snap)
    {
        var byTab = new Dictionary<int, List<Result>>();
        foreach (var place in s_places)
        {
            if (!matches.TryGetValue(place.Key, out var m) || !m.Match) continue;
            if (!byTab.TryGetValue(place.TabId, out var list)) byTab[place.TabId] = list = new List<Result>();
            list.Add(new Result(place, m.Badge));
        }
        var folders = new List<ResultFolder>();
        foreach (var folder in snap.Folders)
        {
            var tabs = new List<ResultTab>();
            foreach (var tab in snap.TabsIn(folder.Id))
                if (byTab.TryGetValue(tab.Id, out var items))
                    tabs.Add(new ResultTab(tab, items.OrderBy(r => r.Place.Y).ThenBy(r => r.Place.X).ToList()));
            if (tabs.Count > 0) folders.Add(new ResultFolder(folder, tabs));
        }
        return folders;
    }

    // ---- the check (pure: search and facts in, answer out) ----

    /// <summary>Whether an item matches a search, and its badge.</summary>
    internal static (bool Match, string? Badge) Match(SearchDoc doc, ItemFacts item)
    {
        if (doc.IsEmpty || FilterFails(doc.Filters, item) != null) return (false, null);
        foreach (var g in doc.Groups)
            if (g.Active && !GroupPasses(g, item, out _)) return (false, null);
        return (true, Badge(doc, item));
    }

    /// <summary>The first filter the item fails, as text; null when it passes them all.</summary>
    internal static string? FilterFails(ItemFilters f, ItemFacts item)
    {
        if (f.Types.Count > 0 && !f.Types.Any(t => t.Type == item.Type && (t.Sub == null || t.Sub == item.Sub)))
            return $"type {item.Type}/{item.Sub} not in [{string.Join(", ", f.Types.Select(t => t.Sub is int s ? $"{t.Type}/{s}" : $"{t.Type}"))}]";
        if (f.Rarities.Count > 0 && !f.Rarities.Contains(item.Rarity)) return $"rarity {item.Rarity} not in [{string.Join(", ", f.Rarities)}]";
        if (f.Uniques.Count > 0 && !f.Uniques.Contains(item.UniqueId)) return $"unique {item.UniqueId} not chosen";
        if (!f.Lp.Holds(item.Lp)) return $"LP {item.Lp} [{f.Lp}]";
        if (!f.Ww.Holds(item.Ww)) return $"WW {item.Ww} [{f.Ww}]";
        if (!f.Wt.Holds(item.Wt)) return $"WT {item.Wt} [{f.Wt}]";
        if (!f.Fp.Holds(item.Fp)) return $"FP {item.Fp} [{f.Fp}]";
        if (!f.LevelReq.Holds(item.LevelReq)) return $"level requirement {item.LevelReq} [{f.LevelReq}]";
        // The game's count, as its own prefixes/suffixes words take it (every item, sets and uniques included); an item
        // whose count couldn't be read (-1) fails.
        if (!f.Prefixes.IsEmpty && (item.Prefixes < 0 || !f.Prefixes.Holds(item.Prefixes))) return $"prefixes {item.Prefixes} [{f.Prefixes}]";
        if (!f.Suffixes.IsEmpty && (item.Suffixes < 0 || !f.Suffixes.Holds(item.Suffixes))) return $"suffixes {item.Suffixes} [{f.Suffixes}]";
        if (f.Corrupted != Tri.Any && item.Corrupted != (f.Corrupted == Tri.Yes)) return $"corrupted {item.Corrupted}";
        if (f.Sealed != Tri.Any && item.Sealed != (f.Sealed == Tri.Yes)) return $"sealed {item.Sealed}";
        // Class requirements are flags (Primalist 1, Mage 2, Sentinel 4, Acolyte 8, Rogue 16): an item for several
        // classes matches each of them.
        if (f.Class is int cls && (item.Class & cls) == 0) return $"class {item.Class}";
        return null;
    }

    /// <summary>Whether a group passes; why holds its rows' outcomes for the dev verb.</summary>
    internal static bool GroupPasses(StatGroup g, ItemFacts item, out List<(StatRow Row, bool Present, StatFact? Hit)> rows)
    {
        rows = new List<(StatRow, bool, StatFact?)>();
        foreach (var row in g.Rows)
        {
            if (!row.On || row.Unreadable != null) continue;
            var hit = RowHit(row, item, out bool present);
            rows.Add((row, present, hit));
        }
        int matched = rows.Count(r => r.Hit != null);
        return g.Type switch
        {
            GroupType.And => matched == rows.Count,
            GroupType.Not => matched == 0,
            GroupType.If => rows.All(r => !r.Present || r.Hit != null),
            GroupType.Count => g.Bound.Holds(matched),
            _ => g.Bound.Holds(rows.Where(r => r.Hit != null).Sum(r => r.Row.Weight * SumValue(r.Row, r.Hit!))),
        };
    }

    /// <summary>The stat line that satisfies a row, or null; present says whether the item has the stat at all (with
    /// the row's position and kind, bounds aside), for If groups.</summary>
    internal static StatFact? RowHit(StatRow row, ItemFacts item, out bool present)
    {
        present = false;
        foreach (var stat in Candidates(row, item))
        {
            present = true;
            if (Holds(row, stat)) return stat;
        }
        return null;
    }

    private static IEnumerable<StatFact> Candidates(StatRow row, ItemFacts item)
    {
        var src = row.Stat.Source;
        if (src == StatSource.Pseudo)
        {
            if (item.Pseudo.TryGetValue(row.Stat.Id, out double total))
                yield return new StatFact(row.Stat, PseudoName(row.Stat.Id), false, false, false, RowKind.Any, null, new[] { new StatValue(0, 0, 0, 0, Math.Round(total, 4), total, total) });
            yield break;
        }
        foreach (var stat in item.Stats)
        {
            bool affix = stat.Ref.Source == StatSource.Affix;
            bool wanted = src switch
            {
                // An affix's stat on an implicit or a unique roll (a unique's "+4 to All Attributes") counts too, unless
                // the row asks for what only affixes have: a tier, a position or a kind.
                StatSource.Affix => affix ? stat.Ref.Id == row.Stat.Id : AffixOnly(row) == false && SameStat(row.Stat.Id, stat),
                StatSource.AnyPrefix => affix && stat.Prefix,
                StatSource.AnySuffix => affix && stat.Suffix,
                _ => stat.Ref.Source == src && stat.Ref.Id == row.Stat.Id,
            };
            if (!wanted) continue;
            if (affix)
            {
                if (row.Position == RowPosition.Prefix && !stat.Prefix || row.Position == RowPosition.Suffix && !stat.Suffix || row.Position == RowPosition.Sealed && !stat.Sealed) continue;
                if (row.Kind != RowKind.Any && stat.Kind != row.Kind) continue;
            }
            yield return stat;
        }
    }

    /// <summary>
    /// For the dev verb: every affix stat filter checked against the stash. An item "has" an affix's stat when one of its
    /// lines carries the same numbers (property, type, tags, special), on an affix, an implicit or a unique roll; the
    /// filter must find each such item. Also lists the game's affix kinds against the filter's kinds.
    /// </summary>
    internal static List<string> Audit()
    {
        var lines = new List<string>();
        var kinds = new Dictionary<string, int>();
        var samples = new List<string>();
        foreach (var a in Il2Cpp.AffixList.get().AllAffixes)
        {
            if (a == null) continue;
            string key = $"{a.specialAffixType}/{a.type}/rollsOn {a.rollsOn}";
            kinds[key] = kinds.TryGetValue(key, out int n) ? n + 1 : 1;
            if (a.specialAffixType is Il2Cpp.AffixList.SpecialAffixType.Personal or Il2Cpp.AffixList.SpecialAffixType.FakeUniqueMod && samples.Count < 16)
                samples.Add($"  {a.specialAffixType} {a.affixId} \"{a.getAffixDisplayName()}\" on [{(a.canRollOn == null ? string.Empty : string.Join(",", a.canRollOn.ToArray()))}]");
        }
        lines.Add("affix kinds in the game (special/type/rolls on: count):");
        foreach (var k in kinds.OrderByDescending(k => k.Value)) lines.Add($"  {k.Key}: {k.Value}");
        lines.Add("Personal and FakeUniqueMod samples:");
        lines.AddRange(samples);
        var items = s_facts.Values.ToList();
        int checkedStats = 0, misses = 0, extra = 0;
        foreach (var entry in Catalogue().Where(e => e.Ref.Source == StatSource.Affix))
        {
            var row = new StatRow { Stat = entry.Ref };
            foreach (var item in items)
            {
                bool has = item.Stats.Any(st => st.Ref.Source == StatSource.Affix ? st.Ref.Id == entry.Ref.Id : SameStat(entry.Ref.Id, st));
                bool hit = RowHit(row, item, out _) != null;
                if (has == hit) continue;
                if (has) misses++; else extra++;
                if (misses + extra <= 40) lines.Add($"  {(has ? "MISSED" : "EXTRA")} \"{entry.Text}\" on {item.Name}");
            }
            checkedStats++;
        }
        // Lines on implicits and unique rolls that no affix shares: only their own picker entry finds them.
        var lone = new Dictionary<string, int>();
        var loneFacts = new Dictionary<string, StatFact>();
        var affixIds = Catalogue().Where(e => e.Ref.Source == StatSource.Affix).Select(e => e.Ref.Id).ToList();
        foreach (var item in items)
            foreach (var st in item.Stats.Where(st => st.Ref.Source is StatSource.Implicit or StatSource.UniqueRoll))
                if (!affixIds.Any(id => SameStat(id, st)))
                {
                    lone[st.Text] = lone.TryGetValue(st.Text, out int c) ? c + 1 : 1;
                    loneFacts.TryAdd(st.Text, st);
                }
        lines.Insert(0, $"audit: {checkedStats} affix stats × {items.Count} items: {misses} missed, {extra} extra; {lone.Count} implicit/unique lines no affix shares");
        lines.Add("implicit/unique lines no affix shares (found only by their own entry or a total):");
        foreach (var l in lone.OrderByDescending(l => l.Value))
        {
            var st = loneFacts[l.Key];
            string Sig((int P, int M, int T, byte S) x) => $"p{x.P} m{x.M} t{x.T} s{x.S}";
            var mine = st.Numbers.Select(n => (n.Property, n.ModType, n.Tags, n.SpecialTag)).ToArray();
            lines.Add($"  {l.Key} ×{l.Value}  [{string.Join(" + ", mine.Select(Sig))}]");
            // Near misses: a one-number affix differing from a one-number line in a single part (type, tags or special).
            if (mine.Length == 1)
                foreach (var id in affixIds)
                {
                    if (!s_affixStats.TryGetValue(id, out var sign)) { SameStat(id, st); s_affixStats.TryGetValue(id, out sign); }
                    if (sign is not { Length: 1 } || sign[0].Item1 != mine[0].Item1) continue;
                    int diff = (sign[0].Item2 != mine[0].Item2 ? 1 : 0) + (sign[0].Item3 != mine[0].Item3 ? 1 : 0) + (sign[0].Item4 != mine[0].Item4 ? 1 : 0);
                    if (diff == 1) lines.Add($"      NEAR affix {id} \"{EntryOf(new StatRef(StatSource.Affix, id))?.Text}\" [{Sig(sign[0])}]");
                }

        }
        return lines;
    }

    private static bool AffixOnly(StatRow row) =>
        !row.Tier.IsEmpty && !row.Tier.Invalid || row.Position != RowPosition.Any || row.Kind != RowKind.Any;

    // An affix's numbers by what they are (property, modifier type, tags, special tag), read once per affix.
    private static readonly Dictionary<string, (int, int, int, byte)[]?> s_affixStats = new();

    private static bool SameStat(string affixId, StatFact stat)
    {
        if (stat.Ref.Source is not (StatSource.Implicit or StatSource.UniqueRoll) || stat.Numbers.Length == 0) return false;
        if (!s_affixStats.TryGetValue(affixId, out var sign))
        {
            sign = null;
            try
            {
                if (int.TryParse(affixId, out int id) && GameData.AffixValues(id, 0, 255, 0f) is { Length: > 0 } values)
                    sign = values.Select(v => (v.Property, v.ModType, v.Tags, v.SpecialTag)).ToArray();
            }
            catch (Exception)
            {
            }
            s_affixStats[affixId] = sign;
        }
        if (sign == null || sign.Length != stat.Numbers.Length) return false;
        for (int i = 0; i < sign.Length; i++)
        {
            var n = stat.Numbers[i];
            if (sign[i] != (n.Property, n.ModType, n.Tags, n.SpecialTag)) return false;
        }
        return true;
    }

    private static bool Holds(StatRow row, StatFact stat)
    {
        if (!row.Tier.IsEmpty && !row.Tier.Invalid && (stat.Tier is not int tier || !row.Tier.Holds(tier))) return false;
        for (int i = 0; i < row.Values.Count; i++)
        {
            var range = row.Values[i];
            if (range.IsEmpty || range.Invalid) continue;
            if (i >= stat.Numbers.Length || !range.Holds(stat.Numbers[i].Value)) return false;
        }
        if (row.QualityMin is double q && QualityOf(row, stat) * 100d < q - 1e-6) return false;
        return true;
    }

    // The lowest quality over the numbers the row covers: those with a value bound, or all when none has one.
    private static double QualityOf(StatRow row, StatFact stat)
    {
        if (stat.Numbers.Length == 0) return 0d;
        var covered = Enumerable.Range(0, stat.Numbers.Length).Where(i => !row.ValueAt(i).IsEmpty).ToList();
        if (covered.Count == 0) covered = Enumerable.Range(0, stat.Numbers.Length).ToList();
        return covered.Min(i => stat.Numbers[i].Quality);
    }

    // The number a row adds to a weighted sum: its first bounded number, else its first.
    private static double SumValue(StatRow row, StatFact stat)
    {
        if (stat.Numbers.Length == 0) return 0d;
        for (int i = 0; i < stat.Numbers.Length; i++)
            if (!row.ValueAt(i).IsEmpty) return stat.Numbers[i].Value;
        return stat.Numbers[0].Value;
    }

    // From the first enabled group that isn't a Not: Count "m/n", Weighted sum the sum, And/If the first matched row's
    // quality if it bounds quality, else its tier if it bounds tier, else its first value.
    private static string? Badge(SearchDoc doc, ItemFacts item)
    {
        var g = doc.Groups.FirstOrDefault(x => x.Active && x.Type != GroupType.Not);
        if (g == null) return null;
        GroupPasses(g, item, out var rows);
        switch (g.Type)
        {
            case GroupType.Count:
                return $"{rows.Count(r => r.Hit != null)}/{rows.Count}";
            case GroupType.WeightedSum:
                return Short(rows.Where(r => r.Hit != null).Sum(r => r.Row.Weight * SumValue(r.Row, r.Hit!)));
            default:
                var first = rows.FirstOrDefault(r => r.Hit != null);
                if (first.Hit == null) return null;
                if (first.Row.QualityMin != null) return $"{Math.Floor(QualityOf(first.Row, first.Hit) * 100d + 1e-6):0}%";
                if (!first.Row.Tier.IsEmpty && first.Hit.Tier is int t) return $"T{t}";
                return first.Hit.Numbers.Length > 0 ? Short(SumValue(first.Row, first.Hit)) : null;
        }
    }

    internal static string Short(double v) => Math.Abs(v) >= 1000 ? (v / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + "k" : v.ToString("0.##", CultureInfo.InvariantCulture);

    internal static string PseudoName(string key) => PseudoStats.FirstOrDefault(p => p.Key == key)?.Name ?? key;

    /// <summary>For the dev verb: each filter and group with the rows' outcomes and the numbers compared.</summary>
    internal static List<string> Why(SearchDoc doc, ItemFacts item)
    {
        var lines = new List<string>();
        if (doc.IsEmpty)
        {
            lines.Add("empty search: lists nothing");
            return lines;
        }
        lines.Add(FilterFails(doc.Filters, item) is { } fail ? "filters FAIL: " + fail : "filters PASS");
        for (int gi = 0; gi < doc.Groups.Count; gi++)
        {
            var g = doc.Groups[gi];
            if (!g.Active)
            {
                lines.Add($"group {gi + 1} {GroupWord(g.Type)} skipped ({(g.On ? g.Unreadable ?? (g.HasBound && g.Bound.Invalid ? "min above max" : "no enabled rows") : "off")})");
                continue;
            }
            bool pass = GroupPasses(g, item, out var rows);
            string bound = g.HasBound ? $" [{g.Bound}]" : string.Empty;
            string sum = g.Type == GroupType.Count ? $" {rows.Count(r => r.Hit != null)} matched"
                : g.Type == GroupType.WeightedSum ? $" sum {Short(rows.Where(r => r.Hit != null).Sum(r => r.Row.Weight * SumValue(r.Row, r.Hit!)))}" : string.Empty;
            lines.Add($"group {gi + 1} {GroupWord(g.Type)}{bound}{sum} {(pass ? "PASS" : "FAIL")}");
            int ri = 0;
            foreach (var (row, present, hit) in rows)
            {
                ri++;
                var shown = hit ?? Candidates(row, item).FirstOrDefault();
                string numbers = shown == null ? "absent" : string.Join(" ", shown.Numbers.Select((n, i) =>
                    $"{Short(n.Value)}{(row.ValueAt(i).IsEmpty ? string.Empty : $" [{row.ValueAt(i)}]")}"));
                string tier = shown?.Tier is int t ? $" T{t}{(row.Tier.IsEmpty ? string.Empty : $" [{row.Tier}]")}" : string.Empty;
                string quality = shown != null && shown.Numbers.Length > 0 ? $" q {QualityOf(row, shown) * 100d:0}%{(row.QualityMin is double q ? $" [min {q:0}]" : string.Empty)}" : string.Empty;
                lines.Add($"  row {ri} {RowName(row)}{tier} {numbers}{quality} {(hit != null ? "PASS" : present ? "FAIL" : "ABSENT")}");
            }
        }
        lines.Add(Match(doc, item).Match ? $"MATCH badge {Badge(doc, item) ?? "none"}" : "NO MATCH");
        return lines;
    }

    /// <summary>A row's stat as text: the affix name, the line's text, or the total's name.</summary>
    internal static string RowName(StatRow row) => row.Stat.Source switch
    {
        StatSource.Affix => GameData.Affix(row.Stat.AffixId).Name ?? $"affix {row.Stat.Id}",
        StatSource.AnyPrefix => "Any prefix",
        StatSource.AnySuffix => "Any suffix",
        StatSource.Pseudo => PseudoName(row.Stat.Id),
        _ => s_facts.Values.SelectMany(f => f.Stats).FirstOrDefault(s => s.Ref == row.Stat)?.Text ?? row.Stat.Id,
    };
}
