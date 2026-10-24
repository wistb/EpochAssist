using UnityEngine;
using static EpochAssist.Ui.Widgets;

namespace EpochAssist.Ui;

/// <summary>
/// A dropdown selector for the menu: a button showing the choice with a chevron; a click opens the options as a list
/// drawn over the page, and a click on one picks it (anywhere else closes it). Left and Right step through the options
/// from the keyboard, Enter opens the list. One list is open at a time. A page using dropdowns calls BeginEvent first
/// and DrawList last, in the same layout space as its dropdowns, so the list takes its clicks first and draws on top.
/// </summary>
internal static class Dropdown
{
    private const float ItemHeight = 28f;
    private const float ListPadding = 4f;

    private static string? s_open;
    // The open dropdown's button, in the page's layout space.
    private static Rect s_anchor;
    private static IReadOnlyList<string> s_options = Array.Empty<string>();
    private static int s_selected;
    private static int s_seenFrame;
    private static (string Key, int Index)? s_picked;
    private static GUIStyle? s_style, s_styleBase, s_item, s_itemBase;

    /// <summary>Draws a dropdown with GUILayout; returns the selected index, changed in the frame an option is picked.</summary>
    public static int Draw(string key, IReadOnlyList<string> options, int selected, params GUILayoutOption[] layout)
    {
        if (s_picked is { } picked && picked.Key == key)
        {
            s_picked = null;
            selected = picked.Index;
        }
        GUIStyle style = ButtonStyle();
        string label = selected >= 0 && selected < options.Count ? options[selected] : string.Empty;
        bool clicked = GUILayout.Button(label, style, layout);
        Rect r = GUILayoutUtility.GetLastRect();
        bool open = s_open == key;
        if (Event.current.type == EventType.Repaint)
        {
            if (Themed)
            {
                Color previous = GUI.color;
                GUI.color = new Color(Theme.Text.r, Theme.Text.g, Theme.Text.b, previous.a);
                // Pointing up while the list is open.
                Rect chevron = new(r.xMax - 22f, r.center.y - 6f, 12f, 12f);
                if (open) GUI.DrawTextureWithTexCoords(chevron, Theme.Chevron, new Rect(0f, 1f, 1f, -1f));
                else GUI.DrawTexture(chevron, Theme.Chevron);
                GUI.color = previous;
            }
            if (open)
            {
                s_anchor = r;
                s_options = options;
                s_seenFrame = Time.frameCount;
            }
        }
        if (Focus.Item("dropdown." + key, r, out int adjust) || clicked)
        {
            if (open)
            {
                s_open = null;
            }
            else
            {
                s_open = key;
                s_anchor = r;
                s_options = options;
                s_selected = selected;
                s_seenFrame = Time.frameCount;
            }
        }
        if (adjust != 0 && options.Count > 0)
        {
            s_open = null;
            selected = Math.Clamp(selected + adjust, 0, options.Count - 1);
        }
        return selected;
    }

    public static void Close() => s_open = null;

    /// <summary>Room the open list needs below the page's content, so it can be scrolled into view; 0 when closed.</summary>
    public static float OpenHeight => s_open != null ? ListRect().height + 4f : 0f;

    /// <summary>
    /// At the start of the page's drawing: while a list is open, a click picks an option or closes the list, and a click
    /// on the list or its button is used up so the controls under it don't get it.
    /// </summary>
    public static void BeginEvent()
    {
        if (s_open == null || Event.current.type != EventType.MouseDown) return;
        Vector2 mouse = Event.current.mousePosition;
        Rect list = ListRect();
        bool onList = list.Contains(mouse), onButton = s_anchor.Contains(mouse);
        if (onList)
        {
            int index = Mathf.FloorToInt((mouse.y - list.y - ListPadding) / ItemHeight);
            if (index >= 0 && index < s_options.Count) s_picked = (s_open, index);
        }
        s_open = null;
        if (onList || onButton) Event.current.Use();
    }

    /// <summary>Last in the page: the open list over what the page drew.</summary>
    public static void DrawList()
    {
        if (s_open == null) return;
        // Its dropdown wasn't drawn lately (another page or tab): the list goes with it.
        if (Time.frameCount - s_seenFrame > 2)
        {
            s_open = null;
            return;
        }
        if (Event.current.type != EventType.Repaint) return;
        Rect list = ListRect();
        if (Themed) Theme.DrawSliced(list, Theme.Panel.normal.background, Theme.PanelRadius);
        else GUI.Box(list, GUIContent.none);
        GUIStyle style = ItemStyle();
        Color previous = GUI.contentColor;
        for (int i = 0; i < s_options.Count; i++)
        {
            var item = new Rect(list.x + ListPadding, list.y + ListPadding + i * ItemHeight, list.width - ListPadding * 2f, ItemHeight);
            bool hover = item.Contains(Event.current.mousePosition);
            if (hover && Themed) Theme.DrawSliced(item, Theme.Segment.hover.background, Theme.PanelRadius / 2);
            GUI.contentColor = i == s_selected && Themed ? Theme.AccentText : previous;
            GUI.Label(item, s_options[i], style);
        }
        GUI.contentColor = previous;
    }

    // Below the button, as wide as it.
    private static Rect ListRect() =>
        new(s_anchor.x, s_anchor.yMax + 2f, s_anchor.width, s_options.Count * ItemHeight + ListPadding * 2f);

    // The segment button, its text on the left with room for the chevron.
    private static GUIStyle ButtonStyle()
    {
        GUIStyle segment = Style(() => Theme.Segment, GUI.skin.button);
        if (s_style == null || !ReferenceEquals(s_styleBase, segment))
        {
            s_styleBase = segment;
            s_style = new GUIStyle(segment) { alignment = TextAnchor.MiddleLeft, padding = new RectOffset(12, 28, segment.padding.top, segment.padding.bottom) };
        }
        return s_style;
    }

    // The list's options and the chevron: row text, no background.
    private static GUIStyle ItemStyle()
    {
        GUIStyle row = Style(() => Theme.RowLabel, GUI.skin.label);
        if (s_item == null || !ReferenceEquals(s_itemBase, row))
        {
            s_itemBase = row;
            s_item = new GUIStyle(row) { alignment = TextAnchor.MiddleLeft, padding = new RectOffset(8, 8, 0, 0), margin = new RectOffset(0, 0, 0, 0) };
        }
        return s_item;
    }
}
