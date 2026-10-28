using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

// The macro library's layout (contracts/menu.md), drawn in the page area while a Library entry's page shows: the
// toolbar (class dropdown, buttons, status tag), then either the open editor (header, buttons, rows as a page draws
// them) or the library tree on the left with the selection's actions on the right, and the footer switches and buttons
// at the bottom. Keys move along the toolbar, tree lines and buttons as on any page; on a tree line Enter or Space
// ticks it, Left and Right close and open a set, and the line's Edit is the next stop.
internal static class LibraryLayout
{
    private const float LineHeight = 22f, Indent = 18f, ActionsWidth = 168f, FooterHeight = 34f, ToolbarHeight = 26f;

    private static readonly HashSet<string> s_closed = new();
    private static Vector2 s_treeScroll;
    private static string? s_editorKey;
    private static int s_undoFrame = -1;

    public static void Draw(PageNode page, LibraryView view, Rect inner)
    {
        // Ctrl+Z on the tab undoes, unless a text field has the keys. Read from the keys once a frame, as the menu's
        // own navigation is (the window doesn't get key events while the game has the input).
        if (Event.current.type == EventType.Layout && Time.frameCount != s_undoFrame && view.Undo != null && GUIUtility.keyboardControl == 0
            && InputState.KeyDown(KeyCode.Z) && (InputState.Key(KeyCode.LeftControl) || InputState.Key(KeyCode.RightControl)))
        {
            s_undoFrame = Time.frameCount;
            Safe.Run(view.Undo, "library.undo");
        }
        float y = inner.y;
        var editor = page == page.Node.Pages[0] ? Safe.Call(view.Editor, null, page.Path + ".editor") : null;
        // The breadcrumb names where the open editor's item lives; the toolbar is for the library, so it hides then.
        string crumb = page != page.Node.Pages[0] ? page.Node.Crumb + " › " + page.Page.Title
            : editor?.Place != null && Safe.Call(editor.Place, null, "library.editor.place") is { } place ? page.Node.Crumb + " › " + place
            : page.Node.Crumb;
        if (Event.current.type == EventType.Repaint) Widgets.Paint(new Rect(inner.x, y, inner.width, 14f), crumb, Widgets.Style(() => Theme.Crumb, GUI.skin.label));
        y += 18f;
        if (editor == null && page == page.Node.Pages[0])
        {
            Toolbar(page, view, new Rect(inner.x, y, inner.width, ToolbarHeight));
            y += ToolbarHeight + 8f;
        }
        if (view.Banner != null && Safe.Call(view.Banner, null, page.Path + ".banner") is { } banner)
        {
            float h = TextRows.BannerHeight(banner, inner.width, null);
            TextRows.Banner(new Rect(inner.x, y, inner.width, h), banner, null, "banner.library");
            y += h + 8f;
        }

        var footer = new Rect(inner.x, inner.yMax - FooterHeight, inner.width, FooterHeight);
        var body = new Rect(inner.x, y, inner.width, footer.y - y - 6f);
        var library = page.Node.Pages[0];
        // Another page of the entry (Settings) shows in the library's place, with Close back to it.
        if (page != library) OtherPage(page, library, body);
        else if (editor != null) Editor(page, editor, body);
        else
        {
            s_editorKey = null;
            Library(view, body);
        }
        Footer(page, view, footer);
    }

    private const float FieldWidth = 360f;

    // The toolbar's text field: the placeholder while empty and unfocused, Enter hands the text to the view.
    private static void ToolbarText(ToolbarField field, Rect rect)
    {
        string text = Safe.Call(field.Get, string.Empty, field.Key + ".get");
        string control = "field." + field.Key;
        bool focused = GUI.GetNameOfFocusedControl() == control;
        if (focused && Event.current.type == EventType.KeyDown && Event.current.keyCode is KeyCode.Return or KeyCode.KeypadEnter && field.Submit != null)
        {
            Event.current.Use();
            Safe.Run(field.Submit, field.Key + ".submit");
            return;
        }
        GUI.SetNextControlName(control);
        string typed = GUI.TextField(rect, text, field.MaxLength, Widgets.Style(() => Theme.TreeFilter, GUI.skin.textField));
        if (typed != text) Safe.Run(() => field.Set(typed), field.Key + ".set");
        if (Focus.Item(field.Key, rect)) GUI.FocusControl(control);
        if (Event.current.type == EventType.Repaint && typed.Length == 0 && !focused)
            Widgets.Paint(new Rect(rect.x + 6f, rect.y, rect.width - 12f, rect.height), field.Placeholder, Widgets.Style(() => Theme.FieldDim, GUI.skin.label));
    }

    // Class dropdown, the toolbar's buttons, and the status tag on the right.
    private static void Toolbar(PageNode page, LibraryView view, Rect r)
    {
        var classes = Safe.Call(view.Classes, Array.Empty<string>(), "library.classes");
        float x = r.x;
        if (classes.Count > 0)
        {
            float w = Mathf.Max(130f, Dropdown.WidthFor(classes));
            Dropdown.Draw(new Rect(x, r.y, w, r.height), "class", classes, Safe.Call(view.Class, 0, "library.class"), view.SetClass, tip: "The class whose sets show and run");
            x += w + 8f;
        }
        foreach (var button in view.Toolbar)
        {
            float w = Controls.ButtonWidth(button.Label);
            bool enabled = GUI.enabled;
            GUI.enabled = enabled && Safe.Enabled(button);
            if (Controls.Button(new Rect(x, r.y, w, r.height), button.Label, button.Key, button.Primary, TextRows.Tip(button))) Run(button);
            GUI.enabled = enabled;
            x += w + 6f;
        }
        if (view.Field is { } field)
        {
            float w = Mathf.Min(FieldWidth, r.xMax - x - 4f);
            if (w > 40f)
            {
                ToolbarText(field, new Rect(x, r.y, w, r.height));
                x += w + 6f;
            }
        }
        float conflict = ConflictTag.Draw(page.Node.Feature, x + 4f, r.center.y, "conflict.library");
        if (conflict > 0f) x += conflict + 10f;
        // The status as plain text with its shape, right-aligned: readable on the bar, never a coloured badge.
        if (view.Status != null && Safe.Call(view.Status, new PageStatus(StatusState.Off, "off"), "library.status") is var status)
        {
            GUIStyle style = Recoloured(Widgets.Style(() => Theme.Label, GUI.skin.label), Theme.Text);
            float w = Mathf.Min(TextCache.Width(status.Text, style) + 6f, r.xMax - x - 14f);
            if (w > 0f && Event.current.type == EventType.Repaint)
            {
                var text = new Rect(r.xMax - w, r.y, w, r.height);
                Menu.DrawState(new Rect(text.x - 12f, Mathf.Round(r.center.y - 3.5f), 7f, 7f), status.State, Theme.StateColor(status.State));
                Widgets.Paint(text, Controls.Fit(status.Text, style, w), style);
            }
        }
    }

    private static readonly Dictionary<(GUIStyle, Color), GUIStyle> s_recoloured = new();

    // A copy of a style in another text colour (state words and hints in a readable colour).
    private static GUIStyle Recoloured(GUIStyle style, Color color)
    {
        if (!Widgets.Themed) return style;
        if (s_recoloured.Count > 32) s_recoloured.Clear();
        if (s_recoloured.TryGetValue((style, color), out var copy)) return copy;
        copy = new GUIStyle(style);
        copy.normal.textColor = color;
        copy.hover.textColor = color;
        s_recoloured[(style, color)] = copy;
        return copy;
    }

    private static void Run(ActionButton button)
    {
        if (button.OnClick != null) Safe.Run(button.OnClick, "library." + button.Key);
        else Core.Actions.Run(button.Action, "menu button");
    }

    // ---- The library: tree left, actions right ----

    private static void Library(LibraryView view, Rect body)
    {
        var lines = Shown(view);
        var tree = new Rect(body.x, body.y, body.width - ActionsWidth - 12f, body.height);
        var actions = new Rect(tree.xMax + 12f, body.y, ActionsWidth, body.height);
        if (Widgets.Themed && Event.current.type == EventType.Repaint)
        {
            Theme.DrawSliced(tree, Theme.FieldBg, Theme.CardRadius);
            Theme.Fill(new Rect(actions.x - 6f, body.y, 1f, body.height), Theme.DividerColor);
        }

        // A selection whose line is gone (deleted, another class) is dropped.
        if (view.Selected != null && view.Line(view.Selected) == null) view.Selected = null;

        float content = lines.Count * LineHeight + 4f;
        var inner = new Rect(0f, 0f, tree.width - 12f, content);
        var visible = new Rect(tree.x + 2f, tree.y + 2f, tree.width - 4f, tree.height - 4f);
        s_treeScroll = GUI.BeginScrollView(visible, s_treeScroll, inner, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);
        Focus.InScroll = "library";
        Popups.PushOrigin(visible.position - s_treeScroll);
        try
        {
            for (int i = 0; i < lines.Count; i++)
            {
                var (line, last, ancestors) = lines[i];
                var r = new Rect(0f, i * LineHeight, inner.width, LineHeight);
                if (r.yMax < s_treeScroll.y - 40f || r.y > s_treeScroll.y + visible.height + 40f) continue;
                Line(view, line, last, ancestors, r);
            }
        }
        finally
        {
            Popups.PopOrigin();
            Focus.InScroll = null;
            GUI.EndScrollView();
        }
        if (Event.current.type == EventType.Repaint && Focus.TakeScrollTarget("library") is Rect target)
        {
            if (target.yMin < s_treeScroll.y) s_treeScroll.y = Mathf.Max(0f, target.yMin - 4f);
            else if (target.yMax > s_treeScroll.y + visible.height) s_treeScroll.y = target.yMax - visible.height + 4f;
        }
        if (lines.Count == 0 && Event.current.type == EventType.Repaint)
            Widgets.Paint(new Rect(tree.x + 10f, tree.y + 8f, tree.width - 20f, 40f), view.EmptyText,Recoloured(Widgets.Style(() => Theme.Hint, GUI.skin.label), Theme.Secondary));

        Actions(view, actions);
    }

    private const float DoubleClickSeconds = 0.4f;
    private static string? s_lastClickKey;
    private static float s_lastClickAt;

    // The lines shown: sets, and the macros of the open ones, with tree-line flags.
    private static List<(LibraryLine Line, bool Last, bool Ancestor)> Shown(LibraryView view)
    {
        var all = Safe.Call(view.Lines, Array.Empty<LibraryLine>(), "library.lines");
        var shown = new List<(LibraryLine, bool, bool)>();
        bool open = true;
        for (int i = 0; i < all.Count; i++)
        {
            var line = all[i];
            if (line.Depth == 0) open = !s_closed.Contains(line.Key);
            else if (!open) continue;
            bool last = i + 1 >= all.Count || all[i + 1].Depth < line.Depth || (line.Depth == 0 && NextTop(all, i) < 0);
            shown.Add((line, last, false));
        }
        return shown;
    }

    private static int NextTop(IReadOnlyList<LibraryLine> all, int from)
    {
        for (int i = from + 1; i < all.Count; i++)
        {
            if (all[i].Depth == 0) return i;
        }
        return -1;
    }

    private static void Line(LibraryView view, LibraryLine line, bool last, bool _, Rect r)
    {
        GUIStyle label = Widgets.Style(() => Theme.Label, GUI.skin.label);
        bool selected = view.Selected == line.Key;
        // Sets keep room for their chevron; macros start right after their branch, at the tick box (or the label).
        float x = r.x + 4f + line.Depth * Indent;
        var chevron = new Rect(x, r.y, 14f, r.height);
        float boxX = line.Parent || line.Depth == 0 ? x + 16f : x;
        var box = new Rect(boxX, r.y, 12f, r.height);
        bool open = line.Parent && !s_closed.Contains(line.Key);

        bool chevronClicked = line.Parent && GUI.Button(chevron, GUIContent.none, GUIStyle.none);
        bool boxClicked = line.Ticked != null && GUI.Button(new Rect(box.x - 2f, r.y, box.width + 4f, r.height), GUIContent.none, GUIStyle.none);
        // A line without a tick box starts its label where the box would be: a set (Slots) right after its chevron, a
        // macro line (Shared Conditions) in the tick box column of its siblings.
        float restX = line.Ticked == null ? (line.Parent ? chevron.xMax + 2f : box.x - 4f) : box.xMax + 4f;
        var rest = new Rect(restX, r.y, r.xMax - restX, r.height);
        bool lineClicked = GUI.Button(rest, GUIContent.none, GUIStyle.none);
        bool renaming = view.Renaming == line.Key;
        // The line being renamed holds the focus, so the Enter that keeps the name presses nothing else.
        if (renaming && Event.current.type == EventType.Layout) Focus.Take("line." + line.Key);
        bool activated = Focus.Item("line." + line.Key, r, out int adjust);
        // The line reached by keys is the selection, so its actions are the next stops.
        if (Focus.Ringed("line." + line.Key) && view.Selected != line.Key && view.Renaming == null) view.Selected = line.Key;
        if (renaming && activated)
        {
            // Enter in the field keeps the name.
            CommitRename(view, line);
            activated = false;
        }
        if (chevronClicked || (line.Parent && adjust != 0 && (adjust > 0) != open))
        {
            if (!s_closed.Remove(line.Key)) s_closed.Add(line.Key);
        }
        if (boxClicked || (activated && line.Ticked != null)) Safe.Run(() => view.Tick(line), "library.tick");
        if (lineClicked || activated || boxClicked)
        {
            // A double click opens the line (clickCount isn't kept on the mouse-up a button reports, so it is timed here).
            if (lineClicked)
            {
                float now = Time.unscaledTime;
                bool twice = s_lastClickKey == line.Key && now - s_lastClickAt <= DoubleClickSeconds;
                s_lastClickKey = twice ? null : line.Key;
                s_lastClickAt = now;
                if (twice && view.Open != null) Safe.Run(() => view.Open(line), "library.open");
            }
            view.Selected = line.Key;
        }
        if (view.Renaming == line.Key) RenameField(view, line, new Rect(rest.x + 2f, r.y + 1f, rest.width - 4f, r.height - 2f));
        if (Event.current.type != EventType.Repaint) return;

        bool hover = r.Contains(Event.current.mousePosition);
        if (Widgets.Themed)
        {
            if (selected) Theme.DrawSliced(r, Theme.SelectionBg, Theme.CardRadius);
            else if (hover) Theme.DrawSliced(r, Theme.HoverBg, Theme.CardRadius);
            if (line.Depth > 0)
            {
                // The branch: down from the parent's chevron, then across to just before the box or label.
                Color tint = new(Theme.Faint.r, Theme.Faint.g, Theme.Faint.b, 0.8f);
                float lx = r.x + 4f + (line.Depth - 1) * Indent + 6f;
                Theme.Fill(new Rect(lx, r.y, 1f, last ? r.height / 2f : r.height), tint);
                float end = box.x - 4f;
                Theme.Fill(new Rect(lx, r.center.y, Mathf.Max(4f, end - lx), 1f), tint);
            }
            if (line.Parent) Theme.Tinted(new Rect(chevron.x + 1f, Mathf.Round(r.center.y - 6f), 12f, 12f), open ? Theme.Chevron : Theme.ChevronRight, Theme.Dim);
        }
        if (line.Ticked is bool ticked) Controls.Check(box, ticked ? 1 : 0, GUI.enabled && hover);
        float stateWidth = Mathf.Min(Controls.StatePillWidth(line.State), rest.width * 0.45f);
        if (view.Renaming == line.Key) return;
        Widgets.Paint(new Rect(rest.x + 4f, r.y, rest.width - stateWidth - 12f, r.height), Controls.Fit(line.Label, label, rest.width - stateWidth - 16f), label);
        Controls.StatePill(new Rect(rest.xMax - stateWidth - 4f, r.y, stateWidth, r.height), line.State, line.Shape);
    }

    // The rename field in place of the label: Enter (the menu's activation of this line) keeps the name, Esc (the
    // menu's Back) drops it.
    private static void RenameField(LibraryView view, LibraryLine line, Rect r)
    {
        const string control = "library.rename";
        GUI.SetNextControlName(control);
        view.RenameText = GUI.TextField(r, view.RenameText, Macro.MaxName, Widgets.Style(() => Theme.TreeFilter, GUI.skin.textField));
        if (Event.current.type == EventType.Repaint && GUI.GetNameOfFocusedControl() != control) GUI.FocusControl(control);
    }

    private static void CommitRename(LibraryView view, LibraryLine line)
    {
        string name = view.RenameText.Trim();
        view.Renaming = null;
        GUIUtility.keyboardControl = 0;
        if (name.Length > 0 && view.Rename != null) Safe.Run(() => view.Rename(line, name), "library.rename");
    }

    // The selection's buttons, one per row, and the hint under them.
    private static void Actions(LibraryView view, Rect r)
    {
        var selected = view.Line(view.Selected);
        var buttons = Safe.Call(() => view.Actions(selected), Array.Empty<ActionButton>(), "library.actions");
        float y = r.y;
        if (selected == null && Event.current.type == EventType.Repaint)
            Widgets.Paint(new Rect(r.x, y, r.width, 40f), view.NoSelectionText,Recoloured(Widgets.Style(() => Theme.Hint, GUI.skin.label), Theme.Secondary));
        foreach (var button in buttons)
        {
            bool enabled = GUI.enabled;
            GUI.enabled = enabled && Safe.Enabled(button);
            if (Controls.Button(new Rect(r.x, y, r.width, 24f), button.Label, "action." + button.Key, button.Primary, TextRows.Tip(button))) Run(button);
            GUI.enabled = enabled;
            y += 30f;
        }
        if (view.ActionHint != null && Safe.Call(() => view.ActionHint(selected), null, "library.hint") is { } hint && Event.current.type == EventType.Repaint)
        {
            float h = Controls.HintHeight(hint, r.width);
            Widgets.Paint(new Rect(r.x, y + 4f, r.width, h), hint, Recoloured(Widgets.Style(() => Theme.Hint, GUI.skin.label), Theme.Secondary));
        }
    }

    // ---- Another page of the entry: its title, Close, then its rows ----

    private static void OtherPage(PageNode page, PageNode library, Rect body)
    {
        if (s_editorKey != page.Path)
        {
            s_editorKey = page.Path;
            PageView.ResetRows();
        }
        var header = new Rect(body.x, body.y, body.width, 26f);
        GUIStyle title = Widgets.Style(() => Theme.PageTitle, GUI.skin.label);
        float w = Controls.ButtonWidth("Close");
        if (Controls.Button(new Rect(header.xMax - w, header.center.y - 12f, w, 24f), "Close", "page.close")) MenuRoutes.Show(library);
        if (Event.current.type == EventType.Repaint) Widgets.Paint(new Rect(header.x, header.y, header.width - w - 8f, header.height), page.Page.Title, title);
        ConflictTag.Draw(page.Node.Feature, header.x + TextCache.Width(page.Page.Title, title) + 12f, header.center.y, "conflict.page");
        float y = header.yMax + 6f;
        PageView.DrawRowList(page, page.Rows, new Rect(body.x, y, body.width, body.yMax - y));
    }

    // ---- The editor: header, then its rows as a page's ----

    private static void Editor(PageNode page, LibraryEditor editor, Rect body)
    {
        if (s_editorKey != editor.Key)
        {
            s_editorKey = editor.Key;
            PageView.ResetRows();
        }
        var header = new Rect(body.x, body.y, body.width, 26f);
        GUIStyle title = Widgets.Style(() => Theme.PageTitle, GUI.skin.label);
        float right = header.xMax;
        for (int i = editor.Buttons.Count - 1; i >= 0; i--)
        {
            var button = editor.Buttons[i];
            float w = Controls.ButtonWidth(button.Label);
            right -= w;
            bool enabled = GUI.enabled;
            GUI.enabled = enabled && Safe.Enabled(button);
            if (Controls.Button(new Rect(right, header.center.y - 12f, w, 24f), button.Label, "editor." + button.Key, button.Primary, TextRows.Tip(button))) Run(button);
            GUI.enabled = enabled;
            right -= 6f;
        }
        string text = Safe.Call(editor.Title, string.Empty, "library.editor.title");
        float tx = header.x;
        // A slot's number as a small square badge before its title, in the accent colour as the tree shows slot numbers.
        if (editor.Badge != null && Safe.Call(editor.Badge, null, "library.editor.badge") is { } badge)
        {
            GUIStyle number = Widgets.Style(() => Theme.TreeNumber, GUI.skin.label);
            float bw = Mathf.Max(26f, TextCache.Width(badge, number) + 12f);
            var square = new Rect(tx, header.center.y - 13f, bw, 26f);
            if (Event.current.type == EventType.Repaint)
            {
                if (Widgets.Themed) Theme.DrawSliced(square, Theme.ButtonBg, Theme.ControlRadius);
                Widgets.Paint(square, Widgets.Themed ? Widgets.Colored(badge, Theme.LightText(Theme.AccentText)) : badge, number);
            }
            tx += bw + 10f;
        }
        // A conflict pill follows the title; the title gives up room for it.
        float pill = ConflictTag.Width(page.Node.Feature);
        float room = right - tx - 8f - (pill > 0f ? pill + 12f : 0f);
        string fitted = Controls.Fit(text, title, room);
        if (Event.current.type == EventType.Repaint)
        {
            // The title's own height, centred on the header's middle like the badge (the title style is top-aligned).
            float th = title.CalcHeight(new GUIContent(fitted), room);
            Widgets.Paint(new Rect(tx, Mathf.Round(header.center.y - th / 2f), room, th), fitted, title);
        }
        if (pill > 0f) ConflictTag.Draw(page.Node.Feature, tx + Mathf.Min(TextCache.Width(fitted, title), room) + 12f, header.center.y, "conflict.editor");
        float y = header.yMax + 6f;
        if (editor.Note != null && Safe.Call(editor.Note, null, "library.editor.note") is { } note)
        {
            if (Event.current.type == EventType.Repaint) Widgets.Paint(new Rect(body.x, y, body.width, 18f), note, Recoloured(Widgets.Style(() => Theme.Hint, GUI.skin.label), Theme.Secondary));
            y += 22f;
        }
        var rows = Safe.Call(editor.Rows, Array.Empty<MenuRow>(), "library.editor.rows");
        PageView.DrawRowList(page, rows, new Rect(body.x, y, body.width, body.yMax - y));
    }

    // ---- Footer: switches left, buttons right ----

    private static void Footer(PageNode page, LibraryView view, Rect r)
    {
        if (Widgets.Themed) Theme.Fill(new Rect(r.x, r.y, r.width, 1f), Theme.DividerColor);
        GUIStyle label = Widgets.Style(() => Theme.Label, GUI.skin.label);
        float x = r.x, cy = r.y + 6f;
        foreach (var toggle in view.Footer)
        {
            var sw = new Rect(x, cy, Controls.SwitchWidth, 22f);
            if (Controls.Switch(sw, toggle.Value, "foot." + toggle.Id, toggle.Help ?? toggle.Label) != toggle.Value) SettingRows.Flip(toggle, "menu library footer");
            float w = TextCache.Width(toggle.Label, label);
            if (Event.current.type == EventType.Repaint) Widgets.Paint(new Rect(sw.xMax + 6f, cy, w + 4f, 22f), toggle.Label, label);
            x = sw.xMax + 6f + w + 18f;
        }
        float right = r.xMax;
        for (int i = view.FooterButtons.Count - 1; i >= 0; i--)
        {
            var button = view.FooterButtons[i];
            float w = Controls.ButtonWidth(button.Label);
            right -= w;
            bool enabled = GUI.enabled;
            GUI.enabled = enabled && Safe.Enabled(button);
            if (Controls.Button(new Rect(right, cy - 1f, w, 24f), button.Label, "foot." + button.Key, button.Primary, TextRows.Tip(button))) Run(button);
            GUI.enabled = enabled;
            right -= 6f;
        }
        // The entry's other pages (Settings) beside them; lit while open, a second press goes back.
        for (int i = page.Node.Pages.Count - 1; i >= 1; i--)
        {
            var other = page.Node.Pages[i];
            float w = Controls.ButtonWidth(other.Page.Title);
            right -= w;
            if (Controls.Button(new Rect(right, cy - 1f, w, 24f), other.Page.Title, "page." + MenuEntry.Slug(other.Page.Title), page == other))
                MenuRoutes.Show(page == other ? page.Node.Pages[0] : other);
            right -= 6f;
        }
    }

    /// <summary>The lines as the bridge prints them: "tree slots/2 on casting" (tick box on/off/none, state).</summary>
    public static IEnumerable<string> BridgeLines(LibraryView view)
    {
        var editor = Safe.Call(view.Editor, null, "library.editor");
        if (editor != null)
        {
            yield return $"editor {RowText.Quote(editor.Key)} {RowText.Quote(Safe.Call(editor.Title, string.Empty, "library.editor.title"))}";
            if (editor.Note != null && Safe.Call(editor.Note, null, "library.editor.note") is { } note) yield return $"note {RowText.Quote(note)}";
            foreach (var button in editor.Buttons) yield return $"button {button.Key} {RowText.Quote(button.Label)} enabled={(Safe.Enabled(button) ? "yes" : "no")}";
            yield break;
        }
        var classes = Safe.Call(view.Classes, Array.Empty<string>(), "library.classes");
        int index = Safe.Call(view.Class, 0, "library.class");
        yield return $"class {RowText.Quote(index >= 0 && index < classes.Count ? classes[index] : "?")}";
        foreach (var line in Safe.Call(view.Lines, Array.Empty<LibraryLine>(), "library.lines"))
        {
            string tick = line.Ticked is bool on ? (on ? "on" : "off") : "none";
            yield return $"tree {line.Key} {tick} {RowText.Quote(line.State)} {RowText.Quote(line.Label)}{(view.Selected == line.Key ? " selected" : string.Empty)}"
                + (view.Renaming == line.Key ? $" renaming {RowText.Quote(view.RenameText)}" : string.Empty);
        }
        var selected = view.Line(view.Selected);
        foreach (var button in Safe.Call(() => view.Actions(selected), Array.Empty<ActionButton>(), "library.actions"))
            yield return $"action {button.Key} {RowText.Quote(button.Label)} enabled={(Safe.Enabled(button) ? "yes" : "no")}";
        foreach (var toggle in view.Footer) yield return $"switch {toggle.Id} {(toggle.Value ? "on" : "off")}";
    }
}
