using System.Globalization;
using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

/// <summary>
/// One dashboard's source: its figures (the current session's, or an opened past session's), whether they are live,
/// its chart textures, the chart's figure and its lists' folds. The same rows draw either.
/// </summary>
internal sealed class SessionDashboard
{
    public SessionDashboard(Func<SessionStats?> stats, bool live)
    {
        Stats = stats;
        Live = live;
    }

    public Func<SessionStats?> Stats { get; }
    /// <summary>Live figures show: recent rates, level progress, the open run, time to next rank.</summary>
    public bool Live { get; }
    public readonly SessionCharts.Canvas Chart = new(), Timeline = new();
    /// <summary>The chart's figure (a metric key), or null for the first focused one with a series.</summary>
    public string? ChartMetric;
    public bool AllEchoes, AllDeaths, AllPicks, AllDrops;

    /// <summary>Folds the lists and frees the textures.</summary>
    public void Reset()
    {
        AllEchoes = AllDeaths = AllPicks = AllDrops = false;
        SessionCharts.Release(Chart);
        SessionCharts.Release(Timeline);
    }
}

/// <summary>
/// A figure the dashboard can put in focus: its tile (value and a detail line), its chart series, its rate per hour for
/// a past session (the trend and best marks), and the setting that keeps it in focus.
/// </summary>
internal sealed record SessionMetric(string Key, string Label, Toggle Focus, Func<SessionStats, string> Value, Func<SessionStats, bool, string> Detail,
    string? Chart, Func<SessionTotals, long?>? Amount, string Kind)
{
    /// <summary>Per hour over a past session, or null: too short, nothing to count, or the session didn't record the
    /// figure's record kind (a build from before it was recorded).</summary>
    public double? PastRate(SessionIndexEntry e) =>
        Amount != null && e.Totals is { } t && e.Duration >= SessionStats.RateAfter && e.Header?.Captured.Contains(Kind) == true && Amount(t) is long amount
            ? amount * 3600d / e.Duration : null;
}

/// <summary>
/// The Session tab's dashboard (contracts/page.md, reworked): focus chips, a tile per focused figure, the other figures
/// on one line, a chart of one focused figure, the timeline, drops and zone time, factions, and the echo, death, pick
/// and filtered drop lists, all from SessionStats. Texts are made once per figures snapshot (at most twice a second
/// live). Characters show as mode and slot.
/// </summary>
internal static class SessionsPage
{
    public const string TimeBlock = "sessions.time", FocusBlock = "sessions.focus", TilesBlock = "sessions.tiles", ChartBlock = "sessions.chart", TimelineBlock = "sessions.timeline";
    public const string DropsBlock = "sessions.drops", ZonesBlock = "sessions.zones", FactionsBlock = "sessions.factions";
    public const string EchoesBlock = "sessions.echoes", DeathsBlock = "sessions.deaths", PicksBlock = "sessions.picks", FilteredBlock = "sessions.filtered";
    private const int Folded = 10;
    private const float LineHeight = 20f, BarHeight = 12f, ChartHeight = 150f, TimelineHeight = 30f, TileWidth = 168f, TileHeight = 72f, TileGap = 10f;
    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;

    /// <summary>The current session's dashboard (the sample's while Sample data is on).</summary>
    public static readonly SessionDashboard Current = new(() => ShowSample && SampleStats != null ? SampleStats() : SessionStats.LiveStats(), live: true);

    /// <summary>Dev builds: generated figures for the Sample data switch, set by the dev bridge; null otherwise.</summary>
    public static Func<SessionStats>? SampleStats { get; set; }

    /// <summary>The Current session page shows the sample instead of the session (dev builds, not kept).</summary>
    public static bool ShowSample { get; set; }

    /// <summary>Every figure that can be in focus, in the order the tiles and chips show them.</summary>
    public static readonly SessionMetric[] Metrics =
    {
        new("favour", "Favour", SessionSettings.FocusFavour, s => Whole(Favour(s).Gained), FavourDetail, "favour",
            t => t.Factions.Where(f => f.Key != SessionStats.AmberFaction).Sum(f => f.Value.FavourGained), "favour"),
        new("rep", "Reputation", SessionSettings.FocusReputation, s => Whole(s.Factions.Sum(f => f.ReputationGained)), ReputationDetail, "rep",
            t => t.Factions.Sum(f => f.Value.ReputationGained), "rep"),
        new("amber", "Memory amber", SessionSettings.FocusAmber, s => Whole(Amber(s)?.FavourGained ?? 0), AmberDetail, "amber",
            t => t.Factions.TryGetValue(SessionStats.AmberFaction, out var a) ? a.FavourGained : 0, "favour"),
        new("echoes", "Echoes", SessionSettings.FocusEchoes, s => Whole(s.Echo.Completed), EchoDetail, "echoes", t => t.EchoClearCount > 0 ? t.EchoClearCount : t.Echoes, "echo"),
        new("level", "Level", SessionSettings.FocusLevel, s => s.Level.CurrentLevel?.ToString(s_inv) ?? "-", LevelDetail, null, t => t.LevelUps, "level"),
        new("drops", "Drops", SessionSettings.FocusDrops, s => Whole(s.Totals.DropCount), DropDetail, "drops", t => t.DropCount, "drop"),
        new("xp", "XP", SessionSettings.FocusXp, s => Short(s.Totals.Xp), (s, live) => RateText(s.XpRates, live) ?? "none yet", "xp", t => t.Xp, "xp"),
        new("gold", "Gold", SessionSettings.FocusGold, s => Short(s.Totals.GoldGained), (s, live) => Join(RateText(s.GoldRates, live), s.Totals.GoldSpent > 0 ? $"spent {Short(s.Totals.GoldSpent)}" : null) ?? "none yet",
            "gold", t => t.GoldGained, "gold"),
        new("deaths", "Deaths", SessionSettings.FocusDeaths, s => Whole(s.Totals.Deaths), DeathDetail, "deaths", t => t.Deaths, "died"),
    };

    public static void Register()
    {
        MenuBlocks.Register(TimeBlock, (rect, d) => DrawTime(rect, (SessionDashboard)d!), d => TimeBridge((SessionDashboard)d!), (_, _) => TimeHeight);
        MenuBlocks.Register(FocusBlock, (rect, _) => DrawFocus(rect), _ => FocusBridge(), (_, width) => FocusHeight(width));
        MenuBlocks.Register(TilesBlock, (rect, d) => DrawTiles(rect, (SessionDashboard)d!), d => TilesBridge((SessionDashboard)d!), (d, width) => TilesHeight((SessionDashboard)d!, width));
        MenuBlocks.Register(ChartBlock, (rect, d) => DrawChart(rect, (SessionDashboard)d!), d => ChartBridge((SessionDashboard)d!), (d, _) => ChartBlockHeight((SessionDashboard)d!));
        MenuBlocks.Register(TimelineBlock, (rect, d) => DrawTimeline(rect, (SessionDashboard)d!), d => TimelineBridge((SessionDashboard)d!), (_, _) => TimelineHeight + LineHeight);
        MenuBlocks.Register(DropsBlock, (rect, d) => DrawDrops(rect, (SessionDashboard)d!), d => DropsBridge((SessionDashboard)d!), (_, _) => BarsHeight);
        MenuBlocks.Register(ZonesBlock, (rect, d) => DrawZones(rect, (SessionDashboard)d!), d => ZonesBridge((SessionDashboard)d!), (_, _) => BarsHeight);
        foreach (var table in s_tables)
            MenuBlocks.Register(table.Block, (rect, d) => DrawTable(rect, (SessionDashboard)d!, table), d => TableBridge((SessionDashboard)d!, table), (d, _) => TableHeight((SessionDashboard)d!, table));
    }

    public static void Reset()
    {
        Current.Reset();
        SessionCharts.Release();
        s_texts.Clear();
    }

    // ---- Rows ----

    /// <summary>The Current session page's rows: the Sample data switch (dev builds), a hint while no session runs, then
    /// the dashboard.</summary>
    public static IEnumerable<MenuRow> CurrentRows()
    {
        yield return new ValueToggle("Sample data", () => ShowSample, on =>
        {
            ShowSample = on;
            Current.Reset();
        }) { VisibleWhen = () => SampleStats != null, Key = "toggle.sample" };
        yield return new BannerRow(() => ShowSample && SampleStats != null ? "Sample figures, generated to show every part of the page; not your session." : null);
        yield return new HintRow(() => SessionSettings.History.Value ? "No session yet. One starts when a character enters a zone." : "Session history is off.")
        {
            VisibleWhen = () => Sessions.Current == null && !(ShowSample && SampleStats != null),
        };
        foreach (var row in Rows(Current)) yield return row;
    }

    /// <summary>
    /// The dashboard's rows for a source: focus chips, tiles, the other figures, the chart, a cut session's note, the
    /// timeline, drops and zone time, factions, the lists and the filtered drops. Everything hides while the source has no
    /// figures.
    /// </summary>
    public static IEnumerable<MenuRow> Rows(SessionDashboard d)
    {
        Func<bool> any = () => d.Stats() != null;
        string k = d.Live ? "live" : "past";
        yield return new BlockRow(TimeBlock, d) { VisibleWhen = any, Key = $"{k}.time" };
        yield return new BlockRow(FocusBlock) { VisibleWhen = any, Key = $"{k}.focus" };
        yield return new BlockRow(TilesBlock, d) { VisibleWhen = any, Key = $"{k}.tiles" };
        yield return new HintRow(() => d.Stats() is { } s ? Made(d, "others", s, Others, FocusMask()) : string.Empty)
        {
            VisibleWhen = () => d.Stats() is { } s && Made(d, "others", s, Others, FocusMask()).Length > 0,
            Key = $"{k}.others",
        };
        yield return new HintRow(() => d.Stats() is { Cut: true } s ? $"Detail stops at the cut ({Clock(s.CutAt)}); totals are complete." : string.Empty)
        {
            VisibleWhen = () => d.Stats() is { Cut: true },
            Key = $"{k}.cut",
        };
        yield return new CardRow("Over the session", null, new BlockRow(ChartBlock, d) { Key = $"{k}.chart" })
        {
            VisibleWhen = () => d.Stats() is { Duration: >= 120d },
            Key = $"card.{k}.chart",
        };
        yield return new CardRow("Timeline", null, new BlockRow(TimelineBlock, d) { Key = $"{k}.timeline" }) { VisibleWhen = any, Key = $"card.{k}.timeline" };
        yield return new CardColumns(new CardRow("Drops", null, new BlockRow(DropsBlock, d) { Key = $"{k}.drops" }),
            new CardRow("Time by zone", null, new BlockRow(ZonesBlock, d) { Key = $"{k}.zones" })) { VisibleWhen = any, Key = $"cards.{k}.bars" };
        yield return new CardRow("Factions", null, new BlockRow(FactionsBlock, d) { Key = $"{k}.factions" })
        {
            VisibleWhen = () => d.Stats() is { Factions.Count: > 0 },
            Key = $"card.{k}.factions",
        };
        yield return new CardRow("Echoes", null, new BlockRow(EchoesBlock, d) { Key = $"{k}.echoes" }) { VisibleWhen = any, Key = $"card.{k}.echoes" };
        yield return new CardColumns(new CardRow("Deaths", null, new BlockRow(DeathsBlock, d) { Key = $"{k}.deaths" }),
            new CardRow("Picks", null, new BlockRow(PicksBlock, d) { Key = $"{k}.picks" })) { VisibleWhen = any, Key = $"cards.{k}.lists" };
        yield return new CardRow("Filtered drops", null, new BlockRow(FilteredBlock, d) { Key = $"{k}.filtered" }) { VisibleWhen = any, Key = $"card.{k}.filtered" };
    }

    /// <summary>The figures in focus, in tile order.</summary>
    public static IEnumerable<SessionMetric> Focused() => Metrics.Where(m => m.Focus.Value);

    /// <summary>A bit per figure in focus, so texts made from the focus are made again when it changes.</summary>
    public static int FocusMask()
    {
        int mask = 0;
        for (int i = 0; i < Metrics.Length; i++)
            if (Metrics[i].Focus.Value) mask |= 1 << i;
        return mask;
    }

    // ---- Texts made once per snapshot ----

    private static readonly Dictionary<(SessionDashboard?, string, int), (object? Stats, object Value)> s_texts = new();

    // A text (or lines) made once per snapshot; variant tells apart the forms of one text (a list folded or not).
    private static T Made<T>(SessionDashboard? d, string what, SessionStats s, Func<SessionStats, T> make, int variant = 0) where T : class
    {
        if (s_texts.TryGetValue((d, what, variant), out var cached) && ReferenceEquals(cached.Stats, s)) return (T)cached.Value;
        var value = make(s);
        s_texts[(d, what, variant)] = (s, value);
        return value;
    }

    // ---- Time ----

    private const float TimeHeight = 40f;

    // The session's length large, then where it went and whose it is: "1:32:10   58:20 out of town · 33:50 in town ·
    // offline, slot 1 · started 14:02". Not a figure to put in or out of focus.
    private static void DrawTime(Rect rect, SessionDashboard d)
    {
        if (Event.current.type != EventType.Repaint || d.Stats() is not { } s) return;
        string time = Made(d, "time", s, x => Clock(x.Duration));
        float w = TextCache.Width(time, Big) + 16f;
        SessionCharts.Paint(new Rect(rect.x, rect.y, w, 32f), time, Big);
        SessionCharts.Paint(new Rect(rect.x + w, rect.y + 8f, rect.width - w, 20f), Made(d, "time.d", s, TimeDetail), Widgets.Style(() => Theme.Hint, GUI.skin.label));
    }

    private static string TimeDetail(SessionStats s)
    {
        var parts = new List<string> { $"{Clock(s.OutOfTownSeconds)} out of town", $"{Clock(s.TownSeconds)} in town", s.Header.CharacterLabel };
        if (s.Header.Start != default) parts.Add($"started {s.Header.Start.LocalDateTime.ToString("HH:mm", s_inv)}");
        return string.Join("  ·  ", parts);
    }

    private static IEnumerable<string> TimeBridge(SessionDashboard d) => d.Stats() is { } s
        ? new[] { $"time value={RowText.Quote(Clock(s.Duration))} detail={RowText.Quote(TimeDetail(s))}" }
        : Array.Empty<string>();

    // ---- Focus chips ----

    private static float FocusHeight(float width) => FocusRows(width).Count * (RowLayout.ControlHeight + 6f) + 4f;

    // The chips in lines that fit the width, after the "Focus" label.
    private static List<List<(SessionMetric Metric, float Width)>> FocusRows(float width)
    {
        var rows = new List<List<(SessionMetric, float)>> { new() };
        float x = LabelWidth();
        foreach (var metric in Metrics)
        {
            float w = Controls.ButtonWidth(metric.Label);
            if (x + w > width && rows[^1].Count > 0)
            {
                rows.Add(new List<(SessionMetric, float)>());
                x = LabelWidth();
            }
            rows[^1].Add((metric, w));
            x += w + 6f;
        }
        return rows;
    }

    private static float LabelWidth() => TextCache.Width("Focus", Widgets.Style(() => Theme.Label, GUI.skin.label)) + 14f;

    // A chip per figure: filled while in focus; a click puts it in or out (the same setting as on History).
    private static void DrawFocus(Rect rect)
    {
        if (Event.current.type == EventType.Repaint) Widgets.Paint(new Rect(rect.x, rect.y, LabelWidth(), RowLayout.ControlHeight), "Focus", Widgets.Style(() => Theme.Label, GUI.skin.label));
        float y = rect.y;
        foreach (var row in FocusRows(rect.width))
        {
            float x = rect.x + LabelWidth();
            foreach (var (metric, w) in row)
            {
                if (Controls.Button(new Rect(x, y, w, RowLayout.ControlHeight), metric.Label, "focus." + metric.Key, metric.Focus.Value, metric.Focus.Value ? "In focus: click to move it to the line under the tiles" : "Click to show it as a tile and in the chart"))
                    SettingRows.Flip(metric.Focus, "session focus chip");
                x += w + 6f;
            }
            y += RowLayout.ControlHeight + 6f;
        }
    }

    private static IEnumerable<string> FocusBridge() => new[] { "focus " + string.Join(" ", Metrics.Select(m => $"{m.Key}={(m.Focus.Value ? "on" : "off")}")) };

    // ---- Tiles ----

    private static GUIStyle? s_big, s_bigOf;

    // The tile value's style: the page title's, larger, made again when the theme rebuilds its styles.
    private static GUIStyle Big
    {
        get
        {
            var title = Widgets.Style(() => Theme.PageTitle, GUI.skin.label);
            if (!ReferenceEquals(s_bigOf, title))
            {
                s_bigOf = title;
                s_big = new GUIStyle(title) { fontSize = title.fontSize + 5, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            }
            return s_big!;
        }
    }

    // A tile per focused figure.
    private static int TileCount() => Focused().Count();

    private static int Columns(float width) => Mathf.Max(1, (int)((width + TileGap) / (TileWidth + TileGap)));

    private static float TilesHeight(SessionDashboard d, float width)
    {
        if (TileCount() == 0) return 0f;
        int rows = Mathf.CeilToInt(TileCount() / (float)Columns(width));
        return rows * (TileHeight + TileGap);
    }

    private static void DrawTiles(Rect rect, SessionDashboard d)
    {
        if (Event.current.type != EventType.Repaint || d.Stats() is not { } s || TileCount() == 0) return;
        int columns = Columns(rect.width), count = TileCount();
        // Tiles stretch to fill the row, so the grid's right edge lines up with the cards under it.
        float w = (rect.width - TileGap * (Mathf.Min(columns, count) - 1)) / Mathf.Min(columns, count);
        int i = 0;
        foreach (var metric in Metrics)
        {
            if (!metric.Focus.Value) continue;
            Tile(TileRect(rect, i++, columns, w), metric.Label, Made(d, "tile." + metric.Key, s, metric.Value), Made(d, "tile." + metric.Key + ".d", s, x => metric.Detail(x, d.Live)),
                Readable(MetricColour(metric.Key, s)));
        }
    }

    private static Rect TileRect(Rect rect, int i, int columns, float w) =>
        new(rect.x + (i % columns) * (w + TileGap), rect.y + (i / columns) * (TileHeight + TileGap), w, TileHeight);

    // A card with the figure's colour as a thin edge on its left, its label, value and detail.
    private static void Tile(Rect r, string label, string value, string detail, Color colour)
    {
        if (Widgets.Themed) Theme.Card.Draw(r, GUIContent.none, false, false, false, false);
        SessionCharts.Primitives++;
        SessionCharts.Fill(new Rect(r.x + 1f, r.y + 8f, 3f, r.height - 16f), colour);
        float pad = 12f;
        SessionCharts.Paint(new Rect(r.x + pad, r.y + 8f, r.width - pad * 2f, 14f), label.ToUpperInvariant(), Widgets.Style(() => Theme.Heading, GUI.skin.label));
        SessionCharts.Paint(new Rect(r.x + pad, r.y + 22f, r.width - pad * 2f, 28f), value, Big);
        SessionCharts.Paint(new Rect(r.x + pad, r.y + 50f, r.width - pad * 2f, 16f), detail, Widgets.Style(() => Theme.Hint, GUI.skin.label));
    }

    private static IEnumerable<string> TilesBridge(SessionDashboard d)
    {
        if (d.Stats() is not { } s) return Array.Empty<string>();
        var lines = new List<string>();
        lines.AddRange(Focused().Select(m => $"tile {m.Key} value={RowText.Quote(m.Value(s))} detail={RowText.Quote(m.Detail(s, d.Live))}"));
        return lines;
    }

    // "XP 259k · Gold 15k (spent 1.5k) · …": the figures not in focus, muted under the tiles.
    private static string Others(SessionStats s) =>
        string.Join("  ·  ", Metrics.Where(m => !m.Focus.Value).Select(m => $"{m.Label} {m.Value(s)}"));

    // ---- Tile details ----

    private static (long Gained, long Spent) Favour(SessionStats s)
    {
        long gained = 0, spent = 0;
        foreach (var f in s.Factions)
        {
            if (f.IsAmber) continue;
            gained += f.FavourGained;
            spent += f.FavourSpent;
        }
        return (gained, spent);
    }

    private static FactionFigures? Amber(SessionStats s) => s.Factions.FirstOrDefault(f => f.IsAmber);

    private static string FavourDetail(SessionStats s, bool live)
    {
        var (gained, spent) = Favour(s);
        double? rate = s.Duration >= SessionStats.RateAfter && gained > 0 ? gained * 3600d / s.Duration : null;
        return Join(rate is double r ? $"{Short(r)}/h" : null, spent > 0 ? $"used {Short(spent)}" : null) ?? "none yet";
    }

    // The faction most reputation went to (else the highest rank): its rank, progress and time to the next.
    private static string ReputationDetail(SessionStats s, bool live)
    {
        // Gains first, then the faction the character is in (the Woven last: its reputation comes from memory amber).
        var top = s.Factions.OrderByDescending(f => f.ReputationGained).ThenByDescending(f => f.Member && !f.IsAmber).ThenByDescending(f => f.Member).ThenByDescending(f => f.Rank ?? 0).FirstOrDefault();
        if (top == null) return "no faction read";
        var parts = new List<string> { top.Name };
        if (top.Rank is int rank) parts.Add($"rank {rank}{(top.PercentToNext is double p ? $" ({p.ToString("0", s_inv)}%)" : string.Empty)}");
        if (live && top.TimeToNext is double next) parts.Add($"{Clock(next)} to next");
        return string.Join(" · ", parts);
    }

    private static string AmberDetail(SessionStats s, bool live)
    {
        if (Amber(s) is not { } amber) return "none yet";
        return Join(amber.FavourRates.Session is double r && r > 0d ? $"{Short(r)}/h" : null, amber.FavourSpent > 0 ? $"used {Short(amber.FavourSpent)}" : null) ?? "none yet";
    }

    private static string EchoDetail(SessionStats s, bool live) =>
        Join(s.Echo.AverageClear is double c ? $"clear {Clock(c)} avg" : null, s.Echo.Left > 0 ? $"{s.Echo.Left} left" : null, s.Echo.PerHour is double r && s.Echo.Completed > 0 ? $"{r.ToString("0.#", s_inv)}/h" : null)
        ?? "none yet";

    private static string LevelDetail(SessionStats s, bool live)
    {
        var l = s.Level;
        if (l.CurrentLevel >= SessionStats.MaxLevel) return l.Gained > 0 ? $"+{l.Gained} · max level" : "max level";
        return Join(l.Gained > 0 ? $"+{l.Gained}" : null, live && l.Percent is double p ? $"{p.ToString("0", s_inv)}%" : null, live && l.TimeToNext is double t ? $"{Clock(t)} to next" : null)
            ?? (l.StartLevel is int start ? $"from {start}" : "not read");
    }

    private static string DropDetail(SessionStats s, bool live) =>
        Join(RateText(s.DropRates, live), s.FilterRead ? $"{Whole(s.Totals.DropsShown)} shown" : null) ?? "none yet";

    private static string DeathDetail(SessionStats s, bool live) =>
        Join(s.Deaths.Count > 0 ? s.Deaths[^1].Killer : null, RateText(s.DeathRates, live)) ?? "none";

    // "12k/h", with the last 10 minutes' rate when live: "12k/h · 15k recent".
    private static string? RateText(Rates r, bool live) =>
        r.Session is double rate && rate > 0d ? $"{Short(rate)}/h" + (live && r.Recent is double recent ? $" · {Short(recent)} recent" : string.Empty) : null;

    private static string? Join(params string?[] parts)
    {
        var shown = parts.Where(p => !string.IsNullOrEmpty(p)).ToList();
        return shown.Count > 0 ? string.Join(" · ", shown) : null;
    }

    // ---- Chart ----

    // The figure the chart shows: the chosen one while it stays in focus with a series, else the first that is.
    private static SessionMetric? ChartMetric(SessionDashboard d)
    {
        SessionMetric? first = null;
        foreach (var m in Metrics)
        {
            if (!m.Focus.Value || m.Chart == null) continue;
            if (m.Key == d.ChartMetric) return m;
            first ??= m;
        }
        return first;
    }

    // The series the chart shows, or null when there is nothing to plot (the block then keeps one line for its note).
    private static ChartSeries? ShownSeries(SessionDashboard d, SessionStats s) =>
        ChartMetric(d) is { Chart: { } key } && s.Charts.TryGetValue(key, out var series) && series.Max > 0d ? series : null;

    private static float ChartBlockHeight(SessionDashboard d) =>
        RowLayout.ControlHeight + 10f + (d.Stats() is { } s && ShownSeries(d, s) != null ? ChartHeight + LineHeight : LineHeight);

    private static void DrawChart(Rect rect, SessionDashboard d)
    {
        if (d.Stats() is not { } s) return;
        var shown = ChartMetric(d);
        // The focused figures as small switches; the shown one filled.
        float x = rect.x;
        foreach (var m in Metrics)
        {
            if (!m.Focus.Value || m.Chart == null) continue;
            float w = Controls.ButtonWidth(m.Label);
            if (Controls.Button(new Rect(x, rect.y, w, RowLayout.ControlHeight), m.Label, $"{(d.Live ? "live" : "past")}.chart.{m.Key}", m == shown)) d.ChartMetric = m.Key;
            x += w + 6f;
        }
        if (Event.current.type != EventType.Repaint) return;
        var area = new Rect(rect.x, rect.y + RowLayout.ControlHeight + 10f, rect.width, ChartHeight);
        GUIStyle hint = Widgets.Style(() => Theme.Hint, GUI.skin.label);
        if (shown == null || !s.Charts.TryGetValue(shown.Chart!, out var series) || series.Max <= 0d)
        {
            SessionCharts.Paint(new Rect(area.x, area.y, area.width, LineHeight), shown == null ? "Put a figure in focus to chart it." : $"No {shown.Label.ToLowerInvariant()} yet.", hint);
            return;
        }
        Color colour = Readable(MetricColour(shown.Key, s));
        SessionCharts.Area(area, d.Chart, HashCode.Combine(s.Id, s.Version, shown.Key), series.Points, s.Duration, series.Max, colour, s.Gaps);
        // The scale's top at the top left, over the chart.
        SessionCharts.Paint(new Rect(area.x + 6f, area.y + 2f, area.width, LineHeight), Made(d, "chart.max." + shown.Key, s, _ => $"{shown.Label} {Whole((long)series.Max)}"), hint);
        AxisLabels(new Rect(area.x, area.yMax + 2f, area.width, LineHeight), d, s);
    }

    /// <summary>
    /// A colour per figure as the game draws it, the same in tiles, charts and tables: favour as the faction's favour gem
    /// and reputation as its rank bar (the faction most was gained with, else the one the character is in), memory amber, gold, XP and
    /// corruption from their roles, drops in the magic item blue, deaths in the high danger red, echoes in the echo
    /// zone colour. Roles, so the Colour vision palettes reach them.
    /// </summary>
    internal static Color MetricColour(string key, SessionStats? s = null) => key switch
    {
        "favour" => s != null && TopFaction(s, f => f.FavourGained) is { } favour ? SessionStats.FavourColour(favour.Faction) ?? Theme.Accent : Theme.Accent,
        "rep" => s != null && TopFaction(s, f => f.ReputationGained) is { } rep ? SessionStats.ReputationColour(rep.Faction) ?? Theme.Accent : Theme.Accent,
        "amber" => ColourRoles.Stat("amber"),
        "gold" => ColourRoles.Stat("gold"),
        "xp" => ColourRoles.Stat("xp"),
        "echoes" => ColourRoles.Zone("echo"),
        "drops" => ColourRoles.ItemNamed("magic"),
        "deaths" => ColourRoles.Danger(2),
        _ => Theme.Accent,
    };

    // The faction (not the Woven) most of a figure went to, else the one the character is in.
    private static FactionFigures? TopFaction(SessionStats s, Func<FactionFigures, long> amount) =>
        s.Factions.Where(f => !f.IsAmber).OrderByDescending(amount).ThenByDescending(f => f.Member).FirstOrDefault();

    // A faction's favour colour; the Woven's favour is memory amber, from its role.
    private static Color FavourColour(FactionFigures f) => f.IsAmber ? ColourRoles.Stat("amber") : SessionStats.FavourColour(f.Faction) ?? Theme.Accent;

    private static void DrawTimeline(Rect rect, SessionDashboard d)
    {
        if (Event.current.type != EventType.Repaint || d.Stats() is not { } s) return;
        var area = new Rect(rect.x, rect.y, rect.width, TimelineHeight);
        SessionCharts.Timeline(area, d.Timeline, s);
        AxisLabels(new Rect(area.x, area.yMax + 2f, area.width, LineHeight), d, s);
    }

    // "0:00" at the start and the duration at the end, muted.
    private static void AxisLabels(Rect r, SessionDashboard d, SessionStats s)
    {
        GUIStyle hint = Widgets.Style(() => Theme.Hint, GUI.skin.label);
        SessionCharts.Paint(r, "0:00", hint);
        string end = Made(d, "axisEnd", s, x => Clock(x.Duration));
        float w = TextCache.Width(end, hint) + 8f;
        SessionCharts.Paint(new Rect(r.xMax - w + 6f, r.y, w, r.height), end, hint);
    }

    private static IEnumerable<string> ChartBridge(SessionDashboard d)
    {
        if (d.Stats() is not { } s) return Array.Empty<string>();
        var shown = ChartMetric(d);
        string series = shown != null && s.Charts.TryGetValue(shown.Chart!, out var c) ? $"points={c.Points.Count} max={c.Max.ToString("0", s_inv)}" : "none";
        return new[] { $"chart {shown?.Key ?? "none"} {series} gaps={s.Gaps.Count}; switches " + string.Join(" ", Focused().Where(m => m.Chart != null).Select(m => m.Key)) };
    }

    private static IEnumerable<string> TimelineBridge(SessionDashboard d)
    {
        if (d.Stats() is not { } s) return Array.Empty<string>();
        float width = Mathf.Max(100f, PageView.BlockWidth - 40f);
        var segments = s.Timeline.Drawn(s.Duration, width, SessionStats.DrawnSegments);
        return new[]
        {
            $"segments raw={s.Timeline.Segments.Count} drawn={segments.Count} marks raw={s.Timeline.Marks.Count} drawn={s.Timeline.DrawnMarks(s.Duration, width, SessionStats.DrawnMarks).Count} gaps={s.Gaps.Count}",
            "zones " + string.Join(" ", segments.Select(g => $"{g.Kind}:{Clock(g.Start)}-{Clock(g.End)}")),
        };
    }

    // ---- Drops and zone time ----

    private static readonly float[] s_values = new float[8];
    private static readonly Color[] s_colours = new Color[8];
    private const float BarsHeight = BarHeight + 8f + LineHeight * 3f;

    private static void DrawDrops(Rect rect, SessionDashboard d)
    {
        if (Event.current.type != EventType.Repaint || d.Stats() is not { } s) return;
        var t = s.Totals;
        int n = 0;
        foreach (string rarity in SessionTotals.DropRarities)
        {
            s_values[n] = t.DropsOf(rarity);
            s_colours[n++] = ColourRoles.ItemNamed(rarity);
        }
        s_values[n] = t.DropsOther;
        s_colours[n++] = Theme.Values.Dim;
        SessionCharts.StackedBar(new Rect(rect.x, rect.y, rect.width, BarHeight), s_values, s_colours, n);
        Legend(new Rect(rect.x, rect.y + BarHeight + 6f, rect.width, LineHeight * 3f), Made(d, "drops", s, DropsLegend));
    }

    private static void DrawZones(Rect rect, SessionDashboard d)
    {
        if (Event.current.type != EventType.Repaint || d.Stats() is not { } s) return;
        int n = 0;
        foreach (string kind in SessionTotals.ZoneKinds)
        {
            s_values[n] = (float)s.Totals.ZoneSeconds.GetValueOrDefault(kind);
            s_colours[n++] = ColourRoles.Zone(kind);
        }
        SessionCharts.StackedBar(new Rect(rect.x, rect.y, rect.width, BarHeight), s_values, s_colours, n);
        Legend(new Rect(rect.x, rect.y + BarHeight + 6f, rect.width, LineHeight * 2f), Made(d, "zones", s, ZonesLegend));
    }

    // Parts after a swatch of their colour, the text in the label colour so dark roles stay readable; wrapping to a
    // second line, and parts that don't fit there left out.
    // A legend part that starts a line of its own, muted (a note under the swatches).
    private const string NoteLine = "note";

    private static void Legend(Rect r, List<(string Text, string? Role)> parts)
    {
        GUIStyle label = Widgets.Style(() => Theme.Label, GUI.skin.label), hint = Widgets.Style(() => Theme.Hint, GUI.skin.label);
        float x = r.x, y = r.y;
        foreach (var (text, part) in parts)
        {
            bool note = part == NoteLine;
            string? role = note ? null : part;
            GUIStyle style = note ? hint : label;
            float w = TextCache.Width(text, style) + (role != null ? 14f : 0f);
            if ((x + w > r.xMax || note) && x > r.x)
            {
                x = r.x;
                y += LineHeight;
                if (y + LineHeight > r.yMax + 1f) break;
            }
            if (role != null)
            {
                SessionCharts.Fill(new Rect(x, Mathf.Round(y + LineHeight / 2f - 5f), 10f, 10f), RoleColour(role));
                x += 14f;
            }
            // Room to spare: the muted style wraps, and a box of the exact width would push its last word to a hidden line.
            SessionCharts.Paint(new Rect(x, y, Mathf.Max(w + 24f, r.xMax - x), LineHeight), text, style);
            x += TextCache.Width(text, style) + 16f;
        }
    }

    // Swatches keep their role's name, so a palette change shows at once.
    private static Color RoleColour(string role) => role.StartsWith("zone.", StringComparison.Ordinal) ? ColourRoles.Zone(role[5..])
        : role == "other" ? Theme.Values.Dim : ColourRoles.ItemNamed(role);

    private static List<(string, string?)> DropsLegend(SessionStats s)
    {
        var t = s.Totals;
        if (t.DropCount == 0) return new List<(string, string?)> { ("No drops yet.", null) };
        var parts = SessionTotals.DropRarities.Where(r => t.DropsOf(r) > 0).Select(r => ($"{Whole(t.DropsOf(r))} {Capital(r)}", (string?)r)).ToList();
        if (t.DropsOther > 0) parts.Add(($"{Whole(t.DropsOther)} other", "other"));
        if (s.FilterRead) parts.Add(($"Loot filter showed {Whole(t.DropsShown)} of {Whole(t.DropCount)}{Share(t.DropsShown, t.DropCount)}", NoteLine));
        return parts;
    }

    private static List<(string, string?)> ZonesLegend(SessionStats s)
    {
        var parts = SessionTotals.ZoneKinds.Where(k => s.Totals.ZoneSeconds.GetValueOrDefault(k) >= 1d)
            .Select(k => ($"{Capital(k)} {Clock(s.Totals.ZoneSeconds[k])}", (string?)("zone." + k))).ToList();
        return parts.Count > 0 ? parts : new List<(string, string?)> { ("No zones yet.", null) };
    }

    private static IEnumerable<string> DropsBridge(SessionDashboard d) => d.Stats() is { } s
        ? new[] { "drops " + string.Join(" ", SessionTotals.DropRarities.Select(r => $"{r}={s.Totals.DropsOf(r)}")) + $" other={s.Totals.DropsOther} shown={s.Totals.DropsShown}" }
        : Array.Empty<string>();

    private static IEnumerable<string> ZonesBridge(SessionDashboard d) => d.Stats() is { } s
        ? new[] { "time " + string.Join(" ", SessionTotals.ZoneKinds.Select(z => $"{z}={s.Totals.ZoneSeconds.GetValueOrDefault(z).ToString("0", s_inv)}")) }
        : Array.Empty<string>();

    // ---- Tables: factions, echoes, deaths, picks, filtered drops ----

    /// <summary>A table cell: its text, its colour (null for the label colour), a bar's fill (0–1) and colour drawn before
    /// the text.</summary>
    private readonly record struct Cell(string Text, Color? Colour = null, float? Bar = null, Color? BarColour = null)
    {
        public static readonly Cell Empty = new("–", Muted);
    }

    /// <summary>A column: heading, share of the width, numbers aligned right.</summary>
    private sealed record Column(string Header, float Weight, bool Right = false);

    /// <summary>A table block: columns, the row count and a row's cells (index 0 is the newest), its empty text, and its
    /// fold (null: never folds).</summary>
    private sealed record TableKind(string Block, string Empty, Column[] Columns, Func<SessionStats, int> Count, Func<SessionStats, int, Cell[]> Row,
        Func<SessionDashboard, bool>? All = null, Action<SessionDashboard, bool>? SetAll = null);

    private const float RowHeight = 22f, HeaderHeight = 20f, CellPad = 10f, BarWidth = 44f;

    // A marker for "muted": the hint colour, read when drawn.
    private static readonly Color Muted = new(0f, 0f, 0f, 0.001f);

    private static readonly TableKind[] s_tables =
    {
        new(FactionsBlock, "No faction read.",
            new Column[] { new("Faction", 1.4f), new("Rank", 1.3f), new("Reputation", 1f, true), new("Favour", 1f, true), new("Used", 0.7f, true), new("To next rank", 1f, true), new("Rank-ups", 0.8f, true) },
            s => s.Factions.Count, (s, i) => FactionRow(s, s.Factions[i])),
        new(EchoesBlock, "No echoes yet.",
            new Column[]
            {
                new("Echo", 1.1f), new("Clear", 0.8f, true), new("Total", 0.8f, true), new("Favour", 0.8f, true), new("Reputation", 1f, true), new("Amber", 0.8f, true),
                new("Drops", 0.7f, true), new("Modifier", 1f), new("Stability", 0.8f, true), new("Reward", 1.3f), new("Corruption", 1f, true),
            },
            s => s.Runs.Count, (s, i) => EchoRow(s, s.Runs[s.Runs.Count - 1 - i]), d => d.AllEchoes, (d, v) => d.AllEchoes = v),
        new(DeathsBlock, "No deaths.", new Column[] { new("Time", 0.7f), new("Zone", 1f), new("Killer", 1.4f), new("Damage", 1f) },
            s => s.Deaths.Count, (s, i) => DeathRow(s.Deaths[s.Deaths.Count - 1 - i]), d => d.AllDeaths, (d, v) => d.AllDeaths = v),
        new(PicksBlock, "No picks yet.", new Column[] { new("Time", 0.7f), new("Modifier", 1f), new("Stability", 0.8f, true), new("Reward", 1.3f), new("Corruption", 0.9f, true) },
            s => s.Picks.Count, (s, i) => PickRow(s.Picks[s.Picks.Count - 1 - i]), d => d.AllPicks, (d, v) => d.AllPicks = v),
        new(FilteredBlock, string.Empty, new Column[] { new("Time", 0.6f), new("Item", 2f), new("Base", 1.6f), new("Rarity", 0.8f) },
            s => s.ShownDrops.Count, (s, i) => DropRow(s.ShownDrops[s.ShownDrops.Count - 1 - i]), d => d.AllDrops, (d, v) => d.AllDrops = v),
    };

    private static Cell Num(long value, Color colour) => value > 0 ? new Cell(Whole(value), colour) : Cell.Empty;

    // Name, rank with a bar of the progress to the next, gains in their figures' colours; the Woven's favour is memory amber.
    private static Cell[] FactionRow(SessionStats s, FactionFigures f) => new[]
    {
        new Cell(f.Name, f.Member ? SessionStats.ReputationColour(f.Faction) : Muted),
        f.Rank is int rank ? new Cell(f.PercentToNext is double p ? $"{rank}  ({p.ToString("0", s_inv)}%)" : rank.ToString(s_inv), null, f.PercentToNext is double q ? (float)(q / 100d) : 1f,
            SessionStats.ReputationColour(f.Faction)) : Cell.Empty,
        Num(f.ReputationGained, SessionStats.ReputationColour(f.Faction) ?? Theme.Accent),
        Num(f.FavourGained, FavourColour(f)),
        f.FavourSpent > 0 ? new Cell(Whole(f.FavourSpent), Muted) : Cell.Empty,
        s.Live && f.TimeToNext is double next ? new Cell(Clock(next)) : Cell.Empty,
        f.RankUps > 0 ? new Cell(f.RankUps.ToString(s_inv), Theme.Accent) : Cell.Empty,
    };

    // The best clear starred in the accent; a run left before completion "left" in the danger colour.
    private static Cell[] EchoRow(SessionStats s, EchoRun run)
    {
        double? best = s.Echo.BestClear;
        long favour = run.Favour.Where(f => f.Key != SessionStats.AmberFaction).Sum(f => f.Value), amber = run.Favour.GetValueOrDefault(SessionStats.AmberFaction);
        // A run's favour and reputation in the colour of the faction most of it went to.
        string? guild = run.Favour.Where(f => f.Key != SessionStats.AmberFaction).OrderByDescending(f => f.Value).Select(f => f.Key).FirstOrDefault()
            ?? run.Reputation.OrderByDescending(f => f.Value).Select(f => f.Key).FirstOrDefault();
        Color favourColour = guild != null ? SessionStats.FavourColour(guild) ?? Theme.Accent : Theme.Accent;
        Color repColour = guild != null ? SessionStats.ReputationColour(guild) ?? Theme.Accent : Theme.Accent;
        bool open = run.End == null && s.Live;
        return new[]
        {
            new Cell(run.Name + (open ? " (now)" : string.Empty), open ? Theme.Accent : null),
            run.Clear is double c ? new Cell((best is double b && Math.Abs(b - c) < 0.01d ? "★ " : string.Empty) + Clock(c), best is double b2 && Math.Abs(b2 - c) < 0.01d ? Theme.Accent : null)
                : new Cell(open ? "…" : "left", open ? Muted : ColourRoles.Danger(2)),
            new Cell(Clock(run.Total(s.Duration)), Muted),
            Num(favour, favourColour), Num(run.Reputation.Values.Sum(), repColour), Num(amber, ColourRoles.Stat("amber")), Num(run.Drops, ColourRoles.ItemNamed("magic")),
            run.Modifier != null ? new Cell(run.Modifier) : run.HasPick ? new Cell("none", Muted) : Cell.Empty,
            run.Stability is long st ? new Cell("+" + Whole(st)) : Cell.Empty,
            run.Reward != null ? new Cell(run.Reward, RewardColour(run.Reward)) : Cell.Empty,
            run.Corruption is long co ? new Cell(Whole(co), co > 0 ? ColourRoles.Stat("corruption") : Muted) : Cell.Empty,
        };
    }

    private static Cell[] DeathRow(DeathEntry death) => new[]
    {
        new Cell(Clock(death.T), Muted), new Cell(death.Zone ?? "unknown"), new Cell(death.Killer ?? "not read", death.Killer != null ? ColourRoles.Danger(2) : Muted),
        death.Types is { Length: > 0 } types ? new Cell(string.Join(", ", types), Muted) : Cell.Empty,
    };

    private static Cell[] PickRow(PickEntry pick) => new[]
    {
        new Cell(Clock(pick.T), Muted), pick.Modifier != null ? new Cell(pick.Modifier) : new Cell("none", Muted),
        pick.Stability is long st ? new Cell("+" + Whole(st)) : Cell.Empty,
        pick.Reward != null ? new Cell(pick.Reward, RewardColour(pick.Reward)) : Cell.Empty,
        pick.Corruption is long co ? new Cell(Whole(co), co > 0 ? ColourRoles.Stat("corruption") : Muted) : Cell.Empty,
    };

    private static Cell[] DropRow(ShownDrop drop)
    {
        string name = drop.Name ?? drop.Base ?? "unknown item";
        return new[]
        {
            new Cell(Clock(drop.T), Muted), new Cell(name, ColourRoles.ItemNamed(drop.Rarity)), drop.Base is { } b && b != name ? new Cell(b, Muted) : Cell.Empty,
            new Cell(drop.Rarity, ColourRoles.ItemNamed(drop.Rarity)),
        };
    }

    // A reward naming an item rarity in that rarity's colour.
    private static Color? RewardColour(string reward)
    {
        foreach (string rarity in SessionTotals.DropRarities)
            if (reward.IndexOf(rarity, StringComparison.OrdinalIgnoreCase) >= 0 && rarity != "normal") return ColourRoles.ItemNamed(rarity);
        return null;
    }

    private static string EmptyText(SessionStats s, TableKind table) => table.Block == FilteredBlock
        ? s.FilterRead ? "Nothing the loot filter shows has dropped yet." : "No drops read with a loot filter yet (none dropped, or no filter active)."
        : table.Empty;

    private static bool Folds(TableKind table) => table.All != null;

    // The rows a table shows (the ten newest, or all when unfolded), made once per snapshot.
    private static List<Cell[]> TableRows(SessionDashboard d, SessionStats s, TableKind table)
    {
        bool all = !Folds(table) || table.All!(d);
        return Made(d, table.Block, s, x =>
        {
            int count = table.Count(x), shown = all ? count : Math.Min(count, Folded);
            var rows = new List<Cell[]>(shown);
            for (int i = 0; i < shown; i++) rows.Add(table.Row(x, i));
            return rows;
        }, all ? 1 : 0);
    }

    private static float TableHeight(SessionDashboard d, TableKind table)
    {
        if (d.Stats() is not { } s) return 0f;
        int count = table.Count(s);
        if (count == 0) return LineHeight;
        float height = HeaderHeight + 4f + TableRows(d, s, table).Count * RowHeight;
        if (Folds(table) && count > Folded) height += RowLayout.ControlHeight + 8f;
        return height;
    }

    private static GUIStyle? s_cellLeft, s_cellRight, s_headLeft, s_headRight, s_cellOf;

    // Cell and heading styles from the theme's label and heading, clipped, left and right aligned; made again when the
    // theme rebuilds its styles.
    private static void TableStyles()
    {
        var label = Widgets.Style(() => Theme.Label, GUI.skin.label);
        if (ReferenceEquals(s_cellOf, label)) return;
        s_cellOf = label;
        s_cellLeft = new GUIStyle(label) { clipping = TextClipping.Clip, alignment = TextAnchor.MiddleLeft };
        s_cellRight = new GUIStyle(s_cellLeft) { alignment = TextAnchor.MiddleRight };
        var heading = Widgets.Style(() => Theme.Heading, GUI.skin.label);
        s_headLeft = new GUIStyle(heading) { clipping = TextClipping.Clip, alignment = TextAnchor.MiddleLeft };
        // A right-aligned heading wider than its numbers runs on to the left instead of losing its first letters.
        s_headRight = new GUIStyle(s_headLeft) { alignment = TextAnchor.MiddleRight, clipping = TextClipping.Overflow };
    }

    private static void DrawTable(Rect rect, SessionDashboard d, TableKind table)
    {
        if (d.Stats() is not { } s) return;
        int count = table.Count(s);
        if (count == 0)
        {
            if (Event.current.type == EventType.Repaint) SessionCharts.Paint(new Rect(rect.x, rect.y, rect.width, LineHeight), EmptyText(s, table), Widgets.Style(() => Theme.Hint, GUI.skin.label));
            return;
        }
        var rows = TableRows(d, s, table);
        if (Event.current.type == EventType.Repaint)
        {
            TableStyles();
            float total = 0f;
            foreach (var column in table.Columns) total += column.Weight;
            float unit = rect.width / total, y = rect.y;
            // Headings, small caps, over a divider.
            float x = rect.x;
            foreach (var column in table.Columns)
            {
                float w = column.Weight * unit;
                SessionCharts.Paint(new Rect(x + (column.Right ? 0f : 0f), y, w - CellPad, HeaderHeight), column.Header.ToUpperInvariant(), column.Right ? s_headRight! : s_headLeft!);
                x += w;
            }
            y += HeaderHeight;
            SessionCharts.Fill(new Rect(rect.x, y + 1f, rect.width, 1f), Theme.DividerColor);
            y += 4f;
            var view = PageView.InView;
            // Muted cells halfway between the dim and the text colour, so they recede but stay readable.
            Color label = s_cellLeft!.normal.textColor, hint = Widgets.Themed ? Color.Lerp(Theme.Values.Dim, Theme.Text, 0.45f) : Color.gray;
            for (int r = 0; r < rows.Count; r++, y += RowHeight)
            {
                // Rows out of the page's view aren't drawn (a table shown in full can be long).
                if (y + RowHeight < view.y || y > view.yMax) continue;
                if (r % 2 == 1) SessionCharts.Fill(new Rect(rect.x - 4f, y, rect.width + 8f, RowHeight), new Color(1f, 1f, 1f, 0.025f));
                x = rect.x;
                for (int c = 0; c < table.Columns.Length && c < rows[r].Length; c++)
                {
                    var column = table.Columns[c];
                    var cell = rows[r][c];
                    float w = column.Weight * unit;
                    var area = new Rect(x, y, w - CellPad, RowHeight);
                    if (cell.Bar is float bar)
                    {
                        var track = new Rect(area.x, Mathf.Round(area.center.y - 3f), BarWidth, 6f);
                        SessionCharts.Fill(track, Theme.Values.Control);
                        SessionCharts.Fill(new Rect(track.x, track.y, Mathf.Round(BarWidth * Mathf.Clamp01(bar)), track.height), Readable(cell.BarColour ?? Theme.Accent));
                        area = new Rect(area.x + BarWidth + 8f, area.y, area.width - BarWidth - 8f, area.height);
                    }
                    var style = column.Right ? s_cellRight! : s_cellLeft!;
                    style.normal.textColor = cell.Colour is { } colour ? (colour == Muted ? hint : Readable(colour)) : label;
                    CellText(area, cell.Text, style);
                    x += w;
                }
            }
            DrawExpanded();
            s_cellLeft.normal.textColor = s_cellRight!.normal.textColor = label;
        }
        if (!Folds(table) || count <= Folded) return;
        bool all = table.All!(d);
        string text = Made(d, table.Block, s, _ => all ? "Show fewer" : $"Show all ({Whole(count)})", all ? 3 : 2);
        var button = new Rect(rect.x, rect.y + HeaderHeight + 4f + rows.Count * RowHeight + 8f, Controls.ButtonWidth(text), RowLayout.ControlHeight);
        if (Controls.Button(button, text, $"{(d.Live ? "live" : "past")}.{table.Block}.all")) table.SetAll!(d, !all);
    }

    // A hovered cell whose text doesn't fit, shown in full over its neighbours once the table's rows are drawn.
    private static (Rect Area, string Text, GUIStyle Style, Color Colour)? s_expanded;

    // A cell's text, clipped to its column. Hovered while it doesn't fit, it is shown in full above the row instead
    // (IMGUI text snaps to whole pixels, so scrolling it can't be smooth).
    private static void CellText(Rect area, string text, GUIStyle style)
    {
        SessionCharts.Paint(area, text, style);
        float full = TextCache.Width(text, style);
        if (full - area.width > 1f && area.Contains(Event.current.mousePosition))
        {
            // A right-aligned cell grows to the left, a left-aligned one to the right.
            float x = style.alignment == TextAnchor.MiddleRight ? area.xMax - full : area.x;
            s_expanded = (new Rect(x - 6f, area.y, full + 12f, area.height), text, style, style.normal.textColor);
        }
    }

    // The expanded cell over the table: a card-coloured box with an edge, and the full text.
    private static void DrawExpanded()
    {
        if (s_expanded is not { } e) return;
        s_expanded = null;
        SessionCharts.Fill(e.Area, Theme.Values.Card);
        SessionCharts.Fill(new Rect(e.Area.x, e.Area.yMax - 1f, e.Area.width, 1f), Theme.DividerColor);
        var anchor = e.Style.alignment;
        var colour = e.Style.normal.textColor;
        e.Style.alignment = TextAnchor.MiddleLeft;
        e.Style.normal.textColor = e.Colour;
        SessionCharts.Paint(new Rect(e.Area.x + 6f, e.Area.y, e.Area.width - 6f, e.Area.height), e.Text, e.Style);
        e.Style.alignment = anchor;
        e.Style.normal.textColor = colour;
    }

    // A game or role colour on a dark card: brightened in its own hue (value raised, saturation capped) so it reads; the
    // faction colours are deep, and mixing in white or chasing a lightness target would turn reds orange and purples grey.
    internal static Color Readable(Color colour)
    {
        if (Luminance(colour) >= 0.5f) return colour;
        Color.RGBToHSV(colour, out float h, out float sat, out float v);
        return Color.HSVToRGB(h, Mathf.Min(sat, 0.68f), Mathf.Max(v, 0.92f));
    }

    private static float Luminance(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

    private static IEnumerable<string> TableBridge(SessionDashboard d, TableKind table)
    {
        if (d.Stats() is not { } s) return Array.Empty<string>();
        if (table.Count(s) == 0) return new[] { EmptyText(s, table) };
        var lines = new List<string> { string.Join(" | ", table.Columns.Select(c => c.Header)) };
        lines.AddRange(TableRows(d, s, table).Select(row => string.Join(" | ", row.Select(c => c.Text))));
        if (Folds(table) && table.Count(s) > Folded) lines.Add($"button \"{(table.All!(d) ? "Show fewer" : $"Show all ({table.Count(s)})")}\"");
        return lines;
    }

    // ---- Words ----

    internal static string Whole(long value) => value.ToString("N0", s_inv);

    /// <summary>"12,400 per hour", or empty when not shown yet.</summary>
    internal static string Rate(double? perHour) => perHour is double r ? $"{Whole((long)Math.Round(r))} per hour" : string.Empty;

    /// <summary>"1:02:03", "4:05" under an hour, "-" when unknown.</summary>
    internal static string Clock(double? seconds)
    {
        if (seconds is not double s) return "-";
        var span = TimeSpan.FromSeconds(Math.Max(0d, s));
        return span.TotalHours >= 1d ? $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}" : $"{span.Minutes}:{span.Seconds:D2}";
    }

    // " (17%)".
    private static string Share(long part, long total) => total > 0 ? $" ({(part * 100d / total).ToString("0", s_inv)}%)" : string.Empty;

    /// <summary>"1.2M", "34k", "950".</summary>
    internal static string Short(double value) =>
        value >= 1e6 ? (value / 1e6).ToString("0.#", s_inv) + "M" : value >= 1e4 ? (value / 1e3).ToString("0", s_inv) + "k" : value >= 1e3 ? (value / 1e3).ToString("0.#", s_inv) + "k" : value.ToString("0", s_inv);

    private static string Colour(string text, Color colour) => Widgets.Themed ? Widgets.Colored(text, colour) : text;

    internal static string Capital(string word) => word.Length > 0 ? char.ToUpperInvariant(word[0]) + word.Substring(1) : word;
}
