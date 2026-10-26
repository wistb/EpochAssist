using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

/// <summary>A list, picker or field drawn over the menu by <see cref="Popups"/>. Rects are in the window's space.</summary>
internal interface IPopup
{
    Rect Rect { get; }
    /// <summary>A mouse or scroll event over the popup; the layer uses it up afterwards.</summary>
    void HandleEvent(Event e);
    /// <summary>A key event while the popup is open (typing into its filter); true when it took the key.</summary>
    bool HandleKey(Event e);
    void Draw();
    /// <summary>Navigation keys while it is open; true lets them go on to the menu (after closing it).</summary>
    bool HandleNav(MenuNav nav);
    /// <summary>It closed: by a pick, a click outside (commit true), or Esc (commit false).</summary>
    void Closed(bool commit);
}

/// <summary>
/// The menu's popup layer, inside the window: one popup at a time. At the start of the window function the open popup
/// takes mouse, scroll and key events first and uses them, and a press outside it closes it (used too, so nothing under
/// it reacts). It draws last, over everything, followed by the tooltip. Popups are placed below their anchor, or above
/// it when there is no room, and kept inside the window.
/// </summary>
internal static class Popups
{
    private const float FadeSeconds = 0.08f;
    private const float TipDelay = 0.5f;

    private static IPopup? s_open;
    private static float s_openedAt;
    // Where the local space of the scroll views and areas being drawn starts, in the window's space.
    private static readonly Stack<Vector2> s_origins = new();
    private static Vector2 s_origin;

    // The tooltip: what the pointer rests on (or what has the focus ring), and since when.
    private static string? s_tipText, s_tipSeen;
    private static Rect s_tipRect;
    private static float s_tipSince;
    private static bool s_tipNow;

    /// <summary>The window's inner rect in its own space; set by the menu each frame.</summary>
    public static Rect Bounds;

    public static bool IsOpen => s_open != null;
    public static IPopup? Current => s_open;

    public static void Open(IPopup popup)
    {
        Close(commit: true);
        s_open = popup;
        s_openedAt = Time.unscaledTime;
    }

    public static void Close(bool commit = true)
    {
        var popup = s_open;
        s_open = null;
        popup?.Closed(commit);
    }

    public static void Reset()
    {
        s_open = null;
        s_tipText = s_tipSeen = null;
    }

    /// <summary>Navigation keys go to the open popup; true when they should reach the menu too.</summary>
    public static bool HandleNav(MenuNav nav)
    {
        if (s_open == null) return true;
        bool pass = s_open.HandleNav(nav);
        if (pass) Close(commit: true);
        return pass;
    }

    /// <summary>A rect drawn here (inside scroll views and areas) in the window's space.</summary>
    public static Rect ToWindow(Rect r) => new(r.x + s_origin.x, r.y + s_origin.y, r.width, r.height);

    /// <summary>
    /// Entering a scroll view or area whose local (0, 0) is at offset in the current space (an area's position, a scroll
    /// view's position less its scroll). Pair with PopOrigin.
    /// </summary>
    public static void PushOrigin(Vector2 offset)
    {
        s_origins.Push(s_origin);
        s_origin += offset;
    }

    public static void PopOrigin() => s_origin = s_origins.Count > 0 ? s_origins.Pop() : Vector2.zero;

    /// <summary>The space a popup takes below its anchor (or above, when there is no room), inside the window.</summary>
    public static Rect Place(Vector2 size, Rect anchor)
    {
        Rect bounds = Bounds;
        float w = Mathf.Min(size.x, bounds.width - 8f), h = Mathf.Min(size.y, bounds.height - 8f);
        float x = Mathf.Clamp(anchor.x, bounds.x + 4f, bounds.xMax - 4f - w);
        float y = anchor.yMax + 2f;
        if (y + h > bounds.yMax - 4f) y = anchor.y - 2f - h;
        y = Mathf.Clamp(y, bounds.y + 4f, bounds.yMax - 4f - h);
        return new Rect(x, y, w, h);
    }

    /// <summary>At the start of the window function: the popup takes its events first.</summary>
    public static void BeginWindowEvent()
    {
        s_origins.Clear();
        s_origin = Vector2.zero;
        Event e = Event.current;
        if (e.type == EventType.Repaint)
        {
            s_tipText = null;
            s_tipNow = false;
        }
        if (s_open == null) return;
        switch (e.type)
        {
            case EventType.MouseDown:
            case EventType.MouseUp:
            case EventType.MouseDrag:
            case EventType.ScrollWheel:
                // Nothing under an open popup reacts, and the page doesn't scroll away from it.
                if (s_open.Rect.Contains(e.mousePosition)) s_open.HandleEvent(e);
                else if (e.type == EventType.MouseDown) Close(commit: true);
                if (e.type != EventType.Used) e.Use();
                break;
            case EventType.KeyDown:
                if (s_open.HandleKey(e)) e.Use();
                break;
        }
    }

    /// <summary>Last in the window function: the popup over everything, then the tooltip.</summary>
    public static void DrawLast()
    {
        if (s_open != null)
        {
            Color previous = GUI.color;
            float t = LookSettings.Animations.Value ? Mathf.Clamp01((Time.unscaledTime - s_openedAt) * Motion.Speed / FadeSeconds) : 1f;
            GUI.color = new Color(previous.r, previous.g, previous.b, previous.a * t);
            s_open.Draw();
            GUI.color = previous;
        }
        DrawTip();
    }

    /// <summary>
    /// Offers a tooltip for a control drawn here: shown after the pointer rests on it, or at once while it has the focus
    /// ring. Call in the repaint pass.
    /// </summary>
    public static void Tip(Rect r, string? text, string? focusKey = null)
    {
        if (string.IsNullOrEmpty(text) || Event.current.type != EventType.Repaint || s_open != null) return;
        bool ringed = focusKey != null && Focus.Ringed(focusKey);
        if (!ringed && !r.Contains(Event.current.mousePosition)) return;
        if (s_tipNow && !ringed) return;
        s_tipText = text;
        s_tipRect = ToWindow(r);
        s_tipNow = ringed;
    }

    private static void DrawTip()
    {
        if (Event.current.type != EventType.Repaint) return;
        if (s_tipText != s_tipSeen)
        {
            s_tipSeen = s_tipText;
            s_tipSince = Time.unscaledTime;
        }
        if (s_tipText == null || !Widgets.Themed || (!s_tipNow && Time.unscaledTime - s_tipSince < TipDelay)) return;
        GUIStyle style = Theme.Tooltip;
        var content = new GUIContent(s_tipText);
        float width = Mathf.Min(300f, style.CalcSize(content).x + 1f);
        float height = style.CalcHeight(content, width);
        Rect r = Place(new Vector2(width, height), s_tipRect);
        Theme.DrawSliced(r, Theme.PopupBg, Theme.ControlRadius + 1);
        Widgets.Paint(r, content, style);
    }
}

/// <summary>The menu's animation speed: Animation speed while Animations is on.</summary>
internal static class Motion
{
    public static float Speed => Mathf.Clamp(LookSettings.AnimationSpeed.Value, 0.25f, 4f);
    public static bool On => LookSettings.Animations.Value;

    /// <summary>0 to 1 over the given seconds since a moment, scaled by the speed; 1 at once with animations off.</summary>
    public static float Progress(float since, float seconds) => On ? Mathf.Clamp01((Time.unscaledTime - since) * Speed / seconds) : 1f;
}
