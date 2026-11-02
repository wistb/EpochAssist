using System.Globalization;
using System.Text.RegularExpressions;
using Il2Cpp;

namespace EpochAssist.Features;

// The Search page's search written as a ~ search for the stash's own search box: filters as the game's words, groups
// with &, |, ! and counts, rows as stats by name with their numbers. What the box can't say is listed, and each stat
// is read back as the box would read it, so the text shown never claims more than it finds.
internal static partial class StashSearch
{
    /// <summary>A search as ~ text, and what it leaves out (empty when it says all of it).</summary>
    internal static (string Text, List<string> Left) BoxTextOf(SearchDoc doc)
    {
        var left = new List<string>();
        var parts = new List<string>();
        parts.AddRange(FilterWords(doc.Filters, left));
        foreach (var g in doc.Groups)
            if (g.Active && GroupText(g, left) is { Length: > 0 } text) parts.Add(text);
        return (parts.Count == 0 ? string.Empty : BoxPrefix + string.Join(" & ", parts), left.Distinct().ToList());
    }

    // The game's word for each base type, by its id.
    private static readonly Dictionary<int, string> s_typeWords = new()
    {
        [0] = "helmet", [1] = "body", [2] = "belt", [3] = "boots", [4] = "gloves", [5] = "1haxe", [6] = "dagger", [7] = "1hmace",
        [8] = "sceptre", [9] = "1hsword", [10] = "wand", [12] = "2haxe", [13] = "2hmace", [14] = "spear", [15] = "staff",
        [16] = "2hsword", [17] = "quiver", [18] = "shield", [19] = "catalyst", [20] = "amulet", [21] = "ring", [22] = "relic",
        [23] = "bow", [25] = "small", [26] = "minor", [27] = "humble", [28] = "stout", [29] = "grand", [30] = "large",
        [31] = "ornate", [32] = "huge", [33] = "adorned", [41] = "altar",
    };

    private static readonly (int Flag, string Word)[] s_classWords =
        { (1, "primalist"), (2, "mage"), (4, "sentinel"), (8, "acolyte"), (16, "rogue") };

    private static IEnumerable<string> FilterWords(ItemFilters f, List<string> left)
    {
        if (f.Types.Count > 0)
        {
            var any = new List<string>();
            foreach (var (type, sub) in f.Types)
            {
                if (!s_typeWords.TryGetValue(type, out var word)) { left.Add("a base type the box has no word for"); continue; }
                // A subtype is its name in the tooltip.
                any.Add(sub is int s && SubName(type, s) is { } name ? $"{word} & \"{name.ToLowerInvariant()}\"" : word);
            }
            if (any.Count > 0) yield return Either(any);
        }
        if (f.Rarities.Count > 0) yield return Either(RarityWords.Where(f.Rarities.Contains).ToList());
        if (f.Uniques.Count > 0) yield return Either(f.Uniques.Select(id => $"\"{UniqueName(id).ToLowerInvariant()}\"").ToList());
        foreach (var (word, range) in new[] { ("lp", f.Lp), ("ww", f.Ww), ("wt", f.Wt), ("fp", f.Fp), ("lvl", f.LevelReq), ("prefixes", f.Prefixes), ("suffixes", f.Suffixes) })
            if (MacroRange(word, range) is { } text) yield return text;
        if (f.Corrupted != Tri.Any) yield return f.Corrupted == Tri.Yes ? "corrupted" : "!corrupted";
        if (f.Sealed != Tri.Any) yield return f.Sealed == Tri.Yes ? "sealed" : "!sealed";
        if (f.Class is int cls && s_classWords.FirstOrDefault(c => c.Flag == cls).Word is { } classWord) yield return classWord;
    }

    private static string Either(List<string> words) => words.Count == 1 ? words[0] : "(" + string.Join(" | ", words) + ")";

    // lp2+, lp4-, lp3, or both ends.
    private static string? MacroRange(string word, Range r)
    {
        if (r.IsEmpty || r.Invalid) return null;
        string N(double v) => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
        if (r.Min is { } lo && r.Max is { } hi) return lo == hi ? word + N(lo) : $"{word}{N(lo)}+ & {word}{N(hi)}-";
        return r.Min is { } min ? $"{word}{N(min)}+" : $"{word}{N(r.Max!.Value)}-";
    }

    private static string? SubName(int type, int sub)
    {
        try
        {
            var bases = ItemList.get()?.EquippableItems;
            for (int i = 0; bases != null && i < bases.Length; i++)
            {
                if (bases[i]?.baseTypeID != type || bases[i].subItems == null) continue;
                foreach (var s in bases[i].subItems)
                    if (s != null && s.subTypeID == sub) return string.IsNullOrWhiteSpace(s.displayName) ? s.name : s.displayName;
            }
        }
        catch (Exception)
        {
        }
        return null;
    }

    private static string GroupText(StatGroup g, List<string> left)
    {
        var rows = g.Rows.Where(r => r.On && r.Unreadable == null).ToList();
        if (rows.Count == 0) return string.Empty;
        switch (g.Type)
        {
            case GroupType.And:
                return string.Join(" & ", rows.Select(r => RowText(r, left, full: true)));
            case GroupType.Not:
                return "!" + Either(rows.Select(r => RowText(r, left, full: true)).ToList());
            case GroupType.If:
                // A stat the item has must match; a missing one passes.
                return string.Join(" & ", rows.Select(r => $"(!{RowText(r, left, full: false)} | {RowText(r, left, full: true)})"));
            case GroupType.Count:
            {
                string list = string.Join(", ", rows.Select(r => RowText(r, left, full: true)));
                string N(double v) => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
                if (g.Bound.IsEmpty) return $"0of({list})";
                if (g.Bound.Min is { } lo && g.Bound.Max is { } hi) return $"{N(lo)}of({list}) & !{N(hi + 1)}of({list})";
                return g.Bound.Min is { } min ? $"{N(min)}of({list})" : $"{N(g.Bound.Max!.Value)}-of({list})";
            }
            default:
                left.Add("weighted sums");
                return string.Empty;
        }
    }

    private static readonly Regex s_unsafe = new(@"[&|!(),""/~#+%\d]", RegexOptions.Compiled);
    private static readonly Regex s_spaces = new(@"\s+", RegexOptions.Compiled);

    // A row as a stat atom: its name the way the box ranks names, then its numbers; read back to check it names the same stat.
    private static string RowText(StatRow row, List<string> left, bool full)
    {
        var entry = EntryOf(row.Stat);
        if (entry == null)
        {
            left.Add("a stat no longer in the game");
            return "\"?\"";
        }
        string raw = entry.Text;
        bool? percent = raw.Contains("(#%)") ? true : raw.Contains("(+#)") ? false : null;
        string name = s_spaces.Replace(s_unsafe.Replace(s_nameMark.Replace(raw, string.Empty), " "), " ").Trim().ToLowerInvariant();
        var words = new List<string>();
        if (row.Stat.Source == StatSource.Implicit) words.Add("implicit");
        if (row.Stat.Source == StatSource.Pseudo && !name.Contains("total")) words.Add("total");
        if (full && row.Position != RowPosition.Any) words.Add(row.Position.ToString().ToLowerInvariant());
        words.Add(name);
        if (row.Stat.Source == StatSource.UniqueRoll) words.Add("roll");
        var value = row.Values.FirstOrDefault();
        if (full)
        {
            if (row.Values.Skip(1).Any(v => !v.IsEmpty)) left.Add("a second number on a two-number stat");
            if (!value.IsEmpty && !value.Invalid) words.Add(ValueWord(value, percent == true));
            else if (percent is { } pct) words.Add(pct ? "%" : "flat");
            if (!row.Tier.IsEmpty && !row.Tier.Invalid)
            {
                words.Add(TierWord(row.Tier));
                if (row.Tier.Min != null && row.Tier.Max != null && row.Tier.Min != row.Tier.Max) left.Add("a tier range with both ends");
            }
            if (row.QualityMin is { } q) words.Add("q" + ((int)Math.Round(q)).ToString(CultureInfo.InvariantCulture));
            if (row.Kind != RowKind.Any) left.Add("affix kinds (corrupted, champion, …)");
        }
        else if (percent is { } pct) words.Add(pct ? "%" : "flat");
        string text = string.Join(" ", words);
        // Read back as the box reads it: the same stat must be among what it names.
        if (StatAtom(text) is not { } node || !node.Rows.Any(r => r.Stat == row.Stat)) left.Add($"\"{entry.Text}\" reads as another stat");
        return text;
    }

    private static string ValueWord(Range v, bool percent)
    {
        string N(double d) => d.ToString("0.##", CultureInfo.InvariantCulture) + (percent ? "%" : string.Empty);
        if (v.Min is { } lo && v.Max is { } hi) return lo == hi ? N(lo) : $"{N(lo)}-{N(hi)}";
        return v.Min is { } min ? N(min) + "+" : N(v.Max!.Value) + "-";
    }

    private static string TierWord(Range t)
    {
        string N(double d) => ((int)Math.Round(d)).ToString(CultureInfo.InvariantCulture);
        if (t.Min is { } lo && t.Max is { } hi) return lo == hi ? "t" + N(lo) : $"t{N(lo)}+";
        return t.Min is { } min ? $"t{N(min)}+" : $"t{N(t.Max!.Value)}-";
    }
}
