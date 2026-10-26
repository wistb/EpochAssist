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
                    if (PageNode.SettingsOf(row).Any(s => s.Section.Group.Shared) || RowKinds.For(row) is not { } renderer) continue;
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
        s_results = words.Length == 0 ? new List<SearchHit>() : s_index.Where(e => e.Texts.Any(t => MenuSearchText.Matches(t, words))).Select(e => e.Hit).ToList();
        return s_results;
    }

    public static void Reset()
    {
        s_index = new List<Entry>();
        s_indexed = -1;
        s_query = null;
        s_results = new List<SearchHit>();
    }

    /// <summary>A hit's path line: tab › folder › node › page, with (off) or (unavailable) for features in those states.</summary>
    public static string PathLine(SearchHit hit)
    {
        string line = hit.Page != null && !hit.Node.Leaf ? $"{hit.Node.Crumb} › {hit.Page.Page.Title}" : hit.Node.Crumb;
        return hit.Node.Feature is { } feature ? line + StateNote(feature) : line;
    }

    public static string StateNote(Feature feature) => feature.Record.TurnedOff ? " (off)" : feature.State == FeatureState.Unavailable ? " (unavailable)" : string.Empty;
}
