using System.Collections;
using EpochAssist.Core;
using EpochAssist.Ui;
using Il2Cpp;
using UnityEngine;

namespace EpochAssist.Features;

internal static partial class StashSearch
{
    private const float ResultLine = 24f, ResultGap = 6f, CellHeight = 30f, IconSize = 26f;

    // The page edits this copy; every change makes a fresh copy the active search.
    private static SearchDoc s_edit = new();
    // The item filter sections shown open.
    private static readonly HashSet<string> s_sectionsOpen = new();
    private static (int Group, int Row)? s_openRow;
    private static List<MenuRow>? s_rows;
    private static Vector2 s_resultScroll;
    private static string? s_resultNote;
    private static List<(string Name, int Id, (string Name, int Id)[] Subs)>? s_bases;
    // The page result under the pointer this pass and the one whose tooltip is open.
    private static ItemPlace? s_pageHover, s_pageTip;
    // The hovered result's top, in screen pixels from the top.
    private static float s_pageHoverY;

    private static readonly string[] s_groupLabels = { "And", "Not", "If", "Count", "Weighted sum" };
    private static readonly string[] s_positionWords = { "Any position", "Prefix", "Suffix", "Sealed" };
    private static readonly string[] s_kindLabels = { "Any kind", "Standard", "Corrupted", "Experimental", "Champion", "Set", "Idol enchantment", "Idol weaver" };
    private static readonly string[] s_triChoices = { "Any", "Yes", "No" };
    private static readonly string[] s_classNames = { "Any class", "Primalist", "Mage", "Sentinel", "Acolyte", "Rogue" };
    private static readonly int?[] s_classFlags = { null, 1, 2, 4, 8, 16 };

    /// <summary>The search the page shows (a copy is the active one).</summary>
    internal static SearchDoc Editing => s_edit;

    // The saved search the page opened (edits are saved back to it); null for one that isn't saved.
    private static SearchDoc? s_current;

    /// <summary>The saved search the page shows, or null.</summary>
    internal static SearchDoc? CurrentSaved => s_current;

    /// <summary>Replaces the page's search (a saved one opened, an import, a dev verb) and makes it active.</summary>
    internal static void Load(SearchDoc doc, SearchDoc? saved = null)
    {
        s_current = saved;
        s_edit = doc.Clone();
        s_editVersion++;
        s_openRow = null;
        s_rows = null;
        SetActive(s_edit.Clone());
    }

    private static void ResetPage()
    {
        s_current = null;
        s_edit = new SearchDoc();
        s_editVersion++;
        s_openRow = null;
        s_rows = null;
        s_resultNote = null;
        s_bases = null;
    }

    // A change to the search: structural ones rebuild the rows; typing in a number field waits for a pause.
    private static void Edit(Action<SearchDoc> change, bool structural = false, bool quiet = false)
    {
        change(s_edit);
        s_editVersion++;
        if (structural) s_rows = null;
        SetActive(s_edit.Clone(), quiet);
    }

    // ---- drawing ----

    // The Search page: the search on the left, the results on the right.
    private static void DrawPage(Rect rect)
    {
        MarkDrawn();
        if (MenuRoutes.Page is not { } page) return;
        if (s_active == null && s_pending == null) SetActive(s_edit.Clone());
        s_rows ??= BuildRows();
        float left = Mathf.Clamp(Mathf.Floor(rect.width * 0.56f), 360f, 620f);
        PageView.DrawRowList(page, s_rows, new Rect(rect.x, rect.y, left, rect.height));
        DrawResults(new Rect(rect.x + left + ResultGap * 2f, rect.y, rect.width - left - ResultGap * 2f, rect.height));
    }

    private static List<MenuRow> BuildRows()
    {
        // A search always has a stat group to add to: a new one, or after the last was removed, starts with an empty And.
        if (s_edit.Groups.Count == 0)
        {
            s_edit.Groups.Add(new StatGroup { Type = GroupType.And });
            s_editVersion++;
        }
        var rows = new List<MenuRow>
        {
            new ValueChoice("Saved filter", () => s_current == null ? 0 : Saved.ToList().IndexOf(s_current) + 1,
                // The first entry leaves the saved filter for a new, empty one (as New does); unsaved edits read "Not saved".
                i =>
                {
                    if (i == 0) { if (s_current != null) Load(new SearchDoc()); }
                    else if (i <= Saved.Count) Load(Saved[i - 1], Saved[i - 1]);
                },
                () => new[] { s_current == null ? "Not saved" : "None (new filter)" }.Concat(Saved.Select(d => d.Name ?? "?")).ToList()) { Key = "saved" },
            new ValueChoice("Recent", () => 0, i => { if (i > 0 && i <= Recent.Count) Load(Recent[i - 1]); },
                () => new[] { Recent.Count == 0 ? "None yet" : "Open a recent search filter" }.Concat(Recent.Select(Describe)).ToList())
            {
                Key = "recent", Width = 300f,
            },
        };
        // The stat filters first: the part used most.
        rows.Add(new HeadingRow("Stat filters"));
        for (int gi = 0; gi < s_edit.Groups.Count; gi++) rows.AddRange(GroupRows(gi));
        rows.Add(new TreeAdd
        {
            Key = "groups.add", Depth = 0, Label = "+ add filter group",
            Choices = () => s_groupLabels,
            Picked = i => Edit(d => d.Groups.Add(new StatGroup { Type = (GroupType)i }), structural: true),
            Blocked = () => s_edit.Groups.Count >= MaxGroups ? $"At most {MaxGroups} groups" : null,
        });
        // Then the item filters, in named sections that open on a click and say what they hold while closed.
        rows.Add(new HeadingRow("Item filters"));
        foreach (var (key, name, summary, sectionRows) in Sections())
        {
            bool open = s_sectionsOpen.Contains(key);
            rows.Add(new TreeLine
            {
                Key = "section." + key, Depth = 0,
                Words = () => (s_sectionsOpen.Contains(key) ? "▾  " : "▸  ") + name + (summary() is { Length: > 0 } set ? "  ·  " + set : string.Empty),
                Opened = open,
                Toggle = () => { if (!s_sectionsOpen.Remove(key)) s_sectionsOpen.Add(key); s_rows = null; },
            });
            if (open) rows.AddRange(sectionRows().Select(r => (MenuRow)new Indented(r, 1)));
        }
        // The Filter's own settings, last and closed: set once, rarely changed.
        rows.Add(new HeadingRow("Options"));
        bool settingsOpen = s_sectionsOpen.Contains("settings");
        rows.Add(new TreeLine
        {
            Key = "section.settings", Depth = 0,
            Words = () => (s_sectionsOpen.Contains("settings") ? "▾  " : "▸  ") + "Filter",
            Opened = settingsOpen,
            Toggle = () => { if (!s_sectionsOpen.Remove("settings")) s_sectionsOpen.Add("settings"); s_rows = null; },
        });
        if (settingsOpen)
            foreach (var setting in new Setting[]
                     {
                         StashSettings.SearchFilterButton, StashSettings.SearchPanel, StashSettings.SearchHighlight, StashSettings.SearchDim,
                         StashSettings.SearchBadges, StashSettings.SearchBox,
                     })
                rows.Add(new Indented(new SettingRow(setting) { Key = "setting." + setting.Id }, 1));
        // Share codes: a filter to text and back, in a section of their own.
        bool codesOpen = s_sectionsOpen.Contains("codes");
        rows.Add(new TreeLine
        {
            Key = "section.codes", Depth = 0,
            Words = () => (s_sectionsOpen.Contains("codes") ? "▾  " : "▸  ") + "Share",
            Opened = codesOpen,
            Toggle = () => { if (!s_sectionsOpen.Remove("codes")) s_sectionsOpen.Add("codes"); s_rows = null; },
        });
        if (codesOpen)
            foreach (var button in CodeButtons())
                rows.Add(new Indented(button, 1));
        return rows;
    }

    private static bool s_syntaxOpen;

    // Examples for the stash's own search box, each with what it finds.
    private static readonly (string Example, string Meaning)[] s_syntaxExamples =
    {
        ("~2hsword & t7 strength & suffixes1-", "two-handed swords with tier 7 Strength and an open suffix"),
        ("~(small | minor) & fire res & 1of(strength, dexterity, intelligence, vitality, attunement)", "1x1 idols with fire resistance and any attribute"),
        ("~max life & fire res & 1of(cold res, lightning res, void res, necrotic res, poison res)", "two stats every item must have, and any one of five"),
        ("~fire res 30+", "a stat by name, 30 or more (30- at most, 20-40 between)"),
        ("~health 8%+", "% lines; a plain number takes the flat ones"),
        ("~t6+ max life q80", "tier 6 or higher, roll at least 80% of its range"),
        ("~prefix crit chance", "prefix, suffix or sealed"),
        ("~implicit armor 100+", "an implicit line; total res 150+ adds lines up"),
        ("~helmet & fire res | lp2+", "the game's own words, & and |, ! for not"),
        ("~(ww20+ | lp2+) & !corrupted", "brackets"),
        ("~2of(fire res 20+, cold res 20+, void res 20+)", "at least 2 of them"),
        ("~\"of the giant\" | /1[4-9] attunement/", "quoted tooltip words, /regex/"),
    };

    private static int FilterCount()
    {
        var f = s_edit.Filters;
        return new[]
        {
            f.Types.Count > 0, f.Rarities.Count > 0, f.Uniques.Count > 0, !f.Lp.IsEmpty, !f.Ww.IsEmpty, !f.Wt.IsEmpty, !f.Fp.IsEmpty,
            !f.LevelReq.IsEmpty, !f.Prefixes.IsEmpty, !f.Suffixes.IsEmpty, f.Corrupted != Tri.Any, f.Sealed != Tri.Any, f.Class != null,
        }.Count(x => x);
    }

    // The item filter sections: key, name, what is set (for the closed line), and the rows.
    private static IEnumerable<(string Key, string Name, Func<string> Summary, Func<IEnumerable<MenuRow>> Rows)> Sections()
    {
        yield return ("type", "Type & rarity", TypeSummary, TypeRows);
        yield return ("potential", "Potential", () => Join(RangeText("LP", s_edit.Filters.Lp), RangeText("WW", s_edit.Filters.Ww),
            RangeText("WT", s_edit.Filters.Wt), RangeText("FP", s_edit.Filters.Fp)), PotentialRows);
        yield return ("requirements", "Requirements", () => Join(RangeText("level", s_edit.Filters.LevelReq),
            s_edit.Filters.Class is int c ? s_classNames[Math.Max(0, Array.IndexOf(s_classFlags, c))] : null), RequirementRows);
        if (Has(CountsGroup))
            yield return ("slots", "Affix slots", () => Join(RangeText("prefixes", s_edit.Filters.Prefixes), RangeText("suffixes", s_edit.Filters.Suffixes)), SlotRows);
        yield return ("state", "Corrupted & sealed", () => Join(TriText(s_edit.Filters.Corrupted, "corrupted"), TriText(s_edit.Filters.Sealed, "sealed")), StateRows);
    }

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    private static string? RangeText(string label, Range r)
    {
        if (r.IsEmpty) return null;
        string N(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        if (r.Min is { } lo && r.Max is { } hi) return lo == hi ? $"{label} {N(lo)}" : $"{label} {N(lo)}–{N(hi)}";
        return r.Min is { } min ? $"{label} ≥ {N(min)}" : $"{label} ≤ {N(r.Max!.Value)}";
    }

    private static string? TriText(Tri t, string word) => t == Tri.Any ? null : t == Tri.Yes ? word : "not " + word;

    private static string TypeSummary()
    {
        var f = s_edit.Filters;
        string? type = null;
        if (f.Types.Count > 0)
        {
            var b = Bases().FirstOrDefault(x => x.Id == f.Types[0].Type);
            type = b.Name ?? "a type";
            if (f.Types[0].Sub is int sub && b.Subs?.FirstOrDefault(x => x.Id == sub).Name is { } subName) type = subName;
        }
        string? rarity = f.Rarities.Count > 0 ? string.Join(", ", RarityWords.Where(f.Rarities.Contains).Select(w => char.ToUpperInvariant(w[0]) + w[1..])) : null;
        string? uniques = f.Uniques.Count switch { 0 => null, 1 => UniqueName(f.Uniques[0]), int n => $"{n} uniques" };
        return Join(type, rarity, uniques);
    }

    private static IEnumerable<MenuRow> TypeRows()
    {
        var bases = Bases();
        int TypeIndex() => s_edit.Filters.Types.Count == 0 ? 0 : bases.FindIndex(b => b.Id == s_edit.Filters.Types[0].Type) + 1;
        yield return new ValueChoice("Type", TypeIndex, i => Edit(d => d.Filters.Types = i <= 0 ? new() : new() { (bases[i - 1].Id, null) }, structural: true),
            () => new[] { "Any type" }.Concat(bases.Select(b => b.Name)).ToList()) { Key = "f.type" };
        if (TypeIndex() > 0)
        {
            var subs = bases[TypeIndex() - 1].Subs;
            yield return new ValueChoice("Subtype",
                () => s_edit.Filters.Types.Count == 0 || s_edit.Filters.Types[0].Sub is not int sub ? 0 : Array.FindIndex(subs, s => s.Id == sub) + 1,
                i => Edit(d => d.Filters.Types = new() { (d.Filters.Types[0].Type, i <= 0 ? null : subs[i - 1].Id) }),
                () => new[] { "Any subtype" }.Concat(subs.Select(s => s.Name)).ToList()) { Key = "f.sub" };
        }
        yield return new HintRow(() => "Rarity") { Key = "f.rarity.label" };
        yield return new CheckTree(() => RarityWords.Select(w => new CheckNode(w, char.ToUpperInvariant(w[0]) + w[1..], Array.Empty<CheckNode>())).ToList(),
            new NotifySet(() => s_edit.Filters.Rarities, () => Edit(_ => { })), "f.rarity");
        foreach (int id in s_edit.Filters.Uniques.ToList())
            yield return new TreeLine
            {
                Key = "f.unique." + id, Depth = 1, Words = () => UniqueName(id),
                Remove = () => Edit(d => d.Filters.Uniques.Remove(id), structural: true),
            };
        var uniques = StashUniques();
        yield return new TreeAdd
        {
            Key = "f.unique.add", Depth = 1, Label = "+ unique or set",
            Choices = () => uniques.Select(u => u.Name).ToList(),
            Picked = i => Edit(d => { if (!d.Filters.Uniques.Contains(uniques[i].Id)) d.Filters.Uniques.Add(uniques[i].Id); }, structural: true),
            Blocked = () => uniques.Count == 0 ? "No uniques or sets in the stash" : null,
        };
    }

    private static IEnumerable<MenuRow> PotentialRows()
    {
        yield return RangeRow("Legendary Potential", () => s_edit.Filters.Lp, (d, r) => d.Filters.Lp = r, "f.lp");
        yield return RangeRow("Weaver's Will", () => s_edit.Filters.Ww, (d, r) => d.Filters.Ww = r, "f.ww");
        yield return RangeRow("Weaver's Touch", () => s_edit.Filters.Wt, (d, r) => d.Filters.Wt = r, "f.wt");
        yield return RangeRow("Forging Potential", () => s_edit.Filters.Fp, (d, r) => d.Filters.Fp = r, "f.fp");
    }

    private static IEnumerable<MenuRow> RequirementRows()
    {
        yield return RangeRow("Level requirement", () => s_edit.Filters.LevelReq, (d, r) => d.Filters.LevelReq = r, "f.lvl");
        yield return new ValueChoice("Class", () => Math.Max(0, Array.IndexOf(s_classFlags, s_edit.Filters.Class)), i => Edit(d => d.Filters.Class = s_classFlags[i]), () => s_classNames) { Key = "f.class" };
    }

    private static IEnumerable<MenuRow> SlotRows()
    {
        yield return RangeRow("Prefix count", () => s_edit.Filters.Prefixes, (d, r) => d.Filters.Prefixes = r, "f.prefixes");
        yield return RangeRow("Suffix count", () => s_edit.Filters.Suffixes, (d, r) => d.Filters.Suffixes = r, "f.suffixes");
    }

    private static IEnumerable<MenuRow> StateRows()
    {
        yield return new ValueChoice("Corrupted", () => (int)s_edit.Filters.Corrupted, i => Edit(d => d.Filters.Corrupted = (Tri)i), () => s_triChoices) { Key = "f.corrupted" };
        yield return new ValueChoice("Sealed affix", () => (int)s_edit.Filters.Sealed, i => Edit(d => d.Filters.Sealed = (Tri)i), () => s_triChoices) { Key = "f.sealed" };
    }

    private static ValueRange RangeRow(string label, Func<Range> get, Action<SearchDoc, Range> set, string key, string unit = "") =>
        new(label, () => (get().Min, get().Max), (lo, hi) => Edit(d => set(d, new Range(lo, hi)), quiet: true))
        {
            Key = key, Unit = unit,
            Hint = () => get().Invalid ? "Min is above max: ignored" : null,
        };

    private static IEnumerable<MenuRow> GroupRows(int gi)
    {
        var g = s_edit.Groups[gi];
        yield return new TreeLine
        {
            Key = $"g{gi}", Depth = 0,
            On = () => gi < s_edit.Groups.Count && s_edit.Groups[gi].On,
            SetOn = on => Edit(d => d.Groups[gi].On = on),
            Type = () => gi < s_edit.Groups.Count ? (int)s_edit.Groups[gi].Type : 0,
            SetType = t => Edit(d => d.Groups[gi].Type = (GroupType)t, structural: true),
            Labels = s_groupLabels,
            Remove = () => Edit(d => { d.Groups.RemoveAt(gi); s_openRow = null; }, structural: true),
            Tip = () => g.Unreadable != null ? "A newer build's group: kept, not checked" : GroupTip(g.Type),
        };
        if (g.HasBound)
            yield return new Indented(RangeRow(g.Type == GroupType.Count ? "Matching rows" : "Sum", () => s_edit.Groups[gi].Bound, (d, r) => d.Groups[gi].Bound = r, $"g{gi}.bound"), 1);
        for (int ri = 0; ri < g.Rows.Count; ri++)
        {
            int r = ri;
            bool open = s_openRow == (gi, ri);
            yield return new TreeLine
            {
                Key = $"g{gi}.r{ri}", Depth = 1,
                On = () => gi < s_edit.Groups.Count && r < s_edit.Groups[gi].Rows.Count && s_edit.Groups[gi].Rows[r].On,
                SetOn = on => Edit(d => d.Groups[gi].Rows[r].On = on),
                Words = () => gi < s_edit.Groups.Count && r < s_edit.Groups[gi].Rows.Count ? RowSummary(s_edit.Groups[gi], s_edit.Groups[gi].Rows[r]) : string.Empty,
                Opened = open,
                Toggle = () => { s_openRow = open ? null : (gi, r); s_rows = null; },
                Remove = () => Edit(d => { d.Groups[gi].Rows.RemoveAt(r); s_openRow = null; }, structural: true),
                Tip = () => g.Rows[r].Unreadable != null ? $"Can't read its {g.Rows[r].Unreadable}: kept, not checked" : null,
            };
            if (open) foreach (var option in RowOptions(gi, ri)) yield return new Indented(option, 2);
        }
        var catalogue = Catalogue();
        var byText = catalogue.GroupBy(e => e.Text).ToDictionary(x => x.Key, x => x.First());
        yield return new TreeAdd
        {
            Key = $"g{gi}.add", Depth = 1, Label = "+ add stat filter",
            Choices = () => catalogue.Select(e => e.Text).ToList(),
            Groups = () => CatalogueGroups(catalogue),
            Ranker = (text, words) => byText.TryGetValue(text, out var e) ? Rank(e, words) : Rank(text, words),
            Picked = i => Edit(d =>
            {
                var entry = catalogue[i];
                d.Groups[gi].Rows.Add(new StatRow { Stat = entry.Ref, Weight = 1d });
                s_openRow = (gi, d.Groups[gi].Rows.Count - 1);
            }, structural: true),
            Blocked = () => s_edit.RowCount >= MaxRows ? $"At most {MaxRows} rows" : null,
        };
    }

    // An open row's options: only those that apply to its stat.
    private static IEnumerable<MenuRow> RowOptions(int gi, int ri)
    {
        var row = s_edit.Groups[gi].Rows[ri];
        var group = s_edit.Groups[gi];
        string k = $"g{gi}.r{ri}";
        StatRow R(SearchDoc d) => d.Groups[gi].Rows[ri];
        bool affixLike = row.Stat.Source is StatSource.Affix or StatSource.AnyPrefix or StatSource.AnySuffix;
        if (affixLike)
        {
            if (row.Stat.Source == StatSource.Affix)
                yield return new ValueChoice("Position", () => (int)R(s_edit).Position, i => Edit(d => R(d).Position = (RowPosition)i), () => s_positionWords) { Key = k + ".pos" };
            yield return new ValueChoice("Kind", () => (int)R(s_edit).Kind, i => Edit(d => R(d).Kind = (RowKind)i), () => s_kindLabels) { Key = k + ".kind" };
            yield return RangeRow("Tier", () => R(s_edit).Tier, (d, r) => R(d).Tier = r, k + ".tier");
        }
        var numbers = EntryOf(row.Stat)?.Numbers ?? Array.Empty<(string Label, string Unit)>();
        if (numbers.Length == 0 && row.Stat.Source is StatSource.AnyPrefix or StatSource.AnySuffix) numbers = new[] { ("First number", string.Empty) };
        if (numbers.Length == 0) numbers = new[] { ("Value", string.Empty) };
        for (int n = 0; n < numbers.Length; n++)
        {
            int i = n;
            string label = numbers.Length == 1 ? "Value" : NumberLabel(numbers[i].Label, i);
            yield return RangeRow(label, () => R(s_edit).ValueAt(i), (d, r) =>
            {
                var values = R(d).Values;
                while (values.Count <= i) values.Add(default);
                values[i] = r;
            }, $"{k}.v{i}", numbers[i].Unit);
        }
        if (row.Stat.Source != StatSource.Pseudo)
            yield return new ValueNumber("Roll quality at least", () => (float)(R(s_edit).QualityMin ?? 0d), v => Edit(d => R(d).QualityMin = v <= 0f ? null : v, quiet: true), 0f, 100f, 1f, "%")
            {
                Key = k + ".quality",
                Hint = "100% is a perfect roll",
            };
        if (group.Type == GroupType.WeightedSum)
            yield return new ValueNumber("Weight", () => (float)R(s_edit).Weight, v => Edit(d => R(d).Weight = v, quiet: true), -100f, 100f, 0.1f) { Key = k + ".weight" };
    }

    private static string NumberLabel(string label, int index) => string.IsNullOrWhiteSpace(label) ? $"Number {index + 1}" : label;

    // "Fire Resistance  T6+ · ≥ 59 · 100%" — the stat and the bounds set on it.
    private static string RowSummary(StatGroup g, StatRow row)
    {
        var parts = new List<string>();
        if (row.Position != RowPosition.Any) parts.Add(s_positionWords[(int)row.Position].ToLowerInvariant());
        if (row.Kind != RowKind.Any) parts.Add(s_kindLabels[(int)row.Kind].ToLowerInvariant());
        if (!row.Tier.IsEmpty) parts.Add("T" + Bounds(row.Tier));
        for (int i = 0; i < row.Values.Count; i++)
            if (!row.Values[i].IsEmpty) parts.Add((row.Values.Count(v => !v.IsEmpty) > 1 || i > 0 ? $"#{i + 1} " : string.Empty) + Bounds(row.Values[i]));
        if (row.QualityMin is double q) parts.Add($"q ≥ {q:0}%");
        if (g.Type == GroupType.WeightedSum) parts.Add($"× {row.Weight:0.##}");
        string name = EntryOf(row.Stat)?.Text ?? RowName(row);
        // Without the game's value functions, numbers can't be read: rows that bound them can't match.
        if (!GameData.Has(GameData.Values) && (row.Values.Any(v => !v.IsEmpty) || row.QualityMin != null || row.Stat.Source is StatSource.Implicit or StatSource.UniqueRoll or StatSource.Pseudo))
            parts.Add("unavailable");
        return parts.Count == 0 ? name : $"{name}  ·  {string.Join(" · ", parts)}";
    }

    private static string Bounds(Range r) =>
        r.Invalid ? "(min above max)" : r.Min != null && r.Max != null ? $"{ValueRange.Format(r.Min)}–{ValueRange.Format(r.Max)}" : r.Min != null ? $"≥ {ValueRange.Format(r.Min)}" : $"≤ {ValueRange.Format(r.Max)}";

    private static string GroupTip(GroupType type) => type switch
    {
        GroupType.And => "Every row must match",
        GroupType.Not => "No row may match",
        GroupType.If => "Rows whose stat the item has must match; a missing stat passes",
        GroupType.Count => "The number of matching rows must be within the range",
        _ => "The sum of each matching row's value × weight must be within the range",
    };

    // The game's equippable base types and their subtypes, by name, read once.
    private static List<(string Name, int Id, (string Name, int Id)[] Subs)> Bases()
    {
        if (s_bases != null) return s_bases;
        var list = new List<(string, int, (string, int)[])>();
        try
        {
            var types = ItemList.get()?.EquippableItems;
            for (int i = 0; types != null && i < types.Length; i++)
            {
                var b = types[i];
                if (b?.subItems == null) continue;
                var subs = new List<(string, int)>();
                for (int k = 0; k < b.subItems.Count; k++)
                    if (b.subItems[k] is { } s)
                        subs.Add((string.IsNullOrWhiteSpace(s.displayName) ? s.name : s.displayName, s.subTypeID));
                string name = string.IsNullOrWhiteSpace(b.displayName) ? b.BaseTypeName : b.displayName;
                list.Add((name, b.baseTypeID, subs.ToArray()));
            }
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("search:bases", $"Stash search: base types unreadable: {e.Message}", 30);
        }
        if (list.Count > 0) s_bases = list;
        return list;
    }

    // Uniques and sets in the stash now, by name.
    private static List<(string Name, int Id)> StashUniques() =>
        s_facts.Values.Where(f => f.Rarity is "unique" or "set" or "legendary").Select(f => f.UniqueId).Distinct()
            .Select(id => (UniqueName(id), id)).OrderBy(u => u.Item1, StringComparer.OrdinalIgnoreCase).ToList();

    // ---- results ----

    // The search as ~ text for the stash's own search box, rebuilt when the search changes.
    private static int s_editVersion;
    private static (int Version, string Text, List<string> Left)? s_boxPreview;
    private static float s_boxCopiedUntil;

    // A small-caps heading with a divider, as the left column's sections have.
    private static void SectionHeading(Rect r, string text)
    {
        if (Event.current.type != EventType.Repaint) return;
        GUIStyle style = Widgets.Style(() => Theme.Heading, GUI.skin.label);
        string upper = text.ToUpperInvariant();
        var line = new Rect(r.x, r.y + 4f, r.width, 14f);
        Widgets.Paint(line, upper, style);
        float x = line.x + TextCache.Width(upper, style) + 8f;
        if (Widgets.Themed && x < line.xMax) Theme.Fill(new Rect(x, Mathf.Round(line.center.y), line.xMax - x, 1f), Theme.DividerColor);
    }

    private static float DrawBoxText(float width, float y, GUIStyle label, GUIStyle hint)
    {
        if (!StashSettings.SearchBox.Value) return y;
        if (s_boxPreview is not { } box || box.Version != s_editVersion)
        {
            var (text, left) = BoxTextOf(s_edit);
            s_boxPreview = box = (s_editVersion, text, left);
        }
        // The section's heading, with ? at its end: how to write a ~ search, shown or hidden.
        const string help = "?";
        float hw = RowLayout.ControlHeight;
        SectionHeading(new Rect(0f, y + (RowLayout.ControlHeight - 22f) / 2f, width - hw - 8f, 22f), "Stash search box");
        if (Controls.Button(new Rect(width - hw, y, hw, RowLayout.ControlHeight), help, "stashsearch.boxtext.help", primary: s_syntaxOpen,
                tip: s_syntaxOpen ? "Hide the ~ examples" : "How to search with ~ in the stash window's search box"))
            s_syntaxOpen = !s_syntaxOpen;
        y += RowLayout.ControlHeight + 2f;
        if (s_syntaxOpen)
        {
            var exampleStyle = new GUIStyle(label) { wordWrap = true };
            var meaningStyle = new GUIStyle(hint) { wordWrap = true };
            foreach (var (example, meaning) in s_syntaxExamples)
            {
                float eh = exampleStyle.CalcHeight(new GUIContent(example), width);
                float mh = meaningStyle.CalcHeight(new GUIContent(meaning), width - 12f);
                if (Event.current.type == EventType.Repaint)
                {
                    Widgets.Paint(new Rect(0f, y, width, eh), example, exampleStyle);
                    Widgets.Paint(new Rect(12f, y + eh, width - 12f, mh), meaning, meaningStyle);
                }
                y += eh + mh + 4f;
            }
            y += 6f;
        }
        if (box.Text.Length == 0)
        {
            if (Event.current.type == EventType.Repaint)
                Widgets.Paint(new Rect(0f, y, width, ResultLine), "This filter as ~ text shows here.", hint);
            return y + ResultLine + 10f;
        }
        // The button says so for a moment after copying, beside the toast the page's other copy gives.
        string copy = Time.unscaledTime < s_boxCopiedUntil ? "Copied" : "Copy";
        float bw = Mathf.Max(Controls.ButtonWidth("Copy"), Controls.ButtonWidth("Copied"));
        // In an input box, as the stash's own box shows it. Copies, so wrapping doesn't change the shared styles.
        var wrap = new GUIStyle(Widgets.Style(() => Theme.FieldText, GUI.skin.label)) { wordWrap = true };
        const float pad = 2f;
        float textWidth = width - bw - 8f;
        float h = Mathf.Max(RowLayout.ControlHeight, wrap.CalcHeight(new GUIContent(box.Text), textWidth - pad * 2f) + 8f);
        if (Event.current.type == EventType.Repaint)
        {
            var field = new Rect(0f, y, textWidth, h);
            if (Widgets.Themed) Theme.DrawSliced(field, Theme.InputBox, Theme.ControlRadius);
            Widgets.Paint(new Rect(pad, y + 4f, textWidth - pad * 2f, h - 8f), box.Text, wrap);
        }
        if (Controls.Button(new Rect(width - bw, y, bw, RowLayout.ControlHeight), copy, "stashsearch.boxtext.copy", primary: copy == "Copied", tip: "Copy the ~ text"))
        {
            GUIUtility.systemCopyBuffer = box.Text;
            s_boxCopiedUntil = Time.unscaledTime + 1.5f;
            Toasts.Show("stashsearch", "~ text copied: paste it in the stash window's search box.", ToastKind.Info);
        }
        y += h + 2f;
        if (box.Left.Count > 0)
        {
            string note = "≈ The box can't say: " + string.Join(", ", box.Left);
            var noteStyle = new GUIStyle(hint) { wordWrap = true };
            float nh = noteStyle.CalcHeight(new GUIContent(note), width);
            if (Event.current.type == EventType.Repaint) Widgets.Paint(new Rect(0f, y, width, nh), note, noteStyle);
            y += nh;
        }
        return y + 8f;
    }

    private static void DrawResults(Rect rect)
    {
        GUILayout.BeginArea(rect);
        Popups.PushOrigin(rect.position);
        try
        {
            var r = new Rect(0f, 0f, rect.width, rect.height);
            GUIStyle heading = Widgets.Style(() => Theme.Heading, GUI.skin.label);
            GUIStyle label = Widgets.Style(() => Theme.Label, GUI.skin.label);
            GUIStyle hint = Widgets.Style(() => Theme.Hint, GUI.skin.label);
            var groups = Grouped();
            // Two sections, headed like the left column's: the filter as ~ text for the stash's own box, then the results.
            float y = DrawBoxText(r.width, 0f, label, hint);
            SectionHeading(new Rect(0f, y, r.width, 22f), "Results");
            y += 24f;
            if (Event.current.type == EventType.Repaint) Widgets.Paint(new Rect(0f, y, r.width, ResultLine), ResultsHeader(groups), label);
            y += ResultLine + 2f;
            if (s_resultNote != null)
            {
                if (Event.current.type == EventType.Repaint) Widgets.Paint(new Rect(0f, y, r.width, ResultLine), s_resultNote, hint);
                y += ResultLine;
            }
            if (groups.Count == 0) return;
            if (Event.current.type == EventType.Repaint) s_pageHover = null;
            float content = groups.Sum(f => ResultLine + f.Tabs.Sum(t => ResultLine + t.Items.Count * CellHeight + 4f));
            var view = new Rect(0f, y, r.width, r.height - y);
            var inner = new Rect(0f, 0f, view.width - 14f, content);
            s_resultScroll = GUI.BeginScrollView(view, s_resultScroll, inner, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);
            try
            {
                float cy = 0f;
                int n = 0;
                foreach (var folder in groups)
                {
                    if (Event.current.type == EventType.Repaint) Widgets.Paint(new Rect(0f, cy, inner.width, ResultLine), $"{folder.Folder.Name}  ·  {folder.Count}", heading);
                    cy += ResultLine;
                    foreach (var tab in folder.Tabs)
                    {
                        if (Event.current.type == EventType.Repaint) Widgets.Paint(new Rect(10f, cy, inner.width - 10f, ResultLine), $"{tab.Tab.Name}  ·  {tab.Items.Count}", label);
                        cy += ResultLine;
                        foreach (var result in tab.Items)
                        {
                            n++;
                            var cell = new Rect(22f, cy, inner.width - 22f, CellHeight);
                            if (cy + CellHeight >= s_resultScroll.y && cy <= s_resultScroll.y + view.height) ResultCell(cell, result, n, folder, tab);
                            cy += CellHeight;
                        }
                        cy += 4f;
                    }
                }
            }
            finally
            {
                GUI.EndScrollView();
            }
            if (Event.current.type == EventType.Repaint) PageTooltip();
        }
        finally
        {
            Popups.PopOrigin();
            GUILayout.EndArea();
        }
    }

    // The game's tooltip for the result under the pointer, beside the menu (no game element behind it: shown alone).
    private static void PageTooltip()
    {
        if (s_pageHover == s_pageTip) return;
        ItemTooltip.Close();
        s_pageTip = null;
        if (s_pageHover is not { } place || !Has(TooltipGroup) || ItemAt(place) is not { } item) return;
        var menu = Ui.Menu.ScreenRect;
        // With the stash window open the tooltip opens as a stash item's, at an anchor of the mod's in the stash window
        // placed at the menu's edge level with the row: the game draws it whole with its comparison, and places it beside
        // the anchor again when it redraws (holding Ctrl or Alt). It is centred on the point given, so half its width
        // goes beyond the menu.
        float rowTop = Screen.height - s_pageHoverY;
        var ui = SceneObjects.Find<StashTabbedUIControls>();
        if (Stash.StashWindowOpen && Game.Alive(ui) && PageAnchor(ui!, menu, rowTop) is { } anchor)
        {
            float scale = Mathf.Max(0.01f, anchor.transform.lossyScale.x);
            float half = (TooltipWithCompare / 2f + PanelGap) * scale;
            float x = menu.xMax + half;
            bool right = x + half <= Screen.width;
            if (!right) x = menu.xMin - half;
            // The tooltips keep their near edge at the menu's, whether the comparison shows or not.
            float edge = right ? menu.xMax + PanelGap * scale : menu.xMin - PanelGap * scale;
            if (ItemTooltip.Open(item, anchor, new Vector3(x, rowTop, 0f), besideEdge: edge, toRight: right)) s_pageTip = place;
            return;
        }
        float ax = menu.xMax + 250f;
        if (ax + 250f > Screen.width) ax = menu.xMin - 250f;
        if (ItemTooltip.Open(item, null, new Vector3(ax, rowTop, 0f), alone: true)) s_pageTip = place;
    }

    public const string PageAnchorName = PanelName + ".PageAnchor";
    private static GameObject? s_pageAnchor;

    // An empty element in the stash window, one row high, along the menu's edge on the side the tooltip opens.
    private static GameObject? PageAnchor(StashTabbedUIControls ui, Rect menu, float rowTop)
    {
        if (PanelHost(ui) is not { } host) return null;
        if (!Game.Alive(s_pageAnchor))
        {
            s_pageAnchor = new GameObject(PageAnchorName);
            var made = s_pageAnchor.AddComponent<RectTransform>();
            made.SetParent(host, false);
            s_pageAnchor.AddComponent<UnityEngine.UI.LayoutElement>().ignoreLayout = true;
            made.anchorMin = made.anchorMax = new Vector2(0.5f, 0.5f);
            made.pivot = new Vector2(0f, 1f);
        }
        var rect = s_pageAnchor!.GetComponent<RectTransform>();
        float scale = Mathf.Max(0.01f, rect.lossyScale.x);
        bool right = menu.xMax + TooltipWithCompare * scale < Screen.width;
        float edge = right ? menu.xMax - 2f : menu.xMin;
        rect.sizeDelta = new Vector2(2f / scale, CellHeight / scale);
        rect.localPosition = host.InverseTransformPoint(new Vector3(edge, rowTop, 0f));
        return s_pageAnchor;
    }

    private static void DestroyPageAnchor()
    {
        if (Game.Alive(s_pageAnchor)) UnityEngine.Object.Destroy(s_pageAnchor);
        s_pageAnchor = null;
    }

    /// <summary>Closes a tooltip the page opened (the page closed, or the pointer left the menu).</summary>
    private static void ClosePageTooltip()
    {
        if (s_pageTip == null) return;
        s_pageTip = null;
        ItemTooltip.Close();
    }

    private static string ResultsHeader(IReadOnlyList<ResultFolder> groups)
    {
        if (s_active == null || s_active.IsEmpty) return "Add a stat filter or an item filter";
        int count = groups.Sum(f => f.Count);
        if (Matching || s_pass != null && s_passNew > 0) return $"Searching… {count} so far";
        return count == 0 ? "No items match" : count == 1 ? "1 item" : $"{count} items";
    }

    // One result: the game's icon, the item's name, its badge at the end. Hover shows the game's tooltip; a click says
    // where the item is.
    private static void ResultCell(Rect cell, Result result, int n, ResultFolder folder, ResultTab tab)
    {
        var facts = FactsOf(result.Place.Key);
        string name = facts?.Name ?? "?";
        GUIStyle label = Widgets.Style(() => Theme.FieldText, GUI.skin.label);
        bool hover = cell.Contains(Event.current.mousePosition);
        if (hover && Event.current.type == EventType.Repaint)
        {
            s_pageHover = result.Place;
            s_pageHoverY = GUIUtility.GUIToScreenPoint(cell.position).y;
        }
        if (Event.current.type == EventType.Repaint)
        {
            if (hover && Widgets.Themed) Theme.DrawSliced(cell, Theme.HoverBg, Theme.CardRadius);
            if (facts != null && Has(IconsGroup))
            {
                bool unique = facts.Rarity is "unique" or "set" or "legendary";
                var iconRect = new Rect(cell.x + 2f, cell.y + (cell.height - IconSize) / 2f, IconSize, IconSize);
                Theme.Fill(new Rect(iconRect.x, iconRect.yMax - 2f, iconRect.width, 2f), RarityColour(facts.Rarity));
                GameIcons.Draw(iconRect, GameIcons.Get(facts.Type, facts.Sub, unique ? facts.UniqueId : 0));
            }
            cell = new Rect(cell.x + IconSize + 6f, cell.y, cell.width - IconSize - 6f, cell.height);
            float badgeWidth = 0f;
            if (result.Badge is { } badge && StashSettings.SearchBadges.Value)
            {
                badgeWidth = TextCache.Width(badge, label) + 12f;
                Widgets.Paint(new Rect(cell.xMax - badgeWidth, cell.y, badgeWidth, cell.height), Widgets.Themed ? Widgets.Colored(badge, Theme.AccentText) : badge, label);
            }
            var nameRect = new Rect(cell.x + 6f, cell.y, cell.width - badgeWidth - 10f, cell.height);
            Widgets.Paint(nameRect, Controls.Fit(name, label, nameRect.width), label);
        }
        if (GUI.Button(cell, GUIContent.none, GUIStyle.none) || Focus.Item("result." + n, cell))
        {
            // With the stash open, a click goes there; otherwise it says where the item is.
            if (Stash.StashWindowOpen && Has(GoGroup)) s_resultNote = GoTo(result.Place) == "going" ? null : s_resultNote;
            else s_resultNote = $"{name}: {folder.Folder.Name} › {tab.Tab.Name}. Open the stash to go there.";
        }
    }

    private static IEnumerable<string> PageBridgeLines()
    {
        yield return "search: " + (s_active?.ToJson() ?? "none");
        var groups = Grouped();
        yield return "results: " + ResultsHeader(groups);
        int n = 0;
        foreach (var folder in groups)
            foreach (var tab in folder.Tabs)
                foreach (var r in tab.Items)
                    yield return $"  {++n}. {folder.Folder.Name} › {tab.Tab.Name} ({r.Place.X},{r.Place.Y}) {FactsOf(r.Place.Key)?.Name}{(r.Badge != null ? " · " + r.Badge : string.Empty)}";
        if (s_resultNote != null) yield return "note: " + s_resultNote;
    }

    // ---- the title row's buttons ----

    /// <summary>New, Save, Rename and Delete, for the page's title row (the share codes are under Settings).</summary>
    internal static IEnumerable<ActionButton> PageButtons()
    {
        yield return new ActionButton("New", "stashsearch.new") { OnClick = () => Load(new SearchDoc()), Tip = "A new, empty filter" };
        yield return new ActionButton("Save", "stashsearch.save")
        {
            Tip = "Save this search by name (up to 50; a name in use gets a number)",
            Prompt = new ButtonPrompt(() => s_current != null ? $"Save over \"{s_current.Name}\", or give a new name" : "Name this search", "Save", SubmitSave)
            {
                Input = true, Initial = () => s_current?.Name ?? Describe(s_edit),
            },
        };
        yield return new ActionButton("Rename", "stashsearch.rename")
        {
            VisibleWhen = () => s_current != null,
            Prompt = new ButtonPrompt(() => "New name", "Rename", name =>
            {
                if (s_current == null) return "Not a saved search";
                if (name.Trim().Length == 0) return "Give it a name";
                return Rename(s_current, name) ?? Done(() => s_rows = null);
            }) { Input = true, Initial = () => s_current?.Name ?? string.Empty },
        };
        yield return new ActionButton("Delete", "stashsearch.delete")
        {
            VisibleWhen = () => s_current != null, Danger = true,
            Prompt = new ButtonPrompt(() => $"Delete \"{s_current?.Name}\"? The search stays open, unsaved.", "Delete", _ =>
            {
                if (s_current == null) return "Not a saved search";
                // The filter stays open, unsaved, without the deleted one's name.
                return Delete(s_current) ?? Done(() => { s_current = null; s_edit.Name = null; s_rows = null; });
            }) { Danger = true },
        };
    }

    /// <summary>Copy code and Import code: a filter as a share code and back, in the page's Settings section.</summary>
    private static IEnumerable<ActionButton> CodeButtons()
    {
        yield return new ActionButton("Copy code", "stashsearch.copy")
        {
            Tip = "A share code for this search (the search only; nothing about the character or stash)",
            OnClick = () =>
            {
                var (code, error) = CodeOf(s_edit);
                if (code != null) GUIUtility.systemCopyBuffer = code;
                Toasts.Show("stashsearch", code != null ? "Search code copied." : $"Not copied: {error}.", code != null ? ToastKind.Info : ToastKind.Warning);
            },
        };
        yield return new ActionButton("Import code", "stashsearch.import")
        {
            Tip = "Open a search from a share code; saved searches are never replaced",
            Prompt = new ButtonPrompt(() => "Paste a search code", "Open", text =>
            {
                if (FromCode(text, out string? error) is not { } doc) return char.ToUpperInvariant(error![0]) + error[1..];
                Load(doc);
                return null;
            }) { Input = true },
        };
    }

    private static string? SubmitSave(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return "Give it a name";
        if (s_current != null && string.Equals(name, s_current.Name, StringComparison.OrdinalIgnoreCase))
            return Overwrite(s_current, s_edit) ?? Done(() => { });
        var error = SaveAs(s_edit, name, out var saved);
        if (error != null) return error;
        s_current = saved;
        s_edit.Name = saved!.Name;
        s_rows = null;
        Toasts.Show("stashsearch", $"Saved as \"{saved.Name}\".", ToastKind.Info);
        return null;
    }

    private static string? Done(Action after)
    {
        after();
        return null;
    }

    /// <summary>A set the check tree changes that tells the page each time (the rarities).</summary>
    private sealed class NotifySet : ISet<string>
    {
        private readonly Func<HashSet<string>> _set;
        private readonly Action _changed;

        public NotifySet(Func<HashSet<string>> set, Action changed)
        {
            _set = set;
            _changed = changed;
        }

        private T Notify<T>(T result)
        {
            _changed();
            return result;
        }

        public int Count => _set().Count;
        public bool IsReadOnly => false;
        public bool Add(string item) => Notify(_set().Add(item));
        void ICollection<string>.Add(string item) => Add(item);
        public bool Remove(string item) => Notify(_set().Remove(item));
        public void Clear() => Notify(Clear(_set()));
        private static bool Clear(HashSet<string> set) { set.Clear(); return true; }
        public bool Contains(string item) => _set().Contains(item);
        public void CopyTo(string[] array, int index) => _set().CopyTo(array, index);
        public IEnumerator<string> GetEnumerator() => _set().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void ExceptWith(IEnumerable<string> other) => Notify(Do(() => _set().ExceptWith(other)));
        public void IntersectWith(IEnumerable<string> other) => Notify(Do(() => _set().IntersectWith(other)));
        public void SymmetricExceptWith(IEnumerable<string> other) => Notify(Do(() => _set().SymmetricExceptWith(other)));
        public void UnionWith(IEnumerable<string> other) => Notify(Do(() => _set().UnionWith(other)));
        private static bool Do(Action a) { a(); return true; }
        public bool IsProperSubsetOf(IEnumerable<string> other) => _set().IsProperSubsetOf(other);
        public bool IsProperSupersetOf(IEnumerable<string> other) => _set().IsProperSupersetOf(other);
        public bool IsSubsetOf(IEnumerable<string> other) => _set().IsSubsetOf(other);
        public bool IsSupersetOf(IEnumerable<string> other) => _set().IsSupersetOf(other);
        public bool Overlaps(IEnumerable<string> other) => _set().Overlaps(other);
        public bool SetEquals(IEnumerable<string> other) => _set().SetEquals(other);
    }
}
