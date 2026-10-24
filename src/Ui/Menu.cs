using EpochAssist.Core;
using UnityEngine;
using static EpochAssist.Ui.Widgets;
using static UnityEngine.GUI;
using BuildInfo = EpochAssist.Core.BuildInfo;

namespace EpochAssist.Ui;

// The full menu: a sidebar of pages on the left, the page on the right. Features declare their pages; Settings is last.
internal static class Menu
{
    private const int WindowId = 0x45417373;
    private const float Width = 580f;
    private const float Height = 520f;

    private const string SearchControl = "EpochAssistSearch";

    /// <summary>The core page's name; every other page is named by its feature's id.</summary>
    public const string SettingsPage = "settings";

    // A feature id or SettingsPage; anything else shows the first page.
    private static string s_page = string.Empty;
    private static Rect s_rect = new(80, 80, Width, Height);
    private static Vector2 s_scroll;
    private static string s_search = string.Empty;
    private static int s_settingsTab;

    public static bool Visible { get; private set; }

    public static void Close() => SetVisible(false);

    public static void Toggle() => SetVisible(!Visible);

    /// <summary>The window in screen pixels (top-left origin), after UI scale.</summary>
    public static Rect ScreenRect
    {
        get
        {
            float scale = Mathf.Clamp(Config.UiScale.Value, 0.5f, 3f);
            return new Rect(s_rect.x * scale, s_rect.y * scale, s_rect.width * scale, s_rect.height * scale);
        }
    }

    /// <summary>Features with a page, by order then registration; Settings follows them.</summary>
    public static IEnumerable<Feature> PageFeatures => FeatureHost.All.Where(f => f.Page != null).OrderBy(f => f.Page!.Order);

    /// <summary>Every page name in sidebar order.</summary>
    public static IEnumerable<string> PageNames => PageFeatures.Select(f => f.Id).Append(SettingsPage);

    /// <summary>The page shown: the chosen one while it exists, otherwise the first.</summary>
    public static string Current => PageNames.Contains(s_page) ? s_page : PageNames.First();

    public static void SetSearch(string text)
    {
        s_page = SettingsPage;
        s_search = text;
        s_scroll = Vector2.zero;
    }

    /// <summary>Opens the menu on a page by name; an unknown name opens the first page.</summary>
    public static void Open(string page)
    {
        if (!PageNames.Contains(page)) page = PageNames.First();
        // Theme files may have changed since the list was read.
        if (page == SettingsPage && (!Visible || Current != page)) Themes.Rescan();
        if (s_page != page) s_scroll = Vector2.zero;
        s_page = page;
        SetVisible(true);
    }

    public static void OnUpdate()
    {
        MenuNav nav = Hotkeys.MenuNav;
        if (Visible && nav.Page != 0)
        {
            var pages = PageNames.ToList();
            int next = Math.Clamp(pages.IndexOf(Current) + nav.Page, 0, pages.Count - 1);
            if (pages[next] != Current) Actions.Run($"menu.page.{pages[next]}", nav.Page > 0 ? "key PageDown" : "key PageUp");
        }

        // Only IMGUI text fields take keyboard control; buttons and sliders don't.
        GameState.UiTyping = Visible && GUIUtility.keyboardControl != 0;

        // The hotkey layer runs menu.toggle and menu.close; while it is down, the menu still answers its key and Esc.
        if (FeatureHost.Find("hotkeys")?.State != FeatureState.Running
            && (Hotkeys.FallbackPressed(Config.MenuKey) || (Visible && Hotkeys.FallbackEscape())))
        {
            SetVisible(!Visible);
        }
        InputGate.Menu = Visible;
    }

    public static void OnGUI()
    {
        if (!Visible) return;

        SettingsView.CommitPending();

        GUISkin previousSkin = GUI.skin;
        Matrix4x4 previousMatrix = GUI.matrix;
        Color previousBackground = GUI.backgroundColor;
        GUISkin? skin = Theme.Skin;
        Themed = skin != null;
        if (skin != null) GUI.skin = skin;

        float scale = Mathf.Clamp(Config.UiScale.Value, 0.5f, 3f);
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
        s_rect.x = Mathf.Clamp(s_rect.x, 0f, Mathf.Max(0f, Screen.width / scale - s_rect.width));
        s_rect.y = Mathf.Clamp(s_rect.y, 0f, Mathf.Max(0f, Screen.height / scale - 40f));
        try
        {
            s_rect = GUI.Window(WindowId, s_rect, (WindowFunction)DrawWindow, string.Empty);
        }
        finally
        {
            GUI.backgroundColor = previousBackground;
            GUI.matrix = previousMatrix;
            GUI.skin = previousSkin;
        }
    }

    private static void SetVisible(bool visible)
    {
        if (visible && !Visible) HotkeyConflicts.Invalidate();
        Visible = visible;
        InputGate.Menu = visible;
        KeepKeysInField(visible);
        if (!visible)
        {
            Hotkeys.CancelCapture();
            Focus.Reset();
            Config.Save();
        }
    }

    private static bool? s_eatKeys;

    /// <summary>
    /// Unity hides key presses from its input reads while an IMGUI text field has focus, which would leave the hotkey
    /// layer blind to Tab and Esc in the search field. Off while the menu is open; the game's own value comes back after.
    /// </summary>
    public static void KeepKeysInField(bool menuOpen)
    {
        if (menuOpen)
        {
            s_eatKeys ??= Input.eatKeyPressOnTextFieldFocus;
            Input.eatKeyPressOnTextFieldFocus = false;
        }
        else if (s_eatKeys is bool eat)
        {
            Input.eatKeyPressOnTextFieldFocus = eat;
            s_eatKeys = null;
        }
    }

    private static void DrawWindow(int id)
    {
        Focus.Active = true;
        Focus.BeginEvent(Hotkeys.MenuNav, Hotkeys.MenuNavCount);
        try
        {
            DrawContents();
        }
        finally
        {
            if (Event.current.type == EventType.Repaint) Focus.EndRepaint();
            Focus.Active = false;
        }
    }

    private static void DrawContents()
    {
        float opacity = Mathf.Clamp(Config.MenuOpacity.Value, 0.2f, 1f);
        if (Themed && Event.current.type == EventType.Repaint)
        {
            Color color = GUI.color;
            GUI.backgroundColor = Color.white;
            // The theme's own opacity, scaled by the player's Background setting.
            GUI.color = new Color(color.r, color.g, color.b, color.a * opacity * Theme.Opacity);
            Theme.DrawSliced(new Rect(0f, 0f, s_rect.width, s_rect.height), Theme.WindowBg, Theme.WindowRadius);
            Theme.DrawSliced(new Rect(0f, 0f, Theme.SidebarWidth, s_rect.height), Theme.SidebarBg, Theme.WindowRadius);
            GUI.color = color;
        }
        // Fades cards, buttons and chips but not text or switches.
        GUI.backgroundColor = new Color(1f, 1f, 1f, opacity);

        // Fixed areas, not a layout row: a layout group grows past its width when a child wants more, which pushed the
        // page over the sidebar's background.
        GUILayout.BeginArea(new Rect(0f, 0f, Theme.SidebarWidth, s_rect.height), Style(() => Theme.Sidebar, GUI.skin.box));
        DrawSidebar();
        GUILayout.EndArea();
        GUILayout.BeginArea(new Rect(Theme.SidebarWidth, 0f, s_rect.width - Theme.SidebarWidth, s_rect.height), Style(() => Theme.Main, GUIStyle.none));
        string current = Current;
        Feature? owner = PageFeatures.FirstOrDefault(f => f.Id == current);
        FaultBanners(owner);
        // The search field lives on Settings; on any other page it can't keep the keyboard (set in a GUI pass to stick).
        if (current != SettingsPage && GUIUtility.keyboardControl != 0 && Event.current.type == EventType.Layout) GUIUtility.keyboardControl = 0;
        // Page controls are keyed under the page, so focus on one page never lands on another's.
        Focus.Scope = current;
        if (owner == null) DrawSettings();
        else DrawFeaturePage(owner);
        Focus.Scope = string.Empty;
        GUILayout.EndArea();
        if (Themed && Event.current.type == EventType.Repaint)
            GUI.DrawTexture(new Rect(Theme.SidebarWidth - 1f, 0f, 1f, s_rect.height), Theme.Divider);

        // Controls claim their clicks first; this only gets clicks on empty space near the top.
        GUI.DragWindow(new Rect(0f, 0f, s_rect.width, 48f));
    }

    // A feature stopped by repeated errors says so at the top of every page, with a way to try again; so do settings
    // that couldn't be brought up to date. The page shown already says it for its own feature.
    private static void FaultBanners(Feature? shown)
    {
        foreach (var group in SettingsRegistry.Groups)
        {
            if (group.Failures.Count == 0) continue;
            GUILayout.BeginHorizontal(Style(() => Theme.Card, GUI.skin.box));
            GUILayout.Label($"{Colored(group.Name + " settings not updated", Theme.Pause)}  {string.Join("; ", group.Failures)}", Style(() => Theme.Hint, GUI.skin.label));
            GUILayout.EndHorizontal();
        }
        foreach (var feature in FeatureHost.All)
        {
            bool degraded = feature.Degraded;
            if (feature.State is not (FeatureState.Faulted or FeatureState.Unavailable) && !degraded) continue;
            if (feature == shown && !degraded) continue;
            bool unavailable = feature.State == FeatureState.Unavailable;
            string detail = degraded
                ? string.Join("; ", feature.MissingGroups.Select(g => $"without {g} (game update)").Concat(feature.PatchFailures.Select(f => "patch failed: " + f)))
                : feature.LastError ?? string.Empty;
            GUILayout.BeginHorizontal(Style(() => Theme.Card, GUI.skin.box));
            string title = Colored(feature.Name + (degraded ? " partly working" : unavailable ? " unavailable" : " stopped"), Theme.Pause);
            GUILayout.Label($"{title}  {detail}", Style(() => Theme.Hint, GUI.skin.label));
            // A restart can't bring back what the game no longer has; an update of the mod can.
            if (!unavailable && !degraded && Pressed(GUILayout.Button("Restart", Style(() => Theme.SmallButton, GUI.skin.button), GUILayout.ExpandWidth(false)), $"restart.{feature.Id}"))
                Actions.Run($"feature.restart.{feature.Id}", "menu fault banner");
            GUILayout.EndHorizontal();
        }
    }

    private static void DrawSidebar()
    {
        GUILayout.Label(BuildInfo.Name, Style(() => Theme.NavTitle, GUI.skin.label));
        GUILayout.Label($"v{BuildInfo.Version}  ·  {Runtime.BuildId}", Style(() => Theme.NavVersion, GUI.skin.label));
        if (Runtime.Hosted) GUILayout.Label($"dev · {Runtime.LoadedAt:HH:mm:ss}", Style(() => Theme.NavVersion, GUI.skin.label));
        GUILayout.Space(10f);

        string current = Current;
        foreach (var feature in PageFeatures)
        {
            bool selected = current == feature.Id;
            bool running = feature.State == FeatureState.Running;
            Color previous = GUI.color;
            // Greyed while the feature isn't running; its page says why.
            if (!running) GUI.color = new Color(previous.r, previous.g, previous.b, previous.a * 0.45f);
            if (Pressed(GUILayout.Toggle(selected, feature.Page!.Title, Style(() => Theme.NavItem, GUI.skin.button)) != selected, $"nav.{feature.Id}") && !selected)
                Actions.Run($"menu.page.{feature.Id}", "menu sidebar");

            Rect item = GUILayoutUtility.GetLastRect();
            if (Themed && Event.current.type == EventType.Repaint)
            {
                StatusState state = running ? feature.Status.State : StatusState.Off;
                Color dot = Theme.StateColor(state);
                if (selected && state == StatusState.Off) dot = Theme.OnAccent;
                DrawState(new Rect(item.x + 10f, item.center.y - 4f, 8f, 8f), state, dot);
            }
            GUI.color = previous;
        }

        // Settings is a gear at the bottom, on the closing hint's line, apart from the feature pages.
        GUILayout.FlexibleSpace();
        GUILayout.BeginHorizontal();
        bool settings = current == SettingsPage;
        var gear = Themed ? new GUIContent(string.Empty, "Settings") : new GUIContent("Settings");
        if (Pressed(GUILayout.Toggle(settings, gear, Style(() => Theme.NavIcon, GUI.skin.button)) != settings, $"nav.{SettingsPage}") && !settings)
            Actions.Run($"menu.page.{SettingsPage}", "menu sidebar");
        Rect icon = GUILayoutUtility.GetLastRect();
        if (Themed && Event.current.type == EventType.Repaint)
        {
            Color tint = settings ? Theme.OnAccent : icon.Contains(Event.current.mousePosition) ? Theme.Text : Theme.Dim;
            DrawTinted(new Rect(icon.center.x - 7f, icon.center.y - 7f, 14f, 14f), Theme.Gear, tint);
        }
        GUILayout.Label($"{Config.MenuKey.Display} or Esc closes", Style(() => Theme.NavHint, GUI.skin.label));
        GUILayout.EndHorizontal();
    }

    private static void PageHeader(string title, StatusState? state, Toggle? toggle)
    {
        GUILayout.BeginHorizontal(GUILayout.Height(30f));
        GUILayout.Label(title, Style(() => Theme.PageTitle, GUI.skin.label));
        if (state.HasValue) StateBadge(state.Value);
        GUILayout.FlexibleSpace();
        if (toggle != null && InlineSwitch(toggle.Value, "header.switch") != toggle.Value)
        {
            // The page's master switch is a quick toggle, so it has its own action.
            if (Actions.Run(SettingsRegistry.ToggleAction(toggle), "menu page switch").StartsWith("error")) toggle.Value = !toggle.Value;
        }
        if (Pressed(GUILayout.Button("×", Style(() => Theme.Close, GUI.skin.button)), "header.close")) Actions.Run("menu.close", "menu ×");
        GUILayout.EndHorizontal();
        GUILayout.Space(8f);
    }

    private static void StateBadge(StatusState state)
    {
        if (!Themed)
        {
            GUILayout.Label(state.ToString());
            return;
        }
        Color color = Theme.StateColor(state);
        var content = new GUIContent(state.ToString());
        Theme.Badge.normal.background = Theme.BadgeFor(state);
        Theme.Badge.normal.textColor = color;
        Rect r = GUILayoutUtility.GetRect(content, Theme.Badge, GUILayout.ExpandWidth(false));
        if (Event.current.type == EventType.Repaint)
        {
            Theme.Badge.Draw(r, content, false, false, false, false);
            DrawState(new Rect(r.x + 9f, r.center.y - 3.5f, 7f, 7f), state, color);
        }
    }

    // A running feature's page is its own; otherwise the reason it isn't running and, when it can help, Restart.
    private static void DrawFeaturePage(Feature feature)
    {
        FeaturePage page = feature.Page!;
        if (feature.State == FeatureState.Running)
        {
            PageHeader(page.Title, feature.Status.State, page.Switch);
            page.Draw();
            return;
        }

        PageHeader(page.Title, StatusState.Off, null);
        bool unavailable = feature.State == FeatureState.Unavailable;
        BeginCard(unavailable ? "UNAVAILABLE" : "NOT RUNNING");
        Hint(Reason(feature));
        // A restart can't bring back what the game no longer has; an update of the mod can.
        if (!unavailable)
        {
            GUILayout.Space(4f);
            if (Pressed(GUILayout.Button("Restart", Style(() => Theme.SmallButton, GUI.skin.button), GUILayout.ExpandWidth(false)), "restart"))
                Actions.Run($"feature.restart.{feature.Id}", "menu page restart");
        }
        EndCard();
    }

    /// <summary>Why a feature isn't running, as a sentence.</summary>
    public static string Reason(Feature feature)
    {
        string detail = feature.Status.Detail.TrimEnd('.');
        return feature.State switch
        {
            FeatureState.Unavailable => $"{feature.Name} is unavailable after a game update: {detail}. An update of the mod can bring it back.",
            FeatureState.Faulted => $"{feature.Name} stopped after an error: {detail}.",
            _ => detail.Length > 0 ? $"{feature.Name} is stopped: {detail}." : $"{feature.Name} is stopped.",
        };
    }

    private static void DrawSettings()
    {
        PageHeader("Settings", null, null);

        SearchField();
        GUILayout.Space(8f);

        // A search looks through every page, so it replaces the tabs while there is one.
        if (s_search.Trim().Length > 0)
        {
            BeginScroll();
            SettingsView.SearchResults(s_search);
            EndScroll();
            return;
        }

        var sections = Config.Group.Sections;
        GUILayout.BeginHorizontal();
        for (int i = 0; i < sections.Count; i++)
        {
            bool selected = s_settingsTab == i;
            bool clicked = GUILayout.Toggle(selected, sections[i].Title, Style(() => Theme.SubTab, GUI.skin.button), GUILayout.ExpandWidth(false)) != selected;
            if (Pressed(clicked, "tab." + sections[i].Title) && !selected) SetSettingsTab(i);
        }
        GUILayout.EndHorizontal();
        DividerLine();

        BeginScroll();
        SettingsView.Card(sections[Math.Clamp(s_settingsTab, 0, sections.Count - 1)]);
        Hint("Double-click a slider to reset it. Search finds settings from every page.");
        EndScroll();
    }

    /// <summary>The Settings page's Mini HUD tab is open, so the HUD shows behind the menu as a preview.</summary>
    public static bool PreviewingHud => Visible && Current == SettingsPage && s_search.Trim().Length == 0
        && Config.Group.Sections[Math.Clamp(s_settingsTab, 0, Config.Group.Sections.Count - 1)] == Config.Hud;

    // One full-width field, with a greyed placeholder while empty and a clear button inside its right end.
    private static void SearchField()
    {
        Rect field = GUILayoutUtility.GetRect(10f, 26f, GUILayout.ExpandWidth(true), GUILayout.Height(26f));
        var clear = new Rect(field.xMax - 26f, field.y, 26f, field.height);
        bool hasText = s_search.Length > 0;
        // Drawn first so the clear button takes its click before the field under it.
        bool cleared = hasText && Themed && GUI.Button(clear, GUIContent.none, GUIStyle.none);
        GUI.SetNextControlName(SearchControl);
        s_search = GUI.TextField(field, s_search, Style(() => Theme.SearchField, GUI.skin.textField));
        // The field is focused when it has the keyboard; reaching it by key (or Enter on it) gives it the keyboard.
        if (GUI.GetNameOfFocusedControl() == SearchControl) Focus.Take("search");
        if (Focus.Item("search", field) || Focus.Arrived("search")) GUI.FocusControl(SearchControl);
        if (!Themed) return;
        if (hasText && (Focus.Item("search.clear", clear) || cleared))
        {
            s_search = string.Empty;
            GUIUtility.keyboardControl = 0;
        }
        if (Event.current.type != EventType.Repaint) return;
        if (s_search.Length == 0) GUI.Label(field, "Search", Theme.Placeholder);
        else GUI.Label(clear, "×", Theme.FieldClear);
    }

    /// <summary>Shows one tab of the Settings page (a section of the General settings).</summary>
    public static void SetSettingsTab(int tab)
    {
        s_settingsTab = Math.Clamp(tab, 0, Config.Group.Sections.Count - 1);
        s_scroll = Vector2.zero;
    }

    private static void DividerLine()
    {
        Rect r = GUILayoutUtility.GetRect(10f, 9f, GUILayout.ExpandWidth(true));
        if (Themed && Event.current.type == EventType.Repaint) GUI.DrawTexture(new Rect(r.x, r.y + 4f, r.width, 1f), Theme.Divider);
    }

    // Page width inside the main area: window minus sidebar, page padding, and the vertical scrollbar with its margin.
    private const float ContentWidth = Width - Theme.SidebarWidth - 32f - 22f;

    // Vertical scrolling only; content is held to the visible width so nothing runs off the right edge.
    public static void BeginScroll()
    {
        s_scroll = GUILayout.BeginScrollView(s_scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);
        GUILayout.BeginVertical(GUILayout.Width(ContentWidth));
        SettingsView.RowWidth = ContentWidth;
        Focus.InScroll = true;
    }

    public static void EndScroll()
    {
        Focus.InScroll = false;
        GUILayout.EndVertical();
        GUILayout.EndScrollView();
        // Brings a control focused by key into view; it shows from the next frame.
        if (Event.current.type == EventType.Repaint && Focus.TakeScrollTarget() is Rect target)
        {
            float view = GUILayoutUtility.GetLastRect().height;
            if (target.yMin < s_scroll.y) s_scroll.y = Mathf.Max(0f, target.yMin - 8f);
            else if (target.yMax > s_scroll.y + view) s_scroll.y = target.yMax - view + 8f;
        }
    }

    /// <summary>Text shortened with an ellipsis until it fits the width.</summary>
    public static string Fit(string text, GUIStyle style, float width)
    {
        if (style.CalcSize(new GUIContent(text)).x <= width) return text;
        for (int n = text.Length - 1; n > 1; n--)
        {
            string cut = text.Substring(0, n).TrimEnd() + "…";
            if (style.CalcSize(new GUIContent(cut)).x <= width) return cut;
        }
        return text.Substring(0, 1);
    }

    public static void DrawDot(Rect r, Color color) => DrawTinted(r, Theme.Dot, color);

    /// <summary>A state's own shape in a colour (usually the state's).</summary>
    public static void DrawState(Rect r, StatusState state, Color color) => DrawTinted(r, Theme.StateShape(state), color);

    private static void DrawTinted(Rect r, Texture2D texture, Color color)
    {
        Color previous = GUI.color;
        GUI.color = new Color(color.r, color.g, color.b, color.a * previous.a);
        GUI.DrawTexture(r, texture);
        GUI.color = previous;
    }
}
