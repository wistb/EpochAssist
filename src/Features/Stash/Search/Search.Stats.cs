using EpochAssist.Core;
using Il2Cpp;

namespace EpochAssist.Features;

internal static partial class StashSearch
{
    /// <summary>One stat the picker offers: what a row would look for, its text, its heading, and its numbers.</summary>
    internal sealed record CatalogueEntry(StatRef Ref, string Text, string Heading, (string Label, string Unit)[] Numbers)
    {
        /// <summary>Words the picker also matches but doesn't show: each number's form ("added", "increased").</summary>
        public string Keywords { get; init; } = string.Empty;
        public bool IsAffix => Ref.Source is StatSource.Affix or StatSource.AnyPrefix or StatSource.AnySuffix;
    }

    public const string AffixHeading = "Affixes", ImplicitHeading = "Implicits", UniqueHeading = "Unique rolls", TotalHeading = "Totals";

    private static List<CatalogueEntry>? s_affixCatalogue;
    private static (int Facts, List<CatalogueEntry> Entries)? s_catalogue;

    /// <summary>
    /// The picker's entries under their headings: totals and any prefix/suffix, every affix of the game's list, and the
    /// implicit and unique roll lines found on the stash's items (what a search can find). Affixes are read once per
    /// session; lines follow the stash.
    /// </summary>
    public static IReadOnlyList<CatalogueEntry> Catalogue()
    {
        if (s_catalogue is { } c && c.Facts == s_factsGeneration) return c.Entries;
        var entries = new List<CatalogueEntry>
        {
            new(new StatRef(StatSource.AnyPrefix, string.Empty), "Any prefix", AffixHeading, Array.Empty<(string, string)>()),
            new(new StatRef(StatSource.AnySuffix, string.Empty), "Any suffix", AffixHeading, Array.Empty<(string, string)>()),
        };
        entries.AddRange(AffixCatalogue());
        var implicits = new Dictionary<string, CatalogueEntry>();
        var uniques = new Dictionary<string, CatalogueEntry>();
        foreach (var item in s_facts.Values)
            foreach (var stat in item.Stats)
            {
                string unit = stat.Text.Contains('%') ? "%" : string.Empty;
                if (stat.Ref.Source == StatSource.Implicit && !implicits.ContainsKey(stat.Ref.Id))
                    implicits[stat.Ref.Id] = new CatalogueEntry(stat.Ref, stat.Text.Replace("#", "+").Replace("++", "+"), ImplicitHeading, new[] { (stat.Text, unit) });
                else if (stat.Ref.Source == StatSource.UniqueRoll && !uniques.ContainsKey(stat.Ref.Id))
                    uniques[stat.Ref.Id] = new CatalogueEntry(stat.Ref, $"{UniqueName(item.UniqueId)} — {stat.Text}", UniqueHeading, new[] { (stat.Text, unit) });
            }
        entries.AddRange(implicits.Values.OrderBy(e => e.Text, StringComparer.OrdinalIgnoreCase));
        entries.AddRange(uniques.Values.OrderBy(e => e.Text, StringComparer.OrdinalIgnoreCase));
        foreach (var p in PseudoStats)
            entries.Add(new CatalogueEntry(new StatRef(StatSource.Pseudo, p.Key), p.Name, TotalHeading,
                new[] { (p.Name, p.Key.StartsWith("res.") || p.Key == "movespeed" ? "%" : string.Empty) }));
        s_catalogue = (s_factsGeneration, entries);
        return entries;
    }

    /// <summary>The headings as the dropdown takes them: each heading's first index.</summary>
    public static (string Label, int First)[] CatalogueGroups(IReadOnlyList<CatalogueEntry> entries)
    {
        var groups = new List<(string, int)>();
        for (int i = 0; i < entries.Count; i++)
            if (i == 0 || entries[i].Heading != entries[i - 1].Heading) groups.Add((entries[i].Heading, i));
        return groups.ToArray();
    }

    /// <summary>The catalogue entry for a row's stat, or null when the stat isn't listed (a line no item carries now).</summary>
    public static CatalogueEntry? EntryOf(StatRef stat) => Catalogue().FirstOrDefault(e => e.Ref == stat);

    // Every affix the game lists, by its shown name; idol affixes and class-only ones say so, since names repeat.
    private static List<CatalogueEntry> AffixCatalogue()
    {
        if (s_affixCatalogue != null) return s_affixCatalogue;
        var list = new List<CatalogueEntry>();
        try
        {
            var all = AffixList.get()?.AllAffixes;
            for (int i = 0; all != null && i < all.Count; i++)
            {
                var a = all[i];
                if (a == null) continue;
                string name = GameData.Affix(a.affixId).Name ?? a.affixName;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var notes = new List<string>();
                if (a.rollsOn == AffixList.RollsOn.Idols) notes.Add("idol");
                if (a.specialAffixType is AffixList.SpecialAffixType.Corrupted) notes.Add("corrupted");
                else if (a.specialAffixType is AffixList.SpecialAffixType.Experimental) notes.Add("experimental");
                else if (a.specialAffixType is AffixList.SpecialAffixType.IdolEnchantment) notes.Add("enchantment");
                else if (a.specialAffixType is AffixList.SpecialAffixType.IdolWeaver) notes.Add("weaver");
                else if (a.specialAffixType is AffixList.SpecialAffixType.Set) notes.Add("set");
                string text = notes.Count > 0 ? $"{name} ({string.Join(", ", notes)})" : name;
                var numbers = GameData.AffixNumbers(a.affixId);
                list.Add(new CatalogueEntry(new StatRef(StatSource.Affix, a.affixId.ToString()), text, AffixHeading, numbers.Select(n => (n.Label, n.Unit)).ToArray())
                {
                    // Flat health and mana are the maximum: "max life" finds them.
                    Keywords = string.Join(" ", numbers.Select(n => n.Form).Distinct())
                        + (numbers.Length > 0 && numbers[0].Form == "added" && name is "Health" or "Mana" ? " maximum" : string.Empty),
                });
            }
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("search:catalogue", $"Stash search: the affix list couldn't be read: {e.Message}", 30);
        }
        // Names repeat (a flat and a percent "Health"): the numbers' forms tell them apart, else the id.
        Rename(list, e => string.Join(", ", e.Numbers.Select(n => n.Unit == "%" ? "#%" : "+#")) is { Length: > 0 } form ? $"{e.Text} ({form})" : e.Text);
        Rename(list, e => $"{e.Text} #{e.Ref.Id}");
        list.Sort((x, y) => string.Compare(x.Text, y.Text, StringComparison.OrdinalIgnoreCase));
        if (list.Count > 0) s_affixCatalogue = list;
        return list;
    }

    // Gives entries whose text another entry shares a new text.
    private static void Rename(List<CatalogueEntry> list, Func<CatalogueEntry, string> rename)
    {
        var counts = list.GroupBy(e => e.Text).ToDictionary(g => g.Key, g => g.Count());
        for (int i = 0; i < list.Count; i++)
            if (counts[list[i].Text] > 1) list[i] = list[i] with { Text = rename(list[i]) };
    }

    private static string UniqueName(int id)
    {
        try
        {
            return UniqueList.getUniqueName((ushort)id, true) ?? $"Unique {id}";
        }
        catch
        {
            return $"Unique {id}";
        }
    }

    private static void ResetCatalogue()
    {
        s_affixCatalogue = null;
        s_catalogue = null;
    }

    // ---- ranking ----

    // Words players type for a stat's words.
    private static readonly Dictionary<string, string[]> s_aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["life"] = new[] { "health" }, ["hp"] = new[] { "health" },
        ["res"] = new[] { "resistance", "resistances" }, ["resist"] = new[] { "resistance", "resistances" },
        ["ms"] = new[] { "movement speed", "movespeed" }, ["spd"] = new[] { "speed" }, ["inc"] = new[] { "increased" },
        ["dmg"] = new[] { "damage" }, ["crit"] = new[] { "critical" }, ["str"] = new[] { "strength" }, ["dex"] = new[] { "dexterity" },
        ["int"] = new[] { "intelligence" }, ["att"] = new[] { "attunement" }, ["vit"] = new[] { "vitality" },
        ["regen"] = new[] { "regeneration", "regen" }, ["ele"] = new[] { "elemental" }, ["max"] = new[] { "maximum" },
    };

    /// <summary>
    /// A stat's score for typed words (0 when a word matches nothing): each word scores 3 at a word's start, 2 inside a
    /// word, 1 as an abbreviation (its letters in order within one word, the first one matching); an alias scores as the
    /// word it stands for. Ties go to texts with fewer words.
    /// </summary>
    public static int Rank(string text, string[] words)
    {
        string lower = text.ToLowerInvariant();
        return RankWords(lower, words);
    }

    /// <summary>A catalogue entry's score, its keywords included.</summary>
    public static int Rank(CatalogueEntry entry, string[] words) => RankWords((entry.Text + " " + entry.Keywords).ToLowerInvariant(), words, entry.Text);

    private static int RankWords(string lower, string[] words, string? shown = null)
    {
        string[] textWords = lower.Split(new[] { ' ', '-', '(', ')', ',', '—' }, StringSplitOptions.RemoveEmptyEntries);
        int total = 0;
        foreach (string raw in words)
        {
            string w = raw.ToLowerInvariant();
            int best = WordScore(w, lower, textWords);
            if (s_aliases.TryGetValue(w, out var alts))
                foreach (string alt in alts)
                    best = Math.Max(best, alt.Contains(' ') ? (lower.Contains(alt) ? 3 : 0) : WordScore(alt, lower, textWords));
            if (best == 0) return 0;
            total += best;
        }
        int shownWords = shown?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length ?? textWords.Length;
        return total * 100 - Math.Min(shownWords, 99);
    }

    private static int WordScore(string w, string lower, string[] textWords)
    {
        if (textWords.Any(t => t.StartsWith(w, StringComparison.Ordinal))) return 3;
        if (lower.Contains(w)) return 2;
        foreach (string t in textWords)
        {
            if (t.Length == 0 || t[0] != w[0]) continue;
            int k = 0;
            foreach (char c in t)
                if (k < w.Length && c == w[k]) k++;
            if (k == w.Length) return 1;
        }
        return 0;
    }
}
