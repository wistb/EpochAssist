using EpochAssist.Core;
using EpochAssist.Features;
using UnityEngine;
using static EpochAssist.Ui.Widgets;
using S = EpochAssist.Features.AutoCastSettings;

namespace EpochAssist.Ui;

// The status pill shown while the menu is closed, and its quick-toggle panel.
internal static class Hud
{
    private const float Margin = 16f;
    private const float PillHeight = 32f;
    private const float PipSize = 18f;
    private const float PanelWidth = 260f;
    private const float PanelHeight = 196f;

    private static bool s_panelOpen;
    // Screen-space areas from the last repaint, for blocking game clicks under the cursor.
    private static Rect s_pillScreen;
    private static Rect s_panelScreen;

    private static bool Shown => Config.HudEnabled.Value && !Menu.Visible && Player.Exists;

    public static void OnUpdate()
    {
        if (!Shown)
        {
            s_panelOpen = false;
            InputGate.Hud = false;
            return;
        }

        var mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
        InputGate.Hud = s_pillScreen.Contains(mouse) || (s_panelOpen && s_panelScreen.Contains(mouse));
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
        float scale = Mathf.Clamp(Config.UiScale.Value, 0.5f, 3f);
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

    private static void Draw(float scale)
    {
        float screenW = Screen.width / scale, screenH = Screen.height / scale;
        HudCorner corner = Config.HudPosition.Value;
        bool right = corner is HudCorner.TopRight or HudCorner.BottomRight;
        bool bottom = corner is HudCorner.BottomLeft or HudCorner.BottomRight;

        var label = new GUIContent("Auto-Cast");
        float labelWidth = Theme.PillLabel.CalcSize(label).x;
        float pillWidth = 8f + 8f + 8f + labelWidth + 8f + AutoCast.Slots * PipSize + (AutoCast.Slots - 1) * 3f + 10f;
        var pill = new Rect(right ? screenW - Margin - pillWidth : Margin, bottom ? screenH - Margin - PillHeight : Margin, pillWidth, PillHeight);

        var panel = new Rect(right ? pill.xMax - PanelWidth : pill.x, bottom ? pill.y - 8f - PanelHeight : pill.yMax + 8f, PanelWidth, PanelHeight);

        Event e = Event.current;
        if (s_panelOpen && e.type == EventType.MouseDown && !panel.Contains(e.mousePosition) && !pill.Contains(e.mousePosition))
        {
            s_panelOpen = false;
        }

        bool hover = pill.Contains(e.mousePosition) || (s_panelOpen && panel.Contains(e.mousePosition));
        Color previousColor = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, hover ? 1f : Mathf.Clamp(Config.HudOpacity.Value, 0.2f, 1f));
        try
        {
            DrawPill(pill, label, labelWidth);
            if (GUI.Button(pill, GUIContent.none, GUIStyle.none)) s_panelOpen = !s_panelOpen;
            if (s_panelOpen) DrawPanel(panel);
        }
        finally
        {
            GUI.color = previousColor;
        }

        s_pillScreen = Scaled(pill, scale);
        s_panelScreen = s_panelOpen ? Scaled(panel, scale) : Rect.zero;
    }

    private static void DrawPill(Rect pill, GUIContent label, float labelWidth)
    {
        if (Event.current.type != EventType.Repaint) return;

        bool hover = pill.Contains(Event.current.mousePosition);
        Theme.DrawSliced(pill, hover ? Theme.Pill.hover.background : Theme.Pill.normal.background, 16);

        AutoCast.State state = AutoCast.CurrentState;
        float x = pill.x + 10f;
        Menu.DrawDot(new Rect(x, pill.center.y - 4f, 8f, 8f), Theme.StateColor(state));
        x += 8f + 8f;
        Theme.PillLabel.normal.textColor = S.Enabled.Value ? Theme.Text : Theme.Dim;
        GUI.Label(new Rect(x, pill.y, labelWidth, pill.height), label, Theme.PillLabel);
        x += labelWidth + 8f;

        for (int i = 0; i < AutoCast.Slots; i++)
        {
            var pip = new Rect(x + i * (PipSize + 3f), pill.center.y - PipSize / 2f, PipSize, PipSize);
            bool toggled = AutoCast.IsSlotToggled(i);
            bool live = toggled && state == AutoCast.State.Active;
            Theme.DrawSliced(pip, live ? Theme.PipOn : toggled ? Theme.PipPaused : Theme.PipOff, 5);
            Theme.Pip.normal.textColor = live ? Color.white : toggled ? Theme.Pause : Theme.Dim;
            GUI.Label(pip, (i + 1).ToString(), Theme.Pip);
        }
    }

    private static void DrawPanel(Rect panel)
    {
        GUILayout.BeginArea(panel, Theme.Panel);
        GUILayout.Label("QUICK TOGGLES", Theme.CardTitle);
        Switch(S.Enabled, "Auto-cast");
        Switch(S.RequireEnemies, "Enemy gate");
        Switch(S.AimAtClosestEnemy, "Aim at closest enemy");
        Switch(S.ShowRangeRing, "Show range ring");
        GUILayout.Space(6f);
        if (GUILayout.Button($"Open full menu  ·  {Config.MenuKey.Value}", Theme.SmallButton))
        {
            s_panelOpen = false;
            Menu.Open(Page.AutoCast);
        }
        GUILayout.EndArea();
    }

    private static Rect Scaled(Rect r, float scale) => new(r.x * scale, r.y * scale, r.width * scale, r.height * scale);
}
