using EpochAssist.Core;
using UnityEngine;
using BuildInfo = EpochAssist.Core.BuildInfo;

namespace EpochAssist.Ui;

// The 40 px bar across the top: the logo and version, the tabs (editor style: the selected tab takes the page's colour
// and opens into it), then the search field, the profile in use and the close button. Its empty space drags the window.
internal static class TopBar
{
    public const float Height = 40f;
    private const string SearchControl = "EpochAssistSearch";
    private const float TabHeight = 34f;

    /// <summary>Where nothing was drawn, for dragging the window.</summary>
    public static Rect DragArea { get; private set; }

    /// <summary>Set by Ctrl+F: the search field takes the keyboard at the next pass.</summary>
    public static bool FocusSearch;

    public static bool SearchFocused => GUI.GetNameOfFocusedControl() == SearchControl;

    public static void Draw(Rect bar)
    {
        Focus.Scope = "top";
        GUIStyle version = Widgets.Style(() => Theme.Version, GUI.skin.label);
        float x = bar.x + 10f;
        // Beside the logo, the version sits on the wordmark's baseline (the logo's foot), not the bar's middle.
        float versionShift = 0f;
        if (Theme.Logo is { } logo && Widgets.Themed)
        {
            versionShift = Mathf.Round(8.5f - version.fontSize * 0.4f);
            if (Event.current.type == EventType.Repaint) GUI.DrawTexture(Menu.PixelSnap(new Rect(x, bar.center.y - 8.5f, 145f, 17f)), logo);
            x += 145f;
        }
        else
        {
            GUIStyle brand = Widgets.Style(() => Theme.Brand, GUI.skin.label);
            float w = TextCache.Width(BuildInfo.Name, brand);
            if (Event.current.type == EventType.Repaint) Widgets.Paint(new Rect(x, bar.y, w, bar.height), BuildInfo.Name, brand);
            x += w;
        }
        x += 10f;
        string v = "v" + BuildInfo.Version;
        float vw = TextCache.Width(v, version);
        if (Event.current.type == EventType.Repaint) Widgets.Paint(new Rect(x, bar.y + versionShift, vw, bar.height), v, version);
        x += vw + 14f;

        // Tab edges, dividers and the bar's bottom line sit on whole screen pixels, one screen pixel wide, so the selected
        // tab's fill meets its dividers and opens into the page with no seam at any UI scale.
        float px = Menu.ScreenPixel;
        float bottom = Menu.SnapY(bar.yMax), top = Menu.SnapY(bar.yMax - TabHeight);
        // The bar's bottom line, broken under the selected tab (its fill covers it).
        if (Widgets.Themed) Theme.Fill(new Rect(bar.x, bottom - px, bar.width, px), Theme.DividerColor);
        var tabs = MenuTree.Tabs;
        var shown = MenuRoutes.Page?.Node.Tab;
        bool searching = Menu.Search.Trim().Length > 0;
        x = Menu.SnapX(x);
        if (Widgets.Themed && tabs.Count > 0) Theme.Fill(new Rect(x, top, px, bottom - px - top), Theme.DividerColor);
        x += px;
        foreach (var tab in tabs)
        {
            GUIStyle style = Widgets.Style(() => tab == shown && !searching ? Theme.TabOn : Theme.Tab, GUI.skin.button);
            float edge = Menu.SnapX(x + TextCache.Width(tab.Title, style) + 28f);
            var r = new Rect(x, top, edge - x, bottom - top);
            bool selected = tab == shown && !searching;
            if (selected && Widgets.Themed) Theme.Fill(r, Theme.Window);
            bool clicked = GUI.Button(r, GUIContent.none, GUIStyle.none);
            if (Event.current.type == EventType.Repaint) Widgets.Paint(r, tab.Title, style);
            if (Widgets.Themed) Theme.Fill(new Rect(edge, top, px, bottom - px - top), Theme.DividerColor);
            if ((Focus.Item("tab." + tab.Id, r) || clicked) && (!selected || searching))
            {
                Menu.SetSearch(string.Empty);
                if (MenuRoutes.ForTab(tab) is { } page) MenuRoutes.Show(page);
            }
            x = edge + px;
        }
        float tabsEnd = x;

        // From the right: close, profile, search.
        float right = bar.xMax - 8f;
        var close = new Rect(right - 22f, Mathf.Round(bar.center.y - 11f), 22f, 22f);
        bool closeClicked = GUI.Button(close, GUIContent.none, GUIStyle.none);
        if (Event.current.type == EventType.Repaint)
        {
            if (Widgets.Themed) Theme.Tinted(new Rect(close.center.x - 5f, close.center.y - 5f, 10f, 10f), Theme.Cross, close.Contains(Event.current.mousePosition) ? Theme.Text : Theme.Secondary);
            else Widgets.Paint(close, "×", GUI.skin.label);
        }
        right = close.x - 6f;

        var profiles = Profiles.All;
        var names = profiles.Select(p => p.Name).Append("Manage profiles…").ToList();
        int active = profiles.ToList().FindIndex(p => p.Id == Profiles.Active.Id);
        GUIStyle field = Widgets.Style(() => Theme.FieldText, GUI.skin.label);
        float pw = Mathf.Clamp(TextCache.Width(Profiles.Active.Name, field) + field.padding.horizontal + 22f, 80f, 150f);
        var profile = new Rect(right - pw, Mathf.Round(bar.center.y - 12f), pw, 24f);
        right = profile.x - 6f;

        bool wide = SearchFocused || Menu.Search.Length > 0;
        float sw = Mathf.Min(wide ? 260f : 130f, Mathf.Max(80f, right - tabsEnd - 8f));
        var search = new Rect(right - sw, Mathf.Round(bar.center.y - 12f), sw, 24f);
        SearchField(search);

        Dropdown.Draw(profile, "profile", names, active, pick =>
        {
            if (pick >= profiles.Count)
            {
                Menu.OpenProfiles();
                return;
            }
            var chosen = profiles[pick];
            if (!Profiles.Use(chosen.Id, out string reason)) Toasts.Show("profiles", $"Could not switch to {chosen.Name}: {reason}.", ToastKind.Warning);
            else Toasts.Show("profiles", Profiles.HasCharacter ? $"This character now uses {chosen.Name}." : $"Editing {chosen.Name}.", ToastKind.Info);
        }, tip: Profiles.HasCharacter ? "The profile this character uses" : "The profile you edit");

        if (Focus.Item("close", close) || closeClicked) Actions.Run("menu.close", "menu ×");
        DragArea = new Rect(tabsEnd, bar.y, Mathf.Max(0f, search.x - tabsEnd), bar.height);
        Focus.Scope = string.Empty;
    }

    // The search field: a placeholder while empty, a clear button inside its right end while not.
    private static void SearchField(Rect field)
    {
        var clear = new Rect(field.xMax - 22f, field.y, 22f, field.height);
        string search = Menu.Search;
        bool hasText = search.Length > 0;
        // Drawn first so it takes its click before the field under it, and always, so the field's control id doesn't move.
        bool cleared = GUI.Button(clear, GUIContent.none, GUIStyle.none) && hasText;
        GUI.SetNextControlName(SearchControl);
        string typed = GUI.TextField(field, search, 64, Widgets.Style(() => Theme.TreeFilter, GUI.skin.textField));
        if (typed != search) Menu.SetSearch(typed);
        if (SearchFocused) Focus.Take("search");
        bool activated = Focus.Item("search", field);
        if (FocusSearch && Event.current.type == EventType.Layout)
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
            // Enter in the field opens the first result.
            PageView.OpenFirstResult();
        }
        if (cleared)
        {
            Menu.SetSearch(string.Empty);
            GUIUtility.keyboardControl = 0;
        }
        if (Event.current.type != EventType.Repaint) return;
        if (Menu.Search.Length == 0 && !SearchFocused) Widgets.Paint(field, "Search", Widgets.Style(() => Theme.Placeholder, GUI.skin.label));
        else if (hasText && Widgets.Themed) Theme.Tinted(new Rect(clear.center.x - 5f, clear.center.y - 5f, 10f, 10f), Theme.Cross, clear.Contains(Event.current.mousePosition) ? Theme.Text : Theme.Dim);
    }

    /// <summary>Lets go of the search field (Esc).</summary>
    public static void BlurSearch()
    {
        if (SearchFocused) GUIUtility.keyboardControl = 0;
    }
}
