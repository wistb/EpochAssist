using EpochAssist.Core;
using EpochAssist.Ui;
using UnityEngine;

namespace EpochAssist.Features;

internal static partial class Stash
{
    // What Apply all would do at this budget, line by line: the stash changes (amber marks those that cost gold), the gold
    // before and after, and that items never move.
    private static List<(string Text, bool Costs)> ApplyAllLines(StashSnapshot snap, Preview preview, int steps)
    {
        var lines = new List<(string, bool)>();
        string N(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
        int created = preview.Folders.Count(f => f.ExistingId == null);
        int renamed = preview.Folders.Count(f => f.Configure && f.ExistingId != null);
        int configured = preview.Targets.Count(x => x.Fill == Fill.Reuse);
        int bought = preview.Targets.Count(x => x.Fill == Fill.Buy);
        int moves = (snap.ClientCopy ? FolderMovesAhead(snap, preview) : 0) + TabMovesAhead(snap, preview);
        if (created > 0) lines.Add(($"Creates {N(created, "folder", "folders")} (free)", false));
        if (renamed > 0) lines.Add(($"Renames or recolours {N(renamed, "folder", "folders")}", false));
        if (configured > 0) lines.Add(($"Changes {N(configured, "of your tabs", "of your tabs")}: names, colours, rules, folder", false));
        if (bought > 0) lines.Add(($"Buys {N(bought, "tab", "tabs")} for {preview.Cost:N0} gold", true));
        if (moves > 0) lines.Add(($"Moves {N(moves, "tab or folder", "tabs or folders")} into place", false));
        lines.Add(($"Gold: {snap.Gold:N0} now, {snap.Gold - preview.Cost:N0} after", preview.Cost > 0));
        lines.Add(($"{N(steps, "step", "steps")}, one at a time; it stops at the first failure.", false));
        lines.Add(("Items are never moved, and tabs you keep stay as they are.", false));
        return lines;
    }

    /// <summary>The Apply all summary over the page: what it does and costs, with Run and Cancel.</summary>
    private sealed class ApplyAllPopup : IPopup
    {
        private const float Pad = 10f, LineH = 22f, ButtonH = 26f, Width = 440f;
        private readonly List<(string Text, bool Costs)> _lines;
        private Rect _rect, _run, _cancel;

        public ApplyAllPopup(Rect anchor, List<(string Text, bool Costs)> lines)
        {
            _lines = lines;
            float height = Pad + LineH + 4f + lines.Count * LineH + 8f + ButtonH + Pad;
            _rect = Popups.Place(new Vector2(Width, height), anchor);
            float by = _rect.yMax - Pad - ButtonH;
            float runW = Controls.ButtonWidth("Run all steps") + 8f, cancelW = Controls.ButtonWidth("Cancel") + 8f;
            _run = new Rect(_rect.x + Pad, by, runW, ButtonH);
            _cancel = new Rect(_run.xMax + 6f, by, cancelW, ButtonH);
        }

        public Rect Rect => _rect;

        public void HandleEvent(Event e)
        {
            if (e.type != EventType.MouseDown || e.button != 0) return;
            if (_run.Contains(e.mousePosition)) Run();
            else if (_cancel.Contains(e.mousePosition)) Popups.Close(commit: false);
        }

        public bool HandleKey(Event e) => false;

        public bool HandleNav(MenuNav nav)
        {
            if (nav.Activate) Run();
            return false;
        }

        public void Closed(bool commit) { }

        private static void Run()
        {
            Popups.Close();
            s_pending = () => ApplyAll(out s_message);
        }

        public void Draw()
        {
            if (Event.current.type != EventType.Repaint) return;
            var r = _rect;
            if (Widgets.Themed) Theme.DrawSliced(r, Theme.PopupBg, Theme.CardRadius);
            var text = Widgets.Style(() => Theme.Label, GUI.skin.label);
            float y = r.y + Pad;
            Widgets.Paint(new Rect(r.x + Pad, y, r.width - Pad * 2f, LineH), "Apply all at this budget", Theme.ButtonText);
            y += LineH + 4f;
            foreach (var (line, costs) in _lines)
            {
                if (costs) Theme.Fill(new Rect(r.x + Pad, y + 4f, 3f, LineH - 8f), Amber);
                Widgets.Paint(new Rect(r.x + Pad + 10f, y, r.width - Pad * 2f - 10f, LineH), Controls.Fit(line, text, r.width - Pad * 2f - 10f), text);
                y += LineH;
            }
            var mouse = Event.current.mousePosition;
            Theme.DrawSliced(_run, _run.Contains(mouse) ? Theme.PrimaryHover : Theme.PrimaryBg, Theme.ControlRadius);
            Widgets.Paint(_run, "Run all steps", Theme.PrimaryText);
            Theme.DrawSliced(_cancel, _cancel.Contains(mouse) ? Theme.ButtonHover : Theme.ButtonBg, Theme.ControlRadius);
            Widgets.Paint(_cancel, "Cancel", Theme.ButtonText);
        }
    }
}
