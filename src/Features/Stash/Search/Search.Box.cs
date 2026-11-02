using System.Globalization;
using System.Text.RegularExpressions;
using EpochAssist.Core;
using HarmonyLib;
using UnityEngine;
using Il2Cpp;

namespace EpochAssist.Features;

// The stash's own search box, extended: text starting with ~ is read here instead of by the game. It keeps the game's
// words (macros like t7, lp2+, helmet, !corrupted, and /regex/) and adds stats by name with values, tiers and roll
// quality, totals, brackets and counts. The game is handed a stand-in expression and asks this file about each item, so
// its own filtering (Quick View's tabs, the grid's dimming) shows the matches. Only reads.
internal static partial class StashSearch
{
    public const string BoxPrefix = "~";

    // ---- the parsed search ----

    internal abstract class BoxNode
    {
        public abstract bool Holds(BoxItem item);
        public abstract string Show();
    }

    /// <summary>One item being checked: the game's item, its tooltip text, and its facts read once.</summary>
    internal sealed class BoxItem
    {
        public BoxItem(ItemDataUnpacked data, string tooltip, ItemFacts? facts = null) => (Data, Tooltip, _facts) = (data, tooltip, facts);
        public readonly ItemDataUnpacked Data;
        public readonly string Tooltip;
        private ItemFacts? _facts;
        public ItemFacts Facts => _facts ??= ReadFacts(Data, "box", 1, 1);
    }

    private sealed class AllNode : BoxNode
    {
        public readonly List<BoxNode> Parts = new();
        public override bool Holds(BoxItem item) => Parts.All(p => p.Holds(item));
        public override string Show() => "(" + string.Join(" & ", Parts.Select(p => p.Show())) + ")";
    }

    private sealed class AnyNode : BoxNode
    {
        public readonly List<BoxNode> Parts = new();
        public override bool Holds(BoxItem item) => Parts.Any(p => p.Holds(item));
        public override string Show() => "(" + string.Join(" | ", Parts.Select(p => p.Show())) + ")";
    }

    private sealed class NotNode : BoxNode
    {
        public NotNode(BoxNode inner) => Inner = inner;
        public readonly BoxNode Inner;
        public override bool Holds(BoxItem item) => !Inner.Holds(item);
        public override string Show() => "!" + Inner.Show();
    }

    private sealed class CountNode : BoxNode
    {
        public CountNode(Range bound) => Bound = bound;
        public readonly Range Bound;
        public readonly List<BoxNode> Parts = new();
        public override bool Holds(BoxItem item) => Bound.Holds(Parts.Count(p => p.Holds(item)));
        public override string Show() => $"count {Bound} of (" + string.Join(", ", Parts.Select(p => p.Show())) + ")";
    }

    // The game's own words, checked by the game.
    private sealed class GameNode : BoxNode
    {
        public GameNode(string text, ItemSearchExpression expression) => (Text, Expression) = (text, expression);
        public readonly string Text;
        public readonly ItemSearchExpression Expression;
        public override bool Holds(BoxItem item) => Expression.ItemMatches(item.Data, item.Tooltip);
        public override string Show() => $"game[{Text}]";
    }

    private sealed class RegexNode : BoxNode
    {
        public RegexNode(Regex regex) => Pattern = regex;
        public readonly Regex Pattern;
        public override bool Holds(BoxItem item)
        {
            try
            {
                return Pattern.IsMatch(PlainTooltip(item.Tooltip));
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }
        public override string Show() => $"/{Pattern}/";
    }

    private sealed class TextNode : BoxNode
    {
        public TextNode(string text) => Text = text.ToLowerInvariant();
        public readonly string Text;
        public override bool Holds(BoxItem item) => PlainTooltip(item.Tooltip).Contains(Text, StringComparison.OrdinalIgnoreCase);
        public override string Show() => $"\"{Text}\"";
    }

    // A stat by name: every line the name fits equally well ("health" is the flat and the % line alike).
    private sealed class StatNode : BoxNode
    {
        public StatNode(List<StatRow> rows, string name) => (Rows, Name) = (rows, name);
        public readonly List<StatRow> Rows;
        public readonly string Name;
        private StatRow Row => Rows[0];
        public override bool Holds(BoxItem item) => Rows.Any(r => RowHit(r, item.Facts, out _) != null);
        public override string Show()
        {
            var parts = new List<string> { Name };
            if (Row.Position != RowPosition.Any) parts.Add(Row.Position.ToString().ToLowerInvariant());
            if (!Row.Tier.IsEmpty) parts.Add($"tier {Row.Tier}");
            if (Row.Values.Count > 0 && !Row.Values[0].IsEmpty) parts.Add($"value {Row.Values[0]}");
            if (Row.QualityMin is { } q) parts.Add($"roll {q:0}%+");
            return "stat[" + string.Join(", ", parts) + "]";
        }
    }

    private static readonly Regex s_tags = new("<[^>]*>", RegexOptions.Compiled);

    private static string PlainTooltip(string tooltip) => s_tags.Replace(tooltip ?? string.Empty, string.Empty);

    // ---- reading the text ----

    /// <summary>Reads a ~ search (without the ~): the parsed search, or why it can't be read.</summary>
    internal static BoxNode? ParseBox(string text, out string? error)
    {
        error = null;
        var parser = new BoxParser(text);
        try
        {
            var node = parser.Or();
            parser.SkipSpace();
            if (!parser.AtEnd) throw new FormatException($"unexpected '{parser.Rest}'");
            return node;
        }
        catch (FormatException e)
        {
            error = e.Message;
            return null;
        }
    }

    private sealed class BoxParser
    {
        private readonly string _text;
        private int _at;
        private static readonly Regex s_count = new(@"\G\s*(\d+)\s*([+-]?)\s*of\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public BoxParser(string text) => _text = text;
        public bool AtEnd => _at >= _text.Length;
        public string Rest => _text[_at..];

        public void SkipSpace()
        {
            while (_at < _text.Length && char.IsWhiteSpace(_text[_at])) _at++;
        }

        private bool Take(char c)
        {
            SkipSpace();
            if (_at < _text.Length && _text[_at] == c)
            {
                _at++;
                return true;
            }
            return false;
        }

        public BoxNode Or()
        {
            var first = And();
            if (!Peek('|')) return first;
            var any = new AnyNode();
            any.Parts.Add(first);
            while (Take('|')) any.Parts.Add(And());
            return any;
        }

        private BoxNode And()
        {
            var first = Unary();
            if (!Peek('&')) return first;
            var all = new AllNode();
            all.Parts.Add(first);
            while (Take('&')) all.Parts.Add(Unary());
            return all;
        }

        private bool Peek(char c)
        {
            SkipSpace();
            return _at < _text.Length && _text[_at] == c;
        }

        private BoxNode Unary()
        {
            if (Take('!')) return new NotNode(Unary());
            return Primary();
        }

        private BoxNode Primary()
        {
            SkipSpace();
            var count = s_count.Match(_text, _at);
            if (count.Success)
            {
                _at = count.Index + count.Length;
                int n = int.Parse(count.Groups[1].Value, CultureInfo.InvariantCulture);
                var bound = count.Groups[2].Value == "-" ? new Range(null, n) : count.Groups[2].Value == "+" ? new Range(n, null) : new Range(n, null);
                var node = new CountNode(bound);
                do node.Parts.Add(Or());
                while (Take(','));
                if (!Take(')')) throw new FormatException("a count's ( has no )");
                return node;
            }
            if (Take('('))
            {
                var inner = Or();
                if (!Take(')')) throw new FormatException("a ( has no )");
                return inner;
            }
            SkipSpace();
            if (_at < _text.Length && _text[_at] == '/')
            {
                int end = _text.IndexOf('/', _at + 1);
                if (end < 0) throw new FormatException("a /regex/ has no closing /");
                string pattern = _text[(_at + 1)..end];
                _at = end + 1;
                try
                {
                    return new RegexNode(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20)));
                }
                catch (ArgumentException e)
                {
                    throw new FormatException("bad regex: " + e.Message);
                }
            }
            if (_at < _text.Length && _text[_at] == '"')
            {
                int end = _text.IndexOf('"', _at + 1);
                if (end < 0) throw new FormatException("a \" has no closing \"");
                string quoted = _text[(_at + 1)..end];
                _at = end + 1;
                return new TextNode(quoted);
            }
            int start = _at;
            while (_at < _text.Length && "&|()!,".IndexOf(_text[_at]) < 0) _at++;
            string atom = _text[start.._at].Trim();
            if (atom.Length == 0) throw new FormatException(AtEnd ? "the search ends too soon" : $"unexpected '{_text[_at]}'");
            return Atom(atom);
        }
    }

    private static readonly Regex s_tierWord = new(@"^t(\d+)([+-]?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_qualityWord = new(@"^q(\d+)\+?%?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_valueWord = new(@"^(\d+(?:\.\d+)?)%?(?:([+-])|-(\d+(?:\.\d+)?)%?)?$", RegexOptions.Compiled);

    // A word or phrase: the game's own word when the game knows it; else a stat by name with its numbers; else text.
    // Bare words (no number, tier, roll or position) also match the item's text, as the game's own search does: "lightning"
    // finds Melee Lightning Damage too, not only the stats the name ranks best for.
    private static BoxNode Atom(string atom)
    {
        if (GameExpression(atom) is { } game) return new GameNode(atom, game);
        if (StatAtom(atom) is not { } stat) return new TextNode(atom);
        if (!IsBare(stat)) return stat;
        var either = new AnyNode();
        either.Parts.Add(stat);
        either.Parts.Add(new TextNode(atom));
        return either;
    }

    private static bool IsBare(StatNode node) => node.Rows.All(r => r.Tier.IsEmpty && r.QualityMin == null && r.Position == RowPosition.Any
        && (r.Values.Count == 0 || r.Values.All(v => v.IsEmpty)) && r.Stat.Source is StatSource.Affix);

    /// <summary>The game's own reading of a search text (its macros and expressions), or null when it has none.</summary>
    private static ItemSearchExpression? GameExpression(string text)
    {
        try
        {
            ItemSearchExpression? expression = null;
            return ItemSearchExpression.TryGet(text, out expression) && expression != null ? expression : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // "fire res 30+", "t6+ health", "max life 80-120 q50", "prefix crit chance", "total res 150+", "implicit armor".
    private static StatNode? StatAtom(string atom)
    {
        var row = new StatRow();
        Range? value = null;
        bool? percent = null;
        var words = new List<string>();
        string? heading = null;
        foreach (string word in atom.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string w = word.ToLowerInvariant();
            Match m;
            if ((m = s_tierWord.Match(w)).Success)
            {
                row.Tier = Bound(m.Groups[1].Value, m.Groups[2].Value);
                continue;
            }
            if ((m = s_qualityWord.Match(w)).Success)
            {
                row.QualityMin = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                continue;
            }
            if ((m = s_valueWord.Match(w)).Success)
            {
                percent = w.Contains('%');
                value = m.Groups[3].Success
                    ? new Range(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture))
                    : Bound(m.Groups[1].Value, m.Groups[2].Value);
                continue;
            }
            switch (w)
            {
                case "%": percent = true; continue;
                case "flat": percent = false; continue;
                case "prefix": row.Position = RowPosition.Prefix; continue;
                case "suffix": row.Position = RowPosition.Suffix; continue;
                case "sealed": row.Position = RowPosition.Sealed; continue;
                case "implicit": heading = ImplicitHeading; continue;
                case "total": heading = TotalHeading; continue;
                case "roll" when words.Count > 0 && heading == null: heading = UniqueHeading; continue;
            }
            words.Add(w);
        }
        if (words.Count == 0) return null;
        var entries = BestEntries(words.ToArray(), heading);
        // A number with % takes the lines shown in %; a plain number the others, when the name fits both.
        if (percent is { } pct && entries.Count > 1)
        {
            var kept = entries.Where(e => e.Numbers.Length > 0 && e.Numbers[0].Unit.Contains('%') == pct).ToList();
            if (kept.Count > 0) entries = kept;
        }
        if (entries.Count == 0) return null;
        var rows = entries.Select(e =>
        {
            var r = new StatRow { Stat = e.Ref, Position = row.Position, Tier = row.Tier, QualityMin = row.QualityMin };
            if (value is { } v) r.Values = new List<Range> { v };
            return r;
        }).ToList();
        return new StatNode(rows, string.Join(" / ", entries.Select(e => e.Text)));
    }

    private static Range Bound(string number, string sign)
    {
        double n = double.Parse(number, CultureInfo.InvariantCulture);
        return sign == "+" ? new Range(n, null) : sign == "-" ? new Range(null, n) : new Range(n, n);
    }

    // The stats the words name best, as the picker ranks them (equal scores and lengths all kept, the "(+#)"/"(#%)" a
    // duplicate name carries aside); lines before totals unless "total" was said, totals when no line fits.
    private static List<CatalogueEntry> BestEntries(string[] words, string? heading)
    {
        var best = new List<CatalogueEntry>();
        int bestScore = 0, bestWords = int.MaxValue;
        foreach (var entry in Catalogue())
        {
            if (heading != null ? entry.Heading != heading : entry.Heading == TotalHeading) continue;
            int score = Rank(entry, words);
            if (score == 0) continue;
            int count = s_nameMark.Replace(entry.Text, string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (score > bestScore || score == bestScore && count < bestWords)
            {
                best.Clear();
                bestScore = score;
                bestWords = count;
            }
            if (score == bestScore && count == bestWords && !best.Contains(entry)) best.Add(entry);
        }
        return best.Count > 0 || heading != null ? best : BestEntries(words, TotalHeading);
    }

    private static readonly Regex s_nameMark = new(@"\s*\((?:\+#|#%|#\d+)\)", RegexOptions.Compiled);

    // ---- the game's search, handed over ----

    // The game takes no text or expression changed by a patch, so a ~ search calls the game's own search again with a
    // text every item passes (level requirement 0 or more) while it is marked live; the game then asks about each item
    // within that call, and the answers come from here. The text it keeps is set back to the ~ text afterwards.
    private const string StandText = "lvl0+";
    private static BoxNode? s_boxLive;
    private static string? s_boxText;
    private static BoxNode? s_boxLast;
    private static int s_boxAsks, s_boxItems, s_boxMatched;

    internal static bool BoxOn => s_feature != null && Has(SyntaxGroup) && StashSettings.SearchEnabled.Value && StashSettings.SearchBox.Value;

    /// <summary>The last ~ search, how it was read, and what the game asked (for the dev verb).</summary>
    internal static string BoxState() => (s_boxLast == null ? "no ~ search yet" : $"\"{s_boxText}\" read as {s_boxLast.Show()}")
        + $"; searches {s_boxAsks}, items asked {s_boxItems} (matched {s_boxMatched}); last read {s_boxParseMs:0.0} ms, game search {s_boxGameMs:0.0} ms";

    private static double s_boxParseMs, s_boxGameMs;
    // The names matched for the last ~ text, for the dev verb (cleared when the text changes).
    private static readonly HashSet<string> s_boxMatchedKeys = new();
    internal static IReadOnlyCollection<string> BoxMatchedNames => s_boxMatchedKeys;

    // Runs the game's search for a ~ text through the stand-in; false when the text isn't one (the game's own then runs).
    private static bool RunBox(string? search, Action<string> runGame)
    {
        if (s_boxLive != null || !BoxOn || search == null) return false;
        string text = search.TrimStart();
        if (!text.StartsWith(BoxPrefix, StringComparison.Ordinal))
        {
            SetBoxQuery(null, null);
            return false;
        }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var node = ParseBox(text[BoxPrefix.Length..], out string? error);
        if (node == null)
        {
            Log.WarnThrottled("search:box", $"Stash search: \"{text}\" can't be read: {error}", 10);
            node = new NothingNode();
        }
        s_boxParseMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000d / System.Diagnostics.Stopwatch.Frequency;
        if (text != s_boxText)
        {
            s_boxAsks = s_boxItems = s_boxMatched = 0;
            s_boxMatchedKeys.Clear();
        }
        s_boxAsks++;
        s_boxText = text;
        s_boxLast = node;
        SetBoxQuery(node, text);
        s_boxLive = node;
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            runGame(StandText);
        }
        finally
        {
            s_boxLive = null;
            s_boxGameMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t1) * 1000d / System.Diagnostics.Stopwatch.Frequency;
        }
        return true;
    }

    private sealed class NothingNode : BoxNode
    {
        public override bool Holds(BoxItem item) => false;
        public override string Show() => "nothing (unreadable)";
    }

    // Set while a ~ search checks an item: the game's own words inside it go to the game's own checks, which must not
    // come back here (that loops until the game's stack runs out).
    private static bool s_boxChecking;

    private static bool BoxMatches(ItemData item, string tooltip, ref bool result)
    {
        if (s_boxLive is not { } node || s_boxChecking) return true;
        s_boxChecking = true;
        try
        {
            var data = item.TryCast<ItemDataUnpacked>() ?? item.getAsUnpacked();
            result = data != null && node.Holds(new BoxItem(data, tooltip ?? string.Empty));
            s_boxItems++;
            if (result) { s_boxMatched++; s_boxMatchedKeys.Add(data!.FullName ?? "?"); }
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("search:boxmatch", $"Stash search: a ~ search failed on an item: {e.Message}", 30);
            result = false;
        }
        finally
        {
            s_boxChecking = false;
        }
        return false;
    }

    // The stash grid's search.
    [HarmonyPatch(typeof(ItemContainerUIWithSearch), nameof(ItemContainerUIWithSearch.PerformSearch))]
    internal static class BoxGridPatch
    {
        private static bool Prefix(ItemContainerUIWithSearch __instance, string __0)
        {
            try
            {
                if (!RunBox(__0, stand => __instance.PerformSearch(stand))) return true;
                __instance.lastSearch = __0;
                return false;
            }
            catch (Exception e)
            {
                s_boxLive = null;
                Log.ErrorThrottled("search:boxgrid", $"Stash search: the stash's search box text wasn't read: {e.Message}", 30);
                return true;
            }
        }
    }

    // Quick View's tab filter.
    [HarmonyPatch(typeof(StashTabbedUIControls), nameof(StashTabbedUIControls.FilterVerticalSearch))]
    internal static class BoxQuickViewPatch
    {
        private static bool Prefix(StashTabbedUIControls __instance, string __0)
        {
            try
            {
                return !RunBox(__0, stand => __instance.FilterVerticalSearch(stand));
            }
            catch (Exception e)
            {
                s_boxLive = null;
                Log.ErrorThrottled("search:boxquick", $"Stash search: Quick View's search text wasn't read: {e.Message}", 30);
                return true;
            }
        }
    }

    // The game asks per item through the expression, or through the macro when the text is a single word.
    [HarmonyPatch(typeof(ItemSearchExpression), nameof(ItemSearchExpression.ItemMatches))]
    internal static class BoxMatchPatch
    {
        private static bool Prefix(ItemData __0, string __1, ref bool __result) => BoxMatches(__0, __1, ref __result);
    }

    [HarmonyPatch(typeof(ItemSearchMacro), nameof(ItemSearchMacro.ItemMatches))]
    internal static class BoxMacroMatchPatch
    {
        private static bool Prefix(ItemData __0, string __1, ref bool __result) => BoxMatches(__0, __1, ref __result);
    }

    // ---- the ~ search's results, for the Filter panel ----

    // The ~ search in the game's box, checked against the facts cache like the page's search, in its own slices; the
    // game's own words in it go to the game with the item the cache points at.
    private static BoxNode? s_boxQuery;
    private static string? s_boxQueryText;
    private static int s_boxGeneration, s_boxFactsGeneration = -1, s_boxVersion;
    private static readonly Dictionary<string, (bool Match, string? Badge)> s_boxMatches = new();
    private static readonly Queue<string> s_boxQueue = new();
    // Items read again since the ~ search last looked (the facts pass adds them).
    private static readonly HashSet<string> s_boxChanged = new();
    private static Dictionary<string, ItemPlace>? s_boxPlaces;
    private static List<ItemPlace>? s_boxPlacesFrom;
    private static readonly HashSet<string> s_boxUnplaced = new();
    private static List<ResultFolder>? s_boxGrouped;
    private static (int Generation, int Facts, int Version) s_boxGroupedFor;
    private static float s_boxGroupedAt;

    /// <summary>A ~ search is in the game's box: the Filter panel shows its results.</summary>
    internal static bool BoxQueryShown => s_boxQuery != null;

    internal static bool BoxMatching => s_boxQuery != null && StashSettings.SearchPanel.Value && (s_boxQueue.Count > 0 || s_boxFactsGeneration != s_factsGeneration);

    internal static string? BoxQueryText => s_boxQueryText;

    // For the dev verb: items the panel's ~ search skipped (no facts, no place, no game item) and checked.
    private static readonly int[] s_boxMiss = new int[4];
    internal static string BoxRunState() => $"panel query {(s_boxQuery == null ? "none" : $"\"{s_boxQueryText}\"")}, queue {s_boxQueue.Count}, "
        + $"matches {s_boxMatches.Count(m => m.Value.Match)} of {s_boxMatches.Count}, facts {s_facts.Count}, places {s_places.Count}; "
        + $"no facts {s_boxMiss[0]}, no place {s_boxMiss[1]}, no item {s_boxMiss[2]}, checked {s_boxMiss[3]}";

    private static void SetBoxQuery(BoxNode? node, string? text)
    {
        if (text == s_boxQueryText && (node == null) == (s_boxQuery == null)) return;
        s_boxQuery = node;
        s_boxQueryText = text;
        s_boxGeneration++;
        s_boxMatches.Clear();
        s_boxQueue.Clear();
        s_boxChanged.Clear();
        s_boxUnplaced.Clear();
        s_boxFactsGeneration = -1;
        s_boxGrouped = null;
    }

    private static void BoxSlice()
    {
        // The panel is the only thing that shows these results.
        if (s_boxQuery is not { } node || !StashSettings.SearchPanel.Value) return;
        if (s_boxFactsGeneration != s_factsGeneration)
        {
            if (s_boxFactsGeneration < 0)
            {
                foreach (var key in s_facts.Keys) s_boxQueue.Enqueue(key);
            }
            else
            {
                foreach (var key in s_boxChanged)
                {
                    if (s_facts.ContainsKey(key)) s_boxQueue.Enqueue(key);
                    else s_boxMatches.Remove(key);
                }
            }
            s_boxChanged.Clear();
            s_boxFactsGeneration = s_factsGeneration;
            s_boxPlaces = null;
        }
        if (s_boxQueue.Count == 0) return;
        s_boxVersion++;
        // The place list is replaced as passes finish: the map follows it.
        if (s_boxPlaces == null || !ReferenceEquals(s_boxPlacesFrom, s_places))
        {
            s_boxPlacesFrom = s_places;
            s_boxPlaces = new Dictionary<string, ItemPlace>();
            foreach (var place in s_places) s_boxPlaces.TryAdd(place.Key, place);
            // Items that had no place yet (the stash still being read) are checked now.
            foreach (var key in s_boxUnplaced) s_boxQueue.Enqueue(key);
            s_boxUnplaced.Clear();
        }
        long until = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(MatchMsPerFrame * System.Diagnostics.Stopwatch.Frequency / 1000d);
        while (s_boxQueue.Count > 0)
        {
            string key = s_boxQueue.Dequeue();
            bool match = false;
            if (!s_facts.TryGetValue(key, out var facts)) s_boxMiss[0]++;
            else if (!s_boxPlaces.TryGetValue(key, out var place))
            {
                // Not scored: checked once the place list has it.
                s_boxMiss[1]++;
                s_boxUnplaced.Add(key);
                continue;
            }
            else if (ItemAt(place) is not { } data) s_boxMiss[2]++;
            else
            {
                s_boxMiss[3]++;
                try
                {
                    // Text atoms read the item's name and lines, as the tooltip shows them.
                    string text = facts.Name + "\n" + string.Join("\n", facts.Stats.Select(f => f.Text));
                    match = node.Holds(new BoxItem(data, text.ToLowerInvariant(), facts));
                }
                catch (Exception e)
                {
                    Log.ErrorThrottled("search:boxslice", $"Stash search: a ~ search failed on an item: {e.Message}", 30);
                }
            }
            s_boxMatches[key] = (match, null);
            if (System.Diagnostics.Stopwatch.GetTimestamp() > until) break;
        }
    }

    /// <summary>The ~ search's results, grouped as the game orders them.</summary>
    internal static IReadOnlyList<ResultFolder> BoxGrouped()
    {
        var snap = s_snap;
        if (s_boxQuery == null || snap == null) return Array.Empty<ResultFolder>();
        var stamp = (s_boxGeneration, s_factsGeneration, s_boxVersion);
        if (s_boxGrouped != null && (s_boxGroupedFor == stamp || BoxMatching && s_boxGroupedFor.Generation == s_boxGeneration && Time.unscaledTime < s_boxGroupedAt))
            return s_boxGrouped;
        s_boxGroupedAt = Time.unscaledTime + 0.25f;
        s_boxGrouped = GroupMatches(s_boxMatches, snap);
        s_boxGroupedFor = stamp;
        return s_boxGrouped;
    }

    internal static string BoxHeader(IReadOnlyList<ResultFolder> groups)
    {
        int count = groups.Sum(f => f.Count);
        if (s_boxQuery is NothingNode) return "Can't read this ~ search";
        if (BoxMatching || s_pass != null && s_passNew > 0) return $"Searching… {count} so far";
        return count == 0 ? "No items match" : count == 1 ? "1 item" : $"{count} items";
    }

    /// <summary>Ends the ~ search: both of the game's boxes are emptied (its own clear empties only Quick View's, and the ~
    /// text left in the other brought the panel straight back).</summary>
    internal static void EndBoxSearch()
    {
        SetBoxQuery(null, null);
        var ui = SceneObjects.Find<StashTabbedUIControls>();
        if (!Has(BoxGroup) || !Game.Alive(ui)) return;
        ui!.ClearSearchField();
        if (Game.Alive(ui.primarySearchBar)) ui.primarySearchBar.text = string.Empty;
        if (Game.Alive(ui.verticalSearchBar)) ui.verticalSearchBar.text = string.Empty;
    }

    // Follows the game's boxes twice a second: a ~ search already there (after a reload) shows, one cleared by the
    // boxes' own button ends.
    private static void CheckBoxText(StashTabbedUIControls ui)
    {
        if (!BoxOn) return;
        string Text(Il2CppTMPro.TMP_InputField? f) => Game.Alive(f) ? (f!.text ?? string.Empty).Trim() : string.Empty;
        string text = Text(ui.primarySearchBar);
        if (!text.StartsWith(BoxPrefix, StringComparison.Ordinal)) text = Text(ui.verticalSearchBar);
        if (!text.StartsWith(BoxPrefix, StringComparison.Ordinal))
        {
            SetBoxQuery(null, null);
            return;
        }
        if (text == s_boxQueryText) return;
        var node = ParseBox(text[BoxPrefix.Length..], out _) ?? new NothingNode();
        s_boxText = text;
        s_boxLast = node;
        SetBoxQuery(node, text);
    }
}
