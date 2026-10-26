using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

/// <summary>
/// Draws the toast queue where the Notifications setting puts it: next to the mini HUD (stacked away from the pill and
/// its open panel, oldest nearest), or at one of the screen's corners or edges (oldest on top). Toasts follow the HUD:
/// none while it is switched off, and faded by its opacity. Only labels and textures, never a control, so a toast can't
/// take focus or a click from the game, the HUD or the menu.
/// </summary>
internal sealed class ToastFeature : Feature
{
    private const float Gap = 6f;
    private const float MaxWidth = 460f;

    public override string Id => "toasts";
    public override string Name => "Toasts";
    // Reads settings, and places notifications by the HUD the HUD drew this frame.
    public override string[] RunsAfter => new[] { "profiles", "hud" };
    public override GuiLayer Layer => GuiLayer.Hud;

    public override void OnGUI()
    {
        if (Event.current.type != EventType.Repaint || !HudSettings.HudEnabled.Value) return;
        IReadOnlyList<Toast> toasts = Toasts.Visible;
        // Previewing the HUD settings shows where notifications go, with a sample when none are up.
        if (toasts.Count == 0 && Ui.Menu.PreviewingHud) toasts = new[] { new Toast("preview", "Notifications show here.", ToastKind.Info, Time.unscaledTime) };
        if (toasts.Count == 0) return;
        GUISkin? skin = Theme.Skin;
        if (skin == null) return;

        // Measured and drawn with the themed skin, whose font the styles fall back to.
        GUISkin previousSkin = GUI.skin;
        Matrix4x4 previousMatrix = GUI.matrix;
        Color previousColor = GUI.color;
        GUI.skin = skin;
        float scale = Mathf.Clamp(LookSettings.UiScale.Value, 0.5f, 3f);
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
        try
        {
            Draw(toasts, scale);
        }
        finally
        {
            GUI.color = previousColor;
            GUI.matrix = previousMatrix;
            GUI.skin = previousSkin;
        }
    }

    private static void Draw(IReadOnlyList<Toast> toasts, float scale)
    {
        float now = Time.unscaledTime, screenW = Screen.width / scale, screenH = Screen.height / scale;
        GUIStyle style = Theme.ToastText;
        style.normal.textColor = Theme.Text;
        var items = toasts.Select(t =>
        {
            var content = new GUIContent(t.Text);
            float width = Mathf.Min(Mathf.Ceil(style.CalcSize(content).x) + 1f, MaxWidth);
            return (Toast: t, Content: content, Size: new Vector2(width, style.CalcHeight(content, width)));
        }).ToList();
        var block = new Vector2(items.Max(i => i.Size.x), items.Sum(i => i.Size.y) + Gap * (items.Count - 1));

        ScreenSpot? own = HudSettings.ToastSpot;
        ScreenSpot spot = own ?? HudSettings.HudSpot;
        Rect area;
        if (own != null)
        {
            area = spot.Place(block, screenW, screenH, Hud.Margin);
        }
        else
        {
            // Beside the pill and its open panel where shown; otherwise where the pill would be.
            Rect hud = Hud.ScreenRect;
            Rect anchor = hud.width > 0f
                ? new Rect(hud.x / scale, hud.y / scale, hud.width / scale, hud.height / scale)
                : Hud.Offset(spot.Place(Vector2.zero, screenW, screenH, Hud.Margin), screenW, screenH);
            float x = spot.Horizontal < 0 ? anchor.xMin : spot.Horizontal > 0 ? anchor.xMax - block.x : anchor.center.x - block.x / 2f;
            float y = spot.Vertical > 0 ? anchor.yMin - Gap - block.y : anchor.yMax + Gap;
            area = new Rect(x, y, block.x, block.y);
            // Above the pill, the oldest stays nearest it.
            if (spot.Vertical > 0) items.Reverse();
        }

        float top = area.y;
        foreach (var (toast, content, size) in items)
        {
            float left = spot.Horizontal < 0 ? area.x : spot.Horizontal > 0 ? area.xMax - size.x : area.center.x - size.x / 2f;
            var rect = new Rect(Mathf.Round(left), top, size.x, size.y);
            GUI.color = new Color(1f, 1f, 1f, Toasts.Alpha(toast, now) * Mathf.Clamp(HudSettings.HudOpacity.Value, 0.2f, 1f));
            Theme.DrawSliced(rect, Theme.Panel.normal.background, Theme.PanelRadius);
            Ui.Menu.DrawDot(new Rect(rect.x + 12f, rect.center.y - 4f, 8f, 8f), toast.Kind == ToastKind.Warning ? Theme.Pause : Theme.AccentText);
            Widgets.Paint(rect, content, style);
            top += size.y + Gap;
        }
    }

    public override void Shutdown() => Toasts.Clear();
}
