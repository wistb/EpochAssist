using EpochAssist.Core;

namespace EpochAssist.Ui;

/// <summary>
/// Which page the menu shows, by path, and the way there: the last page shown for each node and in General, paths
/// matched by slug or unique prefix, next and previous page in tree order, and the old page names
/// (menu.page.&lt;featureId&gt;, menu.page.settings, the old Settings tab's paths and indexes).
/// </summary>
internal static class MenuRoutes
{
    private const string General = "combat/general";
    public const string MiniHud = General + "/interface/mini-hud";
    public const string Console = General + "/diagnostics/console";
    public const string Overview = General + "/diagnostics/overview";
    public const string Profiles = General + "/profiles";

    // The old Settings tabs, by index: Mini HUD, Appearance, Keybinds, Diagnostics › Overview, Profiles.
    private static readonly string[] s_settingsTabs = { MiniHud, General + "/interface/appearance", General + "/keybinds", Overview, Profiles };

    private static string s_path = string.Empty;
    private static readonly Dictionary<string, string> s_lastByNode = new();
    private static readonly Dictionary<string, string> s_lastByTab = new();
    private static string? s_lastSettings;

    /// <summary>Raised when the page changes, for the page's fade and scroll.</summary>
    public static int Changes { get; private set; }

    /// <summary>The page shown: the chosen one while it exists, otherwise the first.</summary>
    public static PageNode? Page
    {
        get
        {
            var pages = MenuTree.Pages;
            return pages.FirstOrDefault(p => p.Path == s_path) ?? pages.FirstOrDefault();
        }
    }

    public static string Path => Page?.Path ?? string.Empty;

    public static void Reset()
    {
        s_path = string.Empty;
        s_lastByNode.Clear();
        s_lastByTab.Clear();
        s_lastSettings = null;
    }

    /// <summary>Shows a page (exact path).</summary>
    public static void Show(PageNode page)
    {
        if (page.Path != s_path) Changes++;
        s_path = page.Path;
        s_lastByNode[page.Node.Path] = page.Path;
        s_lastByTab[page.Node.Tab.Id] = page.Path;
        if (page.Node.Folder == MenuEntry.General) s_lastSettings = page.Path;
    }

    /// <summary>A page by path, matched by slug with a unique prefix allowed for each part; null when none or several match.</summary>
    public static PageNode? Resolve(string path)
    {
        string[] parts = path.Trim().Trim('/').ToLowerInvariant().Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        // The old Settings tab's paths ("settings/profiles") are General's now.
        if (parts[0] == "settings") parts = General.Split('/').Concat(parts.Skip(1)).ToArray();
        var exact = MenuTree.Find(string.Join("/", parts));
        if (exact != null) return exact;
        // A node alone opens its last or first page.
        var candidates = MenuTree.Pages.Where(p => Matches(p.Path.Split('/'), parts)).ToList();
        if (candidates.Count == 1) return candidates[0];
        var nodes = candidates.Select(p => p.Node).Distinct().ToList();
        if (nodes.Count == 1 && parts.Length <= nodes[0].Path.Split('/').Length) return First(nodes[0]);
        return null;
    }

    // Each typed part is the start of the page's part at that place (equal parts count first, via Find).
    private static bool Matches(string[] page, string[] typed)
    {
        if (typed.Length > page.Length) return false;
        for (int i = 0; i < typed.Length; i++)
        {
            // The part's start, or the start of one of its words ("gate" finds "enemy-gate").
            string part = MenuEntry.Slug(typed[i]);
            if (!page[i].StartsWith(part, StringComparison.Ordinal) && !page[i].Split('-').Any(w => w.StartsWith(part, StringComparison.Ordinal))) return false;
        }
        // A node's path (two parts, three in a folder) names the node; more parts must reach a page.
        return typed.Length >= 2 || page.Length == typed.Length;
    }

    /// <summary>A node's last page shown, else its first.</summary>
    public static PageNode? First(MenuNode node) =>
        s_lastByNode.TryGetValue(node.Path, out string? last) && node.Pages.FirstOrDefault(p => p.Path == last) is { } page ? page : node.Pages.FirstOrDefault();

    /// <summary>A feature's page: its last shown, else its first.</summary>
    public static PageNode? ForFeature(string featureId) =>
        FeatureHost.Find(featureId) is { } feature && MenuTree.NodeOf(feature) is { } node ? First(node) : null;

    /// <summary>General's last page shown, else Profiles.</summary>
    public static PageNode? Settings() => (s_lastSettings != null ? MenuTree.Find(s_lastSettings) : null) ?? MenuTree.Find(Profiles);

    /// <summary>An old Settings tab index (0–4).</summary>
    public static PageNode? SettingsTab(int index) => MenuTree.Find(s_settingsTabs[Math.Clamp(index, 0, s_settingsTabs.Length - 1)]);

    /// <summary>A tab's last page shown, else its first node's first page.</summary>
    public static PageNode? ForTab(MenuTab tab)
    {
        if (s_lastByTab.TryGetValue(tab.Id, out string? last) && MenuTree.Find(last) is { } page) return page;
        return tab.Nodes.Select(First).FirstOrDefault(p => p != null);
    }

    /// <summary>The page before or after the shown one in tree order, across tabs; null at either end.</summary>
    public static PageNode? Next(int step)
    {
        var pages = MenuTree.Pages;
        int at = pages.ToList().FindIndex(p => p.Path == Path);
        int next = at + step;
        return next >= 0 && next < pages.Count ? pages[next] : null;
    }
}
