using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

/// <summary>
/// A dropdown: a field showing the choice with a chevron. A click or Enter opens its options as a popup; Left and Right
/// step through them from the keyboard. A pick reaches the caller through its pick function, so it works the same in
/// fixed rects and in GUILayout blocks.
/// </summary>
internal static class Dropdown
{
    /// <summary>A dropdown in a fixed rect. Swatches, when given, are drawn before each option's label.</summary>
    public static void Draw(Rect r, string key, IReadOnlyList<string> options, int selected, Action<int> pick,
        (string Label, int First)[]? groups = null, string? shown = null, IReadOnlyList<Color?>? swatches = null, string? tip = null)
    {
        bool clicked = GUI.Button(r, GUIContent.none, GUIStyle.none);
        bool open = Popups.Current is DropdownPopup popup && popup.Key == key;
        string label = shown ?? (selected >= 0 && selected < options.Count ? options[selected] : string.Empty);
        if (Event.current.type == EventType.Repaint)
        {
            Controls.FieldFrame(r, open);
            float left = 0f;
            if (swatches != null && selected >= 0 && selected < swatches.Count && swatches[selected] is Color swatch)
            {
                Theme.Tinted(new Rect(r.x + 7f, Mathf.Round(r.center.y - 6f), 12f, 12f), Theme.Swatch, swatch);
                left = 16f;
            }
            GUIStyle text = Widgets.Style(() => Theme.FieldText, GUI.skin.label);
            var area = new Rect(r.x + left, r.y, r.width - left - 16f, r.height);
            Widgets.Paint(area, Controls.Fit(label, text, area.width - text.padding.horizontal), text);
            if (Widgets.Themed)
            {
                Rect chevron = new(r.xMax - 18f, Mathf.Round(r.center.y - 6f), 12f, 12f);
                if (open) GUI.DrawTextureWithTexCoords(chevron, Theme.Chevron, new Rect(0f, 1f, 1f, -1f));
                else Theme.Tinted(chevron, Theme.Chevron, Theme.Dim);
            }
        }
        Popups.Tip(r, tip, "dropdown." + key);
        if (Focus.Item("dropdown." + key, r, out int adjust) || clicked)
        {
            if (open) Popups.Close();
            else Popups.Open(new DropdownPopup(key, Popups.ToWindow(r), options, selected, pick, groups, swatches));
        }
        if (adjust != 0 && options.Count > 0)
        {
            int next = Math.Clamp((selected < 0 ? 0 : selected) + adjust, 0, options.Count - 1);
            if (next != selected) pick(next);
        }
    }

    /// <summary>The same in a GUILayout block, taking the given height and the rest of the width.</summary>
    public static void Layout(string key, IReadOnlyList<string> options, int selected, Action<int> pick, float width = -1f)
    {
        Rect r = width > 0f
            ? GUILayoutUtility.GetRect(width, Controls.Height, GUILayout.Width(width), GUILayout.Height(Controls.Height))
            : GUILayoutUtility.GetRect(60f, Controls.Height, GUILayout.ExpandWidth(true), GUILayout.Height(Controls.Height));
        Draw(r, key, options, selected, pick);
    }

    /// <summary>As wide as its longest option needs, within 120 to 220.</summary>
    public static float WidthFor(IReadOnlyList<string> options)
    {
        GUIStyle text = Widgets.Style(() => Theme.FieldText, GUI.skin.label);
        float widest = 0f;
        foreach (string option in options) widest = Mathf.Max(widest, TextCache.Width(option, text));
        return Mathf.Clamp(Mathf.Ceil(widest + text.padding.horizontal + 22f), 120f, 220f);
    }

    public static void Close()
    {
        if (Popups.Current is DropdownPopup) Popups.Close();
    }
}

/// <summary>
/// A dropdown's options over the page: 22 px each, the current one in the accent colour with a check, declared groups as
/// faint headings, a filter at the top when there are more than 8 (typing narrows by word prefix), and a scrolling list
/// past 12.
/// </summary>
internal sealed class DropdownPopup : IPopup
{
    private const float ItemHeight = 22f, HeadingHeight = 18f, FilterHeight = 22f, Pad = 4f;
    private const int FilterFrom = 8, VisibleRows = 12;

    private readonly IReadOnlyList<string> _options;
    private readonly int _selected;
    private readonly Action<int> _pick;
    private readonly (string Label, int First)[]? _groups;
    private readonly IReadOnlyList<Color?>? _swatches;
    private readonly Rect _anchor;
    private string _filter = string.Empty;
    // What is listed: an option's index, or a heading (-1 - group).
    private readonly List<int> _rows = new();
    private int _highlight;
    private float _scroll;
    private Rect _rect;

    public DropdownPopup(string key, Rect anchor, IReadOnlyList<string> options, int selected, Action<int> pick,
        (string Label, int First)[]? groups, IReadOnlyList<Color?>? swatches)
    {
        Key = key;
        _anchor = anchor;
        _options = options;
        _selected = selected;
        _pick = pick;
        _groups = groups;
        _swatches = swatches;
        _highlight = selected;
        Refilter();
        ScrollTo(_highlight);
    }

    public string Key { get; }
    public bool Filterable => _options.Count > FilterFrom;
    public Rect Rect => _rect;

    private float ListTop => Pad + (Filterable ? FilterHeight + 3f : 0f);

    private void Refilter()
    {
        _rows.Clear();
        string[] words = _filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int group = -1;
        for (int i = 0; i < _options.Count; i++)
        {
            if (words.Length > 0 && !MenuSearchText.Matches(_options[i], words)) continue;
            if (words.Length == 0 && _groups != null)
            {
                int g = Array.FindLastIndex(_groups, x => x.First <= i);
                if (g != group && g >= 0)
                {
                    group = g;
                    _rows.Add(-1 - g);
                }
            }
            _rows.Add(i);
        }
        if (!_rows.Contains(_highlight)) _highlight = _rows.FirstOrDefault(x => x >= 0, -1);
        _scroll = 0f;
        Measure();
    }

    private void Measure()
    {
        GUIStyle text = Widgets.Style(() => Theme.OptionText, GUI.skin.label);
        float widest = 0f;
        foreach (string option in _options) widest = Mathf.Max(widest, TextCache.Width(option, text));
        float width = Mathf.Clamp(Mathf.Ceil(widest + text.padding.horizontal + (_swatches != null ? 18f : 0f)) + Pad * 2f, _anchor.width, 260f);
        float list = Mathf.Min(ContentHeight, VisibleRows * ItemHeight);
        _rect = Popups.Place(new Vector2(width, ListTop + Mathf.Max(list, ItemHeight) + Pad), _anchor);
    }

    private float ContentHeight => _rows.Sum(x => x >= 0 ? ItemHeight : HeadingHeight);

    private float RowY(int index)
    {
        float y = 0f;
        for (int i = 0; i < index; i++) y += _rows[i] >= 0 ? ItemHeight : HeadingHeight;
        return y;
    }

    private float ViewHeight => _rect.height - ListTop - Pad;

    private void ScrollTo(int option)
    {
        int index = _rows.IndexOf(option);
        if (index < 0) return;
        float y = RowY(index);
        if (y < _scroll) _scroll = y;
        else if (y + ItemHeight > _scroll + ViewHeight) _scroll = y + ItemHeight - ViewHeight;
        _scroll = Mathf.Clamp(_scroll, 0f, Mathf.Max(0f, ContentHeight - ViewHeight));
    }

    public void HandleEvent(Event e)
    {
        if (e.type == EventType.ScrollWheel)
        {
            _scroll = Mathf.Clamp(_scroll + e.delta.y * ItemHeight / 3f * 2f, 0f, Mathf.Max(0f, ContentHeight - ViewHeight));
            return;
        }
        if (e.type != EventType.MouseDown || e.button != 0) return;
        int option = OptionAt(e.mousePosition);
        if (option >= 0) Pick(option);
    }

    private int OptionAt(Vector2 point)
    {
        float y = point.y - _rect.y - ListTop + _scroll;
        if (point.y < _rect.y + ListTop || point.y > _rect.yMax - Pad) return -1;
        float top = 0f;
        foreach (int row in _rows)
        {
            float height = row >= 0 ? ItemHeight : HeadingHeight;
            if (y >= top && y < top + height) return row;
            top += height;
        }
        return -1;
    }

    private void Pick(int option)
    {
        Popups.Close();
        if (option != _selected) _pick(option);
    }

    public bool HandleKey(Event e)
    {
        if (!Filterable) return false;
        if (e.keyCode == KeyCode.Backspace)
        {
            if (_filter.Length > 0) _filter = _filter.Substring(0, _filter.Length - 1);
            Refilter();
            return true;
        }
        char c = e.character;
        if (c == '\0' || char.IsControl(c)) return false;
        _filter += c;
        Refilter();
        return true;
    }

    public bool HandleNav(MenuNav nav)
    {
        if (nav.Tab) return true;
        var options = _rows.Where(x => x >= 0).ToList();
        if (nav.Move != 0 && options.Count > 0)
        {
            int at = options.IndexOf(_highlight);
            at = at < 0 ? (nav.Move > 0 ? 0 : options.Count - 1) : Math.Clamp(at + nav.Move, 0, options.Count - 1);
            _highlight = options[at];
            ScrollTo(_highlight);
        }
        if (nav.Activate && _highlight >= 0) Pick(_highlight);
        return false;
    }

    public void Closed(bool commit) { }

    public void Draw()
    {
        if (Event.current.type != EventType.Repaint) return;
        Rect r = _rect;
        if (Widgets.Themed) Theme.DrawSliced(r, Theme.PopupBg, Theme.CardRadius);
        else Widgets.Paint(r, GUIContent.none, GUI.skin.box);
        if (Filterable)
        {
            var field = new Rect(r.x + Pad, r.y + Pad, r.width - Pad * 2f, FilterHeight);
            if (Widgets.Themed) Theme.DrawSliced(field, Theme.InputFocus, Theme.ControlRadius);
            bool empty = _filter.Length == 0;
            // The placeholder in the secondary text colour: readable, still plainly not typed text.
            GUIStyle style = Widgets.Style(() => empty ? Theme.FieldHint : Theme.FieldText, GUI.skin.label);
            // A caret after what is typed, blinking.
            string caret = Time.unscaledTime % 1f < 0.5f ? "|" : " ";
            Widgets.Paint(field, empty ? "Type to filter" : _filter + caret, style);
        }

        var view = new Rect(r.x + Pad, r.y + ListTop, r.width - Pad * 2f, ViewHeight);
        GUI.BeginGroup(view);
        try
        {
            Vector2 mouse = Event.current.mousePosition;
            GUIStyle text = Widgets.Style(() => Theme.OptionText, GUI.skin.label);
            float y = -_scroll;
            foreach (int row in _rows)
            {
                float height = row >= 0 ? ItemHeight : HeadingHeight;
                if (y + height >= 0f && y <= view.height)
                {
                    var item = new Rect(0f, y, view.width, height);
                    if (row < 0) Widgets.Paint(item, _groups![-1 - row].Label.ToUpperInvariant(), Widgets.Style(() => Theme.OptionHeading, GUI.skin.label));
                    else DrawOption(item, row, item.Contains(mouse), text);
                }
                y += height;
            }
            if (_rows.Count == 0) Widgets.Paint(new Rect(0f, 0f, view.width, ItemHeight), "No match", Widgets.Style(() => Theme.FieldDim, GUI.skin.label));
        }
        finally
        {
            GUI.EndGroup();
        }
        // A thin bar while the list scrolls.
        float content = ContentHeight;
        if (content > view.height && Widgets.Themed)
        {
            float bar = Mathf.Max(16f, view.height * view.height / content);
            float at = (view.height - bar) * (_scroll / (content - view.height));
            Theme.Fill(new Rect(r.xMax - 3f, view.y + at, 2f, bar), Theme.Values.ControlActive);
        }
    }

    private void DrawOption(Rect item, int option, bool hover, GUIStyle text)
    {
        bool lit = hover || option == _highlight;
        if (lit && Widgets.Themed) Theme.DrawSliced(item, Theme.HighlightBg, Theme.ControlRadius);
        Color previous = GUI.contentColor;
        if (Widgets.Themed) GUI.contentColor = lit ? Theme.Values.Text : option == _selected ? Theme.AccentText : previous;
        var label = item;
        if (_swatches != null)
        {
            if (option < _swatches.Count && _swatches[option] is Color swatch) Theme.Tinted(new Rect(item.x + 6f, Mathf.Round(item.center.y - 6f), 12f, 12f), Theme.Swatch, swatch);
            label = new Rect(item.x + 18f, item.y, item.width - 18f, item.height);
        }
        Widgets.Paint(label, _options[option], text);
        if (option == _selected && Widgets.Themed) Theme.Tinted(new Rect(item.xMax - 18f, Mathf.Round(item.center.y - 6f), 12f, 12f), Theme.TickMark, GUI.contentColor);
        GUI.contentColor = previous;
    }
}
