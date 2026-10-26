using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

// Rows that show text or run an action: headings, hints, banners, buttons, live readouts and blocks.
internal static class TextRows
{
    public static void Register()
    {
        RowKinds.Register<HeadingRow>(new HeadingRenderer());
        RowKinds.Register<HintRow>(new HintRenderer());
        RowKinds.Register<BannerRow>(new BannerRenderer());
        RowKinds.Register<ActionButton>(new ButtonRenderer());
        RowKinds.Register<LiveText>(new LiveRenderer());
        RowKinds.Register<BlockRow>(new BlockRenderer());
    }

    /// <summary>Runs a button's action from the menu.</summary>
    public static void Run(ActionButton button) => Actions.Run(button.Action, "menu");

    /// <summary>A flat banner with the accent bar at its left; the button, when given, at its right. Returns its height.</summary>
    public static float BannerHeight(string text, float width, ActionButton? button) =>
        Mathf.Max(28f, TextCache.Height(text, Widgets.Style(() => Theme.BannerText, GUI.skin.label), width - (button != null ? Controls.ButtonWidth(button.Label) + 12f : 0f)));

    public static void Banner(Rect r, string text, ActionButton? button, string key)
    {
        float buttonWidth = button != null ? Controls.ButtonWidth(button.Label) : 0f;
        if (Event.current.type == EventType.Repaint)
        {
            Theme.Fill(r, Widgets.Themed ? Theme.Values.Card : new Color(0f, 0f, 0f, 0.3f));
            Theme.Fill(new Rect(r.x, r.y, 2f, r.height), Widgets.Themed ? Theme.Accent : Color.white);
            Widgets.Paint(new Rect(r.x, r.y, r.width - (button != null ? buttonWidth + 12f : 0f), r.height), text, Widgets.Style(() => Theme.BannerText, GUI.skin.label));
        }
        if (button == null) return;
        var b = new Rect(r.xMax - buttonWidth - 8f, Mathf.Round(r.center.y - 12f), buttonWidth, 24f);
        if (Controls.Button(b, button.Label, key, button.Primary)) Run(button);
    }

    private sealed class HeadingRenderer : RowRenderer<HeadingRow>
    {
        protected override float Height(HeadingRow row, float width, RowContext ctx) => 22f;

        protected override void Draw(HeadingRow row, Rect rect, RowContext ctx)
        {
            if (Event.current.type != EventType.Repaint) return;
            GUIStyle style = Widgets.Style(() => Theme.Heading, GUI.skin.label);
            string text = row.Text.ToUpperInvariant();
            var r = new Rect(rect.x, rect.y + 4f, rect.width, 14f);
            Widgets.Paint(r, text, style);
            float x = r.x + TextCache.Width(text, style) + 8f;
            if (Widgets.Themed && x < r.xMax) Theme.Fill(new Rect(x, Mathf.Round(r.center.y), r.xMax - x, 1f), Theme.DividerColor);
        }

        protected override IEnumerable<string> Search(HeadingRow row) => new[] { row.Text };
        protected override RowText Bridge(HeadingRow row, RowContext ctx) => new("heading", row.Text);
    }

    private sealed class HintRenderer : RowRenderer<HintRow>
    {
        private static string Text(HintRow row) => Safe.Call(row.Text, string.Empty, row.Key);

        protected override float Height(HintRow row, float width, RowContext ctx) => Controls.HintHeight(Text(row), width) + RowLayout.RowGap;

        protected override void Draw(HintRow row, Rect rect, RowContext ctx) => RowLayout.Hint(new Rect(rect.x, rect.y, rect.width, rect.height - RowLayout.RowGap), Text(row));

        protected override IEnumerable<string> Search(HintRow row) => new[] { Text(row) };
        protected override RowText Bridge(HintRow row, RowContext ctx) => new("hint", Text(row));
    }

    private sealed class BannerRenderer : RowRenderer<BannerRow>
    {
        private static string? Text(BannerRow row) => Safe.Call(row.Text, null, row.Key);

        public override bool Shows(MenuRow row, RowContext ctx) => base.Shows(row, ctx) && Text((BannerRow)row) != null;

        protected override float Height(BannerRow row, float width, RowContext ctx) =>
            Text(row) is { } text ? BannerHeight(text, width, row.Button) + RowLayout.RowGap : 0f;

        protected override void Draw(BannerRow row, Rect rect, RowContext ctx)
        {
            if (Text(row) is { } text) Banner(new Rect(rect.x, rect.y, rect.width, rect.height - RowLayout.RowGap), text, row.Button, row.Key + ".button");
        }

        protected override IEnumerable<string> Search(BannerRow row) => Text(row) is { } text ? new[] { text } : Array.Empty<string>();
        protected override RowText Bridge(BannerRow row, RowContext ctx) => new("banner", Text(row) ?? string.Empty);
    }

    private sealed class ButtonRenderer : RowRenderer<ActionButton>
    {
        protected override float Height(ActionButton row, float width, RowContext ctx) => RowLayout.ControlHeight + RowLayout.RowGap;
        protected override float InlineWidth(ActionButton row) => Controls.ButtonWidth(row.Label);

        protected override void Draw(ActionButton row, Rect rect, RowContext ctx)
        {
            var r = new Rect(rect.x, rect.y, Controls.ButtonWidth(row.Label), RowLayout.ControlHeight);
            if (Controls.Button(r, row.Label, ctx.Prefix + row.Key, row.Primary)) Run(row);
        }

        protected override IEnumerable<string> Search(ActionButton row) => new[] { row.Label };
        protected override RowText Bridge(ActionButton row, RowContext ctx) => new("button", row.Label, row.Action, ctx.Enabled && Safe.Enabled(row));
    }

    private sealed class LiveRenderer : RowRenderer<LiveText>
    {
        public override bool Shows(MenuRow row, RowContext ctx) => ctx.Live && base.Shows(row, ctx);

        protected override float Height(LiveText row, float width, RowContext ctx) => RowLayout.ControlHeight + RowLayout.RowGap;

        protected override void Draw(LiveText row, Rect rect, RowContext ctx)
        {
            if (Event.current.type != EventType.Repaint) return;
            var (text, state) = Safe.Call(row.Value, (string.Empty, (StatusState?)null), row.Key);
            var parts = RowLayout.Lay(rect, rect.width - RowLayout.LabelWidth(rect.width) - RowLayout.Gap, null);
            Color color = state is { } s ? Theme.StateColor(s) : Theme.Text;
            float x = parts.Label.x;
            if (state is { } shape && Widgets.Themed)
            {
                Menu.DrawState(new Rect(x, Mathf.Round(parts.Label.center.y - 4f), 8f, 8f), shape, color);
                x += 14f;
            }
            Widgets.Paint(new Rect(x, parts.Label.y, parts.Label.xMax - x, parts.Label.height), row.Label, Widgets.Style(() => Theme.Label, GUI.skin.label));
            Widgets.Paint(parts.Control, Widgets.Themed ? Widgets.Colored(text, color) : text, Widgets.Style(() => Theme.Label, GUI.skin.label));
        }

        protected override IEnumerable<string> Search(LiveText row) => new[] { row.Label };

        protected override RowText Bridge(LiveText row, RowContext ctx) =>
            new("live", row.Label, Safe.Call(row.Value, (string.Empty, (StatusState?)null), row.Key).Item1);
    }

    private sealed class BlockRenderer : RowRenderer<BlockRow>
    {
        protected override float Height(BlockRow row, float width, RowContext ctx) => MenuBlocks.Height(row.Block, row.Data, width) + RowLayout.RowGap;

        protected override void Draw(BlockRow row, Rect rect, RowContext ctx) =>
            MenuBlocks.Draw(row.Block, new Rect(rect.x, rect.y, rect.width, rect.height - RowLayout.RowGap), row.Data);

        protected override RowText Bridge(BlockRow row, RowContext ctx) => new("block", row.Block)
        {
            Lines = MenuBlocks.Bridge(row.Block, row.Data).ToList(),
        };
    }
}
