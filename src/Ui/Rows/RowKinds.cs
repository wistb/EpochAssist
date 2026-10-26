using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

/// <summary>Where and how a row is drawn: its page, whether it takes input, and inline in a list entry.</summary>
internal sealed class RowContext
{
    /// <summary>The page's path, or the search result's, for focus keys.</summary>
    public string Path = string.Empty;
    /// <summary>False draws the row dimmed and takes no input (the entry's content is off, or the row's EnabledWhen).</summary>
    public bool Enabled = true;
    /// <summary>The feature runs, so live rows show.</summary>
    public bool Live = true;
    /// <summary>Control only: no label column or hint (inside a list entry).</summary>
    public bool Inline;
    /// <summary>Words to mark in the label (search results).</summary>
    public string[]? Highlight;
    /// <summary>Put before the focus keys of rows not bound to a setting (a list entry's controls).</summary>
    public string Prefix = string.Empty;

    public RowContext With(bool? enabled = null, bool? inline = null, string? prefix = null) => new()
    {
        Path = Path, Enabled = enabled ?? Enabled, Live = Live, Inline = inline ?? Inline, Highlight = Highlight, Prefix = prefix ?? Prefix,
    };
}

/// <summary>A row as the bridge prints it: its kind, label, value, state, hint and warning lines, then any child lines.</summary>
internal sealed record RowText(string Kind, string? Label, string? Value = null, bool? Enabled = null, bool? IsDefault = null, string? Hint = null)
{
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    /// <summary>Lines printed under the row (a list's entries, a block's own lines).</summary>
    public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();
    /// <summary>Extra text after the value (visible=no (shows when …)).</summary>
    public string? Note { get; init; }

    public static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ") + "\"";

    /// <summary>One line: kind "label" value=… enabled=… default=… hint="…".</summary>
    public string Line()
    {
        var parts = new List<string> { Kind };
        if (Label != null) parts.Add(Quote(Label));
        if (Value != null) parts.Add("value=" + (Value.Length == 0 || Value.Contains(' ') || Value.Contains('"') ? Quote(Value) : Value));
        if (Enabled is bool enabled) parts.Add("enabled=" + (enabled ? "yes" : "no"));
        if (IsDefault is bool isDefault) parts.Add("default=" + (isDefault ? "yes" : "no"));
        if (!string.IsNullOrEmpty(Hint)) parts.Add("hint=" + Quote(StripTags(Hint!)));
        if (Note != null) parts.Add(Note);
        return string.Join(" ", parts);
    }

    public static string StripTags(string text) => System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", string.Empty);
}

/// <summary>
/// Draws one kind of row: its height, its controls (each reported to Focus), the texts search sees and the text the
/// bridge prints. One renderer per row type, registered in <see cref="RowKinds"/>, so search and the bridge can't fall
/// behind the drawing.
/// </summary>
internal abstract class RowRenderer
{
    public abstract float Height(MenuRow row, float width, RowContext ctx);
    public abstract void Draw(MenuRow row, Rect rect, RowContext ctx);
    public virtual IEnumerable<string> Search(MenuRow row) => Array.Empty<string>();
    public abstract RowText Bridge(MenuRow row, RowContext ctx);
    /// <summary>The control's width inline (in a list entry).</summary>
    public virtual float InlineWidth(MenuRow row) => 120f;
    /// <summary>Whether the row shows: its own rule, and live rows only while the feature runs.</summary>
    public virtual bool Shows(MenuRow row, RowContext ctx) => Safe.Visible(row);
}

internal abstract class RowRenderer<T> : RowRenderer where T : MenuRow
{
    public sealed override float Height(MenuRow row, float width, RowContext ctx) => Height((T)row, width, ctx);
    public sealed override void Draw(MenuRow row, Rect rect, RowContext ctx) => Draw((T)row, rect, ctx);
    public sealed override IEnumerable<string> Search(MenuRow row) => Search((T)row);
    public sealed override RowText Bridge(MenuRow row, RowContext ctx) => Bridge((T)row, ctx);
    public sealed override float InlineWidth(MenuRow row) => InlineWidth((T)row);

    protected abstract float Height(T row, float width, RowContext ctx);
    protected abstract void Draw(T row, Rect rect, RowContext ctx);
    protected virtual IEnumerable<string> Search(T row) => Array.Empty<string>();
    protected abstract RowText Bridge(T row, RowContext ctx);
    protected virtual float InlineWidth(T row) => 120f;
}

/// <summary>Row type to renderer. A later layer adds a kind with one record type and one Register call.</summary>
internal static class RowKinds
{
    private static readonly Dictionary<Type, RowRenderer> s_renderers = new();

    public static void Register<TRow>(RowRenderer renderer) where TRow : MenuRow => s_renderers[typeof(TRow)] = renderer;

    /// <summary>The row's renderer, or null when its type has none (a declaration problem).</summary>
    public static RowRenderer? For(MenuRow row) => s_renderers.TryGetValue(row.GetType(), out var renderer) ? renderer : null;

    /// <summary>The menu's own kinds; called once when the menu starts.</summary>
    public static void RegisterBuiltIn()
    {
        if (s_renderers.Count > 0) return;
        SettingRows.Register();
        TextRows.Register();
        ChipRows.Register();
        CheckTreeRows.Register();
        RuleTreeRowKinds.Register();
        CardRows.Register();
        ListRows.Register();
    }
}

/// <summary>Calls into declarations, which must not take the menu down: a throw is logged once per row and counts as empty.</summary>
internal static class Safe
{
    public static T Call<T>(Func<T> f, T fallback, string what)
    {
        try
        {
            return f();
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("menu:" + what, $"Menu row {what} failed: {e.Message}");
            return fallback;
        }
    }

    public static void Run(Action f, string what)
    {
        try
        {
            f();
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("menu:" + what, $"Menu action {what} failed: {e.Message}");
        }
    }

    public static bool Visible(MenuRow row) => Call(() => row.Visible, false, row.Key + ".visible");
    public static bool Enabled(MenuRow row) => Call(() => row.Enabled, false, row.Key + ".enabled");
}

/// <summary>
/// The common row layout: a label column (160 px, 120 px below 640 px of page width), the control, the ↺ slot and the
/// hint beside it, which wraps and goes under the control when less than 140 px is left. A control is 24 px tall, rows
/// are 6 px apart.
/// </summary>
internal static class RowLayout
{
    public const float ControlHeight = 24f, RowGap = 6f, Gap = 8f, ResetSlot = 16f, MinHint = 140f;

    /// <summary>The label column the page set for its rows (wide enough for its longest label), or 0 for the default.</summary>
    public static float PageLabel;

    public static float LabelWidth(float pageWidth) => PageLabel > 0f ? PageLabel : pageWidth < 640f ? 120f : 160f;

    /// <summary>
    /// A page's label column: the default, widened to fit its longest label so none is cut, up to 45% of the page.
    /// </summary>
    public static float ColumnFor(IEnumerable<MenuRow> rows, float pageWidth)
    {
        float column = pageWidth < 640f ? 120f : 160f;
        GUIStyle style = Widgets.Style(() => Theme.Label, GUI.skin.label);
        foreach (var row in rows)
        {
            string? label = row switch
            {
                SettingRow s => s.Setting.Label,
                ValueChoice c => c.Label,
                ValueNumber n => n.Label,
                ValueToggle t => t.Label,
                LiveText l => l.Label,
                _ => null,
            };
            if (label != null) column = Mathf.Max(column, Mathf.Ceil(TextCache.Width(label, style)) + 12f + (row is LiveText ? 14f : 0f));
        }
        return Mathf.Min(column, Mathf.Floor(pageWidth * 0.45f));
    }

    public readonly record struct Parts(Rect Label, Rect Control, Rect Reset, Rect Hint);

    /// <summary>Where a row's parts go for a control width; the hint's rect is empty when there is none.</summary>
    public static Parts Lay(Rect row, float controlWidth, string? hint)
    {
        float label = LabelWidth(row.width);
        var control = new Rect(row.x + label + Gap, row.y, Mathf.Min(controlWidth, row.width - label - Gap), ControlHeight);
        var reset = new Rect(control.xMax + 2f, row.y + (ControlHeight - ResetSlot) / 2f, ResetSlot, ResetSlot);
        Rect hintRect = default;
        if (!string.IsNullOrEmpty(hint))
        {
            float x = reset.xMax + Gap, width = row.xMax - x;
            if (width >= MinHint)
            {
                float height = Controls.HintHeight(hint!, width);
                hintRect = new Rect(x, row.y + Mathf.Max(0f, (ControlHeight - height) / 2f), width, height);
            }
            else
            {
                width = row.xMax - control.x;
                hintRect = new Rect(control.x, control.yMax + 2f, width, Controls.HintHeight(hint!, width));
            }
        }
        return new Parts(new Rect(row.x, row.y, label, ControlHeight), control, reset, hintRect);
    }

    /// <summary>A row's height for a control width and hint, with the gap after it.</summary>
    public static float Height(float width, float controlWidth, string? hint)
    {
        var parts = Lay(new Rect(0f, 0f, width, 0f), controlWidth, hint);
        return Mathf.Max(ControlHeight, parts.Hint.yMax) + RowGap;
    }

    /// <summary>The label, cut to its column with the full text as a tooltip, and the search's words marked.</summary>
    public static void Label(Rect r, string label, RowContext ctx, string? tip)
    {
        GUIStyle style = Widgets.Style(() => Theme.Label, GUI.skin.label);
        string cut = Controls.Fit(label, style, r.width - 4f);
        if (Event.current.type == EventType.Repaint) Widgets.Paint(r, ctx.Highlight != null ? MenuSearchText.Mark(cut, ctx.Highlight) : cut, style);
        Popups.Tip(r, cut != label ? label + (tip != null ? "\n" + tip : string.Empty) : tip);
    }

    public static void Hint(Rect r, string? hint)
    {
        if (string.IsNullOrEmpty(hint) || Event.current.type != EventType.Repaint) return;
        Widgets.Paint(r, hint!, Widgets.Style(() => Theme.Hint, GUI.skin.label));
    }

    /// <summary>Draws a row dimmed and inert while it is disabled; call End after.</summary>
    public static (bool Enabled, Color Color) Begin(bool enabled)
    {
        var state = (GUI.enabled, GUI.color);
        if (!enabled)
        {
            GUI.enabled = false;
            GUI.color = new Color(GUI.color.r, GUI.color.g, GUI.color.b, GUI.color.a * 0.45f);
        }
        return state;
    }

    public static void End((bool Enabled, Color Color) state)
    {
        GUI.enabled = state.Enabled;
        GUI.color = state.Color;
    }
}
