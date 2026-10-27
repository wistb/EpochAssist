using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

// The page: the breadcrumb, the title row (title, status tag, the page's switch, its buttons, Reset), banners, then
// the entry's top rows and the page's rows in a scroll view, or the page's block. Rows are laid out once per layout
// pass and only those in view are drawn. While the search has text, the search results take the page's place.
internal static class PageView
{
    private const float Pad = 16f, FadeSeconds = 0.1f;

    private sealed record Placed(MenuRow Row, RowRenderer Renderer, RowContext Ctx, float Y, float Height);

    private static readonly List<Placed> s_layout = new();
    // The label columns the page and the search results were laid out with.
    private static float s_labelColumn, s_resultsColumn;
    private static Vector2 s_scroll;
    private static int s_seenChanges = -1;
    private static float s_changedAt = float.MinValue;
    private static string? s_seenSearch;

    /// <summary>The width blocks get for their content (inside the page's padding and scroll bar).</summary>
    public static float BlockWidth { get; private set; } = 500f;

    public static void Reset()
    {
        s_layout.Clear();
        s_scroll = Vector2.zero;
        s_seenChanges = -1;
        s_seenSearch = null;
    }

    public static void Draw(Rect area)
    {
        if (Widgets.Themed) Theme.Fill(area, new Color(Theme.Window.r, Theme.Window.g, Theme.Window.b, Mathf.Clamp(LookSettings.MenuOpacity.Value, 0.2f, 1f) * Theme.Opacity));
        var inner = new Rect(area.x + Pad, area.y + 10f, area.width - Pad * 2f, area.height - 14f);
        string search = Menu.Search.Trim();
        if (search != s_seenSearch)
        {
            s_seenSearch = search;
            s_scroll = Vector2.zero;
        }
        if (search.Length > 0)
        {
            Focus.Scope = "search";
            SearchResults(inner, search);
            Focus.Scope = string.Empty;
            return;
        }
        var page = MenuRoutes.Page;
        if (page == null) return;
        if (MenuRoutes.Changes != s_seenChanges)
        {
            if (s_seenChanges >= 0) s_changedAt = Time.unscaledTime;
            s_seenChanges = MenuRoutes.Changes;
            s_scroll = Vector2.zero;
        }
        Color color = GUI.color;
        GUI.color = new Color(color.r, color.g, color.b, color.a * Motion.Progress(s_changedAt, FadeSeconds));
        Focus.Scope = page.Path;
        try
        {
            DrawPage(page, inner);
        }
        finally
        {
            Focus.Scope = string.Empty;
            GUI.color = color;
        }
    }

    private static void DrawPage(PageNode page, Rect inner)
    {
        var node = page.Node;
        if (node.Entry.Layout == MenuLayout.Library && node.Entry.Library is { } library)
        {
            LibraryLayout.Draw(page, library, inner);
            return;
        }
        Feature? feature = node.Feature;
        float y = inner.y;
        if (Event.current.type == EventType.Repaint) Widgets.Paint(new Rect(inner.x, y, inner.width, 14f), page.Crumb, Widgets.Style(() => Theme.Crumb, GUI.skin.label));
        y += 18f;
        TitleRow(page, new Rect(inner.x, y, inner.width, 26f));
        y += 34f;

        foreach (var (text, button) in Banners(page))
        {
            float h = TextRows.BannerHeight(text, inner.width, button);
            TextRows.Banner(new Rect(inner.x, y, inner.width, h), text, button, "banner." + (button?.Action ?? "text"));
            y += h + 8f;
        }

        var body = new Rect(inner.x, y, inner.width, inner.yMax - y);
        if (page.Page.Block != null)
        {
            BlockWidth = body.width - 14f;
            MenuBlocks.Draw(page.Page.Block, body, null);
            return;
        }

        bool enabled = node.Entry.ContentEnabled == null || Safe.Call(node.Entry.ContentEnabled, true, node.Path + ".enabled");
        var top = new RowContext { Path = page.Path, Live = node.Live };
        var rows = top.With(enabled: enabled);
        if (Event.current.type == EventType.Layout || s_layout.Count == 0) Lay(node.Entry.Top, page.Rows, top, rows, body.width - 12f);
        DrawRows(body);
    }

    /// <summary>Forgets the laid-out rows and the scroll (another editor opened in the library layout).</summary>
    public static void ResetRows()
    {
        s_layout.Clear();
        s_scroll = Vector2.zero;
    }

    /// <summary>Rows given by a layout (the library's open editor) drawn as a page's rows, in a rect of the page.</summary>
    public static void DrawRowList(PageNode page, IReadOnlyList<MenuRow> rows, Rect body)
    {
        var node = page.Node;
        bool enabled = node.Entry.ContentEnabled == null || Safe.Call(node.Entry.ContentEnabled, true, node.Path + ".enabled");
        var ctx = new RowContext { Path = page.Path, Live = node.Live }.With(enabled: enabled);
        if (Event.current.type == EventType.Layout || s_layout.Count == 0) Lay(Array.Empty<MenuRow>(), rows, ctx, ctx, body.width - 12f);
        DrawRows(body);
    }

    // Lays the rows out top to bottom: which show, where, and how tall, for this frame's passes.
    private static void Lay(IEnumerable<MenuRow> topRows, IEnumerable<MenuRow> pageRows, RowContext top, RowContext page, float width)
    {
        s_layout.Clear();
        s_labelColumn = RowLayout.ColumnFor(topRows.Concat(pageRows), width);
        RowLayout.PageLabel = s_labelColumn;
        float y = 0f;
        void Add(MenuRow row, RowContext ctx)
        {
            if (RowKinds.For(row) is not { } renderer || !renderer.Shows(row, ctx)) return;
            var rowCtx = Safe.Enabled(row) ? ctx : ctx.With(enabled: false);
            float height = Safe.Call(() => renderer.Height(row, width, rowCtx), RowLayout.ControlHeight, row.Key + ".height");
            if (height <= 0f) return;
            s_layout.Add(new Placed(row, renderer, rowCtx, y, height));
            y += height;
        }
        foreach (var row in topRows) Add(row, top);
        if (s_layout.Count > 0) y += 4f;
        foreach (var row in pageRows) Add(row, page);
        RowLayout.PageLabel = 0f;
    }

    private static void DrawRows(Rect body)
    {
        float content = s_layout.Count > 0 ? s_layout[^1].Y + s_layout[^1].Height : 0f;
        // The width rows were laid out for: room for the scroll bar is always kept, so hints wrap the same either way.
        var inner = new Rect(0f, 0f, body.width - 12f, content);
        s_scroll = GUI.BeginScrollView(body, s_scroll, inner, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);
        Focus.InScroll = "page";
        Popups.PushOrigin(body.position - s_scroll);
        RowLayout.PageLabel = s_labelColumn;
        try
        {
            foreach (var placed in s_layout)
            {
                // Rows out of view aren't drawn, but those with focus still report it so keys can reach them.
                if (placed.Y + placed.Height < s_scroll.y - 40f || placed.Y > s_scroll.y + body.height + 40f) continue;
                var r = new Rect(0f, placed.Y, inner.width, placed.Height);
                var state = RowLayout.Begin(placed.Ctx.Enabled);
                try
                {
                    placed.Renderer.Draw(placed.Row, r, placed.Ctx);
                }
                catch (Exception e)
                {
                    Log.ErrorThrottled("menu:row:" + placed.Row.Key, $"Menu row {placed.Row.Key} failed: {e.Message}");
                }
                finally
                {
                    RowLayout.End(state);
                }
            }
        }
        finally
        {
            RowLayout.PageLabel = 0f;
            Popups.PopOrigin();
            Focus.InScroll = null;
            GUI.EndScrollView();
        }
        ScrollToFocus("page", body.height, content);
    }

    private static void ScrollToFocus(string view, float height, float content)
    {
        if (Event.current.type != EventType.Repaint || Focus.TakeScrollTarget(view) is not Rect target) return;
        if (target.yMin < s_scroll.y) s_scroll.y = Mathf.Max(0f, target.yMin - 8f);
        else if (target.yMax > s_scroll.y + height) s_scroll.y = Mathf.Min(content, target.yMax - height + 8f);
    }

    // Title, tag, then from the right: Reset, the page's buttons, its switch.
    private static void TitleRow(PageNode page, Rect r)
    {
        GUIStyle title = Widgets.Style(() => Theme.PageTitle, GUI.skin.label);
        float x = r.x;
        float tw = TextCache.Width(page.Page.Title, title);
        // Centred on the row, like the tags and buttons beside it (the style draws from the top).
        float th = title.lineHeight + title.padding.vertical;
        if (Event.current.type == EventType.Repaint) Widgets.Paint(new Rect(x, Mathf.Round(r.center.y - th / 2f), tw, th), page.Page.Title, title);
        x += tw + 10f;
        bool pill = page.Page.ConflictPill ?? ReferenceEquals(page.Node.Entry.Pages.FirstOrDefault(), page.Page);
        float conflict = pill ? ConflictTag.Draw(page.Node.Feature, x, r.center.y, "conflict.page") : 0f;
        if (conflict > 0f) x += conflict + 10f;

        float right = r.xMax;
        if (page.Resettable && page.Sections.Any(s => !s.IsDefault))
        {
            float w = Controls.ButtonWidth("Reset");
            right -= w;
            if (Controls.Button(new Rect(right, r.center.y - 12f, w, 24f), "Reset", "reset", tip: "Every setting on this page back to its default"))
            {
                foreach (var section in page.Sections.ToList()) Actions.Run(SettingsRegistry.ResetAction(section), "menu page reset");
            }
            right -= 6f;
        }
        for (int i = page.Page.Actions.Count - 1; i >= 0; i--)
        {
            var button = page.Page.Actions[i];
            float w = Controls.ButtonWidth(button.Label);
            right -= w;
            if (Controls.Button(new Rect(right, r.center.y - 12f, w, 24f), button.Label, button.Key, button.Primary, button.Tip)) ActionsFor(button);
            right -= 6f;
        }
        if (page.Page.Switch is { } toggle)
        {
            right -= Controls.SwitchWidth;
            var sw = new Rect(right, r.center.y - 12f, Controls.SwitchWidth, 24f);
            if (Controls.Switch(sw, toggle.Value, "switch", toggle.Label) != toggle.Value) SettingRows.Flip(toggle, "menu page switch");
            right -= 6f;
        }
        // The tag goes in what's left: whole, else the state alone ("waiting"), else not at all, never under a button.
        if (StatusOf(page) is { } status && FittedTag(status, right - x) is { } fitted) Tag(new Rect(x, r.center.y - Theme.TagHeight / 2f, 0f, Theme.TagHeight), fitted);
    }

    private static PageStatus? FittedTag(PageStatus status, float room)
    {
        GUIStyle style = Widgets.Style(() => Theme.Tag, GUI.skin.label);
        if (TextCache.Width(status.Text, style) + style.padding.horizontal <= room) return status;
        int colon = status.Text.IndexOf(':');
        if (colon <= 0) return null;
        var state = new PageStatus(status.State, status.Text[..colon]);
        return TextCache.Width(state.Text, style) + style.padding.horizontal <= room ? state : null;
    }

    // Copy diagnostics still copies while the diagnostics feature is down, when its action is gone.
    private static void ActionsFor(ActionButton button)
    {
        if (button.OnClick != null)
        {
            TextRows.Run(button);
            return;
        }
        if (Actions.Run(button.Action, "menu button").StartsWith("error") && button.Action == "diagnostics.copy")
            DiagnosticsPage.Copy(Diagnostics.Build().ToText(), "Diagnostics copied.");
    }

    /// <summary>The title row's tag: the page's own status, else the feature's state (and why).</summary>
    public static PageStatus? StatusOf(PageNode page)
    {
        if (page.Page.Status != null) return Safe.Call(page.Page.Status, new PageStatus(StatusState.Off, "off"), page.Path + ".status");
        if (page.Node.Feature is not { } feature) return null;
        if (feature.Record.TurnedOff) return new PageStatus(StatusState.Off, "turned off");
        if (feature.State != FeatureState.Running) return new PageStatus(StatusState.Off, feature.State.ToString().ToLowerInvariant());
        // The state, and why when there is a reason ("waiting: menu open").
        FeatureStatus current = feature.Status;
        string text = current.State.ToString().ToLowerInvariant();
        return new PageStatus(current.State, current.Detail.Length > 0 ? $"{text}: {current.Detail}" : text);
    }

    /// <summary>A status tag: the state's shape and its text on the state's badge colour.</summary>
    public static float Tag(Rect at, PageStatus status)
    {
        GUIStyle style = Widgets.Style(() => Theme.Tag, GUI.skin.label);
        float w = TextCache.Width(status.Text, style) + style.padding.horizontal;
        var r = new Rect(at.x, at.y, w, at.height);
        if (Event.current.type != EventType.Repaint) return w;
        // On the state's badge, its shape and text in the lightened state colour, so they read on the tint.
        Color color = Theme.StateText(status.State);
        if (Widgets.Themed)
        {
            Theme.DrawSliced(r, Theme.BadgeFor(status.State), Theme.TagRadius);
            Menu.DrawState(new Rect(r.x + 7f, Mathf.Round(r.center.y - 3.5f), 7f, 7f), status.State, color);
            Widgets.Paint(r, Widgets.Colored(status.Text, color), style);
        }
        else
        {
            Widgets.Paint(r, status.Text, style);
        }
        return w;
    }

    // Banners for this page: settings that couldn't be brought up to date, a declaration problem, why the feature isn't
    // running (with Turn on or Restart where that helps), a partly working feature, and the page's own banner.
    private static IEnumerable<(string Text, ActionButton? Button)> Banners(PageNode page)
    {
        foreach (var group in SettingsRegistry.Groups)
        {
            if (group.Failures.Count > 0) yield return ($"{group.Name} settings not updated: {string.Join("; ", group.Failures)}", null);
        }
        if (page.Problem != null) yield return ($"This page has a problem: {page.Problem}.", null);
        if (page.Node.Feature is { } feature)
        {
            if (feature.Record.TurnedOff) yield return (Menu.Reason(feature), new ActionButton("Turn on", $"feature.toggle.{feature.Id}"));
            else if (feature.State != FeatureState.Running)
                yield return (Menu.Reason(feature), feature.State == FeatureState.Unavailable || feature.BlockedBy != null ? null : new ActionButton("Restart", $"feature.restart.{feature.Id}"));
            else if (feature.Degraded)
                yield return ($"{feature.Name} partly works: " + string.Join("; ", feature.MissingGroups.Select(g => $"without {g} (game update)")
                    .Concat(feature.PatchFailures.Select(f => "patch failed: " + f))), null);
        }
        if (page.Page.Banner != null && Safe.Call(page.Page.Banner, null, page.Path + ".banner") is { } text) yield return (text, null);
    }

    // ---- Search results ----

    private sealed record Result(PageNode? Page, MenuNode Node, string Line, List<MenuRow> Rows);

    private static List<Result> Results(string query)
    {
        var results = new List<Result>();
        foreach (var hit in MenuSearch.Query(query))
        {
            var page = hit.Page ?? (hit.Node.Leaf ? hit.Node.Pages[0] : null);
            var last = results.Count > 0 ? results[^1] : null;
            if (hit.Row != null && last != null && last.Page == page && last.Node == hit.Node)
            {
                last.Rows.Add(hit.Row);
                continue;
            }
            results.Add(new Result(page, hit.Node, MenuSearch.PathLine(hit), hit.Row != null ? new List<MenuRow> { hit.Row } : new List<MenuRow>()));
        }
        return results;
    }

    /// <summary>Enter in the search field: opens the first result.</summary>
    public static void OpenFirstResult()
    {
        var first = Results(Menu.Search).FirstOrDefault();
        if (first != null) Choose(first.Node, first.Page, first.Rows.FirstOrDefault());
    }

    private static void Choose(MenuNode node, PageNode? page, MenuRow? row)
    {
        var target = page ?? MenuRoutes.First(node);
        if (target == null) return;
        Menu.SetSearch(string.Empty);
        GUIUtility.keyboardControl = 0;
        MenuRoutes.Show(target);
        if (row != null) Focus.TakeFull($"{target.Path}/{SettingRows.FocusKey(row)}");
    }

    private static void SearchResults(Rect inner, string query)
    {
        var results = Results(query);
        string[] words = MenuSearchText.Words(query);
        float y = inner.y;
        if (Event.current.type == EventType.Repaint)
            Widgets.Paint(new Rect(inner.x, y, inner.width, 14f), $"Search › {Math.Max(results.Count, s_results.Count(p => p.Row != null))} results", Widgets.Style(() => Theme.Crumb, GUI.skin.label));
        y += 22f;
        var body = new Rect(inner.x, y, inner.width, inner.yMax - y);
        if (results.Count == 0)
        {
            Widgets.Paint(new Rect(body.x, body.y, body.width, 40f), $"No settings match \"{query}\". Clear the search to see every page.", Widgets.Style(() => Theme.Hint, GUI.skin.label));
            return;
        }

        float width = body.width - 12f;
        // Laid out in the layout pass: each result's path line, then its rows drawn live.
        if (Event.current.type == EventType.Layout || s_results.Count == 0) LayResults(results, words, width);
        float content = s_results.Count > 0 ? s_results[^1].Y + s_results[^1].Height : 0f;
        var view = new Rect(0f, 0f, width, content);
        s_scroll = GUI.BeginScrollView(body, s_scroll, view, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);
        Focus.InScroll = "page";
        Popups.PushOrigin(body.position - s_scroll);
        RowLayout.PageLabel = s_resultsColumn;
        try
        {
            foreach (var placed in s_results)
            {
                if (placed.Y + placed.Height < s_scroll.y - 40f || placed.Y > s_scroll.y + body.height + 40f) continue;
                var r = new Rect(0f, placed.Y, width, placed.Height);
                if (placed.Row == null)
                {
                    PathLine(r, placed);
                    continue;
                }
                var state = RowLayout.Begin(placed.Ctx!.Enabled);
                string scope = Focus.Scope;
                Focus.Scope = "search/" + placed.Ctx.Path;
                try
                {
                    placed.Renderer!.Draw(placed.Row, r, placed.Ctx);
                    if (placed.Hidden != null && Event.current.type == EventType.Repaint)
                        Widgets.Paint(new Rect(r.x + RowLayout.LabelWidth(width) + RowLayout.Gap, r.yMax - 18f, width, 16f), placed.Hidden, Widgets.Style(() => Theme.Hint, GUI.skin.label));
                }
                catch (Exception e)
                {
                    Log.ErrorThrottled("menu:search:" + placed.Row.Key, $"Search row {placed.Row.Key} failed: {e.Message}");
                }
                finally
                {
                    Focus.Scope = scope;
                    RowLayout.End(state);
                }
            }
        }
        finally
        {
            RowLayout.PageLabel = 0f;
            Popups.PopOrigin();
            Focus.InScroll = null;
            GUI.EndScrollView();
        }
        ScrollToFocus("page", body.height, content);
    }

    private sealed record PlacedResult(Result Result, MenuRow? Row, RowRenderer? Renderer, RowContext? Ctx, float Y, float Height, string? Hidden);

    private static readonly List<PlacedResult> s_results = new();

    private static void LayResults(List<Result> results, string[] words, float width)
    {
        s_results.Clear();
        s_resultsColumn = RowLayout.ColumnFor(results.SelectMany(r => r.Rows), width);
        RowLayout.PageLabel = s_resultsColumn;
        float y = 0f;
        foreach (var result in results)
        {
            s_results.Add(new PlacedResult(result, null, null, null, y, 24f, null));
            y += 26f;
            if (result.Page == null) continue;
            var node = result.Node;
            bool enabled = node.Entry.ContentEnabled == null || Safe.Call(node.Entry.ContentEnabled, true, node.Path + ".enabled");
            var ctx = new RowContext { Path = result.Page.Path, Live = node.Live, Enabled = enabled, Highlight = words };
            foreach (var row in result.Rows)
            {
                if (RowKinds.For(row) is not { } renderer) continue;
                bool shows = renderer.Shows(row, ctx);
                string? hidden = shows ? null : HiddenNote(row);
                if (!shows && hidden == null) continue;
                var rowCtx = shows && Safe.Enabled(row) ? ctx : ctx.With(enabled: false);
                float height = Safe.Call(() => renderer.Height(row, width, rowCtx), RowLayout.ControlHeight, row.Key + ".height") + (hidden != null ? 18f : 0f);
                s_results.Add(new PlacedResult(result, row, renderer, rowCtx, y, height, hidden));
                y += height;
            }
            y += 6f;
        }
        RowLayout.PageLabel = 0f;
    }

    /// <summary>Why a found setting isn't shown on its page, or null for rows that aren't settings.</summary>
    public static string? HiddenNote(MenuRow row)
    {
        var setting = row switch
        {
            SettingRow s => s.Setting,
            NumberGroup g => g.Numbers.FirstOrDefault(),
            _ => null,
        };
        if (setting == null || PageNode.SettingsOf(row).Any(s => s.Visible)) return null;
        return setting.ShownWhen != null ? $"Shows when {setting.ShownWhen}." : "Hidden by another setting.";
    }

    // A result's path line: clickable and focusable; it opens the page and focuses the row.
    private static void PathLine(Rect r, PlacedResult placed)
    {
        var result = placed.Result;
        bool clicked = GUI.Button(r, GUIContent.none, GUIStyle.none);
        string key = "result." + (result.Page?.Path ?? result.Node.Path);
        if (Focus.Item(key, r) || clicked) Choose(result.Node, result.Page, result.Rows.FirstOrDefault());
        if (Event.current.type != EventType.Repaint) return;
        bool hover = r.Contains(Event.current.mousePosition);
        if (hover && Widgets.Themed) Theme.DrawSliced(r, Theme.HoverBg, Theme.CardRadius);
        GUIStyle style = Widgets.Style(() => Theme.Heading, GUI.skin.label);
        Widgets.Paint(new Rect(r.x + 4f, r.y, r.width - 8f, r.height), result.Line.ToUpperInvariant(), style);
    }
}
