using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

// An ordered list of entries, each one line: its number, its inline controls side by side, then up, down and remove;
// "+ Add" last. The rule editor builds on it.
internal static class ListRows
{
    public static void Register() => RowKinds.Register<ListRow>(new Renderer());

    private const float LineHeight = 28f, NumberWidth = 22f, ButtonSize = 22f, Gap = 6f;

    private static IReadOnlyList<object> Items(ListRow row) => Safe.Call(row.Items, Array.Empty<object>(), row.Key);
    private static IReadOnlyList<MenuRow> RowsOf(ListRow row, object item) => Safe.Call(() => row.RowsOf(item), Array.Empty<MenuRow>(), row.Key + ".rows");

    private sealed class Renderer : RowRenderer<ListRow>
    {
        protected override float Height(ListRow row, float width, RowContext ctx) =>
            RowLayout.ControlHeight + 4f + Items(row).Count * LineHeight + RowLayout.ControlHeight + RowLayout.RowGap;

        protected override void Draw(ListRow row, Rect rect, RowContext ctx)
        {
            if (Event.current.type == EventType.Repaint) RowLayout.Label(new Rect(rect.x, rect.y, rect.width, RowLayout.ControlHeight), row.Label, ctx, null);
            var items = Items(row);
            float y = rect.y + RowLayout.ControlHeight + 4f;
            int? remove = null;
            (int From, int To)? move = null;
            for (int i = 0; i < items.Count; i++)
            {
                var line = new Rect(rect.x, y, rect.width, RowLayout.ControlHeight);
                y += LineHeight;
                if (Event.current.type == EventType.Repaint)
                {
                    Theme.Fill(new Rect(line.x, line.y - 2f, line.width, line.height + 4f), Widgets.Themed ? Theme.Values.Card : Color.clear);
                    Widgets.Paint(new Rect(line.x, line.y, NumberWidth, line.height), (i + 1).ToString(), Widgets.Style(() => Theme.TreeNumber, GUI.skin.label));
                }
                float x = line.x + NumberWidth + 4f;
                string prefix = $"{row.Key}.{i + 1}.";
                var entryCtx = ctx.With(inline: true, prefix: prefix);
                float buttons = (row.Move != null ? 2 * (ButtonSize + 2f) : 0f) + (row.Remove != null ? ButtonSize + 2f : 0f);
                foreach (var control in RowsOf(row, items[i]))
                {
                    if (RowKinds.For(control) is not { } renderer || !renderer.Shows(control, entryCtx)) continue;
                    float width = Mathf.Min(renderer.InlineWidth(control), line.xMax - buttons - x - Gap);
                    if (width < 24f) break;
                    var state = RowLayout.Begin(entryCtx.Enabled && Safe.Enabled(control));
                    renderer.Draw(control, new Rect(x, line.y, width, line.height), entryCtx);
                    RowLayout.End(state);
                    x += width + Gap;
                }
                float bx = line.xMax - buttons;
                if (row.Move != null)
                {
                    if (Glyph(new Rect(bx, line.y + 1f, ButtonSize, ButtonSize), Theme.StepUp, prefix + "up", "Move up", i > 0)) move = (i, i - 1);
                    bx += ButtonSize + 2f;
                    if (Glyph(new Rect(bx, line.y + 1f, ButtonSize, ButtonSize), Theme.StepDown, prefix + "down", "Move down", i < items.Count - 1)) move = (i, i + 1);
                    bx += ButtonSize + 2f;
                }
                if (row.Remove != null && Glyph(new Rect(bx, line.y + 1f, ButtonSize, ButtonSize), Theme.Cross, prefix + "remove", "Remove", true)) remove = i;
            }
            if (row.Add != null)
            {
                string add = "+ " + row.AddLabel;
                if (Controls.Button(new Rect(rect.x, y, Controls.ButtonWidth(add), RowLayout.ControlHeight), add, row.Key + ".add")) Safe.Call(() => { row.Add(); return true; }, false, row.Key + ".add");
            }
            // After drawing, so this frame's lines all drew the same list.
            if (move is { } m && row.Move != null) Safe.Call(() => { row.Move(m.From, m.To); return true; }, false, row.Key + ".move");
            if (remove is int r && row.Remove != null) Safe.Call(() => { row.Remove(r); return true; }, false, row.Key + ".remove");
        }

        // A small square button with a glyph; greyed when it can't act.
        private static bool Glyph(Rect r, Texture2D glyph, string key, string tip, bool can)
        {
            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && can;
            bool clicked = GUI.Button(r, GUIContent.none, GUIStyle.none);
            if (Event.current.type == EventType.Repaint && Widgets.Themed)
            {
                bool hover = GUI.enabled && r.Contains(Event.current.mousePosition);
                Theme.DrawSliced(r, hover ? Theme.ButtonHover : Theme.ButtonBg, Theme.ControlRadius);
                Theme.Tinted(new Rect(Mathf.Round(r.center.x - glyph.width / 2f), Mathf.Round(r.center.y - glyph.height / 2f), glyph.width, glyph.height), glyph,
                    GUI.enabled ? Theme.Secondary : Theme.Faint);
            }
            Popups.Tip(r, tip, key);
            bool pressed = Focus.Item(key, r) || clicked;
            GUI.enabled = wasEnabled;
            return pressed && can;
        }

        protected override IEnumerable<string> Search(ListRow row)
        {
            yield return row.Label;
            foreach (var item in Items(row))
            {
                foreach (var control in RowsOf(row, item))
                {
                    if (RowKinds.For(control) is { } renderer)
                    {
                        foreach (string text in renderer.Search(control)) yield return text;
                        yield return renderer.Bridge(control, new RowContext { Inline = true }).Value ?? string.Empty;
                    }
                }
            }
        }

        protected override RowText Bridge(ListRow row, RowContext ctx)
        {
            var items = Items(row);
            var lines = new List<string>();
            for (int i = 0; i < items.Count; i++)
            {
                var parts = RowsOf(row, items[i]).Select(c => RowKinds.For(c)?.Bridge(c, ctx.With(inline: true)))
                    .Where(t => t != null).Select(t => $"{RowText.Quote(t!.Label ?? t.Kind)}={(t.Value ?? string.Empty)}");
                lines.Add($"entry {i + 1} {string.Join(" ", parts)}");
            }
            return new RowText("list", row.Label, items.Count.ToString(), ctx.Enabled && Safe.Enabled(row)) { Lines = lines };
        }
    }
}
