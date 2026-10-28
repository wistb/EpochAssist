using System.Globalization;
using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

/// <summary>
/// The Past sessions page (contracts/page.md). List mode: Character and Range filters, the trend chart of XP and gold
/// per hour, and one selectable line per session with best marks. Detail mode: an opened session's dashboard (the
/// Current session rows without live figures), Leave out of trends and Delete with a confirmation. Esc and the back
/// button return to the list; mode changes run on the next Layout event so a pass never sees half a change. The
/// current session isn't listed. Characters show as mode and slot.
/// </summary>
internal static class SessionsPast
{
    public const string ListBlock = "sessions.past.list", TrendBlock = "sessions.past.trend", DetailBlock = "sessions.past.detail";
    private const float LineHeight = 22f, TrendHeight = 130f, RefreshSeconds = 0.5f;
    private const double BestAfter = 600d;
    private const int TrendCount = 120;
    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;
    private static readonly string[] s_ranges = { "Today", "7 days", "30 days", "All" };

    /// <summary>One listed session, its rates per figure (by metric key) and its words, made when the list changes.</summary>
    private sealed record Line(SessionIndexEntry Entry, Dictionary<string, double?> Rates, bool LeftOut)
    {
        public string Text = string.Empty;
    }

    // Filters, kept for the game session: a character label (null for all) and the range (default 7 days).
    private static string? s_character;
    private static int s_range = 1;

    private static List<Line> s_lines = new();
    private static List<string> s_characters = new() { "All" };
    private static float s_linesAt = float.MinValue;
    private static int s_linesKey;
    private static readonly SessionCharts.Canvas s_trendCanvas = new();
    // The trend: its figure (a metric key, null for the first focused one), the per-hour values oldest first, their
    // moving average, and the scale's cap.
    private static string? s_trendMetric;
    private static List<double> s_trendValues = new(), s_trendAverage = new();
    private static double s_trendCap;
    private static int s_trendCountShown;

    // The opened session: its id and figures, and its dashboard.
    private static (string Id, SessionStats Stats, string Title)? s_detail;
    private static readonly SessionDashboard s_dashboard = new(() => s_detail?.Stats, live: false);
    private static bool s_confirm;
    private static string? s_error;
    // A mode change asked for in an input event, run on the next Layout event.
    private static Action? s_pending;

    public static void Register()
    {
        MenuBlocks.Register(ListBlock, (rect, _) => DrawList(rect), _ => ListBridge(), (_, _) => ListHeight());
        MenuBlocks.Register(TrendBlock, (rect, _) => DrawTrend(rect), _ => TrendBridge(), (_, _) => RowLayout.ControlHeight + 10f + TrendHeight + LineHeight + 4f);
        MenuBlocks.Register(DetailBlock, (rect, _) => DrawDetail(rect), _ => DetailBridge(), (_, _) => RowLayout.ControlHeight + RowLayout.RowGap);
    }

    public static void Reset()
    {
        Close();
        s_pending = null;
        s_lines = new List<Line>();
        s_linesAt = float.MinValue;
        SessionCharts.Release(s_trendCanvas);
    }

    /// <summary>Esc on the page: back to the list from a session; true when handled.</summary>
    public static bool Back()
    {
        if (s_detail == null) return false;
        if (s_confirm)
        {
            s_confirm = false;
            return true;
        }
        Close();
        PageView.ScrollToRow("choice.character");
        return true;
    }

    /// <summary>The page's rows: list mode and detail mode, each shown while it is the mode.</summary>
    public static IEnumerable<MenuRow> Rows()
    {
        Func<bool> list = () => s_detail == null, detail = () => s_detail != null;
        yield return new ValueChoice("Character", () => Math.Max(0, s_character == null ? 0 : Characters().IndexOf(s_character)),
            i => s_character = i <= 0 ? null : Characters()[i], () => Characters()) { VisibleWhen = list };
        yield return new ValueChoice("Range", () => s_range, i => s_range = i, () => s_ranges) { VisibleWhen = list };
        yield return new CardRow("Trend", null, new BlockRow(TrendBlock)) { VisibleWhen = list };
        yield return new BlockRow(ListBlock) { VisibleWhen = list };

        yield return new BlockRow(DetailBlock) { VisibleWhen = detail };
        yield return new ValueToggle("Leave out of trends", () => s_detail is { } d && SessionTrends.IsLeftOut(d.Id), on =>
        {
            if (s_detail is { } d) SessionTrends.Set(d.Id, on);
            s_linesAt = float.MinValue;
        }) { VisibleWhen = detail };
        yield return new ActionButton("Delete", "sessions.past.delete")
        {
            OnClick = () =>
            {
                s_confirm = true;
                s_error = null;
            },
            VisibleWhen = () => detail() && !s_confirm,
            Tip = "Deletes this session's file",
        };
        yield return new BannerRow(() => s_detail == null ? null : s_error ?? (s_confirm ? "Delete this session? This can't be undone." : null),
            new ActionButton("Delete", "sessions.past.confirm", primary: true) { OnClick = () => s_pending = DeleteOpened, VisibleWhen = () => s_confirm },
            new ActionButton("Keep", "sessions.past.keep")
            {
                OnClick = () =>
                {
                    s_confirm = false;
                    s_error = null;
                },
            }) { VisibleWhen = detail };
        foreach (var row in SessionsPage.Rows(s_dashboard)) yield return row;
    }

    // ---- Opening and closing ----

    private static void Open(string id)
    {
        if (Sessions.Read(id) is not { } view)
        {
            Toasts.Show("sessions", "That session can't be read.", ToastKind.Warning);
            return;
        }
        var stats = SessionStats.For(view);
        s_detail = (id, stats, Title(view.Header.Start, view.Header.CharacterLabel, view.EndKind, stats.Level));
        s_confirm = false;
        s_error = null;
        s_dashboard.Reset();
        PageView.ScrollToRow(DetailBlock.Insert(0, "block."));
    }

    private static void Close()
    {
        s_detail = null;
        s_confirm = false;
        s_error = null;
        s_dashboard.Reset();
    }

    private static void DeleteOpened()
    {
        if (s_detail is not { } d) return;
        if (!Sessions.Delete(d.Id, out string? reason))
        {
            s_error = $"Can't delete: {reason}.";
            s_confirm = false;
            return;
        }
        Close();
        s_linesAt = float.MinValue;
        PageView.ScrollToRow("choice.character");
    }

    // "Oct 28, 14:32  ·  offline, slot 1  ·  level 12 → 14  ·  ended unexpectedly".
    private static string Title(DateTimeOffset start, string character, string endKind, LevelFigures level)
    {
        var parts = new List<string> { start == default ? "unknown start" : start.LocalDateTime.ToString("MMM d, HH:mm", s_inv), character };
        if (Levels(level.StartLevel, level.Gained) is { } levels) parts.Add(levels);
        if (EndText(endKind) is { } end) parts.Add(end);
        return string.Join("  ·  ", parts);
    }

    private static string? Levels(int? start, int gained) =>
        start is int s ? gained > 0 ? $"level {s} → {s + gained}" : $"level {s}" : gained > 0 ? $"+{gained} {(gained == 1 ? "level" : "levels")}" : null;

    private static string? EndText(string kind) => kind switch
    {
        "normal" or "open" => null,
        "unexpected" => "ended unexpectedly",
        "cut" => "cut at the size limit",
        _ => kind,
    };

    private static void RunPending()
    {
        if (s_pending == null || Event.current.type != EventType.Layout) return;
        var pending = s_pending;
        s_pending = null;
        Safe.Run(pending, "sessions.past.pending");
    }

    // ---- The list ----

    private static List<string> Characters()
    {
        Refresh();
        return s_characters;
    }

    // The listed lines, made again at most twice a second, and at once after a filter or left-out change.
    private static List<Line> Lines()
    {
        Refresh();
        return s_lines;
    }

    private static void Refresh()
    {
        int key = HashCode.Combine(s_character, s_range, SessionTrends.All.Count, SessionsPage.FocusMask(), TrendMetric()?.Key);
        if (key == s_linesKey && Clock.Now - s_linesAt < RefreshSeconds) return;
        var current = Sessions.Current?.Header.Id;
        var all = Sessions.List().Where(e => e.Id != current && e.Header?.Test != true && e.EndKind != "open").ToList();
        // Characters seen, as mode and slot, kept in the order of the newest session.
        s_characters = new List<string> { "All" };
        s_characters.AddRange(all.Select(e => e.Character).Distinct());
        if (s_character != null && !s_characters.Contains(s_character)) s_character = null;
        var today = DateTimeOffset.Now.Date;
        DateTime? since = s_range switch { 0 => today, 1 => today.AddDays(-6), 2 => today.AddDays(-29), _ => null };
        var lines = all.Where(e => (s_character == null || e.Character == s_character) && (since == null || (e.Start is { } start && start.LocalDateTime >= since)))
            .Select(e => new Line(e, SessionsPage.Metrics.ToDictionary(m => m.Key, m => m.PastRate(e)), SessionTrends.IsLeftOut(e.Id))).ToList();
        // Best among shown, not left out and at least 10 minutes long, per focused figure where more is better.
        var eligible = lines.Where(l => !l.LeftOut && l.Entry.Duration >= BestAfter).ToList();
        var best = new Dictionary<Line, List<string>>();
        foreach (var metric in SessionsPage.Focused())
        {
            if (metric.Key is "deaths" or "level") continue;
            if (eligible.Where(l => l.Rates[metric.Key] > 0).MaxBy(l => l.Rates[metric.Key]) is not { } top) continue;
            if (!best.TryGetValue(top, out var marks)) best[top] = marks = new List<string>();
            marks.Add($"{metric.Label.ToLowerInvariant()}/h");
        }
        foreach (var line in lines) line.Text = LineText(line, best.GetValueOrDefault(line) ?? new List<string>());
        s_lines = lines;
        s_linesKey = key;
        s_linesAt = Clock.Now;

        // The trend: the chosen figure per hour, oldest first, left-out and short sessions skipped, the newest 120. The
        // scale stops at the 95th percentile once there are enough sessions, so one outlier doesn't flatten the rest
        // (its column shows clipped); the line is the average of the five around each session.
        var shown = TrendMetric();
        var trend = shown == null ? new List<double>()
            : lines.Where(l => !l.LeftOut && l.Entry.Duration >= BestAfter && l.Rates[shown.Key] != null).Take(TrendCount).Reverse().Select(l => l.Rates[shown.Key]!.Value).ToList();
        s_trendCountShown = trend.Count;
        s_trendValues = trend;
        var sorted = trend.OrderBy(v => v).ToList();
        s_trendCap = sorted.Count == 0 ? 0d : sorted.Count >= 8 ? Math.Max(sorted[(int)Math.Floor(sorted.Count * 0.95) - 1], sorted[^1] * 0.05) : sorted[^1];
        s_trendAverage = trend.Select((_, i) => trend.Skip(Math.Max(0, i - 2)).Take(Math.Min(trend.Count, i + 3) - Math.Max(0, i - 2)).Average()).ToList();
    }

    // The trend's figure: the chosen one while in focus and countable, else the first focused one that is.
    private static SessionMetric? TrendMetric()
    {
        SessionMetric? first = null;
        foreach (var m in SessionsPage.Metrics)
        {
            if (!m.Focus.Value || m.Amount == null || m.Key is "level") continue;
            if (m.Key == s_trendMetric) return m;
            first ??= m;
        }
        return first;
    }

    private static string LineText(Line line, List<string> best)
    {
        var e = line.Entry;
        var t = e.Totals;
        var parts = new List<string> { e.Start is { } s ? s.LocalDateTime.ToString("MMM d, HH:mm", s_inv) : "?", e.Character };
        if (t != null && Levels(t.StartLevel ?? e.Header?.StartLevel, t.LevelUps) is { } levels) parts.Add(levels);
        parts.Add(SessionsPage.Clock(e.Duration));
        // The first four focused figures with anything to count, per hour, so a line fits the page.
        int shown = 0;
        foreach (var metric in SessionsPage.Focused())
        {
            if (shown == 4 || metric.Key == "level" || line.Rates[metric.Key] is not double rate || rate <= 0d) continue;
            parts.Add($"{SessionsPage.Short(rate)} {metric.Label.ToLowerInvariant()}/h");
            shown++;
        }
        if (EndText(e.EndKind) is { } end) parts.Add(end);
        if (best.Count > 0) parts.Add(Widgets.Themed ? Widgets.Colored("★ " + string.Join(", ", best), Theme.Accent) : "★ " + string.Join(", ", best));
        if (line.LeftOut) parts.Add("left out");
        return string.Join("  ·  ", parts);
    }

    private static float ListHeight()
    {
        int count = Lines().Count;
        return Mathf.Max(1, count) * LineHeight + RowLayout.RowGap;
    }

    // One selectable line per session (a click or Enter opens it); only lines in view are drawn.
    private static void DrawList(Rect rect)
    {
        RunPending();
        var lines = Lines();
        GUIStyle label = Widgets.Style(() => Theme.Label, GUI.skin.label);
        if (lines.Count == 0)
        {
            if (Event.current.type == EventType.Repaint) Widgets.Paint(new Rect(rect.x, rect.y, rect.width, LineHeight), "No sessions match.", Widgets.Style(() => Theme.Hint, GUI.skin.label));
            return;
        }
        var view = PageView.InView;
        for (int i = 0; i < lines.Count; i++)
        {
            var r = new Rect(rect.x, rect.y + i * LineHeight, rect.width, LineHeight);
            if (r.yMax < view.y || r.y > view.yMax) continue;
            var line = lines[i];
            bool clicked = GUI.Button(r, GUIContent.none, GUIStyle.none);
            if (Event.current.type == EventType.Repaint)
            {
                if (r.Contains(Event.current.mousePosition)) Theme.Fill(r, Theme.Values.ControlHover);
                Widgets.Paint(new Rect(r.x + 6f, r.y, r.width - 6f, r.height), line.Text, label);
            }
            if (Focus.Item("past." + line.Entry.Id, r) || clicked)
            {
                string id = line.Entry.Id;
                s_pending = () => Open(id);
            }
        }
    }

    private static IEnumerable<string> ListBridge()
    {
        var lines = Lines();
        if (lines.Count == 0) return new[] { "No sessions match." };
        return lines.Select(l => $"session {l.Entry.Id} {RowText.Quote(RowText.StripTags(l.Text))}");
    }

    private static void DrawTrend(Rect rect)
    {
        RunPending();
        Refresh();
        var shown = TrendMetric();
        // The countable focused figures as small switches; the shown one filled.
        float x = rect.x;
        foreach (var m in SessionsPage.Metrics)
        {
            if (!m.Focus.Value || m.Amount == null || m.Key is "level") continue;
            float bw = Controls.ButtonWidth(m.Label);
            if (Controls.Button(new Rect(x, rect.y, bw, RowLayout.ControlHeight), m.Label, "trend." + m.Key, m == shown) && m != shown)
            {
                s_trendMetric = m.Key;
                s_linesAt = float.MinValue;
            }
            x += bw + 6f;
        }
        if (Event.current.type != EventType.Repaint) return;
        GUIStyle hint = Widgets.Style(() => Theme.Hint, GUI.skin.label);
        var area = new Rect(rect.x, rect.y + RowLayout.ControlHeight + 10f, rect.width, TrendHeight);
        if (shown == null || s_trendCountShown < 2)
        {
            Widgets.Paint(new Rect(area.x, area.y, area.width, LineHeight), shown == null ? "Put a figure in focus to chart it." : s_trendCountShown == 0 ? "No sessions to chart." : "One session so far; the trend needs two.", hint);
            return;
        }
        SessionCharts.Columns(area, s_trendCanvas, HashCode.Combine(s_linesKey, s_trendCountShown, s_trendCap), s_trendValues, s_trendCap, Theme.Accent, s_trendAverage, Theme.Text);
        Widgets.Paint(new Rect(area.x, area.yMax + 2f, area.width, LineHeight),
            $"{shown.Label} per hour, {s_trendCountShown} sessions oldest first; scale to {SessionsPage.Short(s_trendCap)}, taller ones cut; line: average of five", hint);
    }

    private static IEnumerable<string> TrendBridge()
    {
        Refresh();
        return new[] { $"trend {TrendMetric()?.Key ?? "none"} sessions={s_trendCountShown} cap={s_trendCap.ToString("0", s_inv)} max={(s_trendValues.Count > 0 ? s_trendValues.Max() : 0d).ToString("0", s_inv)}" };
    }

    // ---- Detail ----

    // The back button and the opened session's title.
    private static void DrawDetail(Rect rect)
    {
        RunPending();
        if (s_detail is not { } d) return;
        const string back = "← Past sessions";
        float w = Controls.ButtonWidth(back);
        if (Controls.Button(new Rect(rect.x, rect.y, w, RowLayout.ControlHeight), back, "past.back")) s_pending = () => Back();
        if (Event.current.type == EventType.Repaint)
            Widgets.Paint(new Rect(rect.x + w + 12f, rect.y, rect.width - w - 12f, RowLayout.ControlHeight), d.Title, Widgets.Style(() => Theme.Label, GUI.skin.label));
    }

    private static IEnumerable<string> DetailBridge() => s_detail is { } d ? new[] { $"button \"← Past sessions\"", $"session {d.Id} {RowText.Quote(d.Title)}" } : Array.Empty<string>();
}
