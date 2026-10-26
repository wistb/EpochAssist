using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

// Cards: a framed panel with a title, a one-line description and its rows laid out inside, with a label column fitted
// to the card. Columns put cards side by side at the tallest one's height, or one under another on a narrow page.
internal static class CardRows
{
    private const float Pad = 14f, TitleHeight = 20f, Gap = 12f, MinColumnWidth = 300f;

    public static void Register()
    {
        RowKinds.Register<CardRow>(new CardRenderer());
        RowKinds.Register<CardColumns>(new ColumnsRenderer());
    }

    private static GUIStyle TitleStyle => Widgets.Style(() => Theme.CardTitle, GUI.skin.label);
    private static GUIStyle DescriptionStyle => Widgets.Style(() => Theme.Hint, GUI.skin.label);

    private static float DescriptionHeight(CardRow card, float inner) =>
        card.Description == null ? 0f : Controls.HintHeight(card.Description, inner) + 6f;

    // The rows that show in a card, each with its renderer and height for the card's inner width.
    private static List<(MenuRow Row, RowRenderer Renderer, float Height)> Laid(CardRow card, float inner, RowContext ctx)
    {
        var laid = new List<(MenuRow, RowRenderer, float)>();
        float previous = RowLayout.PageLabel;
        RowLayout.PageLabel = RowLayout.ColumnFor(card.Rows, inner);
        try
        {
            foreach (var row in card.Rows)
            {
                if (RowKinds.For(row) is not { } renderer || !renderer.Shows(row, ctx)) continue;
                var rowCtx = Safe.Enabled(row) ? ctx : ctx.With(enabled: false);
                laid.Add((row, renderer, Safe.Call(() => renderer.Height(row, inner, rowCtx), RowLayout.ControlHeight, row.Key + ".height")));
            }
        }
        finally
        {
            RowLayout.PageLabel = previous;
        }
        return laid;
    }

    public static float CardHeight(CardRow card, float width, RowContext ctx)
    {
        float inner = width - Pad * 2f;
        return Pad + TitleHeight + DescriptionHeight(card, inner) + Laid(card, inner, ctx).Sum(l => l.Height) + Pad - RowLayout.RowGap;
    }

    // The panel, its title and description, then its rows with the card's own label column.
    public static void DrawCard(CardRow card, Rect r, RowContext ctx)
    {
        float inner = r.width - Pad * 2f;
        if (Event.current.type == EventType.Repaint)
        {
            if (Widgets.Themed) Theme.Card.Draw(r, GUIContent.none, false, false, false, false);
            Widgets.Paint(new Rect(r.x + Pad, r.y + Pad - 2f, inner, TitleHeight), card.Title.ToUpperInvariant(), TitleStyle);
            if (card.Description != null)
                Widgets.Paint(new Rect(r.x + Pad, r.y + Pad + TitleHeight - 2f, inner, DescriptionHeight(card, inner)), card.Description, DescriptionStyle);
        }
        float y = r.y + Pad + TitleHeight + DescriptionHeight(card, inner);
        float previous = RowLayout.PageLabel;
        var laid = Laid(card, inner, ctx);
        RowLayout.PageLabel = RowLayout.ColumnFor(card.Rows, inner);
        try
        {
            foreach (var (row, renderer, height) in laid)
            {
                var rowCtx = Safe.Enabled(row) ? ctx : ctx.With(enabled: false);
                var state = RowLayout.Begin(rowCtx.Enabled);
                try
                {
                    renderer.Draw(row, new Rect(r.x + Pad, y, inner, height), rowCtx);
                }
                catch (Exception e)
                {
                    Log.ErrorThrottled("menu:row:" + row.Key, $"Menu row {row.Key} failed: {e.Message}");
                }
                finally
                {
                    RowLayout.End(state);
                }
                y += height;
            }
        }
        finally
        {
            RowLayout.PageLabel = previous;
        }
    }

    private static IEnumerable<string> SearchOf(IEnumerable<MenuRow> rows) =>
        rows.SelectMany(row => RowKinds.For(row)?.Search(row) ?? Array.Empty<string>());

    private static RowText BridgeOf(string kind, string title, IEnumerable<MenuRow> rows, RowContext ctx)
    {
        var lines = new List<string>();
        foreach (var row in rows)
        {
            if (RowKinds.For(row) is not { } renderer || !renderer.Shows(row, ctx)) continue;
            var text = renderer.Bridge(row, Safe.Enabled(row) ? ctx : ctx.With(enabled: false));
            lines.Add(text.Line());
            lines.AddRange(text.Warnings.Select(w => $"  warn {RowText.Quote(w)}"));
            lines.AddRange(text.Lines.Select(l => "  " + l));
        }
        return new RowText(kind, title) { Lines = lines };
    }

    private sealed class CardRenderer : RowRenderer<CardRow>
    {
        protected override float Height(CardRow row, float width, RowContext ctx) => CardHeight(row, width, ctx) + Gap;

        protected override void Draw(CardRow row, Rect rect, RowContext ctx) => DrawCard(row, new Rect(rect.x, rect.y, rect.width, rect.height - Gap), ctx);

        protected override IEnumerable<string> Search(CardRow row) => SearchOf(row.Rows).Prepend(row.Title);

        protected override RowText Bridge(CardRow row, RowContext ctx) => BridgeOf("card", row.Title, row.Rows, ctx);
    }

    private sealed class ColumnsRenderer : RowRenderer<CardColumns>
    {
        private static bool Side(CardColumns row, float width) => row.Cards.Count > 1 && (width - Gap * (row.Cards.Count - 1)) / row.Cards.Count >= MinColumnWidth;

        private static float ColumnWidth(CardColumns row, float width) => (width - Gap * (row.Cards.Count - 1)) / row.Cards.Count;

        protected override float Height(CardColumns row, float width, RowContext ctx)
        {
            if (!Side(row, width)) return row.Cards.Sum(c => CardHeight(c, width, ctx) + Gap);
            float column = ColumnWidth(row, width);
            return row.Cards.Max(c => CardHeight(c, column, ctx)) + Gap;
        }

        protected override void Draw(CardColumns row, Rect rect, RowContext ctx)
        {
            if (!Side(row, rect.width))
            {
                float y = rect.y;
                foreach (var card in row.Cards)
                {
                    float h = CardHeight(card, rect.width, ctx);
                    DrawCard(card, new Rect(rect.x, y, rect.width, h), ctx);
                    y += h + Gap;
                }
                return;
            }
            float column = ColumnWidth(row, rect.width), height = rect.height - Gap, x = rect.x;
            foreach (var card in row.Cards)
            {
                DrawCard(card, new Rect(x, rect.y, column, height), ctx);
                x += column + Gap;
            }
        }

        protected override IEnumerable<string> Search(CardColumns row) => row.Cards.SelectMany(c => SearchOf(c.Rows).Prepend(c.Title));

        protected override RowText Bridge(CardColumns row, RowContext ctx) =>
            new("cards", string.Join(" | ", row.Cards.Select(c => c.Title)))
            {
                Lines = row.Cards.SelectMany(c => BridgeOf("card", c.Title, c.Rows, ctx) is var t ? new[] { t.Line() }.Concat(t.Lines.Select(l => "  " + l)) : Array.Empty<string>()).ToList(),
            };
    }
}
