using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

// Chips across the page (Autocast's slots) and number groups (several numbers on one row, each labelled above).
internal static class ChipRows
{
    public static void Register()
    {
        RowKinds.Register<ChipStrip>(new ChipRenderer());
        RowKinds.Register<NumberGroup>(new NumberGroupRenderer());
    }

    private const float ChipHeight = 40f, ChipGap = 6f;

    private static IReadOnlyList<Chip> Chips(ChipStrip row) => Safe.Call(row.Chips, Array.Empty<Chip>(), row.Key);

    private sealed class ChipRenderer : RowRenderer<ChipStrip>
    {
        public override bool Shows(MenuRow row, RowContext ctx) => ctx.Live && base.Shows(row, ctx);

        protected override float Height(ChipStrip row, float width, RowContext ctx) => ChipHeight + 8f;

        protected override void Draw(ChipStrip row, Rect rect, RowContext ctx)
        {
            if (Event.current.type != EventType.Repaint) return;
            var chips = Chips(row);
            if (chips.Count == 0) return;
            float width = (rect.width - ChipGap * (chips.Count - 1)) / chips.Count;
            for (int i = 0; i < chips.Count; i++)
            {
                var chip = new Rect(Mathf.Round(rect.x + i * (width + ChipGap)), rect.y, Mathf.Floor(width), ChipHeight);
                var c = chips[i];
                if (!Widgets.Themed)
                {
                    Widgets.Paint(chip, $"{c.Number}\n{c.State}", GUI.skin.box);
                    continue;
                }
                // Shapes, not colour alone: on has the accent edge and fill, paused its own edge, off is plain.
                Theme.DrawSliced(chip, c.State == ChipState.On ? Theme.ChipOn : c.State == ChipState.Paused ? Theme.ChipPaused : Theme.ChipOff, Theme.ChipRadius);
                Theme.ChipNumber.normal.textColor = c.State == ChipState.On ? Theme.HighlightText : c.State == ChipState.Paused ? Theme.Pause : Theme.Dim;
                Theme.ChipName.normal.textColor = c.State == ChipState.Off ? Theme.Dim : Theme.Text;
                Widgets.Paint(new Rect(chip.x, chip.y + 4f, chip.width, 18f), c.Number, Theme.ChipNumber);
                Widgets.Paint(new Rect(chip.x + 3f, chip.y + 21f, chip.width - 6f, 15f), Controls.Fit(c.Label, Theme.ChipName, chip.width - 8f), Theme.ChipName);
                if (c.State == ChipState.Paused) Menu.DrawState(new Rect(chip.xMax - 11f, chip.y + 4f, 7f, 7f), StatusState.Paused, Theme.Pause);
            }
        }

        protected override IEnumerable<string> Search(ChipStrip row) => Chips(row).Select(c => c.Label);

        protected override RowText Bridge(ChipStrip row, RowContext ctx) =>
            new("chips", null, string.Join(" ", Chips(row).Select(c => $"{c.Number}:{c.State.ToString().ToLowerInvariant()}")));
    }

    private sealed class NumberGroupRenderer : RowRenderer<NumberGroup>
    {
        private const float FieldWidth = 64f, LabelGap = 6f, ItemGap = 14f;

        private static IEnumerable<Number> Shown(NumberGroup row) => row.Numbers.Where(n => n.Visible);

        protected override float Height(NumberGroup row, float width, RowContext ctx) => RowLayout.ControlHeight + RowLayout.RowGap;

        protected override void Draw(NumberGroup row, Rect rect, RowContext ctx)
        {
            // Measured and drawn in the same style, so a label is never cut.
            GUIStyle label = Widgets.Style(() => Theme.FieldDim, GUI.skin.label);
            float x = rect.x;
            foreach (var number in Shown(row))
            {
                float w = Mathf.Ceil(TextCache.Width(number.Label, label));
                if (Event.current.type == EventType.Repaint)
                {
                    var labelRect = new Rect(x - label.padding.left, rect.y, w + label.padding.horizontal, RowLayout.ControlHeight);
                    Widgets.Paint(labelRect, ctx.Highlight != null ? MenuSearchText.Mark(number.Label, ctx.Highlight) : number.Label, label);
                }
                x += w + LabelGap;
                string key = SettingRows.KeyOf(number);
                NumberField.Draw(new Rect(x, rect.y, FieldWidth, RowLayout.ControlHeight), key, number, number.Help ?? number.Label);
                x += FieldWidth;
                if (!number.IsDefault && Controls.ResetMark(new Rect(x + 2f, rect.y + 4f, 16f, 16f), key + ".reset")) number.Reset();
                x += 18f + ItemGap;
            }
        }

        protected override IEnumerable<string> Search(NumberGroup row) => row.Numbers.Select(n => n.Label).Concat(row.Label != null ? new[] { row.Label } : Array.Empty<string>());

        protected override RowText Bridge(NumberGroup row, RowContext ctx) =>
            new("numbers", row.Label ?? string.Empty, string.Join(", ", Shown(row).Select(n => $"{n.Label}={n.Format(n.Value)}")), ctx.Enabled && Safe.Enabled(row), row.Numbers.All(n => n.IsDefault));
    }
}
