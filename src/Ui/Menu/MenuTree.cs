using EpochAssist.Core;

namespace EpochAssist.Ui;

/// <summary>A top tab and the nodes in it.</summary>
internal sealed class MenuTab
{
    public MenuTab(string id, string title)
    {
        Id = id;
        Title = title;
    }

    public string Id { get; }
    public string Title { get; }
    public List<MenuNode> Nodes { get; } = new();
}

/// <summary>A node in the tree: a feature's entry (or one of General's), with its pages.</summary>
internal sealed class MenuNode
{
    public MenuNode(MenuTab tab, MenuEntry entry, Feature? feature, string title)
    {
        Tab = tab;
        Entry = entry;
        Feature = feature;
        Title = title;
        FolderPath = entry.Folder != null ? $"{tab.Id}/{MenuEntry.Slug(entry.Folder)}" : null;
        Path = $"{FolderPath ?? tab.Id}/{MenuEntry.Slug(title)}";
    }

    public MenuTab Tab { get; }
    public MenuEntry Entry { get; }
    /// <summary>The feature the entry belongs to; null for the menu's own entries.</summary>
    public Feature? Feature { get; }
    public string Title { get; }
    public string Path { get; }
    /// <summary>The folder the node sits in (General), or null.</summary>
    public string? Folder => Entry.Folder;
    /// <summary>The folder's path (its tree key), or null.</summary>
    public string? FolderPath { get; }
    /// <summary>"Config › General", the node's place without its own title.</summary>
    public string Place => Folder != null ? $"{Tab.Title} › {Folder}" : Tab.Title;
    /// <summary>"Config › General".</summary>
    public string Crumb => $"{Place} › {Title}";
    public List<PageNode> Pages { get; } = new();
    /// <summary>One page with the node's own title: the node is the page, with nothing to expand.</summary>
    public bool Leaf => Pages.Count == 1 && Entry.Groups.Count == 0 && Pages[0].Page.Title == Title;
    /// <summary>The feature runs (or there is no feature): live rows show.</summary>
    public bool Live => Feature == null || Feature.State == FeatureState.Running;
}

/// <summary>A page with its rows resolved: a section's settings become rows, its folds headings.</summary>
internal sealed class PageNode
{
    public PageNode(MenuNode node, MenuPage page)
    {
        Node = node;
        Page = page;
        Path = node.Leaf ? node.Path : $"{node.Path}/{MenuEntry.Slug(page.Title)}";
    }

    public MenuNode Node { get; }
    public MenuPage Page { get; }
    public string Path { get; internal set; }
    public List<MenuRow> Rows { get; } = new();
    /// <summary>A declaration problem shown on the page, or null.</summary>
    public string? Problem { get; internal set; }

    /// <summary>The sections this page's settings come from, for Reset.</summary>
    public IEnumerable<Section> Sections => Rows.SelectMany(SettingsOf).Select(s => s.Section).Distinct();

    public bool Resettable => Page.Resettable ?? Sections.Any();

    public static IEnumerable<Setting> SettingsOf(MenuRow row) => row switch
    {
        SettingRow s => new[] { s.Setting },
        NumberGroup g => g.Numbers,
        IRowContainer c => c.Children.SelectMany(SettingsOf),
        _ => Array.Empty<Setting>(),
    };

    /// <summary>"Config › Macros", for the breadcrumb.</summary>
    public string Crumb => Node.Leaf ? Node.Place : Node.Crumb;
}

/// <summary>
/// The menu's tree, built from every feature's <see cref="Feature.Menu"/> and General's own declarations, in the
/// fixed tab order. Paths are tab/node/page slugs (tab/folder/node/page in a folder). Declaration problems (a duplicate
/// path, an unknown tab or block, a row kind with no renderer, a section from another group, a switch without a quick
/// label) are logged once, listed in Diagnostics and shown on the page they break; every other page works.
/// </summary>
internal static class MenuTree
{
    private static readonly (string Id, string Title)[] s_tabs =
    {
        (MenuCategory.Combat, "Config"), (MenuCategory.Macros, "Macros"), (MenuCategory.Planner, "Planner"), (MenuCategory.Sessions, "Session"), (MenuCategory.Overlays, "Overlays"), (MenuCategory.Map, "Map"),
        (MenuCategory.Visuals, "Visuals"), (MenuCategory.Stats, "Stats"),
    };

    private static List<MenuTab> s_built = new();
    private static List<PageNode> s_pages = new();
    private static MenuEntry?[] s_seen = Array.Empty<MenuEntry?>();
    private static readonly List<string> s_problems = new();
    private static readonly HashSet<string> s_logged = new();
    private static int s_generation;

    /// <summary>Tabs with nodes, in order.</summary>
    public static IReadOnlyList<MenuTab> Tabs
    {
        get
        {
            Ensure();
            return s_built;
        }
    }

    /// <summary>Every page in tree order, across tabs.</summary>
    public static IReadOnlyList<PageNode> Pages
    {
        get
        {
            Ensure();
            return s_pages;
        }
    }

    public static IReadOnlyList<string> Problems => s_problems;

    /// <summary>Counts rebuilds, so the search index knows when to rebuild.</summary>
    public static int Generation => s_generation;

    public static PageNode? Find(string path) => Pages.FirstOrDefault(p => p.Path == path);

    public static MenuNode? NodeOf(Feature feature) =>
        Tabs.SelectMany(t => t.Nodes).FirstOrDefault(n => n.Feature == feature && ReferenceEquals(n.Entry, feature.Menu))
        ?? Tabs.SelectMany(t => t.Nodes).FirstOrDefault(n => n.Feature == feature);

    /// <summary>The node of a feature's Config entry, or null.</summary>
    public static MenuNode? ConfigNodeOf(Feature feature) =>
        feature.ConfigMenu == null ? null : Tabs.SelectMany(t => t.Nodes).FirstOrDefault(n => ReferenceEquals(n.Entry, feature.ConfigMenu));

    /// <summary>Forgets the tree; the next use builds it again (the menu opening, shutdown).</summary>
    public static void Invalidate() => s_seen = Array.Empty<MenuEntry?>();

    // Rebuilt when a feature's entry changes (or the first time).
    private static void Ensure()
    {
        var features = FeatureHost.All;
        bool same = s_seen.Length == features.Count;
        for (int i = 0; same && i < features.Count; i++) same = ReferenceEquals(s_seen[i], features[i].Menu);
        if (same && s_built.Count > 0) return;
        s_seen = features.Select(f => f.Menu).ToArray();
        Build();
    }

    private static void Build()
    {
        s_generation++;
        s_problems.Clear();
        var tabs = s_tabs.Select(t => new MenuTab(t.Id, t.Title)).ToList();
        var entries = FeatureHost.All.Where(f => f.Menu != null).Select(f => (Entry: f.Menu!, Feature: (Feature?)f, Title: f.Menu!.Title ?? f.Name))
            .Concat(FeatureHost.All.Where(f => f.ConfigMenu != null).Select(f => (Entry: f.ConfigMenu!, Feature: (Feature?)f, Title: f.ConfigMenu!.Title ?? f.Name)))
            .Concat(SettingsContent.Entries.Select(e => (Entry: e, Feature: (Feature?)null, Title: e.Title ?? string.Empty)))
            .Select((e, index) => (e.Entry, e.Feature, e.Title, Index: index))
            .OrderBy(e => e.Entry.Order).ThenBy(e => e.Index)
            // A folder's entries sit together, at its first entry's place.
            .GroupBy(e => e.Entry.Folder != null ? $"{e.Entry.Category}/{e.Entry.Folder}" : $"#{e.Index}")
            .SelectMany(g => g);
        foreach (var (entry, feature, title, _) in entries)
        {
            var tab = tabs.FirstOrDefault(t => t.Id == entry.Category);
            if (tab == null)
            {
                Problem($"{title}: unknown tab '{entry.Category}'");
                continue;
            }
            var node = new MenuNode(tab, entry, feature, title);
            if (entry.Switch != null && entry.Switch.QuickLabel == null) Problem($"{title}: its switch {entry.Switch.Id} has no quick label");
            if (entry.Pages.Count == 0) Problem($"{title}: no pages");
            if (entry.Layout == MenuLayout.Library && entry.Library == null) Problem($"{title}: library layout without its view");
            foreach (var page in entry.Pages) node.Pages.Add(new PageNode(node, page));
            // Paths read the node's leaf state, which needs every page added first.
            foreach (var page in node.Pages) page.Path = node.Leaf ? node.Path : $"{node.Path}/{MenuEntry.Slug(page.Page.Title)}";
            tab.Nodes.Add(node);
        }

        var pages = new List<PageNode>();
        var paths = new HashSet<string>();
        foreach (var page in tabs.SelectMany(t => t.Nodes).SelectMany(n => n.Pages))
        {
            Resolve(page);
            if (!paths.Add(page.Path)) PageProblem(page, $"another page has the path {page.Path}");
            pages.Add(page);
        }
        s_built = tabs.Where(t => t.Nodes.Count > 0).ToList();
        s_pages = s_built.SelectMany(t => t.Nodes).SelectMany(n => n.Pages).ToList();
    }

    // The page's rows: its own, or its section's settings in order with each fold as a heading (and a number group when
    // the fold is all numbers). Then the checks.
    private static void Resolve(PageNode node)
    {
        var page = node.Page;
        node.Rows.Clear();
        if (page.Rows.Count > 0 || page.Section == null)
        {
            node.Rows.AddRange(page.Rows);
        }
        else
        {
            string? fold = null;
            var settings = page.Section.Settings;
            for (int i = 0; i < settings.Count; i++)
            {
                var setting = settings[i];
                if (setting.Fold == null)
                {
                    fold = null;
                    node.Rows.Add(new SettingRow(setting));
                    continue;
                }
                if (setting.Fold == fold) continue;
                fold = setting.Fold;
                string name = fold;
                var members = settings.Skip(i).TakeWhile(s => s.Fold == name).ToList();
                node.Rows.Add(new HeadingRow(name) { VisibleWhen = () => members.Any(m => m.Visible) });
                if (members.All(m => m is Number)) node.Rows.Add(new NumberGroup(members.Cast<Number>().ToList(), name));
                else node.Rows.AddRange(members.Select(m => new SettingRow(m)));
            }
        }

        var group = node.Node.Pages.Select(p => p.Page.Section).FirstOrDefault(s => s != null)?.Group;
        if (page.Section != null && group != null && page.Section.Group != group) PageProblem(node, $"section {page.Section.Id} is from another settings group");
        if (page.Switch != null && page.Switch.QuickLabel == null) PageProblem(node, $"its switch {page.Switch.Id} has no quick label");
        if (page.Block != null && !MenuBlocks.Has(page.Block)) PageProblem(node, $"no block named {page.Block}");
        foreach (var row in node.Node.Entry.Top.Concat(node.Rows).Concat(page.Actions))
        {
            if (RowKinds.For(row) == null) PageProblem(node, $"no renderer for {row.GetType().Name} rows");
            else if (row is BlockRow block && !MenuBlocks.Has(block.Block)) PageProblem(node, $"no block named {block.Block}");
        }
    }

    private static void PageProblem(PageNode page, string problem)
    {
        page.Problem ??= problem;
        Problem($"{page.Path}: {problem}");
    }

    private static void Problem(string text)
    {
        s_problems.Add(text);
        if (s_logged.Add(text)) Log.Warn("Menu: " + text);
    }

    public static string TitleOf(string tab) => s_tabs.FirstOrDefault(t => t.Id == tab).Title ?? tab;
}
