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
            float scale = Mathf.Clamp(LookSettings.UiScale.Value, 0.5f, 3f);
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
        if (page != SettingsPage) s_backPage = page;
        SetVisible(true);
    }

    public static void OnUpdate()
    {
        FinishClosing();
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
            && (Hotkeys.FallbackPressed(KeySettings.MenuKey) || (Visible && Hotkeys.FallbackEscape())))
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

        float scale = Mathf.Clamp(LookSettings.UiScale.Value, 0.5f, 3f);
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
            Dropdown.Close();
            s_closingAt = float.MinValue;
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
        // A click lets go of the keyboard first; a text field under the cursor takes it back as it handles the click.
        if (Event.current.type == EventType.MouseDown)
        {
            Focus.PointerDown();
            GUIUtility.keyboardControl = 0;
        }
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
        float opacity = Mathf.Clamp(LookSettings.MenuOpacity.Value, 0.2f, 1f);
        if (Themed && Event.current.type == EventType.Repaint)
        {
            Color color = GUI.color;
            GUI.backgroundColor = Color.white;
            // The theme's own opacity, scaled by the player's Background setting.
            GUI.color = new Color(color.r, color.g, color.b, color.a * opacity * Theme.Opacity);
            Theme.DrawSliced(new Rect(0f, 0f, s_rect.width, s_rect.height), Theme.WindowBg, Theme.WindowRadius);
            Theme.DrawSliced(new Rect(0f, 0f, Theme.SidebarWidth, s_rect.height), Theme.SidebarBg, Theme.WindowRadius);
            GUI.color = color;
            // Settings has the sidebar: a faint accent wash, faded in with the slide.
            if (Current == SettingsPage)
            {
                float t = PanelAlpha;
                GUI.color = new Color(Theme.Accent.r, Theme.Accent.g, Theme.Accent.b, color.a * 0.07f * t);
                Theme.DrawSliced(new Rect(0f, 0f, Theme.SidebarWidth, s_rect.height), Theme.SidebarBg, Theme.WindowRadius);
                GUI.color = color;
            }
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
        // The version, then the profile in use: every setting belongs to it, so it is always in view; a click opens
        // Profiles. The build id is on Diagnostics.
        GUIStyle subtext = Style(() => Theme.NavVersion, GUI.skin.label);
        // The whole line fits the sidebar's inner width (the name, last, is what gets cut).
        float room = Theme.SidebarWidth - Style(() => Theme.Sidebar, GUI.skin.box).padding.horizontal - subtext.margin.horizontal;
        if (Pressed(GUILayout.Button(Fit($"v{BuildInfo.Version}  ·  {Profiles.Active.Name}", subtext, room), subtext), "nav.profile"))
            OpenProfiles();
        GUILayout.Space(5f);
        // The feature tabs hang under this line, and fold up into it as Settings grows in.
        Rect separator = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true), GUILayout.Height(1f));
        if (Event.current.type == EventType.Repaint)
        {
            s_separatorY = separator.y;
            if (Themed) GUI.DrawTexture(separator, Theme.Divider);
        }
        GUILayout.Space(6f);

        string current = Current;
        bool settingsMode = current == SettingsPage;
        if (settingsMode != s_navSettings)
        {
            s_navSettings = settingsMode;
            s_navSwitchedAt = Time.unscaledTime;
        }
        if (settingsMode && !Closing) FoldFeatureTabs();
        else if (Event.current.type == EventType.Repaint) s_outgoing.Clear();
        // Coming back from Settings the feature tabs unfold out of the line above them, top first; Settings' items grow out of the gear.
        Color navColor = GUI.color;
        int featureRow = 0;
        // Settings' panel sits at the foot of the sidebar, right above the way back and the gear it grows out of.
        if (settingsMode) GUILayout.FlexibleSpace();
        GUILayout.BeginVertical();
        // While Settings is open the sidebar is its navigation; otherwise it lists the feature pages.
        if (settingsMode) SettingsNav();
        else foreach (var feature in PageFeatures)
        {
            bool selected = current == feature.Id;
            bool running = feature.State == FeatureState.Running;
            Color previous = GUI.color;
            // Greyed while the feature isn't running; its page says why.
            DropBegin(featureRow++);
            if (!running) GUI.color = new Color(GUI.color.r, GUI.color.g, GUI.color.b, GUI.color.a * 0.45f);
            // The page shown has the same quiet mark as Settings' sidebar, under its text, rather than a filled button.
            var content = new GUIContent(feature.Page!.Title);
            Rect item = GUILayoutUtility.GetRect(content, FeatureNavStyle(), GUILayout.ExpandWidth(true));
            if (selected) Selected(item);
            bool clicked = GUI.Toggle(item, selected, content, FeatureNavStyle()) != selected;
            if (selected) SelectedBar(item);
            if ((Focus.Item($"nav.{feature.Id}", item) || clicked) && !selected) Actions.Run($"menu.page.{feature.Id}", "menu sidebar");

            StatusState state = running ? feature.Status.State : StatusState.Off;
            if (Themed && Event.current.type == EventType.Repaint) DrawState(new Rect(item.x + 10f, item.center.y - 4f, 8f, 8f), state, Theme.StateColor(state));
            if (Event.current.type == EventType.Repaint) s_outgoing.Add(new FeatureTab(item, feature.Page.Title, selected, running, state));
            GrowEnd();
            GUI.color = previous;
        }
        GUILayout.EndVertical();
        GUI.color = navColor;

        // Settings is a gear at the bottom, on the closing hint's line, apart from the feature pages; it opens Settings
        // and, while Settings has the sidebar, closes it again.
        if (!settingsMode) GUILayout.FlexibleSpace();
        else GUILayout.Space(8f);
        GUILayout.BeginHorizontal();
        bool settings = current == SettingsPage;
        // No button behind it: the gear itself turns and takes the accent colour as Settings opens, and turns back as
        // it closes (with the menu's animation speed; at once with animations off).
        GUIStyle iconStyle = Style(() => Theme.NavIcon, GUI.skin.button);
        Rect icon = GUILayoutUtility.GetRect(iconStyle.fixedWidth > 0f ? iconStyle.fixedWidth : 24f, 24f, GUILayout.Width(24f), GUILayout.Height(24f));
        bool clickedGear = Themed ? GUI.Button(icon, GUIContent.none, GUIStyle.none) : GUI.Button(icon, "Settings");
        if (Focus.Item($"nav.{SettingsPage}", icon) || clickedGear)
        {
            // A second click folds Settings back into the gear and returns to the last feature page.
            if (!settings) Actions.Run($"menu.page.{SettingsPage}", "menu sidebar");
            else StartClosing(PageNames.Contains(s_backPage) && s_backPage != SettingsPage ? s_backPage : PageNames.First());
        }
        if (Event.current.type == EventType.Repaint) s_gearCenter = icon.center;
        if (Themed && Event.current.type == EventType.Repaint)
        {
            float target = settings && !Closing ? 1f : 0f;
            float step = LookSettings.Animations.Value ? Time.unscaledDeltaTime * Mathf.Clamp(LookSettings.AnimationSpeed.Value, 0.25f, 4f) / GearSeconds : 1f;
            s_gear = Mathf.MoveTowards(s_gear, target, step);
            float ease = s_gear * s_gear * (3f - 2f * s_gear);
            bool hover = icon.Contains(Event.current.mousePosition);
            Color rest = hover ? Theme.Text : Theme.Dim;
            int frame = Mathf.RoundToInt(ease * 2f * Theme.GearFrameCount) % Theme.GearFrameCount;
            DrawTinted(new Rect(icon.center.x - 8f, icon.center.y - 8f, 16f, 16f), Theme.GearFrames.Length > 0 ? Theme.GearFrames[frame] : Theme.Gear, Color.Lerp(rest, Theme.AccentText, ease));
        }
        GUILayout.Space(6f);
        GUILayout.Label($"{KeySettings.MenuKey.Display} or Esc closes", Style(() => Theme.NavHint, GUI.skin.label));
        GUILayout.EndHorizontal();
    }

    // The gear's turn toward Settings: 0 at rest, 1 open (a quarter turn, in the accent colour).
    private static float s_gear;
    private const float GearSeconds = 0.25f;

    // The page Settings goes back to: the last feature page shown.
    private static string s_backPage = string.Empty;

    // Which list the sidebar shows, and when it last changed, for the slide.
    private static bool s_navSettings;
    private static float s_navSwitchedAt = float.MinValue;
    private const float NavSlideSeconds = 0.2f;

    // Settings' items grow out of the gear: each fades in and rises into place in turn, the one nearest the gear first.
    private const float GrowSeconds = 0.18f;
    private const float GrowStagger = 0.03f;
    private static Matrix4x4 s_growMatrix;
    private static Color s_growColor;

    // Where the gear was last drawn (in the sidebar), which Settings grows out of and shrinks back into, and where the
    // Settings panel's bottom border was, which the fold squashes onto first.
    private static Vector2 s_gearCenter;
    private static float s_panelBottom;

    private static void GrowBegin(int fromBottom)
    {
        s_growMatrix = GUI.matrix;
        s_growColor = GUI.color;
        if (Closing)
        {
            // Into the gear, in two steps: first down onto the panel's bottom border (top row first), then, flat, leftward
            // toward the gear. Shrinking toward a point inside the sidebar keeps every part of a row between where it was and the
            // gear, so nothing leaves the window.
            float down = CollapseOf(fromBottom), left = FoldLeft();
            if (down <= 0f) return;
            GUI.color = new Color(s_growColor.r, s_growColor.g, s_growColor.b, s_growColor.a * FoldAlpha(down, left));
            GUI.matrix = s_growMatrix * TowardGear(1f - left, 1f - down);
            return;
        }
        float e = GrowOf(fromBottom);
        if (e >= 1f) return;
        GUI.color = new Color(s_growColor.r, s_growColor.g, s_growColor.b, s_growColor.a * e);
        // Drawn lower than its place, so it rises; the layout itself doesn't move.
        GUI.matrix = s_growMatrix * Matrix4x4.Translate(new Vector3(0f, (1f - e) * 28f, 0f));
    }

    // How far a row has grown in: 0 to 1, the one nearest the gear first, easing out.
    private static float GrowOf(int fromBottom)
    {
        float t = Mathf.Clamp01((Elapsed(s_navSwitchedAt) - fromBottom * GrowStagger) / GrowSeconds);
        return 1f - (1f - t) * (1f - t) * (1f - t);
    }

    // ---- Back to the features: the reverse of the grow ----

    // Clicking the gear while Settings is open folds Settings away: its rows and panel shrink into the gear and fade,
    // the heading first, while the gear turns back; the feature page opens once that is done (from the menu's update,
    // so a frame's passes all draw the same page).
    private const float CollapseSeconds = 0.09f;
    private const float CollapseStagger = 0.014f;
    private static float s_closingAt = float.MinValue;
    private static string s_closingTo = string.Empty;
    // Settings' rows (the gear counting as 0), counted when Settings' list is drawn.
    private static int s_growRows = 1;

    private static bool Closing => s_closingAt > float.MinValue;
    private static float ClosingTime => Elapsed(s_closingAt);
    private static float CollapseTotal => CollapseSeconds + CollapseStagger * Math.Max(0, s_growRows - 1);

    // How far a row has collapsed: 0 to 1, the top one first.
    private static float CollapseOf(int fromBottom)
    {
        if (fromBottom == 0) return 0f;
        float t = Mathf.Clamp01((ClosingTime - (s_growRows - fromBottom) * CollapseStagger) / CollapseSeconds);
        return t * t * (3f - 2f * t);
    }

    // Settings' panel and wash: in with the grow, out with the collapse.
    // Settings' panel and wash: in with the grow; folding, the panel squashes down with its rows, then goes left.
    private static float PanelAlpha => Closing ? FoldAlpha(PanelDown, FoldLeft()) : NavEase();
    private static float PanelDown
    {
        get
        {
            float t = Mathf.Clamp01(ClosingTime / CollapseTotal);
            return t * t * (3f - 2f * t);
        }
    }

    // The second step of the fold: everything, flat on the gear's line by then, shrinks leftward into the gear.
    private const float LeftSeconds = 0.12f;
    private static float FoldLeft()
    {
        if (!Closing) return 0f;
        float t = Mathf.Clamp01((ClosingTime - CollapseTotal) / LeftSeconds);
        return t * t;
    }

    // Partly faded on the way down, gone by the end of the way left.
    private static float FoldAlpha(float down, float left) => (1f - 0.35f * down) * (1f - left);

    private static void StartClosing(string page)
    {
        if (Closing) return;
        s_closingAt = Time.unscaledTime;
        s_closingTo = page;
    }

    // Called each update: once the fold is done, the feature page opens.
    private static void FinishClosing()
    {
        if (!Closing || ClosingTime < CollapseTotal + LeftSeconds) return;
        string page = s_closingTo;
        s_closingAt = float.MinValue;
        Actions.Run($"menu.page.{page}", "menu sidebar");
    }

    // A feature tab unfolding out of the line above the tabs as the sidebar comes back from Settings, the top one
    // first: the fold's reverse, so nothing shows above the line.
    private static void DropBegin(int fromTop)
    {
        s_growMatrix = GUI.matrix;
        s_growColor = GUI.color;
        float t = Mathf.Clamp01((Elapsed(s_navSwitchedAt) - fromTop * GrowStagger) / GrowSeconds);
        float e = 1f - (1f - t) * (1f - t) * (1f - t);
        if (e >= 1f) return;
        GUI.color = new Color(s_growColor.r, s_growColor.g, s_growColor.b, s_growColor.a * e);
        GUI.matrix = s_growMatrix * AboutSeparator(e);
    }

    // A vertical scale about the line above the feature tabs. As in TowardGear, the matrix works in the window's space,
    // so the line's place is offset by the window's.
    private static Matrix4x4 AboutSeparator(float y)
    {
        var pivot = new Vector3(s_rect.x, s_rect.y + s_separatorY, 0f);
        return Matrix4x4.Translate(pivot) * Matrix4x4.Scale(new Vector3(1f, Mathf.Max(0.001f, y), 1f)) * Matrix4x4.Translate(-pivot);
    }

    // A feature tab as last drawn, for folding it away as Settings opens.
    private readonly record struct FeatureTab(Rect Item, string Title, bool Selected, bool Running, StatusState State);

    private static readonly List<FeatureTab> s_outgoing = new();
    // Where the line above the feature tabs was last drawn (in the sidebar).
    private static float s_separatorY;
    private const float FoldUpSeconds = 0.14f;

    // Opening Settings, the feature tabs fold up into the line above them while Settings grows out of the gear: drawn
    // where they last were (not laid out, so Settings' panel has the room at once), squashed toward the line and faded,
    // the bottom one first. They take no clicks on the way.
    private static void FoldFeatureTabs()
    {
        if (Event.current.type != EventType.Repaint || s_outgoing.Count == 0) return;
        float time = Elapsed(s_navSwitchedAt);
        Matrix4x4 matrix = GUI.matrix;
        Color color = GUI.color;
        for (int i = 0; i < s_outgoing.Count; i++)
        {
            float t = Mathf.Clamp01((time - (s_outgoing.Count - 1 - i) * CollapseStagger) / FoldUpSeconds);
            float e = t * t * (3f - 2f * t);
            if (e >= 1f) continue;
            var tab = s_outgoing[i];
            GUI.matrix = matrix * AboutSeparator(1f - e);
            GUI.color = new Color(color.r, color.g, color.b, color.a * (1f - e) * (tab.Running ? 1f : 0.45f));
            if (tab.Selected) Selected(tab.Item);
            Widgets.Paint(tab.Item, tab.Title, FeatureNavStyle());
            if (tab.Selected) SelectedBar(tab.Item);
            if (Themed) DrawState(new Rect(tab.Item.x + 10f, tab.Item.center.y - 4f, 8f, 8f), tab.State, Theme.StateColor(tab.State));
        }
        GUI.matrix = matrix;
        GUI.color = color;
    }

    // A scale (x and y apart) about the panel's bottom border, at the gear's x. The sidebar's layout space starts at the window's corner, so the gear's place in the
    // GUI space the matrix works in is the window's position plus its place in the sidebar.
    private static Matrix4x4 TowardGear(float x, float y)
    {
        // Down onto the panel's bottom border (where it was last drawn), then left toward the gear.
        float bottom = s_panelBottom > 0f ? s_panelBottom : s_gearCenter.y;
        var pivot = new Vector3(s_rect.x + s_gearCenter.x, s_rect.y + bottom, 0f);
        return Matrix4x4.Translate(pivot) * Matrix4x4.Scale(new Vector3(Mathf.Max(0.001f, x), Mathf.Max(0.001f, y), 1f)) * Matrix4x4.Translate(-pivot);
    }

    private static void GrowEnd()
    {
        GUI.matrix = s_growMatrix;
        GUI.color = s_growColor;
    }

    // Animation time since a moment, scaled by the Animation speed setting; with animations off, long past.
    private static float Elapsed(float since) =>
        LookSettings.Animations.Value ? (Time.unscaledTime - since) * Mathf.Clamp(LookSettings.AnimationSpeed.Value, 0.25f, 4f) : 1e6f;

    // 0 to 1 over the slide, easing out.
    private static float NavEase()
    {
        float t = Mathf.Clamp01((Elapsed(s_navSwitchedAt)) / NavSlideSeconds);
        return 1f - (1f - t) * (1f - t) * (1f - t);
    }

    // Settings in the sidebar: back to the features, then each General section, Diagnostics with its pages under it
    // on tree lines. A pick shows the Settings side (not Profiles) and ends a search.
    private static void SettingsNav()
    {
        var tabs = Tabs;
        var pages = DiagnosticsPage.Tabs;
        bool settingsShown = s_search.Trim().Length == 0;
        int shownTab = settingsShown ? Math.Clamp(s_settingsTab, 0, tabs.Count - 1) : -1;
        bool pagesOpen = (shownTab >= 0 && tabs[shownTab].Section == Config.Diagnostics) || s_pagesOpen > 0f;
        // Counted from the bottom for the grow: the way back (0), then the rows upward, the heading last.
        int fromBottom = 1 + (pagesOpen ? pages.Count : 0) + tabs.Count;
        s_growRows = fromBottom;

        // One bordered panel from the heading to the last row; it fits what is shown, so it grows while a category's
        // pages are open. Growing in, it rises from its bottom border with the rows and fades in with the first ones.
        // Its frame is drawn at its last size (a layout group's own background would stand at full height at once).
        Color panelColor = GUI.color;
        Matrix4x4 panelMatrix = GUI.matrix;
        GUI.color = new Color(panelColor.r, panelColor.g, panelColor.b, panelColor.a * PanelAlpha);
        // Folding, the panel's frame goes into the gear with its rows.
        if (Closing) GUI.matrix = panelMatrix * TowardGear(1f - FoldLeft(), 1f - PanelDown);
        if (Event.current.type == EventType.Repaint && s_panelRect.height > 0f)
        {
            Rect frame = s_panelRect;
            if (!Closing)
            {
                float height = Mathf.Max(frame.height * PanelGrow(), PanelStyle().border.vertical);
                frame = new Rect(frame.x, frame.yMax - height, frame.width, height);
            }
            Widgets.Paint(frame, GUIContent.none, PanelStyle());
        }
        GUILayout.BeginVertical(PanelLayoutStyle());
        GUI.matrix = panelMatrix;
        GUI.color = panelColor;

        // A heading in the accent colour with the gear, so it's plain the sidebar belongs to Settings now.
        GrowBegin(fromBottom--);
        // One row, the gear and the word both centred on it, so their style margins can't set them apart.
        Rect heading = GUILayoutUtility.GetRect(10f, 24f, GUILayout.ExpandWidth(true), GUILayout.Height(24f));
        if (Event.current.type == EventType.Repaint)
        {
            if (Themed) DrawTinted(new Rect(heading.x + 4f, Mathf.Round(heading.center.y - 7f), 14f, 14f), Theme.Gear, Theme.AccentText);
            Widgets.Paint(new Rect(heading.x + 24f, heading.y, heading.width - 24f, heading.height), Themed ? Colored("SETTINGS", Theme.AccentText) : "SETTINGS", NavHeadingStyle());
        }
        GrowEnd();
        GUILayout.Space(4f);

        for (int i = 0; i < tabs.Count; i++)
        {
            bool hasPages = tabs[i].Section == Config.Diagnostics;
            bool selected = i == shownTab;
            GrowBegin(fromBottom--);
            if (CategoryRow(tabs[i].Title, selected, hasPages, "nav.tab." + tabs[i].Title)) PickTab(i);
            Rect parent = GUILayoutUtility.GetLastRect();
            GrowEnd();
            if (!hasPages) continue;
            // The pages open and close smoothly: each row eases between no height and its full height as it fades, so
            // the panel grows and shrinks with them instead of jumping.
            float open = PagesOpen(selected);
            if (open <= 0f) continue;
            float rowHeight = PageRowHeight * open;

            // The pages hang on branches that start under the category's text and run on from row to row.
            float lineX = parent.x + 14f, lastY = parent.yMax;
            Color pagesColor = GUI.color;
            GUI.color = new Color(pagesColor.r, pagesColor.g, pagesColor.b, pagesColor.a * open * open);
            for (int p = 0; p < pages.Count; p++)
            {
                bool shown = selected && DiagnosticsPage.Tab == p;
                GrowBegin(fromBottom--);
                GUILayout.BeginHorizontal(GUILayout.Height(rowHeight));
                // Room for the branch and its node, then a gap before the page's own highlight.
                GUILayout.Space(34f);
                var page = new GUIContent(pages[p]);
                Rect item = GUILayoutUtility.GetRect(10f, rowHeight, GUILayout.ExpandWidth(true), GUILayout.Height(rowHeight));
                if (shown) Selected(item);
                if (Focus.Item("nav.diagnostics." + pages[p], item) | GUI.Toggle(item, shown, page, SubNavStyle()) != shown)
                {
                    PickTab(i);
                    DiagnosticsPage.Tab = p;
                }
                if (shown) SelectedBar(item);
                GUILayout.EndHorizontal();
                Rect row = GUILayoutUtility.GetLastRect();
                GUILayout.Space(2f * open);
                if (Themed && Event.current.type == EventType.Repaint)
                {
                    float y = row.center.y;
                    Tree(new Rect(lineX, lastY, 1f, y - lastY + 0.5f));
                    Tree(new Rect(lineX, y, 8f, 1f));
                    // A node at the branch's end, in the accent colour for the page shown.
                    DrawDot(new Rect(lineX + 8f, y - 2.5f, 5f, 5f), shown ? Theme.AccentText : new Color(Theme.Dim.r, Theme.Dim.g, Theme.Dim.b, 0.8f));
                }
                lastY = row.center.y;
                GrowEnd();
            }
            GUI.color = pagesColor;
            GUILayout.Space(2f * open);
        }
        GUILayout.EndVertical();
        if (!Closing && Event.current.type == EventType.Repaint)
        {
            s_panelRect = GUILayoutUtility.GetLastRect();
            s_panelBottom = s_panelRect.yMax - 1f;
        }
    }

    // The panel's place and size as last laid out, for drawing its frame.
    private static Rect s_panelRect;

    // How far the panel's frame has risen: 0 to 1 over the whole grow, up to the heading's arrival, easing out.
    private static float PanelGrow()
    {
        float t = Mathf.Clamp01(Elapsed(s_navSwitchedAt) / (GrowSeconds + GrowStagger * s_growRows));
        return 1f - (1f - t) * (1f - t) * (1f - t);
    }

    // How far Diagnostics' pages are open in the sidebar: eases toward 1 while it is picked and 0 otherwise, moved once
    // per frame (in the layout pass, so the passes of a frame agree) at the animation speed; at once with animations off.
    private static float s_pagesOpen = -1f;
    private const float PagesSeconds = 0.22f;
    private const float PageRowHeight = 28f;

    private static float PagesOpen(bool target)
    {
        float goal = target ? 1f : 0f;
        if (s_pagesOpen < 0f || !LookSettings.Animations.Value) s_pagesOpen = goal;
        else if (Event.current.type == EventType.Layout)
            s_pagesOpen = Mathf.MoveTowards(s_pagesOpen, goal, Time.unscaledDeltaTime * Mathf.Clamp(LookSettings.AnimationSpeed.Value, 0.25f, 4f) / PagesSeconds);
        float t = s_pagesOpen;
        return t * t * (3f - 2f * t);
    }

    // A category: marked as the page shown (Selected), or, while one of its pages is shown, as their parent: its text
    // bright like the page's, without the mark, one step quieter than the page. The branches below show the nesting.
    private static bool CategoryRow(string title, bool selected, bool hasPages, string key)
    {
        bool marked = selected && !hasPages;
        GUIStyle style = CategoryStyle();
        var content = new GUIContent(title);
        // The mark goes under the text, so it is drawn first.
        Rect r = GUILayoutUtility.GetRect(content, style, GUILayout.ExpandWidth(true));
        if (marked) Selected(r);
        // A parent uses its own style with bright text: a content colour only tints the style's dim text.
        bool clicked = GUI.Toggle(r, marked, content, selected && hasPages ? ParentStyle() : style) != marked;
        if (marked) SelectedBar(r);
        return Focus.Item(key, r) || clicked && !marked;
    }

    // The quiet mark for the item shown in the sidebar: a faint accent tint under its text (Selected, drawn before the
    // item) and a thin accent bar at its left on top (SelectedBar, after it), so hovering never hides it.
    private static void Selected(Rect r)
    {
        if (!Themed || Event.current.type != EventType.Repaint) return;
        Color previous = GUI.color;
        GUI.color = new Color(Theme.Accent.r, Theme.Accent.g, Theme.Accent.b, previous.a * 0.12f);
        Theme.DrawSliced(r, Theme.Swatch, Theme.ChipRadius);
        GUI.color = previous;
    }

    private static void SelectedBar(Rect r)
    {
        if (!Themed || Event.current.type != EventType.Repaint) return;
        Color previous = GUI.color;
        GUI.color = new Color(Theme.Accent.r, Theme.Accent.g, Theme.Accent.b, previous.a);
        GUI.DrawTexture(new Rect(r.x, r.y + 7f, 2f, r.height - 14f), Texture2D.whiteTexture);
        GUI.color = previous;
    }

    // A nav item's look for Settings' sidebar: no filled "on" state (Selected marks it), the shown item in full text colour.
    private static void Quiet(GUIStyle style)
    {
        // The shown item keeps no hover fill of its own, so its tint stays visible under the cursor.
        style.onNormal.background = style.normal.background;
        style.onHover.background = style.normal.background;
        style.onActive.background = style.normal.background;
        style.onFocused.background = style.focused.background;
        if (!Themed) return;
        style.onNormal.textColor = Theme.Text;
        style.onHover.textColor = Theme.Text;
        style.onActive.textColor = Theme.Text;
    }

    private static GUIStyle? s_panel, s_panelBase, s_category, s_categoryBase, s_heading, s_headingBase, s_featureNav, s_featureNavBase;

    // Feature pages in the sidebar: the nav item's look (room for the state dot) with the quiet mark for the one shown.
    private static GUIStyle FeatureNavStyle() => Derived(ref s_featureNav, ref s_featureNavBase, Style(() => Theme.NavItem, GUI.skin.button), Quiet);

    // The Settings panel in the sidebar: the card's look (edge and fill), with room around its rows.
    private static GUIStyle PanelStyle() => Derived(ref s_panel, ref s_panelBase, Style(() => Theme.Card, GUI.skin.box), style =>
    {
        style.padding = new RectOffset(6, 6, 6, 6);
        style.margin = new RectOffset(0, 0, 0, 0);
    });

    private static GUIStyle? s_panelLayout, s_panelLayoutBase;

    // The panel's room around its rows without its frame, which SettingsNav draws itself so it can grow.
    private static GUIStyle PanelLayoutStyle() => Derived(ref s_panelLayout, ref s_panelLayoutBase, PanelStyle(), style =>
    {
        style.normal.background = null;
        style.hover.background = null;
        style.active.background = null;
        style.focused.background = null;
    });

    // Categories in the panel: the nav item's look without the room a state dot needs.
    private static GUIStyle CategoryStyle() => Derived(ref s_category, ref s_categoryBase, Style(() => Theme.NavItem, GUI.skin.button), style =>
    {
        style.padding = new RectOffset(12, 10, style.padding.top, style.padding.bottom);
        style.margin = new RectOffset(0, 0, 0, 2);
        Quiet(style);
    });

    private static GUIStyle? s_parent, s_parentBase;

    // A category whose page is shown: the category's look with its text bright.
    private static GUIStyle ParentStyle() => Derived(ref s_parent, ref s_parentBase, CategoryStyle(), style =>
    {
        if (!Themed) return;
        style.normal.textColor = Theme.Text;
        style.hover.textColor = Theme.Text;
    });

    private static GUIStyle NavHeadingStyle() => Derived(ref s_heading, ref s_headingBase, Style(() => Theme.CardTitle, GUI.skin.label), style =>
    {
        style.alignment = TextAnchor.MiddleLeft;
        style.padding = new RectOffset(0, 0, 0, 0);
        style.margin = new RectOffset(0, 0, 0, 0);
        style.contentOffset = Vector2.zero;
    });

    // A copy of a theme style, remade when the theme rebuilds its styles.
    private static GUIStyle Derived(ref GUIStyle? style, ref GUIStyle? from, GUIStyle source, Action<GUIStyle> change)
    {
        if (style == null || !ReferenceEquals(from, source))
        {
            from = source;
            style = new GUIStyle(source);
            change(style);
        }
        return style;
    }

    private static void PickTab(int tab)
    {
        s_search = string.Empty;
        SetSettingsTab(tab);
    }

    private static void Tree(Rect r)
    {
        Color previous = GUI.color;
        GUI.color = new Color(Theme.Dim.r, Theme.Dim.g, Theme.Dim.b, previous.a * 0.6f);
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = previous;
    }

    private static GUIStyle? s_subNav, s_subNavBase;

    // Diagnostics' pages in the sidebar: the nav item's look, smaller and shorter, without the room a state dot needs.
    private static GUIStyle SubNavStyle()
    {
        GUIStyle nav = Style(() => Theme.NavItem, GUI.skin.button);
        if (s_subNav == null || !ReferenceEquals(s_subNavBase, nav))
        {
            s_subNavBase = nav;
            s_subNav = new GUIStyle(nav) { padding = new RectOffset(10, nav.padding.right, nav.padding.top, nav.padding.bottom), margin = new RectOffset(0, 0, 0, 2) };
            if (Themed)
            {
                s_subNav.fontSize = Math.Max(10, nav.fontSize - 1);
                s_subNav.fixedHeight = 28f;
            }
            Quiet(s_subNav);
        }
        return s_subNav;
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
        SettingsHeader();
        // A search looks through every page, so it replaces the tabs while there is one.
        if (s_search.Trim().Length > 0)
        {
            BeginScroll();
            SettingsView.SearchResults(s_search);
            EndScroll();
            return;
        }
        // The sidebar picks the section (and Diagnostics' page).
        var tabs = Tabs;
        int tab = Math.Clamp(s_settingsTab, 0, tabs.Count - 1);

        if (tabs[tab].Section is not { } section)
        {
            BeginScroll();
            ProfilesPage.Draw();
            EndScroll();
            return;
        }

        if (section == Config.Diagnostics)
        {
            // Diagnostics has tabs of its own, and its Console handles its own scrolling.
            DiagnosticsPage.DrawTab();
            return;
        }
        BeginScroll();
        SettingsView.Card(section);
        Hint("Double-click a slider to reset it. Search finds settings from every page.");
        EndScroll();
    }

    private const string ProfilesTab = "Profiles";

    // Settings' pages in the sidebar: the General sections (Diagnostics draws more than its settings), then Profiles
    // last (no section).
    private static List<(string Title, Section? Section)> Tabs
    {
        get
        {
            var tabs = Config.Group.Sections.Select(section => (section.Title, (Section?)section)).ToList();
            tabs.Add((ProfilesTab, null));
            return tabs;
        }
    }

    // The page's top row: its title, then after a gap the search field beside the close button. The field is narrow
    // until it has the keyboard, then takes the free space.
    private static void SettingsHeader()
    {
        const float gap = 16f;
        GUIStyle title = Style(() => Theme.PageTitle, GUI.skin.label);
        GUIStyle close = Style(() => Theme.Close, GUI.skin.button);
        float titles = title.CalcSize(new GUIContent("Settings")).x;
        float closeWidth = close.fixedWidth + close.margin.horizontal;
        float narrow = Mathf.Floor(PageWidth / 4f);
        bool expanded = GUI.GetNameOfFocusedControl() == SearchControl || s_search.Length > 0;
        float width = expanded ? Mathf.Max(narrow, PageWidth - titles - closeWidth - gap) : narrow;

        GUILayout.BeginHorizontal(GUILayout.Height(30f));
        GUILayout.Label("Settings", title);
        GUILayout.FlexibleSpace();
        GUILayout.BeginVertical(GUILayout.Width(width));
        GUILayout.Space(2f);
        SearchField(width);
        GUILayout.EndVertical();
        if (Pressed(GUILayout.Button("×", close), "header.close")) Actions.Run("menu.close", "menu ×");
        GUILayout.EndHorizontal();
        GUILayout.Space(8f);
    }

    /// <summary>The Settings page's Mini HUD tab is open, so the HUD shows behind the menu as a preview.</summary>
    public static bool PreviewingHud => Visible && Current == SettingsPage && s_search.Trim().Length == 0
        && Tabs[Math.Clamp(s_settingsTab, 0, Tabs.Count - 1)].Section == HudSettings.Hud;

    // One full-width field, with a greyed placeholder while empty and a clear button inside its right end.
    private static void SearchField(float width)
    {
        Rect field = GUILayoutUtility.GetRect(width, 26f, GUILayout.Width(width), GUILayout.Height(26f));
        var clear = new Rect(field.xMax - 26f, field.y, 26f, field.height);
        bool hasText = s_search.Length > 0;
        // Drawn first so the clear button takes its click before the field under it, and always (acting only with
        // text), so the field's control id doesn't move when the first letter is typed and the field keeps the keyboard.
        bool cleared = Themed && GUI.Button(clear, GUIContent.none, GUIStyle.none) && hasText;
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
        if (s_search.Length == 0) Widgets.Paint(field, "Search", Theme.Placeholder);
        else Widgets.Paint(clear, "×", Theme.FieldClear);
    }

    /// <summary>Opens the menu on the Settings tab of a General section.</summary>
    public static void OpenSettingsTab(Section section)
    {
        Open(SettingsPage);
        s_search = string.Empty;
        SetSettingsTab(Tabs.FindIndex(t => t.Section == section));
    }

    /// <summary>Opens the menu on Settings.</summary>
    public static void OpenSettings() => Open(SettingsPage);

    /// <summary>Opens the menu on Settings' Profiles page.</summary>
    public static void OpenProfiles()
    {
        Open(SettingsPage);
        s_search = string.Empty;
        SetSettingsTab(Tabs.FindIndex(t => t.Section == null));
    }

    /// <summary>Set by console.open: the console's input line takes the keyboard when it is next drawn.</summary>
    public static bool FocusConsoleField;

    /// <summary>Opens the menu on Diagnostics' Console tab with its input line ready to type.</summary>
    public static void OpenConsole()
    {
        OpenSettingsTab(Config.Diagnostics);
        DiagnosticsPage.ShowConsole();
        FocusConsoleField = true;
    }

    /// <summary>Shows one tab of the Settings page, in the order shown.</summary>
    public static void SetSettingsTab(int tab)
    {
        s_settingsTab = Math.Clamp(tab, 0, Tabs.Count - 1);
        s_scroll = Vector2.zero;
    }

    private static void DividerLine()
    {
        Rect r = GUILayoutUtility.GetRect(10f, 9f, GUILayout.ExpandWidth(true));
        if (Themed && Event.current.type == EventType.Repaint) GUI.DrawTexture(new Rect(r.x, r.y + 4f, r.width, 1f), Theme.Divider);
    }

    // Page width inside the main area: window minus sidebar, page padding, and the vertical scrollbar with its margin.
    private const float ContentWidth = Width - Theme.SidebarWidth - 32f - 22f;

    /// <summary>The page's width inside its padding, without a scrollbar.</summary>
    public const float PageWidth = Width - Theme.SidebarWidth - 32f;

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
