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
    private static IReadOnlyList<MenuRow>? DetailsOf(ListRow row, object item) =>
        row.Details == null ? null : Safe.Call(() => row.Details(item), null, row.Key + ".details");

    // Each entry's line, plus its detail line while unfolded.
    private static float LinesHeight(ListRow row)
    {
        float height = 0f;
        foreach (var item in Items(row)) height += DetailsOf(row, item) != null ? 2 * LineHeight : LineHeight;
        return height;
    }

    // Controls side by side from x, as wide as each needs, up to limit.
    private static void DrawControls(IReadOnlyList<MenuRow> controls, float x, float y, float limit, RowContext ctx)
    {
        foreach (var control in controls)
        {
            if (RowKinds.For(control) is not { } renderer || !renderer.Shows(control, ctx)) continue;
            float width = Mathf.Min(renderer.InlineWidth(control), limit - x);
            if (width < 24f) break;
            var state = RowLayout.Begin(ctx.Enabled && Safe.Enabled(control));
            renderer.Draw(control, new Rect(x, y, width, RowLayout.ControlHeight), ctx);
            RowLayout.End(state);
            x += width + Gap;
        }
    }

    private static string BridgeParts(IEnumerable<MenuRow> controls, RowContext ctx) => string.Join(" ", controls
        .Select(c => RowKinds.For(c)?.Bridge(c, ctx.With(inline: true))).Where(t => t != null)
        .Select(t => $"{RowText.Quote(t!.Label ?? t.Kind)}={(t.Value ?? string.Empty)}"));

    private sealed class Renderer : RowRenderer<ListRow>
    {
        protected override float Height(ListRow row, float width, RowContext ctx) =>
            RowLayout.ControlHeight + 4f + LinesHeight(row) + RowLayout.ControlHeight + RowLayout.RowGap;

        protected override void Draw(ListRow row, Rect rect, RowContext ctx)
        {
            if (Event.current.type == EventType.Repaint) RowLayout.Label(new Rect(rect.x, rect.y, rect.width, RowLayout.ControlHeight), row.Label, ctx, null);
            var items = Items(row);
            float y = rect.y + RowLayout.ControlHeight + 4f;
            int? remove = null;
            (int From, int To)? move = null;
            for (int i = 0; i < items.Count; i++)
            {
                var details = DetailsOf(row, items[i]);
                var line = new Rect(rect.x, y, rect.width, RowLayout.ControlHeight);
                y += details != null ? 2 * LineHeight : LineHeight;
                if (Event.current.type == EventType.Repaint)
                {
                    float cardHeight = (details != null ? LineHeight : 0f) + line.height + 4f;
                    Theme.Fill(new Rect(line.x, line.y - 2f, line.width, cardHeight), Widgets.Themed ? Theme.Values.Card : Color.clear);
                    Widgets.Paint(new Rect(line.x, line.y, NumberWidth, line.height), (i + 1).ToString(), Widgets.Style(() => Theme.TreeNumber, GUI.skin.label));
                }
                float x = line.x + NumberWidth + 4f;
                string prefix = $"{row.Key}.{i + 1}.";
                var entryCtx = ctx.With(inline: true, prefix: prefix);
                float buttons = (row.Move != null ? 2 * (ButtonSize + 2f) : 0f) + (row.Remove != null ? ButtonSize + 2f : 0f);
                DrawControls(RowsOf(row, items[i]), x, line.y, line.xMax - buttons - Gap, entryCtx);
                if (details != null) DrawControls(details, x + 16f, line.y + LineHeight, line.xMax - Gap, entryCtx.With(prefix: prefix + "details."));
                float bx = line.xMax - buttons;
                if (row.Move != null)
                {
                    if (Glyph(new Rect(bx, line.y + 1f, ButtonSize, ButtonSize), Theme.StepUp, prefix + "up", "Move up", i > 0)) move = (i, i - 1);
                    bx += ButtonSize + 2f;
                    if (Glyph(new Rect(bx, line.y + 1f, ButtonSize, ButtonSize), Theme.StepDown, prefix + "down", "Move down", i < items.Count - 1)) move = (i, i + 1);
                    bx += ButtonSize + 2f;
                }
                if (row.Remove != null)
                {
                    int index = i;
                    string? blocked = row.RemoveBlocked == null ? null : Safe.Call(() => row.RemoveBlocked(index), null, row.Key + ".removeblocked");
                    var glyph = new Rect(bx, line.y + 1f, ButtonSize, ButtonSize);
                    if (Glyph(glyph, Theme.Cross, prefix + "remove", blocked ?? "Remove", blocked == null))
                    {
                        // One that asks first opens its question beside the button.
                        if (row.RemovePrompt != null && Safe.Call(() => row.RemovePrompt(index), null, row.Key + ".removeprompt") is { } prompt)
                            Popups.Open(new PromptPopup(Popups.ToWindow(glyph), prompt));
                        else remove = i;
                    }
                }
            }
            if (row.Add != null || row.AddPicked != null) DrawAdd(row, new Rect(rect.x, y, 0f, RowLayout.ControlHeight));
            // After drawing, so this frame's lines all drew the same list.
            if (move is { } m && row.Move != null) Safe.Call(() => { row.Move(m.From, m.To); return true; }, false, row.Key + ".move");
            if (remove is int r && row.Remove != null) Safe.Call(() => { row.Remove(r); return true; }, false, row.Key + ".remove");
        }

        // "+ Add": adds at once, or opens a dropdown of choices; greyed with the reason when nothing can be added.
        private static void DrawAdd(ListRow row, Rect at)
        {
            string add = "+ " + row.AddLabel;
            string key = row.Key + ".add";
            string? blocked = row.AddBlocked == null ? null : Safe.Call(row.AddBlocked, null, row.Key + ".addblocked");
            var r = new Rect(at.x, at.y, Controls.ButtonWidth(add), at.height);
            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && blocked == null;
            bool pressed = Controls.Button(r, add, key, tip: blocked) && GUI.enabled;
            GUI.enabled = wasEnabled;
            if (!pressed) return;
            if (row.AddChoices != null && row.AddPicked != null)
            {
                var choices = Safe.Call(row.AddChoices, Array.Empty<string>(), row.Key + ".addchoices");
                var groups = row.AddGroups == null ? null : Safe.Call(row.AddGroups, null, row.Key + ".addgroups");
                if (Popups.Current is DropdownPopup open && open.Key == key) Popups.Close();
                else Popups.Open(new DropdownPopup(key, Popups.ToWindow(r), choices, -1, i => Safe.Call(() => { row.AddPicked(i); return true; }, false, key), groups, null));
            }
            else if (row.Add != null)
            {
                Safe.Call(() => { row.Add(); return true; }, false, key);
            }
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
                string line = $"entry {i + 1} {BridgeParts(RowsOf(row, items[i]), ctx)}";
                if (DetailsOf(row, items[i]) is { } details) line += $" | details {BridgeParts(details, ctx)}";
                if (row.RemoveBlocked?.Invoke(i) is { } blocked) line += $" | remove blocked {RowText.Quote(blocked)}";
                lines.Add(line);
            }
            if (row.AddBlocked?.Invoke() is { } addBlocked) lines.Add($"add blocked {RowText.Quote(addBlocked)}");
            return new RowText("list", row.Label, items.Count.ToString(), ctx.Enabled && Safe.Enabled(row)) { Lines = lines };
        }
    }
}
