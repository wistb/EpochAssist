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
        RowKinds.Register<TextButtonRow>(new TextButtonRenderer());
        RowKinds.Register<BlockRow>(new BlockRenderer());
    }

    /// <summary>Runs a button's action from the menu.</summary>
    public static void Run(ActionButton button)
    {
        if (button.OnClick != null) Safe.Call(() => { button.OnClick(); return true; }, false, button.Key);
        else Actions.Run(button.Action, "menu");
    }

    /// <summary>A button's tip: its live text when it has one.</summary>
    public static string? Tip(ActionButton button) => button.TipText != null ? Safe.Call(button.TipText, null, button.Key + ".tip") : button.Tip;

    /// <summary>A flat banner with the accent bar at its left; the buttons, when given, at its right (the second at the
    /// far right). Returns its height.</summary>
    public static float BannerHeight(string text, float width, ActionButton? button, ActionButton? second = null) =>
        Mathf.Max(28f, TextCache.Height(text, Widgets.Style(() => Theme.BannerText, GUI.skin.label), width - ButtonsWidth(button, second)));

    // The buttons' room: each button and the gap before it, and the margin at the right.
    private static float ButtonsWidth(ActionButton? button, ActionButton? second)
    {
        float w = 0f;
        if (button != null) w += Controls.ButtonWidth(button.Label) + 6f;
        if (second != null) w += Controls.ButtonWidth(second.Label) + 6f;
        return w > 0f ? w + 6f : 0f;
    }

    public static void Banner(Rect r, string text, ActionButton? button, string key, ActionButton? second = null)
    {
        if (Event.current.type == EventType.Repaint)
        {
            Theme.Fill(r, Widgets.Themed ? Theme.Values.Card : new Color(0f, 0f, 0f, 0.3f));
            Theme.Fill(new Rect(r.x, r.y, 2f, r.height), Widgets.Themed ? Theme.Accent : Color.white);
            Widgets.Paint(new Rect(r.x, r.y, r.width - ButtonsWidth(button, second), r.height), text, Widgets.Style(() => Theme.BannerText, GUI.skin.label));
        }
        float right = r.xMax - 8f;
        foreach (var (b, k) in new[] { (second, key + ".second"), (button, key) })
        {
            if (b == null) continue;
            float w = Controls.ButtonWidth(b.Label);
            right -= w;
            if (Controls.Button(new Rect(right, Mathf.Round(r.center.y - 12f), w, 24f), b.Label, k, b.Primary)) Run(b);
            right -= 6f;
        }
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
            Text(row) is { } text ? BannerHeight(text, width, row.Button, row.Second) + RowLayout.RowGap : 0f;

        protected override void Draw(BannerRow row, Rect rect, RowContext ctx)
        {
            if (Text(row) is { } text) Banner(new Rect(rect.x, rect.y, rect.width, rect.height - RowLayout.RowGap), text, row.Button, row.Key + ".button", row.Second);
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
            if (Controls.Button(r, row.Label, ctx.Prefix + row.Key, row.Primary, Tip(row))) Run(row);
        }

        protected override IEnumerable<string> Search(ActionButton row) => new[] { row.Label };
        protected override RowText Bridge(ActionButton row, RowContext ctx) => new("button", row.Label, row.Action, ctx.Enabled && Safe.Enabled(row));
    }

    private sealed class TextButtonRenderer : RowRenderer<TextButtonRow>
    {
        private static string Text(TextButtonRow row) => Safe.Call(row.Text, string.Empty, row.Key);

        protected override float Height(TextButtonRow row, float width, RowContext ctx) => RowLayout.ControlHeight + RowLayout.RowGap;

        protected override void Draw(TextButtonRow row, Rect rect, RowContext ctx)
        {
            var parts = RowLayout.Lay(rect, rect.width - RowLayout.LabelWidth(rect.width) - RowLayout.Gap, null);
            float buttonWidth = Controls.ButtonWidth(row.Button.Label);
            var style = Widgets.Style(() => Theme.Label, GUI.skin.label);
            string text = Text(row);
            float x = parts.Control.x;
            if (row.Before is { } before)
            {
                float beforeWidth = Controls.ButtonWidth(before.Label);
                Button(new Rect(x, parts.Control.y, beforeWidth, RowLayout.ControlHeight), before, ctx.Prefix + row.Key + ".before");
                x += beforeWidth + 12f;
            }
            // The button follows the text, or sits at the end when the text needs the room.
            float textWidth = Mathf.Min(TextCache.Width(text, style), parts.Control.xMax - x - buttonWidth - 12f);
            var button = new Rect(x + textWidth + 12f, parts.Control.y, buttonWidth, RowLayout.ControlHeight);
            if (Event.current.type == EventType.Repaint)
            {
                Widgets.Paint(parts.Label, row.Label, style);
                Widgets.Paint(new Rect(x, parts.Control.y, textWidth, parts.Control.height), text, style);
            }
            Button(button, row.Button, ctx.Prefix + row.Key + ".button");
        }

        private static void Button(Rect r, ActionButton button, string key)
        {
            bool enabled = GUI.enabled;
            GUI.enabled = enabled && Safe.Enabled(button);
            if (Controls.Button(r, button.Label, key, button.Primary, Tip(button))) Run(button);
            GUI.enabled = enabled;
        }

        protected override IEnumerable<string> Search(TextButtonRow row) => new[] { row.Label, Text(row) };
        protected override RowText Bridge(TextButtonRow row, RowContext ctx) => new("textbutton", row.Label, Text(row));
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
            Widgets.Paint(parts.Control, Widgets.Themed ? Widgets.Colored(text, state != null ? Theme.LightText(color) : color) : text, Widgets.Style(() => Theme.Label, GUI.skin.label));
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
