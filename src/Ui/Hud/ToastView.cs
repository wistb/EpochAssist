using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

/// <summary>
/// Draws the toast queue where the Notifications settings put it: next to the mini HUD (stacked away from the pill and
/// its open panel, oldest nearest), or at one of the screen's corners or edges (oldest on top); Order puts the newest
/// there instead. Duration, the stack size and the kinds shown are handed to Toasts while this runs. Toasts follow the
/// HUD: none while it is switched off, and faded by its opacity. Only labels and textures, never a control, so a toast can't
/// take focus or a click from the game, the HUD or the menu; the one exception is a sticky toast's ×, which takes a click
/// on itself only (the HUD's hit test keeps it from the game). Sticky toasts show with the HUD switched off too.
/// </summary>
internal sealed class ToastFeature : Feature
{
    private const float Gap = 6f;
    private const float MaxWidth = 460f;
    // A sticky toast's × at its right end.
    private const float DismissWidth = 24f;

    // Each shown sticky toast and its × in screen pixels, from the last repaint.
    private static readonly List<(Toast Toast, Rect Screen)> s_dismiss = new();

    public override string Id => "toasts";
    public override string Name => "Toasts";
    // Reads settings, and places notifications by the HUD the HUD drew this frame.
    public override string[] RunsAfter => new[] { "profiles", "hud" };
    public override GuiLayer Layer => GuiLayer.Hud;

    public override void Start()
    {
        Toasts.ShowSecondsSetting = () => HudSettings.ToastDuration.Value;
        Toasts.MaxShownSetting = () => Mathf.RoundToInt(HudSettings.ToastStack.Value);
        Toasts.ShowsKind = kind => HudSettings.ToastKinds.Value == 0 || kind == ToastKind.Warning;
    }

    public override void OnGUI()
    {
        if (Event.current.type != EventType.Repaint)
        {
            DismissClick();
            return;
        }
        Toasts.ShownArea = Rect.zero;
        s_dismiss.Clear();
        Toasts.DismissAreas = Array.Empty<Rect>();
        IReadOnlyList<Toast> toasts = HudSettings.HudEnabled.Value ? Toasts.Visible : Toasts.Visible.Where(t => t.Sticky).ToList();
        // Previewing the HUD settings shows where notifications go and how many stack, with samples when none are up.
        if (toasts.Count == 0 && Ui.Menu.PreviewingHud)
        {
            int count = Toasts.MaxShown;
            toasts = Enumerable.Range(1, count).Select(i => new Toast("preview", count == 1 ? "Notifications show here." : $"Notification {i} of {count}", ToastKind.Info, Time.unscaledTime)).ToList();
        }
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
        Toasts.DismissAreas = s_dismiss.Select(d => d.Screen).ToList();
    }

    // A press on a sticky toast's × (where the last repaint drew it) takes the toast down and goes no further.
    private static void DismissClick()
    {
        Event e = Event.current;
        if (e.type != EventType.MouseDown || e.button != 0) return;
        foreach (var (toast, screen) in s_dismiss)
        {
            if (!screen.Contains(e.mousePosition)) continue;
            Toasts.Dismiss(toast);
            e.Use();
            return;
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
            float room = t.Sticky ? DismissWidth : 0f;
            float width = Mathf.Min(Mathf.Ceil(style.CalcSize(content).x) + 1f, MaxWidth - room);
            return (Toast: t, Content: content, Size: new Vector2(width + room, style.CalcHeight(content, width)));
        }).ToList();
        if (HudSettings.ToastOrder.Value == 1) items.Reverse();
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

        Toasts.ShownArea = new Rect(area.x * scale, area.y * scale, area.width * scale, area.height * scale);
        float top = area.y;
        foreach (var (toast, content, size) in items)
        {
            float left = spot.Horizontal < 0 ? area.x : spot.Horizontal > 0 ? area.xMax - size.x : area.center.x - size.x / 2f;
            var rect = new Rect(Mathf.Round(left), top, size.x, size.y);
            GUI.color = new Color(1f, 1f, 1f, Toasts.Alpha(toast, now) * Mathf.Clamp(HudSettings.HudOpacity.Value, 0.2f, 1f));
            Theme.DrawSliced(rect, Theme.Panel.normal.background, Theme.PanelRadius);
            Ui.Menu.DrawDot(new Rect(rect.x + 12f, rect.center.y - 4f, 8f, 8f), toast.Kind == ToastKind.Warning ? Theme.Pause : Theme.AccentText);
            if (toast.Sticky)
            {
                var cross = new Rect(rect.xMax - DismissWidth, rect.y, DismissWidth, rect.height);
                var screen = new Rect(cross.x * scale, cross.y * scale, cross.width * scale, cross.height * scale);
                Color tint = screen.Contains(Event.current.mousePosition) ? Theme.Text : Theme.Secondary;
                Theme.Tinted(new Rect(cross.center.x - 6f - 2f, cross.center.y - 6f, 12f, 12f), Theme.CloseCross, tint);
                s_dismiss.Add((toast, screen));
                Widgets.Paint(new Rect(rect.x, rect.y, rect.width - DismissWidth, rect.height), content, style);
            }
            else
            {
                Widgets.Paint(rect, content, style);
            }
            top += size.y + Gap;
        }
    }

    public override void Shutdown()
    {
        s_dismiss.Clear();
        Toasts.Clear();
        Toasts.ShowSecondsSetting = null;
        Toasts.MaxShownSetting = null;
        Toasts.ShowsKind = null;
    }
}
