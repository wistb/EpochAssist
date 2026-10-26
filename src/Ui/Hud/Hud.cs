using EpochAssist.Core;
using UnityEngine;
using static EpochAssist.Ui.Widgets;

namespace EpochAssist.Ui;

// The status pill shown while the menu is closed, and its quick-toggle panel. The pill has a section per feature that
// declares a status name, side by side in registration order, wrapping to more rows when it would leave the screen.
internal static class Hud
{
    /// <summary>Distance from the screen edges to the pill; toasts use it too.</summary>
    public const float Margin = 16f;
    private const float PillRow = 26f;
    private const float PillPad = 3f;
    private const float SectionGap = 14f;
    public const float PipSize = 18f;
    /// <summary>How far a slot's edge sits inside the pill's: the pill's padding, then the slot centred in its row.</summary>
    public const float PipInset = PillPad + (PillRow - PipSize) / 2f;
    private const float PanelWidth = 260f;
    private const float PanelChrome = 84f;
    private const float RowHeight = 28f;

    private static bool s_panelOpen;
    // Screen-space areas from the last repaint, for blocking game clicks under the cursor.
    private static Rect s_pillScreen;
    private static Rect s_panelScreen;

    /// <summary>Pill and open panel together, in screen pixels; empty when hidden.</summary>
    public static Rect ScreenRect
    {
        get
        {
            if (s_pillScreen.width <= 0f) return Rect.zero;
            if (s_panelScreen.width <= 0f) return s_pillScreen;
            return Rect.MinMaxRect(Mathf.Min(s_pillScreen.xMin, s_panelScreen.xMin), Mathf.Min(s_pillScreen.yMin, s_panelScreen.yMin),
                Mathf.Max(s_pillScreen.xMax, s_panelScreen.xMax), Mathf.Max(s_pillScreen.yMax, s_panelScreen.yMax));
        }
    }

    public static bool PanelOpen => s_panelOpen;

    public static void TogglePanel() => s_panelOpen = !s_panelOpen;

    /// <summary>Whether a screen point (top-left origin) is on the pill or the open panel; InputGate blocks clicks there.</summary>
    public static bool HitTest(Vector2 screenPoint) =>
        Shown && (s_pillScreen.Contains(screenPoint) || (s_panelOpen && s_panelScreen.Contains(screenPoint)));

    private static IEnumerable<Feature> StatusFeatures => FeatureHost.All.Where(f => f.StatusName != null);

    // A feature that isn't running keeps its section, greyed; with no section at all there is no pill.
    // While the menu is open only as a preview, on the Settings page's Mini HUD tab.
    private static bool Shown => HudSettings.HudEnabled.Value && (!Menu.Visible || Menu.PreviewingHud) && Player.Exists && StatusFeatures.Any();

    public static void OnUpdate()
    {
        if (!Shown || Menu.Visible) s_panelOpen = false;
    }

    public static void OnGUI()
    {
        if (!Shown)
        {
            s_pillScreen = s_panelScreen = Rect.zero;
            return;
        }

        GUISkin? skin = Theme.Skin;
        if (skin == null) return;

        GUISkin previousSkin = GUI.skin;
        Matrix4x4 previousMatrix = GUI.matrix;
        GUI.skin = skin;
        Themed = true;
        float scale = Mathf.Clamp(LookSettings.UiScale.Value, 0.5f, 3f);
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
        try
        {
            Draw(scale);
        }
        finally
        {
            GUI.matrix = previousMatrix;
            GUI.skin = previousSkin;
        }
    }

    // One section of the pill: where it sits relative to the pill, and what it shows.
    private readonly record struct PillSection(Feature Feature, FeatureStatus Status, bool Running, GUIContent Label, float LabelWidth, Vector2 At);

    private static void Draw(float scale)
    {
        float screenW = Screen.width / scale, screenH = Screen.height / scale;
        ScreenSpot spot = HudSettings.HudSpot;

        var sections = Layout(screenW - Margin * 2f, out Vector2 size);
        var pill = Offset(spot.Place(size, screenW, screenH, Margin), screenW, screenH);

        var reasons = sections.Where(x => !x.Running).Select(x => new GUIContent(Menu.Reason(x.Feature))).ToList();
        float reasonsHeight = reasons.Sum(r => Theme.Hint.CalcHeight(r, PanelWidth - 24f) + 6f);
        float panelHeight = PanelChrome + reasonsHeight + SettingsRegistry.QuickToggles.Count() * RowHeight;
        // The panel lines up with the pill's side (or centre) and opens away from the screen edge: up from the bottom.
        float panelX = spot.Horizontal < 0 ? pill.x : spot.Horizontal > 0 ? pill.xMax - PanelWidth : pill.center.x - PanelWidth / 2f;
        var panel = new Rect(panelX, spot.Vertical > 0 ? pill.y - 8f - panelHeight : pill.yMax + 8f, PanelWidth, panelHeight);

        Event e = Event.current;
        if (s_panelOpen && e.type == EventType.MouseDown && !panel.Contains(e.mousePosition) && !pill.Contains(e.mousePosition))
        {
            s_panelOpen = false;
        }

        bool hover = pill.Contains(e.mousePosition) || (s_panelOpen && panel.Contains(e.mousePosition));
        Color previousColor = GUI.color;
        // The preview shows the opacity setting and takes no clicks.
        bool preview = Menu.Visible;
        GUI.color = new Color(1f, 1f, 1f, hover && !preview ? 1f : Mathf.Clamp(HudSettings.HudOpacity.Value, 0.2f, 1f));
        try
        {
            DrawPill(pill, sections);
            if (!preview && GUI.Button(pill, GUIContent.none, GUIStyle.none)) Actions.Run("hud.togglePanel", "HUD pill");
            if (s_panelOpen) DrawPanel(panel, reasons);
        }
        finally
        {
            GUI.color = previousColor;
        }

        s_pillScreen = Scaled(pill, scale);
        s_panelScreen = s_panelOpen ? Scaled(panel, scale) : Rect.zero;
    }

    // Sections in rows no wider than maxWidth; a section wider than that still gets a row of its own.
    private static List<PillSection> Layout(float maxWidth, out Vector2 size)
    {
        var sections = new List<PillSection>();
        float x = 10f, width = 0f;
        int row = 0;
        foreach (var feature in StatusFeatures)
        {
            bool running = feature.State == FeatureState.Running;
            FeatureStatus status = running ? feature.Status : FeatureStatus.Off(feature.Status.Detail);
            var label = new GUIContent(feature.StatusName);
            float labelWidth = Theme.PillLabel.CalcSize(label).x;
            float sectionWidth = 8f + 8f + labelWidth + (status.Slots is { Length: > 0 } slots ? 8f + slots.Length * PipSize + (slots.Length - 1) * 3f : 0f);
            float end = sectionWidth + (status.Slots != null ? 8f : 12f);
            if (x > 10f && x + end > maxWidth)
            {
                row++;
                x = 10f;
            }
            sections.Add(new PillSection(feature, status, running, label, labelWidth, new Vector2(x, PillPad + row * PillRow)));
            width = Mathf.Max(width, x + end);
            x += sectionWidth + SectionGap;
        }
        size = new Vector2(width, PillPad * 2f + (row + 1) * PillRow);
        return sections;
    }

    private static void DrawPill(Rect pill, List<PillSection> sections)
    {
        if (Event.current.type != EventType.Repaint) return;

        bool hover = pill.Contains(Event.current.mousePosition);
        Theme.DrawSliced(pill, hover ? Theme.Pill.hover.background : Theme.Pill.normal.background, Theme.PillRadius);

        foreach (var section in sections)
        {
            Color previous = GUI.color;
            if (!section.Running) GUI.color = new Color(previous.r, previous.g, previous.b, previous.a * 0.45f);
            FeatureStatus status = section.Status;
            float x = pill.x + section.At.x, centerY = pill.y + section.At.y + PillRow / 2f;
            Menu.DrawState(new Rect(x, centerY - 4f, 8f, 8f), status.State, Theme.StateColor(status.State));
            x += 8f + 8f;
            Theme.PillLabel.normal.textColor = status.State != StatusState.Off ? Theme.Text : Theme.Dim;
            Widgets.Paint(new Rect(x, centerY - PillRow / 2f, section.LabelWidth, PillRow), section.Label, Theme.PillLabel);
            x += section.LabelWidth + 8f;

            int count = status.Slots?.Length ?? 0;
            for (int i = 0; i < count; i++)
            {
                var pip = new Rect(x + i * (PipSize + 3f), centerY - PipSize / 2f, PipSize, PipSize);
                bool toggled = status.Slots![i];
                bool live = toggled && status.State == StatusState.Active;
                Theme.DrawSliced(pip, live ? Theme.PipOn : toggled ? Theme.PipPaused : Theme.PipOff, Theme.PipRadius);
                Theme.Pip.normal.textColor = live ? Theme.OnAccent : toggled ? Theme.Pause : Theme.Dim;
                Widgets.Paint(pip, (i + 1).ToString(), Theme.Pip);
            }
            GUI.color = previous;
        }
    }

    private static void DrawPanel(Rect panel, List<GUIContent> reasons)
    {
        GUILayout.BeginArea(panel, Theme.Panel);
        // Why a greyed section isn't running, before the toggles.
        foreach (var reason in reasons) GUILayout.Label(reason, Theme.Hint);
        GUILayout.Label("QUICK TOGGLES", Theme.CardTitle);
        foreach (var toggle in SettingsRegistry.QuickToggles)
        {
            if (Switch(toggle.Label, toggle.Value) != toggle.Value) Actions.Run(toggle.ActionId, "HUD quick toggle");
        }
        GUILayout.Space(6f);
        if (GUILayout.Button($"Open full menu  ·  {KeySettings.MenuKey.Display}", Theme.SmallButton))
        {
            s_panelOpen = false;
            string? first = FeatureHost.All.FirstOrDefault(f => f.Menu != null)?.Id;
            Actions.Run(first != null ? $"menu.page.{first}" : "menu.page.settings", "HUD open full menu");
        }
        GUILayout.EndArea();
    }

    /// <summary>A rect moved by the player's offsets, then kept on screen.</summary>
    public static Rect Offset(Rect r, float screenW, float screenH)
    {
        r.x = Mathf.Clamp(r.x + HudSettings.HudOffsetX.Value, 0f, Mathf.Max(0f, screenW - r.width));
        r.y = Mathf.Clamp(r.y + HudSettings.HudOffsetY.Value, 0f, Mathf.Max(0f, screenH - r.height));
        return r;
    }

    private static Rect Scaled(Rect r, float scale) => new(r.x * scale, r.y * scale, r.width * scale, r.height * scale);
}
