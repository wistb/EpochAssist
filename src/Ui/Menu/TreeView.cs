using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

// The compact tree on the left (200 px): a collapse-all button and the menu's search, then the shown tab's folders and
// nodes, each node with its groups, items and pages on 20 px rows. The search shows its results across every tab on the
// page and narrows the tree to the names that match. A node carries its feature's on/off dot; a node that isn't running
// is dimmed and shows its state's shape. The page shown has the selection fill. Left and Right close and open folders
// and nodes, or move to the parent or first child.
internal static class TreeView
{
    public const float Width = 200f;
    private const float RowHeight = 20f, Indent = 12f, FilterHeight = 22f, OpenSeconds = 0.15f;
    private const string SearchControl = "EpochAssistSearch";

    /// <summary>Set by Ctrl+F: the search field takes the keyboard at the next pass.</summary>
    public static bool FocusSearch;

    // Set by Esc: the search field lets go of the keyboard at the next pass (focus can only change inside OnGUI).
    private static bool s_blurSearch;

    public static bool SearchFocused => GUI.GetNameOfFocusedControl() == SearchControl;

    private enum Kind { Folder, Node, Group, Item, Page }

    private sealed record Row(Kind Kind, int Depth, string Key, string Label, MenuNode Node, PageNode? Page, TreeItem? Item, string? Parent, float Open);

    private static Vector2 s_scroll;
    // Closed nodes and groups, by key (all start open), and how far each is open (0 to 1) for the motion.
    private static readonly HashSet<string> s_closed = new();
    private static readonly Dictionary<string, float> s_open = new();

    public static void Reset()
    {
        s_scroll = Vector2.zero;
        s_open.Clear();
    }

    /// <summary>On opening the menu: the shown page's node (and its folder) opens.</summary>
    public static void Reveal(PageNode? page)
    {
        if (page == null) return;
        s_closed.Remove(page.Node.Path);
        if (page.Node.FolderPath is { } folder) s_closed.Remove(folder);
    }

    private static bool Closed(string key) => s_closed.Contains(key) && Menu.Search.Length == 0;

    // How far a node or group is open, eased toward its target once per frame (in the layout pass).
    private static float Openness(string key)
    {
        float target = Closed(key) ? 0f : 1f;
        if (!s_open.TryGetValue(key, out float open) || !Motion.On) open = target;
        else if (Event.current.type == EventType.Layout) open = Mathf.MoveTowards(open, target, Time.unscaledDeltaTime * Motion.Speed / OpenSeconds);
        s_open[key] = open;
        return open * open * (3f - 2f * open);
    }

    /// <summary>Leaves dev-only features' entries out of the tree (dev builds, for screenshots players would see).</summary>
    internal static bool HideDevPages;

    private static bool DevOnly(MenuNode node) => node.Feature?.GetType().Namespace == "EpochAssist.Dev";

    private static List<Row> Rows(MenuTab tab)
    {
        string[] words = MenuSearchText.Words(Menu.Search);
        var rows = new List<Row>();
        // Each folder's openness, read once per pass (reading it eases it).
        var folders = new Dictionary<string, float>();
        foreach (var node in tab.Nodes)
        {
            if (HideDevPages && DevOnly(node)) continue;
            // A folder's name matching shows all its nodes.
            bool nodeMatch = MenuSearchText.Matches(node.Title, words) || (node.Folder != null && MenuSearchText.Matches(node.Folder, words));
            var items = node.Entry.Groups.Select(g => (Group: g, Items: Safe.Call(g.Items, Array.Empty<TreeItem>(), node.Path + "." + g.Title))).ToList();
            var pages = node.Leaf ? new List<PageNode>() : node.Pages.Where(p => p.Page.InTree && (nodeMatch || MenuSearchText.Matches(p.Page.Title, words)
                || p.Page.Children.Exists(c => MenuSearchText.Matches(c.Title, words)))).ToList();
            var groups = items.Select(g => (g.Group, Items: g.Items.Where(i => nodeMatch || MenuSearchText.Matches(g.Group.Title, words)
                || MenuSearchText.Matches(i.Label, words)).ToList())).Where(g => g.Items.Count > 0 || nodeMatch).ToList();
            if (!nodeMatch && pages.Count == 0 && groups.All(g => g.Items.Count == 0)) continue;

            // A node in a folder sits one level in, under the folder's row (added with its first shown node).
            int depth = 0;
            float folderOpen = 1f;
            string? folderKey = node.FolderPath;
            if (folderKey != null)
            {
                if (!folders.TryGetValue(folderKey, out folderOpen))
                {
                    rows.Add(new Row(Kind.Folder, 0, folderKey, node.Folder!, node, null, null, null, 1f));
                    folders[folderKey] = folderOpen = Openness(folderKey);
                }
                depth = 1;
                if (folderOpen <= 0.01f) continue;
            }

            string nodeKey = node.Path;
            rows.Add(new Row(Kind.Node, depth, nodeKey, node.Title, node, node.Leaf ? node.Pages[0] : null, null, folderKey, folderOpen));
            if (node.Leaf) continue;
            float open = folderOpen * Openness(nodeKey);
            if (open <= 0.01f) continue;
            foreach (var (group, groupItems) in groups)
            {
                string groupKey = $"{nodeKey}/{MenuEntry.Slug(group.Title)}";
                rows.Add(new Row(Kind.Group, depth + 1, groupKey, group.Title, node, null, null, nodeKey, open));
                float groupOpen = open * Openness(groupKey);
                if (groupOpen <= 0.01f) continue;
                for (int i = 0; i < groupItems.Count; i++)
                    rows.Add(new Row(Kind.Item, depth + 2, $"{groupKey}/{i + 1}", groupItems[i].Label, node, null, groupItems[i], groupKey, groupOpen));
            }
            foreach (var page in pages)
            {
                rows.Add(new Row(Kind.Page, depth + 1, page.Path, page.Page.Title, node, page, null, nodeKey, open));
                if (page.Page.Children.Count == 0) continue;
                float pageOpen = open * Openness(page.Path);
                if (pageOpen <= 0.01f) continue;
                foreach (var child in node.Pages)
                    if (page.Page.Children.Contains(child.Page))
                        rows.Add(new Row(Kind.Page, depth + 2, child.Path, child.Page.Title, node, child, null, page.Path, pageOpen));
            }
        }
        return rows;
    }

    public static void Draw(Rect area)
    {
        Focus.Scope = string.Empty;
        // The button sits in the top-level rows' chevron column (their x + 6), the search to its right.
        var collapse = new Rect(area.x + 6f, area.y + 6f, 12f, FilterHeight);
        var filter = new Rect(collapse.xMax + 4f, area.y + 6f, area.xMax - 6f - collapse.xMax - 4f, FilterHeight);
        SearchField(filter);

        var tab = MenuRoutes.Page?.Node.Tab;
        if (tab == null) return;
        CollapseAll(collapse, tab);
        var rows = Rows(tab);
        var view = new Rect(area.x, filter.yMax + 6f, area.width, area.yMax - filter.yMax - 8f);
        float content = rows.Sum(r => RowHeight * r.Open);
        var inner = new Rect(0f, 0f, view.width - (content > view.height ? 8f : 0f), content);
        s_scroll = GUI.BeginScrollView(view, s_scroll, inner, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);
        Focus.InScroll = "tree";
        Popups.PushOrigin(view.position - s_scroll);
        try
        {
            string current = MenuRoutes.Path;
            float y = 0f;
            foreach (var row in rows)
            {
                float height = RowHeight * row.Open;
                var r = new Rect(4f, y, inner.width - 8f, RowHeight);
                y += height;
                if (r.yMax < s_scroll.y - RowHeight || r.y > s_scroll.y + view.height + RowHeight) continue;
                var color = GUI.color;
                if (row.Open < 1f) GUI.color = new Color(color.r, color.g, color.b, color.a * row.Open);
                DrawRow(row, r, current, rows);
                GUI.color = color;
            }
        }
        finally
        {
            Popups.PopOrigin();
            Focus.InScroll = null;
            GUI.EndScrollView();
        }
        if (Event.current.type == EventType.Repaint && Focus.TakeScrollTarget("tree") is Rect target)
        {
            if (target.yMin < s_scroll.y) s_scroll.y = Mathf.Max(0f, target.yMin - 4f);
            else if (target.yMax > s_scroll.y + view.height) s_scroll.y = target.yMax - view.height + 4f;
        }
    }

    // The search field: a placeholder while empty, a clear button inside its right end while not; Enter opens the first
    // result.
    private static void SearchField(Rect field)
    {
        var clear = new Rect(field.xMax - 20f, field.y, 20f, field.height);
        string search = Menu.Search;
        bool hasText = search.Length > 0;
        // Drawn first so it takes its click before the field under it, and always, so the field's control id doesn't move.
        bool cleared = GUI.Button(clear, GUIContent.none, GUIStyle.none) && hasText;
        GUI.SetNextControlName(SearchControl);
        string typed = GUI.TextField(field, search, 64, Widgets.Style(() => Theme.TreeFilter, GUI.skin.textField));
        if (typed != search) Menu.SetSearch(typed);
        if (SearchFocused) Focus.Take("search");
        bool activated = Focus.Item("search", field);
        if (s_blurSearch && Event.current.type == EventType.Layout)
        {
            s_blurSearch = false;
            if (SearchFocused)
            {
                GUI.FocusControl(null);
                GUIUtility.keyboardControl = 0;
            }
        }
        else if (FocusSearch && Event.current.type == EventType.Layout)
        {
            FocusSearch = false;
            Focus.Take("search");
            GUI.FocusControl(SearchControl);
        }
        else if (Focus.Arrived("search") || (activated && !SearchFocused))
        {
            GUI.FocusControl(SearchControl);
        }
        else if (activated && hasText)
        {
            PageView.OpenFirstResult();
        }
        if (cleared)
        {
            Menu.SetSearch(string.Empty);
            GUIUtility.keyboardControl = 0;
        }
        if (Event.current.type != EventType.Repaint) return;
        if (Menu.Search.Length == 0 && !SearchFocused) Widgets.Paint(field, "Search", Widgets.Style(() => Theme.FieldDim, GUI.skin.label));
        else if (hasText && Widgets.Themed) Theme.Tinted(new Rect(clear.center.x - 5f, clear.center.y - 5f, 10f, 10f), Theme.Cross, clear.Contains(Event.current.mousePosition) ? Theme.Text : Theme.Dim);
    }

    // Closes every folder and node in the tab, or opens them all again once all are closed.
    private static void CollapseAll(Rect r, MenuTab tab)
    {
        var keys = tab.Nodes.SelectMany(n => new[] { n.FolderPath, n.Leaf ? null : n.Path }).OfType<string>().Distinct().ToList();
        if (keys.Count == 0) return;
        bool allClosed = keys.All(s_closed.Contains);
        if (GUI.Button(r, GUIContent.none, GUIStyle.none))
        {
            if (allClosed) s_closed.ExceptWith(keys);
            else s_closed.UnionWith(keys);
        }
        if (Event.current.type == EventType.Repaint && Widgets.Themed)
            Theme.Tinted(new Rect(r.x + 2f, Mathf.Round(r.center.y - 6f), 8f, 12f), allClosed ? Theme.TreeExpandAll : Theme.TreeCollapseAll,
                r.Contains(Event.current.mousePosition) ? Theme.Text : Theme.Dim);
    }

    /// <summary>Lets go of the search field (Esc), at the next GUI pass.</summary>
    public static void BlurSearch() => s_blurSearch = true;

    private static void DrawRow(Row row, Rect r, string current, List<Row> rows)
    {
        MenuNode node = row.Node;
        bool selected = row.Page != null && row.Page.Path == current;
        float x = r.x + 2f + row.Depth * Indent;
        bool expandable = row.Kind is Kind.Folder or Kind.Group || (row.Kind == Kind.Node && !node.Leaf)
            || (row.Kind == Kind.Page && row.Page!.Page.Children.Count > 0);
        var chevron = new Rect(x, r.y, 12f, r.height);
        bool chevronClicked = expandable && GUI.Button(chevron, GUIContent.none, GUIStyle.none);
        bool clicked = GUI.Button(r, GUIContent.none, GUIStyle.none);
        string key = Focus.TreePrefix + row.Key;
        bool activated = Focus.Item(key, r, out int adjust);

        if (chevronClicked) Toggle(row.Key);
        else if (clicked || activated) Pick(row);
        if (adjust > 0)
        {
            if (expandable && Closed(row.Key)) Toggle(row.Key);
            else if (expandable && rows.FirstOrDefault(c => c.Parent == row.Key) is { } child) Focus.Take(Focus.TreePrefix + child.Key);
        }
        else if (adjust < 0)
        {
            if (expandable && !Closed(row.Key)) Toggle(row.Key);
            else if (row.Parent != null) Focus.Take(Focus.TreePrefix + row.Parent);
        }

        if (Event.current.type != EventType.Repaint) return;
        bool live = row.Kind == Kind.Folder || node.Feature == null || node.Feature.State == FeatureState.Running;
        Color previous = GUI.color;
        if (!live && row.Kind != Kind.Page) GUI.color = new Color(previous.r, previous.g, previous.b, previous.a * 0.45f);
        if (Widgets.Themed)
        {
            if (selected) Theme.DrawSliced(r, Theme.SelectionBg, Theme.CardRadius);
            else if (r.Contains(Event.current.mousePosition)) Theme.DrawSliced(r, Theme.HoverBg, Theme.CardRadius);
            if (expandable) Theme.Tinted(new Rect(chevron.x + 2f, Mathf.Round(r.center.y - 4f), 8f, 8f), Closed(row.Key) ? Theme.TreeChevronRight : Theme.TreeChevron, Theme.Dim);
        }
        x += 16f;
        bool heading = row.Kind is Kind.Folder or Kind.Node;
        GUIStyle style = Widgets.Style(() => heading ? Theme.TreeNode : Theme.TreeRow, GUI.skin.label);
        if (row.Item is { } item && item.Number != null)
        {
            bool on = item.State != StatusState.Off;
            Color number = on ? Theme.AccentText : Theme.Dim;
            Widgets.Paint(new Rect(x - 2f, r.y, 12f, r.height), Widgets.Themed ? Widgets.Colored(item.Number, number) : item.Number, Widgets.Style(() => Theme.TreeNumber, GUI.skin.label));
            x += 14f;
        }
        Color text = selected ? Theme.Text : heading ? Theme.Text : row.Item is { State: StatusState.Off } ? Theme.Dim : Theme.Secondary;
        bool conflict = row.Kind == Kind.Node && node.Feature is { } withConflicts && ModOverlap.ConflictsFor(withConflicts.Id).Count > 0;
        float dotRoom = conflict ? 34f : 18f;
        Widgets.Paint(new Rect(x, r.y, r.xMax - x - dotRoom, r.height), Controls.Fit(row.Label, style, r.xMax - x - dotRoom),
            Widgets.Themed && (selected || row.Item is { State: StatusState.Off }) ? Recoloured(style, text) : style);

        // The dot: the node's master switch, its run state when it has none or isn't running; an item's own state.
        var dot = new Rect(r.xMax - 12f, Mathf.Round(r.center.y - 3.5f), 7f, 7f);
        if (row.Kind == Kind.Item && row.Item != null)
        {
            Menu.DrawState(dot, row.Item.State, row.Item.State == StatusState.Off ? Theme.Faint : Theme.StateColor(row.Item.State));
        }
        else if (row.Kind == Kind.Node && node.Feature is { } feature)
        {
            StatusState state = !live ? StatusState.Off
                : node.Entry.Switch is { } master ? (master.Value ? StatusState.Active : StatusState.Off)
                : feature.Status.State;
            Menu.DrawState(dot, state, state == StatusState.Off ? Theme.Faint : Theme.StateColor(state));
        }
        // Another mod conflicts with the feature: the conflict pill's warning triangle, before the dot.
        if (conflict) Theme.Tinted(Menu.PixelSnap(new Rect(dot.x - 17f, Mathf.Round(r.center.y - 6f), 12f, 12f)), Theme.IconWarning, Controls.ConflictColor);
        GUI.color = previous;
    }

    private static readonly Dictionary<(GUIStyle, Color), GUIStyle> s_recoloured = new();

    // A copy of a style in another text colour (the selected row's text, an empty slot's).
    private static GUIStyle Recoloured(GUIStyle style, Color color)
    {
        if (s_recoloured.Count > 32) s_recoloured.Clear();
        if (s_recoloured.TryGetValue((style, color), out var copy)) return copy;
        copy = new GUIStyle(style);
        copy.normal.textColor = color;
        copy.hover.textColor = color;
        s_recoloured[(style, color)] = copy;
        return copy;
    }

    private static void Toggle(string key)
    {
        if (!s_closed.Remove(key)) s_closed.Add(key);
    }

    // A node opens its last or first page (and opens in the tree); a folder or group opens and closes; an item opens its
    // page.
    private static void Pick(Row row)
    {
        switch (row.Kind)
        {
            case Kind.Folder:
            case Kind.Group:
                Toggle(row.Key);
                return;
            case Kind.Node:
                s_closed.Remove(row.Key);
                if ((row.Page ?? MenuRoutes.First(row.Node)) is { } first) Open(first);
                return;
            case Kind.Item:
                var opens = row.Item?.Opens is { } target ? row.Node.Pages.FirstOrDefault(p => p.Page == target) : null;
                if ((opens ?? row.Node.Pages.FirstOrDefault()) is { } itemPage) Open(itemPage);
                return;
            default:
                if (row.Page != null) Open(row.Page);
                return;
        }
    }

    private static void Open(PageNode page)
    {
        Menu.SetSearch(string.Empty);
        MenuRoutes.Show(page);
    }
}
