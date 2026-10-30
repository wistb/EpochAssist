using System.Text;
using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

/// <summary>Word-prefix matching and marking, for search and filters: every query word must start a word of the text.</summary>
internal static class MenuSearchText
{
    public static string[] Words(string query) => query.ToLowerInvariant().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

    public static bool Matches(string text, string[] words)
    {
        if (words.Length == 0) return true;
        string lower = text.ToLowerInvariant();
        foreach (string word in words)
        {
            if (!StartsAWord(lower, word)) return false;
        }
        return true;
    }

    private static bool StartsAWord(string text, string word)
    {
        int at = 0;
        while ((at = text.IndexOf(word, at, StringComparison.Ordinal)) >= 0)
        {
            if (at == 0 || !char.IsLetterOrDigit(text[at - 1])) return true;
            at++;
        }
        return false;
    }

    /// <summary>The text with each word start that a query word matches in the accent colour.</summary>
    public static string Mark(string text, string[] words)
    {
        if (words.Length == 0 || !Widgets.Themed) return text;
        string lower = text.ToLowerInvariant();
        var marked = new bool[text.Length];
        foreach (string word in words)
        {
            int at = 0;
            while ((at = lower.IndexOf(word, at, StringComparison.Ordinal)) >= 0)
            {
                if (at == 0 || !char.IsLetterOrDigit(lower[at - 1]))
                {
                    for (int i = at; i < at + word.Length; i++) marked[i] = true;
                }
                at++;
            }
        }
        string open = $"<color=#{ColorUtility.ToHtmlStringRGB(Theme.HighlightText)}>";
        var result = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            if (marked[i] && (i == 0 || !marked[i - 1])) result.Append(open);
            result.Append(text[i]);
            if (marked[i] && (i == text.Length - 1 || !marked[i + 1])) result.Append("</color>");
        }
        return result.ToString();
    }
}

/// <summary>A search hit: a node, a page, or a row on a page.</summary>
internal sealed record SearchHit(MenuNode Node, PageNode? Page, MenuRow? Row);

/// <summary>Text a feature offers the search from a view of its own that isn't a menu page (the Planner's guide): its
/// path line, the text, and what opening it does.</summary>
internal sealed record SearchEntry(string Path, string Text, Action Open);

/// <summary>
/// Search over every node, page, heading and row in the menu, through the same texts each row's renderer draws. The index
/// is rebuilt with the tree; a query runs only when its text changes. Results keep tree order.
/// </summary>
internal static class MenuSearch
{
    private sealed record Entry(SearchHit Hit, string[] Texts);

    private static List<Entry> s_index = new();
    private static int s_indexed = -1;
    private static string? s_query;
    private static List<SearchHit> s_results = new();

    private static void Ensure()
    {
        // Reading the pages builds the tree first, so its generation is current.
        _ = MenuTree.Pages;
        if (s_indexed == MenuTree.Generation) return;
        s_indexed = MenuTree.Generation;
        s_query = null;
        var index = new List<Entry>();
        foreach (var node in MenuTree.Tabs.SelectMany(t => t.Nodes))
        {
            index.Add(new Entry(new SearchHit(node, null, null), new[] { node.Title }));
            foreach (var page in node.Pages)
            {
                if (!node.Leaf) index.Add(new Entry(new SearchHit(node, page, null), new[] { page.Page.Title }));
                foreach (var row in Flatten(page.Rows))
                {
                    if (PageNode.SettingsOf(row).Any(s => s.Section.Group.Shared || !SettingsRegistry.IsShown(s.Section)) || RowKinds.For(row) is not { } renderer) continue;
                    var texts = Safe.Call(() => renderer.Search(row).Where(t => !string.IsNullOrEmpty(t)).ToArray(), Array.Empty<string>(), row.Key + ".search");
                    if (texts.Length > 0) index.Add(new Entry(new SearchHit(node, page, row), texts));
                }
            }
        }
        s_index = index;
    }

    // A page's rows with containers (cards) opened, so each setting in a card is its own hit.
    private static IEnumerable<MenuRow> Flatten(IEnumerable<MenuRow> rows) =>
        rows.SelectMany(row => row is IRowContainer container ? Flatten(container.Children) : new[] { row });

    /// <summary>The hits for a query, in tree order: nodes, pages and rows whose texts match every word.</summary>
    public static IReadOnlyList<SearchHit> Query(string query)
    {
        Ensure();
        string q = query.Trim();
        if (q == s_query) return s_results;
        s_query = q;
        string[] words = MenuSearchText.Words(q);
        // A row its page hides (a module's Mini HUD switch while the module isn't loaded) is no hit.
        s_results = words.Length == 0 ? new List<SearchHit>()
            : s_index.Where(e => e.Texts.Any(t => MenuSearchText.Matches(t, words)) && (e.Hit.Row == null || Safe.Visible(e.Hit.Row))).Select(e => e.Hit).ToList();
        return s_results;
    }

    public static void Reset()
    {
        s_index = new List<Entry>();
        s_indexed = -1;
        s_query = null;
        s_results = new List<SearchHit>();
        s_sourceQuery = null;
    }

    // ---- Sources: text from feature views that aren't menu pages ----

    private sealed class Source
    {
        public Func<int> Generation = null!;
        public Func<IReadOnlyList<SearchEntry>> Entries = null!;
        public int Read = int.MinValue;
        public IReadOnlyList<SearchEntry> Cached = Array.Empty<SearchEntry>();
    }

    private static readonly Dictionary<string, Source> s_sources = new();
    private static string? s_sourceQuery;
    private static List<SearchEntry> s_sourceResults = new();

    /// <summary>Adds a feature's search source (replacing one by the same id); its entries are read again whenever its
    /// generation changes.</summary>
    public static void AddSource(string id, Func<int> generation, Func<IReadOnlyList<SearchEntry>> entries)
    {
        s_sources[id] = new Source { Generation = generation, Entries = entries };
        s_sourceQuery = null;
    }

    public static void RemoveSource(string id)
    {
        if (s_sources.Remove(id)) s_sourceQuery = null;
    }

    /// <summary>The sources' entries whose text matches every word of the query, after the tree's hits.</summary>
    public static IReadOnlyList<SearchEntry> QuerySources(string query)
    {
        string q = query.Trim();
        bool changed = false;
        foreach (var (id, source) in s_sources)
        {
            int generation = Safe.Call(source.Generation, source.Read, "search.source." + id);
            if (generation == source.Read) continue;
            source.Read = generation;
            source.Cached = Safe.Call(source.Entries, Array.Empty<SearchEntry>(), "search.source." + id);
            changed = true;
        }
        if (!changed && q == s_sourceQuery) return s_sourceResults;
        s_sourceQuery = q;
        string[] words = MenuSearchText.Words(q);
        s_sourceResults = words.Length == 0 ? new List<SearchEntry>()
            : s_sources.Values.SelectMany(source => source.Cached).Where(e => MenuSearchText.Matches(e.Text, words)).ToList();
        return s_sourceResults;
    }

    /// <summary>A hit's path line: tab › folder › node › page, with (unavailable) for a feature in that state.</summary>
    public static string PathLine(SearchHit hit)
    {
        string line = hit.Page != null && !hit.Node.Leaf ? $"{hit.Node.Crumb} › {hit.Page.Page.Title}" : hit.Node.Crumb;
        return hit.Node.Feature is { } feature ? line + StateNote(feature) : line;
    }

    public static string StateNote(Feature feature) => feature.State == FeatureState.Unavailable ? " (unavailable)" : string.Empty;
}
