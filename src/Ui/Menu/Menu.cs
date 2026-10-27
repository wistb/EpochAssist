using EpochAssist.Core;
using UnityEngine;
using static UnityEngine.GUI;

namespace EpochAssist.Ui;

// The full menu: tabs and search along the top, the tree on the left, the page on the right and the quick toggles at
// the bottom. Features declare what it shows (Feature.Menu); General is declared in SettingsContent.
internal static class Menu
{
    private const int WindowId = 0x45417373;
    private const float Width = 800f;
    private const float Height = 820f;

    private static Rect s_rect = new(80, 60, Width, Height);
    private static string s_search = string.Empty;

    public static bool Visible { get; private set; }

    public static void Close() => SetVisible(false);

    public static void Toggle() => SetVisible(!Visible);

    /// <summary>The window in screen pixels (top-left origin), after UI scale.</summary>
    public static Rect ScreenRect
    {
        get
        {
            float scale = Scale;
            return new Rect(s_rect.x * scale, s_rect.y * scale, s_rect.width * scale, s_rect.height * scale);
        }
    }

    private static float Scale => Mathf.Clamp(LookSettings.UiScale.Value, 0.5f, 3f);

    /// <summary>Every page's path in tree order.</summary>
    public static IEnumerable<string> PageNames => MenuTree.Pages.Select(p => p.Path);

    /// <summary>The path of the page shown.</summary>
    public static string Current => MenuRoutes.Path;

    /// <summary>The search's text; while it has any, results take the page's place.</summary>
    public static string Search => s_search;

    public static void SetSearch(string text)
    {
        if (text == s_search) return;
        s_search = text;
    }

    /// <summary>Opens the menu on a page: a path, a feature id (its last or first page) or "settings".</summary>
    public static void Open(string page)
    {
        var target = page == "settings" ? MenuRoutes.Settings() : MenuRoutes.ForFeature(page) ?? MenuRoutes.Resolve(page);
        Open(target ?? MenuTree.Pages.FirstOrDefault());
    }

    public static void Open(PageNode? page)
    {
        // Theme files may have changed since the list was read.
        if (page != null && MenuRoutes.IsSettings(page) && (!Visible || page != MenuRoutes.Page)) Themes.Rescan();
        if (page != null) MenuRoutes.Show(page);
        SetVisible(true);
    }

    public static void OnUpdate()
    {
        MenuNav nav = Hotkeys.MenuNav;
        if (Visible && nav.Page != 0 && MenuRoutes.Next(nav.Page) is { } next)
        {
            s_search = string.Empty;
            MenuRoutes.Show(next);
        }
        if (Visible && nav.Search) TreeView.FocusSearch = true;

        // Only IMGUI text fields take keyboard control; buttons don't. A dropdown's filter takes typing too.
        GameState.UiTyping = Visible && (GUIUtility.keyboardControl != 0 || Popups.Current is DropdownPopup { Filterable: true });

        // The hotkey layer runs menu.toggle and menu.escape; while it is down, the menu still answers its key and Esc.
        if (FeatureHost.Find("hotkeys")?.State != FeatureState.Running
            && (Hotkeys.FallbackPressed(KeySettings.MenuKey) || (Visible && Hotkeys.FallbackEscape())))
        {
            SetVisible(!Visible);
        }
        InputGate.Menu = Visible;
    }

    /// <summary>Esc: closes the open popup, else cancels key capture, else clears a focused search, else closes the menu.</summary>
    public static void Escape()
    {
        if (Popups.IsOpen) Popups.Close(commit: false);
        else if (Hotkeys.CaptureActive) Hotkeys.CancelCapture();
        else if (s_search.Length == 0 && MenuRoutes.Page?.Node.Entry.Library is { } library && LibraryBack(library)) { }
        else if (s_search.Length > 0 && Focus.Current == "search")
        {
            s_search = string.Empty;
            TreeView.BlurSearch();
        }
        else Actions.Run("menu.close", "key Escape");
    }

    // Esc in the library: drops a rename, else closes the open editor.
    private static bool LibraryBack(LibraryView library)
    {
        if (MenuRoutes.Page is { } page && page != page.Node.Pages[0])
        {
            MenuRoutes.Show(page.Node.Pages[0]);
            return true;
        }
        if (library.Renaming != null)
        {
            library.Renaming = null;
            GUIUtility.keyboardControl = 0;
            return true;
        }
        return library.Back?.Invoke() == true;
    }

    public static void OnGUI()
    {
        if (!Visible) return;

        NumberField.CommitPending();

        GUISkin previousSkin = GUI.skin;
        Matrix4x4 previousMatrix = GUI.matrix;
        Color previousBackground = GUI.backgroundColor;
        GUISkin? skin = Theme.Skin;
        Widgets.Themed = skin != null;
        if (skin != null) GUI.skin = skin;

        float scale = Scale;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
        // The mock-up's size, kept inside the game window at any scale; the tree and page scroll when it is smaller.
        s_rect.width = Mathf.Min(Width, Screen.width / scale - 40f);
        s_rect.height = Mathf.Min(Height, Screen.height / scale - 40f);
        s_rect.x = Mathf.Clamp(s_rect.x, 0f, Mathf.Max(0f, Screen.width / scale - s_rect.width));
        s_rect.y = Mathf.Clamp(s_rect.y, 0f, Mathf.Max(0f, Screen.height / scale - s_rect.height));
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
        if (visible && !Visible)
        {
            HotkeyConflicts.Invalidate();
            MenuTree.Invalidate();
            TreeView.Reveal(MenuRoutes.Page);
        }
        Visible = visible;
        InputGate.Menu = visible;
        KeepKeysInField(visible);
        if (!visible)
        {
            Hotkeys.CancelCapture();
            Focus.Reset();
            Popups.Reset();
            NumberField.Reset();
            Slider.Reset();
            GUIUtility.keyboardControl = 0;
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
        // The open popup takes its events first.
        Popups.Bounds = new Rect(0f, 0f, s_rect.width, s_rect.height);
        Popups.BeginWindowEvent();
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
            Focus.Scope = string.Empty;
        }
    }

    private static void DrawContents()
    {
        float opacity = Mathf.Clamp(LookSettings.MenuOpacity.Value, 0.2f, 1f);
        float w = s_rect.width, h = s_rect.height;
        if (Widgets.Themed && Event.current.type == EventType.Repaint)
        {
            Color color = GUI.color;
            // The bars and the tree share the sidebar colour; the page fills its own rect over it.
            GUI.color = new Color(color.r, color.g, color.b, color.a * opacity * Theme.Opacity);
            Theme.DrawSliced(new Rect(0f, 0f, w, h), Theme.SidebarFill, Theme.WindowRadius);
            GUI.color = color;
        }
        GUI.backgroundColor = new Color(1f, 1f, 1f, opacity);

        var top = new Rect(0f, 0f, w, TopBar.Height);
        var foot = new Rect(0f, h - Footer.Height, w, Footer.Height);
        // The library layout has its own tree, so the menu's gives it the room (search results keep the usual layout).
        bool library = s_search.Trim().Length == 0 && MenuRoutes.Page?.Node.Entry.Layout == MenuLayout.Library;
        var tree = new Rect(0f, top.yMax, library ? 0f : TreeView.Width, foot.y - top.yMax);
        var page = new Rect(tree.xMax, top.yMax, w - tree.xMax - 1f, foot.y - top.yMax);

        if (Widgets.Themed && !library) Theme.Fill(new Rect(tree.xMax - 1f, tree.y, 1f, tree.height), Theme.DividerColor);
        // In reading order, which is the order keys move along.
        TopBar.Draw(top);
        if (!library) TreeView.Draw(tree);
        PageView.Draw(page);
        Footer.Draw(foot);
        Popups.DrawLast();

        // Controls claim their clicks first; this only gets presses on the bar's empty space.
        GUI.DragWindow(TopBar.DragArea);
    }

    /// <summary>Why a feature isn't running, as a sentence.</summary>
    public static string Reason(Feature feature)
    {
        if (feature.Record.TurnedOff) return $"{feature.Name} is turned off. Its settings are kept.";
        string detail = feature.Status.Detail.TrimEnd('.');
        return feature.State switch
        {
            FeatureState.Unavailable => $"{feature.Name} is unavailable after a game update: {detail}. An update of the mod can bring it back.",
            FeatureState.Faulted => $"{feature.Name} stopped after an error: {detail}.",
            _ => detail.Length > 0 ? $"{feature.Name} is stopped: {detail}." : $"{feature.Name} is stopped.",
        };
    }

    /// <summary>The Mini HUD page is shown with no search, so the HUD shows behind the menu as a preview.</summary>
    public static bool PreviewingHud => Visible && s_search.Trim().Length == 0 && MenuRoutes.Path == MenuRoutes.MiniHud;

    /// <summary>Opens the menu on the page of a General group section.</summary>
    public static void OpenSettingsTab(Section section)
    {
        s_search = string.Empty;
        Open(MenuTree.Pages.FirstOrDefault(p => p.Page.Section == section || p.Rows.OfType<SettingRow>().Any(r => r.Setting.Section == section)));
    }

    /// <summary>Opens the menu on General.</summary>
    public static void OpenSettings() => Open(MenuRoutes.Settings());

    /// <summary>Opens the menu on General › Profiles.</summary>
    public static void OpenProfiles()
    {
        s_search = string.Empty;
        Open(MenuTree.Find(MenuRoutes.Profiles));
    }

    /// <summary>Set by console.open: the console's input line takes the keyboard when it is next drawn.</summary>
    public static bool FocusConsoleField;

    /// <summary>Opens the menu on Diagnostics › Console with its input line ready to type.</summary>
    public static void OpenConsole()
    {
        s_search = string.Empty;
        Open(MenuTree.Find(MenuRoutes.Console));
        FocusConsoleField = true;
    }

    /// <summary>Shows one of the old Settings tabs' pages, by index: Mini HUD, Appearance, Keybinds, Diagnostics, Profiles.</summary>
    public static void SetSettingsTab(int tab)
    {
        s_search = string.Empty;
        if (MenuRoutes.SettingsTab(tab) is { } page) MenuRoutes.Show(page);
    }

    // ---- Blocks: they lay themselves out with GUILayout in the rect they get, and scroll with these ----

    private static Vector2 s_blockScroll;
    private static string? s_blockPath;

    /// <summary>Vertical scrolling for a block, its content held to the page's width.</summary>
    public static void BeginScroll()
    {
        if (s_blockPath != MenuRoutes.Path)
        {
            s_blockPath = MenuRoutes.Path;
            s_blockScroll = Vector2.zero;
        }
        s_blockScroll = GUILayout.BeginScrollView(s_blockScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);
        GUILayout.BeginVertical(GUILayout.Width(PageView.BlockWidth));
        Focus.InScroll = "block";
        Popups.PushOrigin(-s_blockScroll);
    }

    public static void EndScroll()
    {
        Popups.PopOrigin();
        Focus.InScroll = null;
        GUILayout.EndVertical();
        GUILayout.EndScrollView();
        // Brings a control focused by key into view; it shows from the next frame.
        if (Event.current.type == EventType.Repaint && Focus.TakeScrollTarget("block") is Rect target)
        {
            float view = GUILayoutUtility.GetLastRect().height;
            if (target.yMin < s_blockScroll.y) s_blockScroll.y = Mathf.Max(0f, target.yMin - 8f);
            else if (target.yMax > s_blockScroll.y + view) s_blockScroll.y = target.yMax - view + 8f;
        }
    }

    /// <summary>
    /// A rect in the window moved and sized to whole screen pixels at the UI scale, so an image drawn in it isn't
    /// resampled (the logo at 1.25x otherwise lands on half pixels).
    /// </summary>
    /// <summary>One screen pixel in the window's units.</summary>
    public static float ScreenPixel => 1f / Scale;

    /// <summary>A window x or y moved to the nearest screen pixel edge.</summary>
    public static float SnapX(float v)
    {
        float scale = Scale;
        return Mathf.Round((s_rect.x + v) * scale) / scale - s_rect.x;
    }

    public static float SnapY(float v)
    {
        float scale = Scale;
        return Mathf.Round((s_rect.y + v) * scale) / scale - s_rect.y;
    }

    public static Rect PixelSnap(Rect r)
    {
        float scale = Scale;
        float x = Mathf.Round((s_rect.x + r.x) * scale), y = Mathf.Round((s_rect.y + r.y) * scale);
        float w = Mathf.Round(r.width * scale), h = Mathf.Round(r.height * scale);
        return new Rect(x / scale - s_rect.x, y / scale - s_rect.y, w / scale, h / scale);
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

    /// <summary>
    /// A state's own shape in a colour (usually the state's): the crisp state icons, at least 9 px, centred on the
    /// rect given and on whole pixels.
    /// </summary>
    public static void DrawState(Rect r, StatusState state, Color color)
    {
        float size = Mathf.Max(9f, Mathf.Max(r.width, r.height));
        var at = new Rect(Mathf.Round(r.center.x - size / 2f), Mathf.Round(r.center.y - size / 2f), size, size);
        DrawTinted(at, Theme.StateIcon(state), color);
    }

    private static void DrawTinted(Rect r, Texture2D texture, Color color)
    {
        if (Event.current.type != EventType.Repaint) return;
        Color previous = GUI.color;
        GUI.color = new Color(color.r, color.g, color.b, color.a * previous.a);
        GUI.DrawTexture(r, texture);
        GUI.color = previous;
    }
}
