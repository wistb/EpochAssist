using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

// An ordered list of entries, each one line: its number, its inline controls side by side, then up, down and remove;
// "+ Add" last. An entry that can move can also be dragged by its number. The rule editor builds on it.
internal static class ListRows
{
    public static void Register() => RowKinds.Register<ListRow>(new Renderer());

    private const float LineHeight = 28f, NumberWidth = 22f, ButtonSize = 22f, Gap = 6f;

    // The entry being dragged: its list's key, where it started and where it would land. One drag at a time.
    private static string? s_dragKey;
    private static int s_dragFrom, s_dragTo;

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
        // The label, the entries, then "+ Add" when the list has one.
        protected override float Height(ListRow row, float width, RowContext ctx) =>
            RowLayout.ControlHeight + 4f + LinesHeight(row) + (row.Add != null || row.AddPicked != null ? RowLayout.ControlHeight : 0f) + RowLayout.RowGap;

        protected override void Draw(ListRow row, Rect rect, RowContext ctx)
        {
            if (Event.current.type == EventType.Repaint) RowLayout.Label(new Rect(rect.x, rect.y, rect.width, RowLayout.ControlHeight), row.Label, ctx, null);
            var items = Items(row);
            float y = rect.y + RowLayout.ControlHeight + 4f;
            int? remove = null;
            (int From, int To)? move = null;
            Event e = Event.current;
            bool dragging = s_dragKey == row.Key && row.Move != null;
            var lines = new List<Rect>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                var details = DetailsOf(row, items[i]);
                var line = new Rect(rect.x, y, rect.width, RowLayout.ControlHeight);
                y += details != null ? 2 * LineHeight : LineHeight;
                lines.Add(new Rect(line.x, line.y - 2f, line.width, (details != null ? LineHeight : 0f) + line.height + 4f));
                var handle = new Rect(line.x, line.y, NumberWidth, line.height);
                // Holding the grip's control keeps the drag's events (the release too) coming here, off the window as well.
                int grip = row.Move != null ? GUIUtility.GetControlID(FocusType.Passive, handle) : 0;
                if (row.Move != null && ctx.Enabled && e.type == EventType.MouseDown && e.button == 0 && handle.Contains(e.mousePosition) && items.Count > 1)
                {
                    s_dragKey = row.Key;
                    s_dragFrom = s_dragTo = i;
                    dragging = true;
                    GUIUtility.hotControl = grip;
                    e.Use();
                }
                if (e.type == EventType.Repaint)
                {
                    bool held = dragging && i == s_dragFrom;
                    Theme.Fill(lines[i], Widgets.Themed ? held ? Color.Lerp(Theme.Values.Card, Theme.Accent, 0.25f) : Theme.Values.Card : Color.clear);
                    // The number turns into a grip under the pointer (and while held), where a drag can start.
                    if (row.Move != null && items.Count > 1 && (held || (!dragging && handle.Contains(e.mousePosition))))
                        Grip(handle, held ? Theme.Accent : Theme.Secondary);
                    else Widgets.Paint(handle, (i + 1).ToString(), Widgets.Style(() => Theme.TreeNumber, GUI.skin.label));
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
            if (dragging) Drag(row, lines, e);
            if (row.Add != null || row.AddPicked != null) DrawAdd(row, new Rect(rect.x, y, 0f, RowLayout.ControlHeight));
            // After drawing, so this frame's lines all drew the same list.
            if (move is { } m && row.Move != null) Safe.Call(() => { row.Move(m.From, m.To); return true; }, false, row.Key + ".move");
            if (remove is int r && row.Remove != null) Safe.Call(() => { row.Remove(r); return true; }, false, row.Key + ".remove");
        }

        // While an entry is dragged: the place under the pointer becomes its landing place, marked by a bar on that side
        // of the entry there; letting go moves it there one step at a time (Move takes neighbouring steps).
        private static void Drag(ListRow row, List<Rect> lines, Event e)
        {
            if (lines.Count == 0 || s_dragFrom >= lines.Count)
            {
                s_dragKey = null;
                return;
            }
            float pointer = e.mousePosition.y;
            int to = lines.Count - 1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (pointer < lines[i].yMax)
                {
                    to = i;
                    break;
                }
            }
            s_dragTo = to;
            if (e.type == EventType.MouseDrag) e.Use();
            if (e.type == EventType.Repaint && s_dragTo != s_dragFrom)
            {
                var target = lines[s_dragTo];
                float barY = s_dragTo > s_dragFrom ? target.yMax : target.y - 2f;
                Theme.Fill(new Rect(target.x, barY, target.width, 2f), Theme.Accent);
            }
            if (e.rawType != EventType.MouseUp) return;
            int from = s_dragFrom;
            s_dragKey = null;
            GUIUtility.hotControl = 0;
            e.Use();
            if (row.Move == null || to == from) return;
            int step = to > from ? 1 : -1;
            Safe.Call(() =>
            {
                for (int i = from; i != to; i += step) row.Move(i, i + step);
                return true;
            }, false, row.Key + ".drag");
        }

        // Two columns of three dots, centred in the number's place.
        private static void Grip(Rect r, Color colour)
        {
            const float dot = 3f, gap = 3f;
            float x0 = Mathf.Round(r.center.x - dot - gap / 2f), y0 = Mathf.Round(r.center.y - dot * 1.5f - gap);
            for (int column = 0; column < 2; column++)
                for (int line = 0; line < 3; line++)
                    Theme.Tinted(new Rect(x0 + column * (dot + gap), y0 + line * (dot + gap), dot, dot), Theme.Dot, colour);
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
