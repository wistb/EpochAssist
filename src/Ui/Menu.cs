using EpochAssist.Core;
using EpochAssist.Features;
using UnityEngine;
using static EpochAssist.Ui.Widgets;
using static UnityEngine.GUI;
using BuildInfo = EpochAssist.Core.BuildInfo;
using S = EpochAssist.Features.AutoCastSettings;

namespace EpochAssist.Ui;

internal enum Page { AutoCast, Potion, Settings }

// The full menu: a sidebar of pages on the left, the page on the right.
internal static class Menu
{
    private const int WindowId = 0x45417373;
    private const float Width = 580f;
    private const float Height = 520f;

    private static readonly string[] s_pageNames = { "Auto-Cast", "Potion", "Settings" };

    private static Page s_page;
    private static int s_castTab;
    private static Rect s_rect = new(80, 80, Width, Height);
    private static Vector2 s_scroll;
    private static string s_search = string.Empty;

    public static bool Visible { get; private set; }

    public static void Close() => SetVisible(false);

    /// <summary>The window in screen pixels (top-left origin), after UI scale.</summary>
    public static Rect ScreenRect
    {
        get
        {
            float scale = Mathf.Clamp(Config.UiScale.Value, 0.5f, 3f);
            return new Rect(s_rect.x * scale, s_rect.y * scale, s_rect.width * scale, s_rect.height * scale);
        }
    }

    public static void SetTab(int tab)
    {
        s_castTab = Math.Clamp(tab, 0, S.Tabs.Length - 1);
        s_scroll = Vector2.zero;
    }

    public static void SetSearch(string text)
    {
        s_page = Page.Settings;
        s_search = text;
        s_scroll = Vector2.zero;
    }

    public static void Open(Page page)
    {
        s_page = page;
        SetVisible(true);
    }

    public static void OnUpdate()
    {
        if (UpdateKeyCapture(Visible)) return;

        if (Input.GetKeyDown(Config.MenuKey.Value) || (Visible && Input.GetKeyDown(KeyCode.Escape)))
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
        Visible = visible;
        InputGate.Menu = visible;
        if (!visible)
        {
            Capturing = null;
            Config.Save();
        }
    }

    private static void DrawWindow(int id)
    {
        float opacity = Mathf.Clamp(Config.MenuOpacity.Value, 0.2f, 1f);
        if (Themed && Event.current.type == EventType.Repaint)
        {
            Color color = GUI.color;
            GUI.backgroundColor = Color.white;
            GUI.color = new Color(color.r, color.g, color.b, color.a * opacity);
            Theme.DrawSliced(new Rect(0f, 0f, s_rect.width, s_rect.height), Theme.WindowBg, 14);
            Theme.DrawSliced(new Rect(0f, 0f, Theme.SidebarWidth, s_rect.height), Theme.SidebarBg, 14);
            GUI.color = color;
        }
        // Fades cards, buttons and chips but not text or switches.
        GUI.backgroundColor = new Color(1f, 1f, 1f, opacity);

        GUILayout.BeginHorizontal();
        DrawSidebar();
        GUILayout.BeginVertical(Style(() => Theme.Main, GUIStyle.none));
        FaultBanners();
        switch (s_page)
        {
            case Page.AutoCast: DrawAutoCast(); break;
            case Page.Potion: DrawPotion(); break;
            default: DrawSettings(); break;
        }
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();

        // Controls claim their clicks first; this only gets clicks on empty space near the top.
        GUI.DragWindow(new Rect(0f, 0f, s_rect.width, 48f));
    }

    // A feature stopped by repeated errors says so at the top of every page, with a way to try again.
    private static void FaultBanners()
    {
        foreach (var feature in FeatureHost.All)
        {
            bool degraded = feature.Degraded;
            if (feature.State is not (FeatureState.Faulted or FeatureState.Unavailable) && !degraded) continue;
            bool unavailable = feature.State == FeatureState.Unavailable;
            string detail = degraded
                ? string.Join("; ", feature.MissingGroups.Select(g => $"without {g} (game update)").Concat(feature.PatchFailures.Select(f => "patch failed: " + f)))
                : feature.LastError ?? string.Empty;
            GUILayout.BeginHorizontal(Style(() => Theme.Card, GUI.skin.box));
            string title = Colored(feature.Name + (degraded ? " partly working" : unavailable ? " unavailable" : " stopped"), Theme.Pause);
            GUILayout.Label($"{title}  {detail}", Style(() => Theme.Hint, GUI.skin.label));
            // A restart can't bring back what the game no longer has; an update of the mod can.
            if (!unavailable && !degraded && GUILayout.Button("Restart", Style(() => Theme.SmallButton, GUI.skin.button), GUILayout.ExpandWidth(false)))
                FeatureHost.Restart(feature);
            GUILayout.EndHorizontal();
        }
    }

    private static void DrawSidebar()
    {
        GUILayout.BeginVertical(Style(() => Theme.Sidebar, GUI.skin.box), GUILayout.Width(Theme.SidebarWidth), GUILayout.ExpandHeight(true));
        GUILayout.Label(BuildInfo.Name, Style(() => Theme.NavTitle, GUI.skin.label));
        GUILayout.Label($"v{BuildInfo.Version}  ·  {Runtime.BuildId}", Style(() => Theme.NavVersion, GUI.skin.label));
        if (Runtime.Hosted) GUILayout.Label($"dev · loaded {Runtime.LoadedAt:HH:mm:ss}", Style(() => Theme.NavVersion, GUI.skin.label));
        GUILayout.Space(10f);

        for (int i = 0; i < s_pageNames.Length; i++)
        {
            var page = (Page)i;
            bool selected = s_page == page;
            if (GUILayout.Toggle(selected, s_pageNames[i], Style(() => Theme.NavItem, GUI.skin.button)) && !selected)
            {
                s_page = page;
                s_scroll = Vector2.zero;
            }

            Rect item = GUILayoutUtility.GetLastRect();
            if (Themed && page != Page.Settings && Event.current.type == EventType.Repaint)
            {
                bool castRunning = FeatureHost.Get<AutoCastFeature>()?.State == FeatureState.Running;
                Color dot = page == Page.AutoCast && castRunning ? Theme.StateColor(AutoCast.CurrentState) : Theme.Off;
                if (selected && page == Page.AutoCast && AutoCast.CurrentState == AutoCast.State.Off) dot = Color.white;
                DrawDot(new Rect(item.x + 10f, item.center.y - 4f, 8f, 8f), dot);
            }
        }

        GUILayout.FlexibleSpace();
        GUILayout.Label($"{Config.MenuKey.Value} or Esc closes", Style(() => Theme.Hint, GUI.skin.label));
        GUILayout.EndVertical();

        if (Themed && Event.current.type == EventType.Repaint)
        {
            Rect bar = GUILayoutUtility.GetLastRect();
            GUI.DrawTexture(new Rect(bar.xMax - 1f, bar.y, 1f, bar.height), Theme.Divider);
        }
    }

    private static void PageHeader(string title, AutoCast.State? state, Toggle? toggle)
    {
        GUILayout.BeginHorizontal(GUILayout.Height(30f));
        GUILayout.Label(title, Style(() => Theme.PageTitle, GUI.skin.label));
        if (state.HasValue) StateBadge(state.Value);
        GUILayout.FlexibleSpace();
        if (toggle != null) toggle.Value = InlineSwitch(toggle.Value);
        if (GUILayout.Button("×", Style(() => Theme.Close, GUI.skin.button))) SetVisible(false);
        GUILayout.EndHorizontal();
        GUILayout.Space(8f);
    }

    private static void StateBadge(AutoCast.State state)
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
            DrawDot(new Rect(r.x + 9f, r.center.y - 3.5f, 7f, 7f), color);
        }
    }

    private static void DrawAutoCast()
    {
        PageHeader("Auto-Cast", AutoCast.CurrentState, S.Enabled);

        SlotChips(52f);
        string detail = AutoCast.StateDetail;
        Hint($"Hold {AutoCast.ModifierKeyName} and press a skill's key to toggle its slot."
            + (detail.Length > 0 ? $"  {Colored(detail, Theme.StateColor(AutoCast.CurrentState))}" : string.Empty));
        GUILayout.Space(8f);

        GUILayout.BeginHorizontal();
        for (int i = 0; i < S.Tabs.Length; i++)
        {
            if (GUILayout.Toggle(s_castTab == i, S.Tabs[i].Title, Style(() => Theme.SubTab, GUI.skin.button), GUILayout.ExpandWidth(false)) && s_castTab != i)
            {
                s_castTab = i;
                s_scroll = Vector2.zero;
            }
        }
        GUILayout.EndHorizontal();
        DividerLine();

        bool wasEnabled = GUI.enabled;
        Color previousColor = GUI.color;
        GUI.enabled = S.Enabled.Value;
        if (!GUI.enabled) GUI.color = new Color(1f, 1f, 1f, 0.45f);

        BeginScroll();
        Section tab = S.Tabs[Math.Clamp(s_castTab, 0, S.Tabs.Length - 1)];
        SettingsView.Rows(tab.Settings);
        if (tab == S.Gate && S.RequireEnemies.Value)
        {
            string gate = AutoCast.EnemyGateOpen ? Colored("casting", Theme.Good) : Colored("holding", Theme.Pause);
            Hint($"In range: {AutoCast.EnemiesInRange} enemies, weight {AutoCast.EnemyWeightInRange:F0}  ·  {gate}");
        }
        if (!tab.IsDefault)
        {
            GUILayout.Space(6f);
            if (GUILayout.Button($"Reset {tab.Title.ToLowerInvariant()}", Style(() => Theme.SmallButton, GUI.skin.button), GUILayout.ExpandWidth(false)))
                tab.Reset();
        }
        EndScroll();

        GUI.color = previousColor;
        GUI.enabled = wasEnabled;
    }

    private static void DrawPotion()
    {
        PageHeader("Potion", null, null);
        BeginCard("AUTO POTION");
        Hint("Drinks a health potion below a health threshold, optionally only when ward is low too. Coming in the next update.");
        EndCard();
    }

    private static void DrawSettings()
    {
        PageHeader("Settings", null, null);

        GUILayout.BeginHorizontal();
        GUILayout.Label("Search", Style(() => Theme.RowLabel, GUI.skin.label), GUILayout.Width(60f), GUILayout.Height(26f));
        s_search = GUILayout.TextField(s_search, Style(() => Theme.SearchField, GUI.skin.textField));
        if (s_search.Length > 0 && GUILayout.Button("Clear", Style(() => Theme.SmallButton, GUI.skin.button), GUILayout.ExpandWidth(false)))
            s_search = string.Empty;
        GUILayout.EndHorizontal();
        GUILayout.Space(8f);

        BeginScroll();
        if (s_search.Trim().Length > 0)
        {
            SettingsView.SearchResults(s_search);
        }
        else
        {
            foreach (var section in Config.Group.Sections) SettingsView.Card(section);
            Hint("Double-click a slider to reset it. Search finds settings from every page.");
        }
        EndScroll();
    }

    /// <summary>Five slot chips across the available width.</summary>
    public static void SlotChips(float height)
    {
        Rect row = GUILayoutUtility.GetRect(10f, height, GUILayout.ExpandWidth(true));
        if (Event.current.type != EventType.Repaint) return;

        AutoCast.State state = AutoCast.CurrentState;
        const float gap = 6f;
        float width = (row.width - gap * (AutoCast.Slots - 1)) / AutoCast.Slots;
        for (int i = 0; i < AutoCast.Slots; i++)
        {
            var chip = new Rect(row.x + i * (width + gap), row.y, width, row.height);
            bool toggled = AutoCast.IsSlotToggled(i);
            bool live = toggled && state == AutoCast.State.Active;
            string name = AutoCast.SlotAbilityName(i) ?? "-";

            if (!Themed)
            {
                GUI.Box(chip, $"{i + 1}\n{(toggled ? "ON" : "off")}");
                continue;
            }
            Theme.DrawSliced(chip, live ? Theme.ChipOn : toggled ? Theme.ChipPaused : Theme.ChipOff, 9);
            Theme.ChipNumber.normal.textColor = live ? Theme.AccentText : toggled ? Theme.Pause : Theme.Dim;
            Theme.ChipName.normal.textColor = toggled ? Theme.Text : Theme.Dim;
            GUI.Label(new Rect(chip.x, chip.y + 6f, chip.width, 22f), (i + 1).ToString(), Theme.ChipNumber);
            GUI.Label(new Rect(chip.x + 3f, chip.y + 28f, chip.width - 6f, 16f), Fit(name, Theme.ChipName, chip.width - 8f), Theme.ChipName);
        }
    }

    // Page width inside the main area: window minus sidebar, page padding, and the vertical scrollbar with its margin.
    private const float ContentWidth = Width - Theme.SidebarWidth - 32f - 22f;

    // Vertical scrolling only; content is held to the visible width so nothing runs off the right edge.
    private static void BeginScroll()
    {
        s_scroll = GUILayout.BeginScrollView(s_scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);
        GUILayout.BeginVertical(GUILayout.Width(ContentWidth));
        SettingsView.RowWidth = ContentWidth;
    }

    private static void EndScroll()
    {
        GUILayout.EndVertical();
        GUILayout.EndScrollView();
    }

    /// <summary>Text shortened with an ellipsis until it fits the width.</summary>
    private static string Fit(string text, GUIStyle style, float width)
    {
        if (style.CalcSize(new GUIContent(text)).x <= width) return text;
        for (int n = text.Length - 1; n > 1; n--)
        {
            string cut = text.Substring(0, n).TrimEnd() + "…";
            if (style.CalcSize(new GUIContent(cut)).x <= width) return cut;
        }
        return text.Substring(0, 1);
    }

    private static void DividerLine()
    {
        Rect r = GUILayoutUtility.GetRect(10f, 9f, GUILayout.ExpandWidth(true));
        if (Themed && Event.current.type == EventType.Repaint) GUI.DrawTexture(new Rect(r.x, r.y + 4f, r.width, 1f), Theme.Divider);
    }

    public static void DrawDot(Rect r, Color color)
    {
        Color previous = GUI.color;
        GUI.color = new Color(color.r, color.g, color.b, color.a * previous.a);
        GUI.DrawTexture(r, Theme.Dot);
        GUI.color = previous;
    }
}
