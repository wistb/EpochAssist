using EpochAssist.Core;
using UnityEngine;
using BuildInfo = EpochAssist.Core.BuildInfo;

namespace EpochAssist.Ui;

// The 40 px bar across the top: the logo and version, the tabs (editor style: the selected tab takes the page's colour
// and opens into it), then the profile in use, the collapse chevron and the close button. Its empty space drags the
// window. The search is at the top of the tree.
internal static class TopBar
{
    public const float Height = 40f;
    private const float TabHeight = 34f;

    /// <summary>Where nothing was drawn, for dragging the window.</summary>
    public static Rect DragArea { get; private set; }

    public static void Draw(Rect bar)
    {
        Focus.Scope = "top";
        GUIStyle version = Widgets.Style(() => Theme.Version, GUI.skin.label);
        float x = bar.x + 10f;
        // The version always shows; a narrow window (a small screen at a large UI scale) leaves the logo out, so the tabs
        // and the profile never overlap.
        x = Brand(bar, x, version, withLogo: LogoFits(bar, version));
        x = Tabs(bar, x);
        Right(bar, x);
    }

    // The logo (or the name), the version, the tabs, the profile and close all fit across the bar.
    private static bool LogoFits(Rect bar, GUIStyle version)
    {
        GUIStyle brand = Widgets.Style(() => Theme.Brand, GUI.skin.label), tab = Widgets.Style(() => Theme.Tab, GUI.skin.button);
        float brandWidth = Theme.Logo != null && Widgets.Themed ? 145f : TextCache.Width(BuildInfo.Name, brand);
        float tabs = MenuTree.Tabs.Sum(t => TextCache.Width(t.Title, tab) + 29f);
        // Close, collapse and profile with their gaps.
        const float rightSide = 8f + 22f + 2f + 22f + 6f + 150f + 8f;
        return 10f + brandWidth + 10f + TextCache.Width("v" + BuildInfo.Version, version) + 14f + tabs + rightSide <= bar.width;
    }

    private static float Brand(Rect bar, float x, GUIStyle version, bool withLogo)
    {
        // Beside the logo, the version sits on the wordmark's baseline (the logo's foot); alone, on the bar's middle.
        float versionShift = 0f;
        if (withLogo && Theme.Logo is { } logo && Widgets.Themed)
        {
            versionShift = Mathf.Round(8.5f - version.fontSize * 0.4f);
            if (Event.current.type == EventType.Repaint) GUI.DrawTexture(Menu.PixelSnap(new Rect(x, bar.center.y - 8.5f, 145f, 17f)), logo);
            x += 145f;
        }
        else if (withLogo)
        {
            GUIStyle brand = Widgets.Style(() => Theme.Brand, GUI.skin.label);
            float w = TextCache.Width(BuildInfo.Name, brand);
            if (Event.current.type == EventType.Repaint) Widgets.Paint(new Rect(x, bar.y, w, bar.height), BuildInfo.Name, brand);
            x += w;
        }
        if (withLogo) x += 10f;
        string v = "v" + BuildInfo.Version;
        float vw = TextCache.Width(v, version);
        if (Event.current.type == EventType.Repaint) Widgets.Paint(new Rect(x, bar.y + versionShift, vw, bar.height), v, version);
        return x + vw + 14f;
    }

    private static float Tabs(Rect bar, float x)
    {

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
        return x;
    }

    private static void Right(Rect bar, float tabsEnd)
    {
        // From the right: close, collapse, then the profile.
        float right = bar.xMax - 8f;
        var close = new Rect(right - 22f, Mathf.Round(bar.center.y - 11f), 22f, 22f);
        bool closeClicked = GUI.Button(close, GUIContent.none, GUIStyle.none);
        if (Event.current.type == EventType.Repaint)
        {
            if (Widgets.Themed) Theme.Tinted(new Rect(close.center.x - 6f, close.center.y - 6f, 12f, 12f), Theme.CloseCross, close.Contains(Event.current.mousePosition) ? Theme.Text : Theme.Secondary);
            else Widgets.Paint(close, "×", GUI.skin.label);
        }
        var collapse = new Rect(close.x - 2f - 22f, close.y, 22f, 22f);
        bool collapseClicked = GUI.Button(collapse, GUIContent.none, GUIStyle.none);
        if (Event.current.type == EventType.Repaint)
        {
            Color tint = collapse.Contains(Event.current.mousePosition) ? Theme.Text : Theme.Secondary;
            if (Widgets.Themed) Theme.Tinted(new Rect(collapse.center.x - 6f, collapse.center.y - 6f, 12f, 12f), Menu.Collapsed ? Theme.Chevron : Theme.ChevronUp, tint);
            else Widgets.Paint(collapse, Menu.Collapsed ? "v" : "^", GUI.skin.label);
        }
        right = collapse.x - 6f;

        var profiles = Profiles.All;
        var names = profiles.Select(p => p.Name).Append("Manage profiles…").ToList();
        int active = profiles.ToList().FindIndex(p => p.Id == Profiles.Active.Id);
        GUIStyle field = Widgets.Style(() => Theme.FieldText, GUI.skin.label);
        float pw = Mathf.Clamp(TextCache.Width(Profiles.Active.Name, field) + field.padding.horizontal + 22f, 80f, 150f);
        var profile = new Rect(right - pw, Mathf.Round(bar.center.y - 12f), pw, 24f);

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

        if (Focus.Item("collapse", collapse) || collapseClicked) Actions.Run("menu.collapse", "menu chevron");
        if (Focus.Item("close", close) || closeClicked) Actions.Run("menu.close", "menu ×");
        DragArea = new Rect(tabsEnd, bar.y, Mathf.Max(0f, profile.x - tabsEnd), bar.height);
        Focus.Scope = string.Empty;
    }
}
