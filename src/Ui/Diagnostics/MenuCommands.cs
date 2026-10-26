using EpochAssist.Core;

namespace EpochAssist.Ui;

// The menu command: opens, closes and searches the menu, and prints its tabs, tree, pages and search results as text,
// each row as its renderer reports it, so checks compare text.
internal static class MenuCommands
{
    private const int SettleFrames = 2;
    private const string Usage = "menu open <path>|<feature id>|settings [tab] | close | search [text] | tree [tab] | page | rows <path> | find <text>";

    public static void Register() => Commands.Register(new Command("menu", Usage,
        "Opens, closes or searches the menu, or prints its tree, a page or search results as text.", CommandMark.Release, "host", Run));

    private static CommandResult Run(CommandCall call)
    {
        switch (call.Lower(1))
        {
            case "open":
                return Open(call);
            case "close":
                Actions.Run("menu.close", call.Trigger);
                return CommandResult.Ok("menu closed") with { Settle = SettleFrames };
            case "search":
                Menu.SetSearch(call.Rest(2));
                if (!Menu.Visible) Menu.Open(MenuRoutes.Page);
                int count = call.Rest(2).Trim().Length == 0 ? 0 : MenuSearch.Query(call.Rest(2)).Count;
                return CommandResult.Ok($"menu search: {count} results") with { Settle = SettleFrames };
            case "tree":
                return new CommandResult(Tree(call.Lower(2)));
            case "page":
                return MenuRoutes.Page is { } shown ? new CommandResult(Page(shown, "page")) : CommandResult.Error("menu: no page");
            case "rows":
                return MenuRoutes.Resolve(call.Rest(2)) is { } page ? new CommandResult(Page(page, "rows")) : NoPage(call.Rest(2));
            case "find":
                return new CommandResult(Find(call.Rest(2)));
            default:
                return CommandResult.Error("menu: usage " + Usage);
        }
    }

    private static CommandResult NoPage(string path) =>
        CommandResult.Error($"menu: no page '{path}'; pages: {string.Join(", ", MenuTree.Pages.Select(p => p.Path))}");

    // open <path> | <feature id> | settings [tab 0-4]
    private static CommandResult Open(CommandCall call)
    {
        string target = call.Rest(2).Trim();
        PageNode? page;
        if (call.Lower(2) == "settings" && call.Args.Length <= 3)
            page = int.TryParse(call.Lower(3), out int tab) ? MenuRoutes.SettingsTab(tab) : MenuRoutes.Settings();
        else page = MenuRoutes.ForFeature(call.Lower(2)) ?? MenuRoutes.Resolve(target);
        if (page == null) return NoPage(target);
        Menu.SetSearch(string.Empty);
        Menu.Open(page);
        return CommandResult.Ok($"menu: {page.Path}") with { Settle = SettleFrames };
    }

    private static string Tree(string only)
    {
        var lines = new List<string> { "ok menu tree:" };
        var shown = Menu.Visible ? MenuRoutes.Page : null;
        foreach (var tab in MenuTree.Tabs)
        {
            lines.Add($"tab {tab.Id} {RowText.Quote(tab.Title)}{(shown?.Node.Tab == tab ? " selected" : string.Empty)}");
            if (only.Length > 0 && only != tab.Id) continue;
            foreach (var node in tab.Nodes)
            {
                string state = NodeState(node);
                string master = node.Entry.Switch is { } s ? (s.Value ? "on" : "off") : "none";
                string on = node.Feature is { } f ? (f.CanTurnOff ? (FeatureSwitches.IsOn(f) ? "yes" : "no") : "fixed") : "fixed";
                lines.Add($"  node {node.Path} {RowText.Quote(node.Title)} state={state} switch={master} on={on}{(node.Leaf && shown?.Node == node ? " selected" : string.Empty)}");
                foreach (var group in node.Entry.Groups)
                {
                    lines.Add($"    group {RowText.Quote(group.Title)}");
                    foreach (var item in Safe.Call(group.Items, Array.Empty<TreeItem>(), node.Path + "." + group.Title))
                        lines.Add($"      item {item.Number ?? "-"} {RowText.Quote(item.Label)} state={item.State.ToString().ToLowerInvariant()}");
                }
                if (node.Leaf) continue;
                foreach (var page in node.Pages)
                    lines.Add($"    page {page.Path} {RowText.Quote(page.Page.Title)}{(shown == page ? " selected" : string.Empty)}");
            }
        }
        return string.Join("\n", lines);
    }

    private static string NodeState(MenuNode node)
    {
        if (node.Feature is not { } feature) return "none";
        StatusState state = feature.State != FeatureState.Running ? StatusState.Off
            : node.Entry.Switch is { } master ? (master.Value ? StatusState.Active : StatusState.Off) : feature.Status.State;
        return state.ToString().ToLowerInvariant();
    }

    // One page: its header line, then every row that shows (the entry's top rows first), each with its warnings and lines.
    private static string Page(PageNode page, string verb)
    {
        var node = page.Node;
        string status = Status(page);
        string master = page.Page.Switch is { } s ? (s.Value ? "on" : "off") : "none";
        string banner = page.Node.Feature is { Record.TurnedOff: true } ? "turned-off" : page.Problem != null ? "problem" : "none";
        var lines = new List<string> { $"ok menu {verb} {page.Path} {RowText.Quote(page.Page.Title)} status={status} switch={master} banner={banner}" };
        if (page.Problem != null) lines.Add($"  warn {RowText.Quote(page.Problem)}");
        if (page.Page.Block != null) lines.Add($"  block {RowText.Quote(page.Page.Block)}");
        foreach (string line in page.Page.Block != null ? MenuBlocks.Bridge(page.Page.Block, null) : Enumerable.Empty<string>()) lines.Add("    " + line);
        bool enabled = node.Entry.ContentEnabled == null || Safe.Call(node.Entry.ContentEnabled, true, node.Path + ".enabled");
        var top = new RowContext { Path = page.Path, Live = node.Live };
        if (node.Entry.Layout == MenuLayout.Library && node.Entry.Library is { } library && page == node.Pages[0])
        {
            foreach (string line in LibraryLayout.BridgeLines(library)) lines.Add("  " + line);
            // An open editor's rows, as a page's.
            if (Safe.Call(library.Editor, null, node.Path + ".editor") is { } editor)
            {
                foreach (var row in Safe.Call(editor.Rows, Array.Empty<MenuRow>(), node.Path + ".editor.rows")) AddRow(lines, row, top.With(enabled: enabled));
            }
            return string.Join("\n", lines);
        }
        foreach (var row in node.Entry.Top) AddRow(lines, row, top);
        foreach (var row in page.Rows) AddRow(lines, row, top.With(enabled: enabled));
        return string.Join("\n", lines);
    }

    private static string Status(PageNode page) =>
        PageView.StatusOf(page) is { } s ? $"{s.State.ToString().ToLowerInvariant()}:{RowText.Quote(s.Text)}" : "none";

    private static void AddRow(List<string> lines, MenuRow row, RowContext ctx)
    {
        if (RowKinds.For(row) is not { } renderer)
        {
            lines.Add($"  warn {RowText.Quote($"no renderer for {row.GetType().Name}")}");
            return;
        }
        if (!renderer.Shows(row, ctx)) return;
        var text = renderer.Bridge(row, Safe.Enabled(row) ? ctx : ctx.With(enabled: false));
        lines.Add("  " + text.Line());
        foreach (string warning in text.Warnings) lines.Add($"    warn {RowText.Quote(warning)}");
        foreach (string line in text.Lines) lines.Add("    " + line);
    }

    private static string Find(string query)
    {
        var hits = MenuSearch.Query(query);
        var lines = new List<string> { $"ok menu find {RowText.Quote(query)}: {hits.Count} results" };
        foreach (var hit in hits)
        {
            if (hit.Row == null)
            {
                lines.Add(hit.Page != null ? $"  {hit.Page.Path} {RowText.Quote(hit.Page.Page.Title)} page" : $"  {hit.Node.Path} {RowText.Quote(hit.Node.Title)} node");
                continue;
            }
            var page = hit.Page!;
            var ctx = new RowContext { Path = page.Path, Live = hit.Node.Live };
            if (RowKinds.For(hit.Row) is not { } renderer) continue;
            var text = renderer.Bridge(hit.Row, ctx);
            string label = text.Label ?? text.Kind;
            string value = text.Value != null ? $" value={RowText.Quote(text.Value)}" : string.Empty;
            string hidden = PageView.HiddenNote(hit.Row) is { } note ? $" visible=no ({note.TrimEnd('.').Replace("Shows when", "shows when").Replace("Hidden by", "hidden by")})" : string.Empty;
            string state = hit.Node.Feature is { } feature ? MenuSearch.StateNote(feature) : string.Empty;
            lines.Add($"  {page.Path} {RowText.Quote(label)} {text.Kind}{value}{hidden}{state}");
        }
        return string.Join("\n", lines);
    }
}
