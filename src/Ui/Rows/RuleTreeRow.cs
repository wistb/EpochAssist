using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

// The rule tree and step list rows of the macro editor (contracts/menu.md): tree lines with a switch, a group's type
// dropdown or the line's words (a click or Enter opens its options under it), the live value with its state shape,
// and a remove button; "+ add" rows with a searchable list; options drawn indented under the open line; a text field.
internal static class RuleTreeRowKinds
{
    private const float LineHeight = 24f, Indent = 18f, RemoveSize = 20f, ValueWidth = 130f;

    public static void Register()
    {
        RowKinds.Register<TreeLine>(new LineRenderer());
        RowKinds.Register<TreeAdd>(new AddRenderer());
        RowKinds.Register<Indented>(new IndentedRenderer());
        RowKinds.Register<ValueTextField>(new TextFieldRenderer());
    }

    // The remove button: the theme's cross glyph on a small square, as list rows draw theirs.
    private static bool CrossButton(Rect r, string key, string tip)
    {
        bool clicked = GUI.Button(r, GUIContent.none, GUIStyle.none);
        if (Event.current.type == EventType.Repaint && Widgets.Themed)
        {
            bool hover = GUI.enabled && r.Contains(Event.current.mousePosition);
            Theme.DrawSliced(r, hover ? Theme.ButtonHover : Theme.ButtonBg, Theme.ControlRadius);
            var glyph = Theme.Cross;
            Theme.Tinted(new Rect(Mathf.Round(r.center.x - glyph.width / 2f), Mathf.Round(r.center.y - glyph.height / 2f), glyph.width, glyph.height), glyph,
                GUI.enabled ? Theme.Secondary : Theme.Faint);
        }
        Popups.Tip(r, tip, key);
        return Focus.Item(key, r) || clicked;
    }

    // The vertical tree lines of the ancestors and the elbow into this line.
    private static void TreeLines(Rect r, int depth, bool last, bool[] ancestors)
    {
        if (!Widgets.Themed || Event.current.type != EventType.Repaint || depth == 0) return;
        Color line = new(Theme.Faint.r, Theme.Faint.g, Theme.Faint.b, 0.8f);
        for (int i = 0; i < depth - 1; i++)
        {
            if (i < ancestors.Length && ancestors[i]) continue;
            Theme.Fill(new Rect(r.x + i * Indent + 6f, r.y, 1f, r.height), line);
        }
        float lx = r.x + (depth - 1) * Indent + 6f;
        Theme.Fill(new Rect(lx, r.y, 1f, last ? r.height / 2f : r.height), line);
        Theme.Fill(new Rect(lx, r.y + LineHeight / 2f, Indent - 8f, 1f), line);
    }

    private sealed class LineRenderer : RowRenderer<TreeLine>
    {
        protected override float Height(TreeLine row, float width, RowContext ctx) => LineHeight + 2f;

        protected override void Draw(TreeLine row, Rect rect, RowContext ctx)
        {
            var r = new Rect(rect.x, rect.y, rect.width, LineHeight);
            TreeLines(rect, row.Depth, row.Last, row.Ancestors);
            float x = r.x + row.Depth * Indent;
            bool blocked = row.Blocked != null && Safe.Call(row.Blocked, null, row.Key + ".blocked") != null;
            bool enabled = GUI.enabled;
            GUI.enabled = enabled && !blocked;

            if (row.On != null && row.SetOn != null)
            {
                bool on = Safe.Call(row.On, false, row.Key + ".on");
                if (Controls.Switch(new Rect(x, r.y, Controls.SwitchWidth, r.height), on, row.Key + ".on", "On") != on) Safe.Run(() => row.SetOn(!on), row.Key + ".seton");
                x += Controls.SwitchWidth + 6f;
            }
            if (row.Number != null)
            {
                GUIStyle dim = Widgets.Style(() => Theme.FieldHint, GUI.skin.label);
                if (Event.current.type == EventType.Repaint) Widgets.Paint(new Rect(x, r.y, 22f, r.height), row.Number, dim);
                x += 22f;
            }

            float right = r.xMax;
            if (row.Remove != null)
            {
                right -= RemoveSize;
                if (CrossButton(new Rect(right, r.y + 2f, RemoveSize, RemoveSize), row.Key + ".remove", "Remove (undo brings it back)"))
                    Safe.Run(row.Remove, row.Key + ".remove");
                right -= 6f;
            }
            var live = row.Live != null ? Safe.Call(row.Live, null, row.Key + ".live") : null;
            if (row.Type != null && row.SetType != null)
            {
                int type = Safe.Call(row.Type, 0, row.Key + ".type");
                Dropdown.Draw(new Rect(x, r.y, 96f, r.height), row.Key + ".type", TreeLine.TypeLabels, type, i => Safe.Run(() => row.SetType(i), row.Key + ".settype"));
                x += 104f;
                if (live is var (gv, gs)) Controls.StatePill(new Rect(x, r.y, right - x, r.height), gv, gs);
            }
            else
            {
                GUIStyle label = Widgets.Style(() => Theme.Label, GUI.skin.label);
                string words = Safe.Call(row.Words, string.Empty, row.Key + ".words");
                // The value sits right after the words; the words give it the room they don't need.
                float pillWidth = live is var (pv, _) ? Controls.StatePillWidth(pv) + 8f : 0f;
                float wordsWidth = Mathf.Min(TextCache.Width(words, label) + 16f, Mathf.Max(0f, right - x - pillWidth));
                if (live is var (lv, ls)) Controls.StatePill(new Rect(x + wordsWidth + 4f, r.y, pillWidth, r.height), lv, ls);
                right = x + wordsWidth;
                var area = new Rect(x, r.y, Mathf.Max(0f, right - x), r.height);
                bool clicked = row.Toggle != null && GUI.Button(area, GUIContent.none, GUIStyle.none);
                bool activated = row.Toggle != null && Focus.Item(row.Key, area);
                if ((clicked || activated) && row.Toggle != null) Safe.Run(row.Toggle, row.Key + ".toggle");
                if (Event.current.type == EventType.Repaint)
                {
                    if (Widgets.Themed && (row.Opened || (row.Toggle != null && area.Contains(Event.current.mousePosition))))
                        Theme.DrawSliced(area, row.Opened ? Theme.SelectionBg : Theme.HoverBg, Theme.CardRadius);
                    Widgets.Paint(new Rect(area.x + 4f, area.y, area.width - 4f, area.height), Controls.Fit(words, label, area.width - 6f), label);
                }
                Popups.Tip(area, row.Tip != null ? Safe.Call(row.Tip, null, row.Key + ".tip") : null);
            }
            GUI.enabled = enabled;
        }

        protected override IEnumerable<string> Search(TreeLine row) => new[] { Safe.Call(row.Words, string.Empty, row.Key + ".words") };

        protected override RowText Bridge(TreeLine row, RowContext ctx)
        {
            string on = row.On != null ? (Safe.Call(row.On, false, row.Key + ".on") ? "on" : "off") : "none";
            string words = row.Type != null ? TreeLine.TypeLabels[Math.Clamp(Safe.Call(row.Type, 0, row.Key + ".type"), 0, 2)] : Safe.Call(row.Words, string.Empty, row.Key + ".words");
            var live = row.Live != null ? Safe.Call(row.Live, null, row.Key + ".live") : null;
            string kind = row.Number != null ? "step" : "line";
            string value = live is var (v, s) ? $"{v} {s.ToString().ToLowerInvariant()}" : "-";
            return new RowText(kind, $"{row.Key} {(row.Number ?? string.Empty)}{words}".Trim(), value, ctx.Enabled) { Note = $"switch={on}{(row.Opened ? " open" : string.Empty)}" };
        }
    }

    private sealed class AddRenderer : RowRenderer<TreeAdd>
    {
        protected override float Height(TreeAdd row, float width, RowContext ctx) => LineHeight + 4f;

        protected override void Draw(TreeAdd row, Rect rect, RowContext ctx)
        {
            var r = new Rect(rect.x, rect.y, rect.width, LineHeight);
            TreeLines(rect, row.Depth, true, Array.Empty<bool>());
            float x = r.x + row.Depth * Indent;
            string? blocked = row.Blocked != null ? Safe.Call(row.Blocked, null, row.Key + ".blocked") : null;
            float w = Controls.ButtonWidth(row.Label);
            bool enabled = GUI.enabled;
            GUI.enabled = enabled && blocked == null;
            var button = new Rect(x, r.y, w, r.height);
            string key = row.Key;
            if (Controls.Button(button, row.Label, key, tip: blocked))
            {
                var choices = Safe.Call(row.Choices, Array.Empty<string>(), key + ".choices");
                var groups = row.Groups != null ? Safe.Call(row.Groups, null, key + ".groups") : null;
                if (Popups.Current is DropdownPopup open && open.Key == key) Popups.Close();
                else Popups.Open(new DropdownPopup(key, Popups.ToWindow(button), choices, -1, i => Safe.Run(() => row.Picked(i), key + ".picked"), groups, null));
            }
            GUI.enabled = enabled;
            if (blocked != null) Popups.Tip(button, blocked);
            if (row.SecondLabel != null && row.Second != null)
            {
                string? second = row.SecondBlocked != null ? Safe.Call(row.SecondBlocked, null, key + ".second.blocked") : null;
                var r2 = new Rect(button.xMax + 8f, r.y, Controls.ButtonWidth(row.SecondLabel), r.height);
                GUI.enabled = enabled && second == null;
                if (Controls.Button(r2, row.SecondLabel, key + ".second", tip: second)) Safe.Run(row.Second, key + ".second");
                GUI.enabled = enabled;
                if (second != null) Popups.Tip(r2, second);
            }
        }

        protected override RowText Bridge(TreeAdd row, RowContext ctx)
        {
            string? blocked = row.Blocked != null ? Safe.Call(row.Blocked, null, row.Key + ".blocked") : null;
            string? second = row.SecondBlocked != null ? Safe.Call(row.SecondBlocked, null, row.Key + ".second.blocked") : null;
            return new RowText("add", $"{row.Key} {row.Label}", blocked ?? "ok", ctx.Enabled && blocked == null)
            {
                Note = row.SecondLabel != null ? $"second={RowText.Quote(row.SecondLabel)}:{(second ?? "ok").Replace(' ', '_')}" : null,
            };
        }
    }

    private sealed class IndentedRenderer : RowRenderer<Indented>
    {
        private static Rect Shift(Rect r, int depth) => new(r.x + depth * Indent + 8f, r.y, Mathf.Max(0f, r.width - depth * Indent - 8f), r.height);

        protected override float Height(Indented row, float width, RowContext ctx) =>
            RowKinds.For(row.Inner) is { } inner ? inner.Height(row.Inner, Mathf.Max(0f, width - row.Depth * Indent - 8f), ctx) : 0f;

        protected override void Draw(Indented row, Rect rect, RowContext ctx)
        {
            if (RowKinds.For(row.Inner) is not { } inner) return;
            if (Widgets.Themed && Event.current.type == EventType.Repaint)
                Theme.Fill(new Rect(rect.x + row.Depth * Indent + 2f, rect.y, 2f, rect.height), new Color(Theme.Faint.r, Theme.Faint.g, Theme.Faint.b, 0.6f));
            inner.Draw(row.Inner, Shift(rect, row.Depth), ctx);
        }

        protected override IEnumerable<string> Search(Indented row) => RowKinds.For(row.Inner)?.Search(row.Inner) ?? Array.Empty<string>();

        protected override RowText Bridge(Indented row, RowContext ctx) =>
            RowKinds.For(row.Inner) is { } inner ? inner.Bridge(row.Inner, ctx) : new RowText("indented", row.Key);

        public override bool Shows(MenuRow row, RowContext ctx) => Safe.Visible(row);
    }

    private sealed class TextFieldRenderer : RowRenderer<ValueTextField>
    {
        protected override float Height(ValueTextField row, float width, RowContext ctx) => RowLayout.Height(width, 220f, null);

        protected override void Draw(ValueTextField row, Rect rect, RowContext ctx)
        {
            var parts = RowLayout.Lay(rect, Mathf.Min(260f, rect.width * 0.6f), null);
            RowLayout.Label(parts.Label, row.Label, ctx, null);
            string text = Safe.Call(row.Get, string.Empty, row.Key + ".get");
            string control = "field." + row.Key;
            GUI.SetNextControlName(control);
            string typed = GUI.TextField(parts.Control, text, row.MaxLength, Widgets.Style(() => Theme.TreeFilter, GUI.skin.textField));
            if (typed != text) Safe.Run(() => row.Set(typed), row.Key + ".set");
            if (Focus.Item(row.Key, parts.Control)) GUI.FocusControl(control);
        }

        protected override IEnumerable<string> Search(ValueTextField row) => new[] { row.Label };

        protected override RowText Bridge(ValueTextField row, RowContext ctx) =>
            new("text", row.Label, Safe.Call(row.Get, string.Empty, row.Key + ".get"), ctx.Enabled);
    }
}
