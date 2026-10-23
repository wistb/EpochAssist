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
    private static readonly string[] s_castTabs = { "Controls", "Pausing", "Targeting", "Enemy gate" };
    private static readonly string[] s_cornerNames = { "Top left", "Top right", "Bottom left", "Bottom right" };

    private static Page s_page;
    private static int s_castTab;
    private static Rect s_rect = new(80, 80, Width, Height);
    private static Vector2 s_scroll;
    private static bool s_weightsOpen;
    // Applied on release: rescaling mid-drag moves the slider under the cursor.
    private static float? s_pendingScale;

    public static bool Visible { get; private set; }

    public static void Close() => SetVisible(false);

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

        if (s_pendingScale.HasValue && GUIUtility.hotControl == 0)
        {
            Config.UiScale.Value = s_pendingScale.Value;
            s_pendingScale = null;
        }

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
            if (feature.State != FeatureState.Faulted) continue;
            GUILayout.BeginHorizontal(Style(() => Theme.Card, GUI.skin.box));
            GUILayout.Label($"{Colored(feature.Name + " stopped", Theme.Pause)}  {feature.LastError}", Style(() => Theme.Hint, GUI.skin.label));
            if (GUILayout.Button("Restart", Style(() => Theme.SmallButton, GUI.skin.button), GUILayout.ExpandWidth(false)))
                FeatureHost.Restart(feature);
            GUILayout.EndHorizontal();
        }
    }

    private static void DrawSidebar()
    {
        GUILayout.BeginVertical(Style(() => Theme.Sidebar, GUI.skin.box), GUILayout.Width(Theme.SidebarWidth), GUILayout.ExpandHeight(true));
        GUILayout.Label(BuildInfo.Name, Style(() => Theme.NavTitle, GUI.skin.label));
        GUILayout.Label("v" + BuildInfo.Version, Style(() => Theme.NavVersion, GUI.skin.label));

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
                Color dot = page == Page.AutoCast ? Theme.StateColor(AutoCast.CurrentState) : Theme.Off;
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

    private static void PageHeader(string title, AutoCast.State? state, MelonLoader.MelonPreferences_Entry<bool>? toggle)
    {
        GUILayout.BeginHorizontal(GUILayout.Height(30f));
        GUILayout.Label(title, Style(() => Theme.PageTitle, GUI.skin.label));
        if (state.HasValue) StateBadge(state.Value);
        GUILayout.FlexibleSpace();
        if (toggle != null)
        {
            bool value = InlineSwitch(toggle.Value);
            if (value != toggle.Value) toggle.Value = value;
        }
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
        for (int i = 0; i < s_castTabs.Length; i++)
        {
            if (GUILayout.Toggle(s_castTab == i, s_castTabs[i], Style(() => Theme.SubTab, GUI.skin.button), GUILayout.ExpandWidth(false)) && s_castTab != i)
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

        s_scroll = GUILayout.BeginScrollView(s_scroll);
        switch (s_castTab)
        {
            case 0: DrawControls(); break;
            case 1: DrawPausing(); break;
            case 2: DrawTargeting(); break;
            default: DrawEnemyGate(); break;
        }
        GUILayout.EndScrollView();

        GUI.color = previousColor;
        GUI.enabled = wasEnabled;
    }

    private static void DrawControls()
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("Modifier", Style(() => Theme.RowLabel, GUI.skin.label), GUILayout.Width(LabelWidth), GUILayout.Height(30f));
        for (int i = 0; i < AutoCast.ModifierKeys.Length; i++)
        {
            bool selected = S.ModifierKey.Value == AutoCast.ModifierKeys[i];
            if (GUILayout.Toggle(selected, AutoCast.ModifierKeyNames[i], Style(() => Theme.Segment, GUI.skin.button)) && !selected)
                S.ModifierKey.Value = AutoCast.ModifierKeys[i];
        }
        GUILayout.EndHorizontal();
        KeyRow("Hold to pause", S.HoldToPauseKey, clearable: true);
        Hint("While held, auto-cast stops and lets go of any channel.");
    }

    private static void DrawPausing()
    {
        Switch(S.PauseOnZoneChange, "Pause on zone change");
        Hint("Press any skill yourself to resume.");
        GUILayout.Space(4f);
        Switch(S.PauseInNonCombatZones, "Pause in non-combat zones");
    }

    private static void DrawTargeting()
    {
        Switch(S.AimAtClosestEnemy, "Aim at closest enemy");
        if (S.AimAtClosestEnemy.Value) Slider(S.AimRange, "Aim range", 2f, 60f, " m");
        Hint("Uses the cursor when nothing is in range.");
    }

    private static void DrawEnemyGate()
    {
        Switch(S.RequireEnemies, "Only cast with enemies in range");
        if (!S.RequireEnemies.Value) return;

        Slider(S.EnemyRange, "Range", 2f, 60f, " m");
        Slider(S.EnemyMinWeight, "Minimum weight", 1f, 50f);
        Switch(S.ShowRangeRing, "Show range ring");
        string gate = AutoCast.EnemyGateOpen ? Colored("casting", Theme.Good) : Colored("holding", Theme.Pause);
        Hint($"In range: {AutoCast.EnemiesInRange} enemies, weight {AutoCast.EnemyWeightInRange:F0}  ·  {gate}");

        GUILayout.Space(4f);
        s_weightsOpen = GUILayout.Toggle(s_weightsOpen, s_weightsOpen ? "Hide enemy weights" : "Enemy weights",
            Style(() => Theme.SmallButton, GUI.skin.button), GUILayout.ExpandWidth(false));
        if (s_weightsOpen)
        {
            Slider(S.WeightNormal, "Normal", 0f, 50f);
            Slider(S.WeightMagic, "Magic", 0f, 50f);
            Slider(S.WeightRare, "Rare", 0f, 50f);
            Slider(S.WeightBoss, "Boss", 0f, 50f);
        }
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
        s_scroll = GUILayout.BeginScrollView(s_scroll);

        BeginCard("MENU");
        KeyRow("Menu key", Config.MenuKey, clearable: false);
        float current = s_pendingScale ?? Config.UiScale.Value;
        float scale = Mathf.Round(SliderRow("UI scale", current, 0.75f, 2f, $"{current:F2}x", (float)Config.UiScale.DefaultValue) * 20f) / 20f;
        if (scale != current) s_pendingScale = scale;
        PercentSlider(Config.MenuOpacity, "Background", 0.2f, 1f);
        Hint("Scale applies when you let go of the slider. Background opacity leaves text and switches solid. Double-click any slider to reset it.");
        EndCard();

        BeginCard("MINI HUD");
        Switch(Config.HudEnabled, "Show the mini HUD");
        Hint("A small status pill while the menu is closed. Click it for quick toggles.");
        GUILayout.BeginHorizontal();
        for (int i = 0; i < s_cornerNames.Length; i++)
        {
            bool selected = (int)Config.HudPosition == i;
            if (GUILayout.Toggle(selected, s_cornerNames[i], Style(() => Theme.Segment, GUI.skin.button)) && !selected)
                Config.HudPosition = (HudCorner)i;
        }
        GUILayout.EndHorizontal();
        PercentSlider(Config.HudOpacity, "Opacity", 0.2f, 1f);
        Hint("Full opacity while the cursor is over it.");
        EndCard();

        GUILayout.EndScrollView();
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
            GUI.Label(new Rect(chip.x + 3f, chip.y + 28f, chip.width - 6f, 16f), name, Theme.ChipName);
        }
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
