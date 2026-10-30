using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

/// <summary>
/// The current session's figures on the mini HUD (Session tab › Mini HUD): a card under the pill, one line in the pill,
/// or a tile per figure. Each figure can carry its game colour (the dashboard's roles, so palettes reach them), a
/// smoothed graph of its pace over the session, and its name. Texts and graph points are made again only when the
/// figures snapshot (at most twice a second) or a setting changes; graphs draw into cached textures (SessionCharts),
/// redrawn only when their figures change. The Hud places and draws them.
/// </summary>
internal static class SessionHud
{
    /// <summary>
    /// One figure: its key (the dashboard's metric), label, total and per hour texts (either may be empty), colour, its
    /// pace over the session (per hour, oldest first) and that pace's top, or the share of the level done for Level.
    /// </summary>
    public sealed record Line(string Key, string Label, string Total, string Rate, Color Colour,
        IReadOnlyList<(double T, double V)>? Pace, double PaceMax, float? Progress)
    {
        /// <summary>The pace in more detail, for the hover view's larger graph.</summary>
        public IReadOnlyList<(double T, double V)>? Fine { get; init; }

        private string? _tileLabel;
        /// <summary>A tile's label: the name, with the total when the rate leads ("Drops · 271"). Made once.</summary>
        public string TileLabel => _tileLabel ??= Rate.Length > 0 && Total.Length > 0 ? $"{Label} · {Total}" : Label;
    }

    // Sizes at M. Text size moves the text (XS -2, S -1, L +2 points on the pill's text); Card size scales the rest, and
    // rows and tiles still grow to fit the text.
    private const float BasePad = 10f, BaseRow = 22f, BaseGap = 12f, BaseDot = 7f, BaseDotGap = 8f, BaseGraphWidth = 58f, BaseGraphHeight = 14f;
    private const float BaseTileWidth = 92f, TileGap = 6f;
    private static readonly int[] s_sizeSteps = { -2, -1, 0, 2 };
    // The Graph line choices as multiples of the line's normal width.
    private static readonly float[] s_lineWidths = { 0.6f, 1f, 1.5f, 2f };
    private static readonly float[] s_cardScales = { 0.8f, 0.9f, 1f, 1.2f };

    private static SessionStats? s_from;
    private static int s_stamp = -1;
    private static readonly List<Line> s_lines = new();
    private static string s_time = string.Empty;
    private static GUIContent s_inline = GUIContent.none;
    private static double s_duration;
    private static int s_version;
    private static readonly Dictionary<string, SessionCharts.Canvas> s_canvases = new();

    // The current session's figures, or the sample's while the dashboard shows it (page images); none while Sessions isn't
    // loaded.
    private static SessionStats? Stats => SessionSettings.HudShow.Value && Modules.Loaded("sessions") ? SessionsPage.Current.Stats() : null;

    /// <summary>Whether there is anything to show: Sessions is loaded, the switch is on and a session is recording.</summary>
    public static bool Shown
    {
        get
        {
            // The figures are measured with the theme's styles, which a reload builds on its first drawn frame.
            if (Theme.Built && Stats != null && Refresh()) return true;
            Release();
            return false;
        }
    }

    public static bool Inline => SessionSettings.HudLook.Value == 1;
    public static bool Tiles => SessionSettings.HudLook.Value == 2;
    private static bool Graphs => SessionSettings.HudGraphs.Value;
    private static bool Colours => SessionSettings.HudColours.Value;
    private static bool Labels => SessionSettings.HudLabels.Value;
    private static bool Fixed => SessionSettings.HudFixed.Value;

    // The card's and tiles' styles at the chosen size, made again when the theme rebuilds its own or the size changes.
    private static GUIStyle? s_value, s_label, s_labelRight, s_title, s_builtFrom;
    private static int s_builtSize = -1;
    private static float s_scale = 1f;
    private static float s_digit, s_valueHeight, s_labelHeight;

    // ---- Measuring once ----
    // Texts change at most twice a second, but IMGUI draws several events a frame: widths, the card's columns and the tile
    // width are kept until the next rebuild or style change, and one content object carries every text drawn.
    private static readonly GUIContent s_content = new();
    // One cache per style, keyed by text: a style is a game object, and hashing or comparing one calls into the game.
    private static readonly Dictionary<string, float> s_valueWidths = new(), s_labelWidths = new(), s_titleWidths = new(), s_pillWidths = new(), s_fixedWidths = new();
    private static readonly Dictionary<char, (string Text, float Width)> s_glyphs = new();
    private static CardColumns? s_columns;
    private static float s_tileWidth = -1f;

    private static GUIContent Content(string text)
    {
        s_content.text = text;
        return s_content;
    }

    private static float Width(GUIStyle style, string text)
    {
        var cache = ReferenceEquals(style, s_value) ? s_valueWidths : ReferenceEquals(style, s_label) ? s_labelWidths
            : ReferenceEquals(style, s_title) ? s_titleWidths : s_pillWidths;
        if (!cache.TryGetValue(text, out float width)) cache[text] = width = style.CalcSize(Content(text)).x;
        return width;
    }

    // A character's text and width in the value style, for fixed-width digits.
    private static (string Text, float Width) Glyph(char ch)
    {
        if (!s_glyphs.TryGetValue(ch, out var glyph)) s_glyphs[ch] = glyph = (ch.ToString(), char.IsDigit(ch) ? s_digit : s_value!.CalcSize(Content(ch.ToString())).x);
        return glyph;
    }

    // Forgets every measure: new texts, or new styles.
    private static void Remeasure()
    {
        s_valueWidths.Clear();
        s_labelWidths.Clear();
        s_titleWidths.Clear();
        s_pillWidths.Clear();
        s_fixedWidths.Clear();
        s_columns = null;
        s_tileWidth = -1f;
    }

    private static void Styles()
    {
        s_scale = s_cardScales[Mathf.Clamp(SessionSettings.HudScale.Value, 0, s_cardScales.Length - 1)];
        int size = Mathf.Clamp(SessionSettings.HudSize.Value, 0, s_sizeSteps.Length - 1);
        if (s_value != null && ReferenceEquals(s_builtFrom, Theme.PillLabel) && s_builtSize == size) return;
        int step = s_sizeSteps[size];
        s_value = new GUIStyle(Theme.PillLabel) { fontSize = Theme.PillLabel.fontSize + step };
        // Labels and the title centred in their row, level with the figures beside them.
        s_label = new GUIStyle(Theme.Hint) { fontSize = Theme.Hint.fontSize + step, wordWrap = false, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(0, 0, 0, 0) };
        s_labelRight = new GUIStyle(s_label) { alignment = TextAnchor.MiddleRight };
        s_title = new GUIStyle(Theme.CardTitle) { fontSize = Theme.CardTitle.fontSize + step, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0) };
        s_valueHeight = Mathf.Ceil(s_value.CalcSize(Content("0")).y);
        s_labelHeight = Mathf.Ceil(s_label.CalcSize(Content("Xg")).y);
        // The widest digit: every digit's cell in fixed-width figures.
        s_digit = 0f;
        for (char c = '0'; c <= '9'; c++) s_digit = Mathf.Max(s_digit, s_value.CalcSize(Content(c.ToString())).x);
        s_glyphs.Clear();
        Remeasure();
        s_builtFrom = Theme.PillLabel;
        s_builtSize = size;
    }

    private static float Pad => BasePad * s_scale;
    private static float RowHeight => Mathf.Max(Mathf.Round(BaseRow * s_scale), Mathf.Max(s_valueHeight, s_labelHeight) + 2f);
    private static float ColumnGap => BaseGap * s_scale;
    private static float DotSize => Mathf.Round(BaseDot * s_scale);
    private static float DotGap => BaseDotGap * s_scale;
    private static float GraphWidth => Mathf.Round(BaseGraphWidth * s_scale);
    private static float GraphHeight => Mathf.Round(BaseGraphHeight * s_scale);
    // A tile as wide as Card size makes it, or as its widest value or label needs.
    private static float TileWidth
    {
        get
        {
            if (s_tileWidth >= 0f) return s_tileWidth;
            float widest = s_time.Length > 0 ? Measure(s_time) : 0f;
            foreach (var line in s_lines)
            {
                widest = Mathf.Max(widest, Measure(line.Rate.Length > 0 ? line.Rate : line.Total));
                if (Labels) widest = Mathf.Max(widest, Width(s_label!, line.TileLabel));
            }
            return s_tileWidth = Mathf.Max(Mathf.Round(BaseTileWidth * s_scale), Mathf.Ceil(widest + 14f));
        }
    }
    private static float TileValue => Mathf.Max(Mathf.Round(20f * s_scale), s_valueHeight);
    private static float TileLabel => Mathf.Max(Mathf.Round(16f * s_scale), s_labelHeight);

    /// <summary>The pill section's text ("Session 0:42 · XP 2.9M/h · Gold 480k/h"), coloured per figure.</summary>
    public static GUIContent InlineText => s_inline;

    // Frees the graphs' textures (hidden, or the mod stopping).
    private static void Release()
    {
        if (s_canvases.Count == 0) return;
        foreach (var canvas in s_canvases.Values) SessionCharts.Release(canvas);
        s_canvases.Clear();
        s_from = null;
    }

    // ---- Figures ----

    // Makes the texts again for a new snapshot or changed settings; false when no figure is shown.
    private static bool Refresh()
    {
        var stats = Stats;
        if (stats == null) return false;
        int stamp = Stamp();
        if (!ReferenceEquals(stats, s_from) || stamp != s_stamp)
        {
            s_from = stats;
            s_stamp = stamp;
            Build(stats);
        }
        return s_time.Length > 0 || s_lines.Count > 0;
    }

    private static readonly Choice<int>[] s_choices =
    {
        SessionSettings.HudTime, SessionSettings.HudXp, SessionSettings.HudGold, SessionSettings.HudDrops, SessionSettings.HudEchoes,
        SessionSettings.HudLevel, SessionSettings.HudFavour, SessionSettings.HudReputation, SessionSettings.HudAmber, SessionSettings.HudDeaths,
    };

    // Every choice in one number (four values each), with the rate basis and the three switches.
    private static int Stamp()
    {
        int stamp = SessionSettings.HudRate.Value;
        foreach (var choice in s_choices) stamp = stamp * 4 + choice.Value;
        return HashCode.Combine(stamp, Graphs, Colours, Labels, Fixed, SessionSettings.HudSize.Value, SessionSettings.HudScale.Value,
            HashCode.Combine(SessionSettings.HudLine.Value, Theme.Accent, Theme.Text, Theme.Hint.normal.textColor));
    }

    /// <summary>Text and graph point rebuilds since load (dev `sessions cost`).</summary>
    public static int Builds;

    private static void Build(SessionStats s)
    {
        Builds++;
        Remeasure();
        s_lines.Clear();
        s_duration = s.Duration;
        s_version = HashCode.Combine(s.Id, s.Version);
        s_time = SessionSettings.HudTime.Value != SessionSettings.Hidden ? SessionsPage.Clock(s.Duration) : string.Empty;
        if (SessionSettings.HudLevel.Value != SessionSettings.Hidden && s.Level.CurrentLevel is int level)
        {
            float? progress = s.Level.Percent is double p ? Mathf.Clamp01((float)(p / 100d)) : null;
            s_lines.Add(new Line("level", "Level", level.ToString(System.Globalization.CultureInfo.InvariantCulture), string.Empty, ColourOf("xp", s), null, 0d, progress));
        }
        Add(SessionSettings.HudXp, "xp", "XP", s.Totals.Xp, Basis(s.XpRates), s);
        Add(SessionSettings.HudGold, "gold", "Gold", s.Totals.GoldGained, Basis(s.GoldRates), s);
        Add(SessionSettings.HudDrops, "drops", "Drops", s.Totals.DropCount, Basis(s.DropRates), s);
        Add(SessionSettings.HudEchoes, "echoes", "Echoes", s.Echo.Completed, Basis(s.EchoRates), s);
        Add(SessionSettings.HudFavour, "favour", "Favour", SessionsPage.Favour(s).Gained, Sum(s.Factions.Where(f => !f.IsAmber).Select(f => Basis(f.FavourRates))), s);
        Add(SessionSettings.HudReputation, "rep", "Reputation", s.Factions.Sum(f => f.ReputationGained), Sum(s.Factions.Select(f => Basis(f.ReputationRates))), s);
        var amber = SessionsPage.Amber(s);
        Add(SessionSettings.HudAmber, "amber", "Amber", amber?.FavourGained ?? 0, amber != null ? Basis(amber.FavourRates) : null, s);
        Add(SessionSettings.HudDeaths, "deaths", "Deaths", s.Totals.Deaths, Basis(s.DeathRates), s);
        s_inline = new GUIContent(InlineLine());
    }

    private static void Add(Choice<int> choice, string key, string label, long total, double? rate, SessionStats s)
    {
        int shown = choice.Value;
        if (shown == SessionSettings.Hidden) return;
        bool totals = shown is SessionSettings.Total or SessionSettings.Both, rates = shown is SessionSettings.PerHour or SessionSettings.Both;
        // Before a rate can be worked out (the first minute), per hour reads as a dash.
        string rateText = rates ? (rate is double r ? $"{SessionsPage.Short(r)}/h" : "-/h") : string.Empty;
        var raw = s.Charts.TryGetValue(key, out var series) ? Pace(series, s.Duration) : null;
        var pace = Graphs && raw != null ? Buckets(raw, SparkPoints) : null;
        double paceMax = raw?.Max(p => p.V) ?? 0d;
        s_lines.Add(new Line(key, label, totals ? SessionsPage.Short(total) : string.Empty, rateText, ColourOf(key, s),
            pace != null && paceMax > 0d ? pace : null, paceMax, null)
        {
            Fine = raw != null && paceMax > 0d ? Buckets(raw, DetailPoints) : null,
        });
    }

    private static Color ColourOf(string key, SessionStats s) => Colours ? SessionsPage.Readable(SessionsPage.MetricColour(key, s)) : Theme.Accent;

    /// <summary>
    /// A cumulative series as its pace per hour over a moving window, so a burst reads as a bump and a lull as a dip; until
    /// a whole window has passed, the pace since the session started (from its first minute), so the graph spans the
    /// session like the level bar beside it; null before that. The window is a tenth of the session, at least two minutes, and long enough to
    /// hold about four of the figure's events at its session pace, so a rare figure (echoes, deaths) doesn't jump in steps;
    /// at most half the session.
    /// </summary>
    private static List<(double T, double V)>? Pace(ChartSeries series, double duration)
    {
        var points = series.Points;
        if (points.Count < 3 || duration < SessionStats.RateAfter) return null;
        double perSecond = series.Max / Math.Max(1d, duration);
        double window = Math.Min(duration / 2d, Math.Max(Math.Max(120d, duration / 10d), perSecond > 0d ? 4d / perSecond : 0d));
        var pace = new List<(double, double)>(points.Count);
        int from = 0;
        for (int i = 1; i < points.Count; i++)
        {
            double t = points[i].T;
            if (t < SessionStats.RateAfter) continue;
            while (from + 1 < i && points[from + 1].T <= t - window) from++;
            double span = t - points[from].T;
            if (span > 0d) pace.Add((t, Math.Max(0d, points[i].V - points[from].V) * 3600d / span));
        }
        return pace.Count >= 2 ? pace : null;
    }

    // Points a sparkline keeps: a few pixels apart at its size, so it shows the session's shape and not its noise. The
    // hover view's graph is wider and keeps more.
    private const int SparkPoints = 14, DetailPoints = 60;

    // The pace averaged into equal spans of time, one point at each span's end.
    private static List<(double T, double V)> Buckets(List<(double T, double V)> pace, int count)
    {
        if (pace.Count <= count) return pace;
        double t0 = pace[0].T, span = (pace[^1].T - t0) / count;
        var buckets = new List<(double, double)>(count + 1) { pace[0] };
        int i = 1;
        for (int b = 1; b <= count; b++)
        {
            double end = t0 + span * b, sum = 0d;
            int n = 0;
            for (; i < pace.Count && (pace[i].T <= end || b == count); i++)
            {
                sum += pace[i].V;
                n++;
            }
            if (n > 0) buckets.Add((end, sum / n));
        }
        return buckets;
    }

    // The rate the "Per hour over" setting picks.
    private static double? Basis(Rates r) => SessionSettings.HudRate.Value switch
    {
        1 => r.Recent,
        2 => r.OutOfTown,
        _ => r.Session,
    };

    private static double? Sum(IEnumerable<double?> rates)
    {
        double sum = 0d;
        bool any = false;
        foreach (var rate in rates)
        {
            if (rate is not double r) continue;
            sum += r;
            any = true;
        }
        return any ? sum : null;
    }

    // "Session 0:42 · XP 1.2M (2.9M/h)": values in their colours; names only with Labels on. One segment per figure, so
    // each can be hovered.
    private const string Separator = "  ·  ";
    private static readonly List<(string Key, GUIContent Text)> s_segments = new();

    private static string InlineLine()
    {
        s_segments.Clear();
        // Names muted as on the card, so the values stand out: coloured, or the text colour for the time.
        string Name(string label) => Labels ? Widgets.Colored(label, Theme.Hint.normal.textColor) + " " : string.Empty;
        if (s_time.Length > 0) s_segments.Add((TimeKey, new GUIContent(Name("Session") + Widgets.Colored(s_time, Theme.Text))));
        foreach (var l in s_lines)
        {
            string value = l.Rate.Length > 0 && l.Total.Length > 0 ? $"{l.Total} ({l.Rate})" : l.Rate.Length > 0 ? l.Rate : l.Total;
            value = Widgets.Colored(value, Colours ? Ink(l.Colour) : Theme.Text);
            s_segments.Add((l.Key, new GUIContent(Name(l.Label) + value)));
        }
        return string.Join(Separator, s_segments.Select(x => x.Text.text));
    }

    /// <summary>
    /// Draws the pill's session section in rect (the label's place) a segment at a time; with the pointer over one (and
    /// detail allowed), it is lit and its expanded view opens past the HUD (beyond), below or above.
    /// </summary>
    public static void DrawInline(Rect rect, Rect beyond, bool hover, bool below, bool detail)
    {
        if (Event.current.type != EventType.Repaint) return;
        Styles();
        s_hovered = null;
        GUIStyle style = Theme.PillLabel;
        float gap = Width(style, Separator), x = rect.x;
        for (int i = 0; i < s_segments.Count; i++)
        {
            var (key, text) = s_segments[i];
            float width = Width(style, text.text);
            var at = new Rect(x, rect.y, width, rect.height);
            if (hover && at.Contains(Event.current.mousePosition))
            {
                Hover(at, key);
                Theme.DrawSliced(new Rect(at.x - 5f, at.y + 2f, at.width + 10f, at.height - 4f), Theme.Pill.hover.background, Theme.ControlRadius);
            }
            Widgets.Paint(at, text, style);
            x += width;
            if (i + 1 < s_segments.Count)
            {
                Widgets.Paint(new Rect(x, rect.y, gap, rect.height), Content(Separator), style);
                x += gap;
            }
        }
        if (detail && hover && s_hovered is { } hovered) DrawDetail(beyond, s_hoveredRect, hovered, below);
    }

    // ---- Card and tiles ----

    /// <summary>The card's or the tiles' size, in GUI units.</summary>
    public static Vector2 Size(float maxWidth)
    {
        Styles();
        if (Tiles)
        {
            int count = TileCount;
            int perRow = Mathf.Max(1, Mathf.Min(count, Mathf.FloorToInt((maxWidth + TileGap) / (TileWidth + TileGap))));
            int rows = (count + perRow - 1) / perRow;
            return new Vector2(perRow * TileWidth + (perRow - 1) * TileGap, rows * TileHeight + (rows - 1) * TileGap);
        }
        var c = Columns();
        float title = Labels ? Width(s_title!, "SESSION") + ColumnGap + (s_time.Length > 0 ? Measure(s_time) : 0f) : s_time.Length > 0 ? Measure(s_time) : 0f;
        float width = Mathf.Max(title, c.Graph + c.Width);
        int rows2 = s_lines.Count + (title > 0f ? 1 : 0);
        return new Vector2(Mathf.Ceil(width + Pad * 2f), Mathf.Ceil(Pad * 2f + RowHeight * rows2));
    }

    private static int TileCount => s_lines.Count + (s_time.Length > 0 ? 1 : 0);

    // Top padding, the value, the label, the graph and the bottom padding.
    private static float TileHeight => Mathf.Round(8f * s_scale) + TileValue + (Labels ? TileLabel : 0f) + (Graphs ? GraphHeight + Mathf.Round(4f * s_scale) : 0f) + Mathf.Round(6f * s_scale);

    // The card's columns: the colour dot, the widest label, total and rate, then the graph; Width is up to the graph.
    private readonly record struct CardColumns(float Dot, float Label, float Total, float Rate, float Graph, float Gap)
    {
        public float Width => Dot + Label + (Total > 0f ? (Label > 0f ? Gap : 0f) + Total : 0f) + (Rate > 0f ? Gap + Rate : 0f);
    }

    private static CardColumns Columns() => s_columns ??= MeasureColumns();

    private static CardColumns MeasureColumns()
    {
        float label = 0f, total = 0f, rate = 0f;
        bool graph = false;
        foreach (var line in s_lines)
        {
            if (Labels) label = Mathf.Max(label, Width(s_label!, line.Label) + 6f * s_scale);
            // The totals column is only for rows showing both; a single value goes in the right-hand column.
            if (line.Total.Length > 0 && line.Rate.Length > 0) total = Mathf.Max(total, Measure(line.Total));
            string single = line.Rate.Length > 0 ? line.Rate : line.Total;
            if (single.Length > 0) rate = Mathf.Max(rate, Measure(single));
            graph |= line.Pace != null || line.Progress != null;
        }
        // Without labels the dots would be stranded at the edge; the values and graphs carry the colour.
        float dot = Labels && Colours && s_lines.Count > 0 ? DotSize + DotGap : 0f;
        // Without labels the time is the first value row, so the value column fits it too.
        if (!Labels && s_time.Length > 0) rate = Mathf.Max(rate, Measure(s_time) - (total > 0f ? total + ColumnGap : 0f));
        return new CardColumns(dot, label, total, rate, Graphs && graph ? GraphWidth + ColumnGap : 0f, ColumnGap);
    }

    /// <summary>
    /// Draws the card or the tiles in the rect Size gave; with the pointer over a figure (and detail allowed), its
    /// expanded view opens past the whole HUD (beyond: the pill and the card together), below or (below false) above.
    /// </summary>
    public static void Draw(Rect rect, Rect beyond, bool hover, bool below, bool detail)
    {
        if (Event.current.type != EventType.Repaint) return;
        Styles();
        s_hovered = null;
        if (Tiles) DrawTiles(rect, hover);
        else DrawCard(rect, hover);
        if (detail && hover && s_hovered is { } hovered) DrawDetail(beyond, s_hoveredRect, hovered, below);
    }

    // The figure under the pointer this repaint ("time" for the session time), and its tile or row.
    private static string? s_hovered;
    private static Rect s_hoveredRect;

    private static void Hover(Rect r, string key)
    {
        if (!r.Contains(Event.current.mousePosition)) return;
        s_hovered = key;
        s_hoveredRect = r;
    }

    private static void DrawCard(Rect r, bool hover)
    {
        Theme.DrawSliced(r, hover ? Theme.Pill.hover.background : Theme.Pill.normal.background, Theme.PillRadius);
        float x = r.x + Pad, y = r.y + Pad, inner = r.width - Pad * 2f, row = RowHeight;
        var c = Columns();
        float totalRight = x + c.Dot + c.Label + (c.Total > 0f ? (c.Label > 0f ? c.Gap : 0f) + c.Total : 0f);
        float valueRight = x + c.Width;
        float graphX = r.xMax - Pad - GraphWidth;
        if (Labels || s_time.Length > 0)
        {
            if (s_time.Length > 0) Hover(new Rect(r.x, y, r.width, row), TimeKey);
            // With labels: the title on the left and the time on the right. Without: the time as the first value.
            if (Labels) Widgets.Paint(new Rect(x, y, inner, row), "SESSION", s_title!);
            if (s_time.Length > 0) Figure(new Rect(x, y, (Labels ? inner + x : valueRight) - x, row), s_time, Theme.Text, TextAnchor.MiddleRight);
            y += row;
        }

        foreach (var line in s_lines)
        {
            float mid = y + row / 2f;
            Hover(new Rect(r.x, y, r.width, row), line.Key);
            if (c.Dot > 0f) Dot(new Rect(x, Mathf.Round(mid - DotSize / 2f), DotSize, DotSize), line.Colour);
            if (c.Label > 0f) Widgets.Paint(new Rect(x + c.Dot, y, c.Label, row), line.Label, s_label!);
            Color value = Colours ? Ink(line.Colour) : Theme.Text;
            bool both = line.Total.Length > 0 && line.Rate.Length > 0;
            // A figure with one value keeps it in the right-hand column, against its graph; with both, the total sits
            // in its own column before the rate.
            if (both) Figure(new Rect(x, y, totalRight - x, row), line.Total, value, TextAnchor.MiddleRight);
            string single = line.Rate.Length > 0 ? line.Rate : line.Total;
            if (single.Length > 0) Figure(new Rect(x, y, valueRight - x, row), single, both ? Theme.Dim : value, TextAnchor.MiddleRight);
            if (c.Graph > 0f) Graph(new Rect(graphX, Mathf.Round(mid - GraphHeight / 2f), GraphWidth, GraphHeight), line);
            y += row;
        }
    }

    private static void DrawTiles(Rect r, bool hover)
    {
        float width = TileWidth, height = TileHeight, k = s_scale;
        int perRow = Mathf.Max(1, Mathf.RoundToInt((r.width + TileGap) / (width + TileGap)));
        int i = 0;
        void Tile(string value, string label, Color colour, Line? line)
        {
            var tile = new Rect(r.x + i % perRow * (width + TileGap), r.y + i / perRow * (height + TileGap), width, height);
            Hover(tile, line?.Key ?? TimeKey);
            bool lit = hover && tile.Contains(Event.current.mousePosition);
            Theme.DrawSliced(tile, lit ? Theme.Pill.hover.background : Theme.Pill.normal.background, Theme.PillRadius);
            bool graph = Graphs && line != null && (line.Pace != null || line.Progress != null);
            // A tile without a graph (the session time) centres its value and label in the tile's height.
            float content = TileValue + (Labels ? TileLabel : 0f);
            float y = graph || !Graphs ? tile.y + Mathf.Round(8f * k) : tile.y + Mathf.Round((height - content) / 2f);
            Figure(new Rect(tile.x, y, tile.width, TileValue), value, Colours && line != null ? Ink(colour) : Theme.Text, TextAnchor.MiddleCenter);
            y += TileValue;
            if (Labels)
            {
                Centred(new Rect(tile.x, y, tile.width, TileLabel), label, s_label!);
                y += TileLabel;
            }
            if (graph)
                Graph(new Rect(tile.x + 8f * k, y + 2f * k, tile.width - 16f * k, GraphHeight), line!);
            i++;
        }
        if (s_time.Length > 0) Tile(s_time, "Session", Theme.Text, null);
        // The rate leads when both show; the total joins the label.
        foreach (var line in s_lines)
        {
            Tile(line.Rate.Length > 0 ? line.Rate : line.Total, line.TileLabel, line.Colour, line);
        }
    }

    // The graphs' line width multiplier for the Graph line setting.
    private static float LineWidth => s_lineWidths[Mathf.Clamp(SessionSettings.HudLine.Value, 0, s_lineWidths.Length - 1)];

    // A figure's graph: its pace over the session as a smoothed line over a soft fill, or Level's progress as a bar as
    // thick as the graphs' lines (their width is a half-width each side) and as wide as the graphs.
    private static void Graph(Rect r, Line line)
    {
        if (line.Progress is float done)
        {
            float thick = Mathf.Max(1f, Mathf.Round(2f * LineWidth));
            var bar = new Rect(r.x, Mathf.Round(r.center.y - thick / 2f), r.width, thick);
            Theme.Fill(bar, Theme.Values.Control);
            if (done > 0f) Theme.Fill(new Rect(bar.x, bar.y, Mathf.Max(1f, bar.width * done), bar.height), line.Colour);
            return;
        }
        if (line.Pace == null) return;
        if (!s_canvases.TryGetValue(line.Key, out var canvas)) s_canvases[line.Key] = canvas = new SessionCharts.Canvas();
        // A little headroom over the top, so the line's peak isn't cut by the edge.
        SessionCharts.Area(r, canvas, HashCode.Combine(s_version, line.Key, s_stamp), line.Pace, s_duration, line.PaceMax * 1.15d, line.Colour,
            Array.Empty<(double, double)>(), grid: false, line: LineWidth);
    }

    // ---- Hover view ----

    private const string TimeKey = "time";
    private static readonly string[] s_rateNames = { "Session", "Last 10 min", "Out of town" };
    private const float BaseDetailWidth = 280f, BaseDetailGraph = 70f;

    // One line of the hover view: a name on the left, a value on the right in a colour (null: the text colour).
    private readonly record struct DetailRow(string Name, string Value, Color? Colour);

    // The hover view's rows, made again only for another figure, snapshot or setting.
    private static (string? Key, SessionStats? From, int Stamp) s_detailFor;
    private static readonly List<DetailRow> s_detailRows = new();
    private static (string Session, string Recent, string Town) s_detailRates;
    private static string s_detailPeak = string.Empty;

    private static void BuildDetail(string key, SessionStats s)
    {
        if (s_detailFor == (key, s, s_stamp)) return;
        s_detailFor = (key, s, s_stamp);
        s_detailRows.Clear();
        s_detailPeak = string.Empty;
        Rates? rates = RatesOf(key, s);
        string Per(double? r) => r is double v ? $"{SessionsPage.Short(v)}/h" : "-";
        s_detailRates = rates is { } rr ? (Per(rr.Session), Per(rr.Recent), Per(rr.OutOfTown)) : (string.Empty, string.Empty, string.Empty);
        var line = s_lines.FirstOrDefault(l => l.Key == key);
        if (line?.Fine != null) s_detailPeak = $"peak {SessionsPage.Short(line.PaceMax)}/h";
        switch (key)
        {
            case TimeKey:
                s_detailRows.Add(new DetailRow("Out of town", SessionsPage.Clock(s.OutOfTownSeconds), null));
                s_detailRows.Add(new DetailRow("In town", SessionsPage.Clock(s.TownSeconds), null));
                if (s.Echo.Completed > 0) s_detailRows.Add(new DetailRow("Echoes", SessionsPage.Whole(s.Echo.Completed), ColourOf("echoes", s)));
                break;
            case "level":
                if (s.Level.Percent is double p) s_detailRows.Add(new DetailRow("Progress", $"{p:0}%", null));
                if (s.Level.TimeToNext is double t) s_detailRows.Add(new DetailRow("Next level in", SessionsPage.Clock(t), null));
                if (s.Level.Gained > 0) s_detailRows.Add(new DetailRow("Gained", $"+{s.Level.Gained}", null));
                break;
            case "favour":
            case "rep":
                // The factions it went to, most first, each in its own colour.
                foreach (var f in s.Factions.Where(f => !f.IsAmber).OrderByDescending(f => key == "rep" ? f.ReputationGained : f.FavourGained).Take(4))
                {
                    long amount = key == "rep" ? f.ReputationGained : f.FavourGained;
                    if (amount <= 0) continue;
                    Color? colour = Colours ? (key == "rep" ? SessionStats.ReputationColour(f.Faction) : SessionStats.FavourColour(f.Faction)) : null;
                    s_detailRows.Add(new DetailRow(f.Name, SessionsPage.Short(amount), colour is { } c ? SessionsPage.Readable(c) : null));
                }
                break;
            // What the rates above don't already say.
            case "drops":
                if (s.FilterRead) s_detailRows.Add(new DetailRow("Shown by the loot filter", SessionsPage.Whole(s.Totals.DropsShown), null));
                break;
            case "gold":
                if (s.Totals.GoldSpent > 0) s_detailRows.Add(new DetailRow("Spent", SessionsPage.Short(s.Totals.GoldSpent), null));
                break;
            case "deaths":
                if (s.Deaths.Count > 0 && s.Deaths[^1].Killer is { Length: > 0 } killer) s_detailRows.Add(new DetailRow("Last killed by", killer, ColourOf("deaths", s)));
                break;
        }
    }

    private static Rates? RatesOf(string key, SessionStats s)
    {
        Rates Total(IEnumerable<Rates> all)
        {
            var list = all.ToList();
            return new Rates(Sum(list.Select(r => r.Session)), Sum(list.Select(r => r.OutOfTown)), Sum(list.Select(r => r.Recent)));
        }
        return key switch
        {
            "xp" => s.XpRates,
            "gold" => s.GoldRates,
            "drops" => s.DropRates,
            "echoes" => s.EchoRates,
            "deaths" => s.DeathRates,
            "favour" => Total(s.Factions.Where(f => !f.IsAmber).Select(f => f.FavourRates)),
            "rep" => Total(s.Factions.Select(f => f.ReputationRates)),
            "amber" => SessionsPage.Amber(s)?.FavourRates,
            _ => null,
        };
    }

    // The hover view: the figure's name and total, its pace over the session with its peak and time axis, its three rates,
    // then its own rows. Beside the group, at the hovered figure's side, kept on screen.
    private static void DrawDetail(Rect group, Rect item, string key, bool below)
    {
        if (s_from is not { } s) return;
        BuildDetail(key, s);
        var line = s_lines.FirstOrDefault(l => l.Key == key);
        float k = s_scale, pad = Pad, row = RowHeight, width = Mathf.Round(BaseDetailWidth * k), inner = width - pad * 2f;
        bool graph = line?.Fine != null, progress = line?.Progress != null, rates = s_detailRates.Session.Length > 0;
        float graphHeight = Mathf.Round(BaseDetailGraph * k);
        float height = pad * 2f + row + (graph ? graphHeight + row : 0f) + (progress ? row : 0f) + (rates ? row * 2f : 0f) + s_detailRows.Count * row;

        float scale = Mathf.Max(0.5f, Mathf.Abs(GUI.matrix.m00));
        float screenW = Screen.width / scale, screenH = Screen.height / scale;
        float x = Mathf.Clamp(item.x, 0f, Mathf.Max(0f, screenW - width));
        float y = below ? group.yMax + 8f : group.y - 8f - height;
        y = Mathf.Clamp(y, 0f, Mathf.Max(0f, screenH - height));
        var r = new Rect(x, y, width, height);
        // Only the hovered tile or row is lit; the view itself stays plain.
        Theme.DrawSliced(r, Theme.Pill.normal.background, Theme.PillRadius);

        float cx = r.x + pad, cy = r.y + pad;
        Color colour = line != null && Colours ? line.Colour : Theme.Text;
        // The name in the figure's colour, the total (or the time) on the right.
        string name = line?.Label ?? "Session";
        Color titleColour = s_title!.normal.textColor;
        if (line != null && Colours) s_title.normal.textColor = Ink(colour);
        Widgets.Paint(new Rect(cx, cy, inner, row), name.ToUpperInvariant(), s_title);
        s_title.normal.textColor = titleColour;
        string total = key == TimeKey ? s_time : line?.Total.Length > 0 ? line.Total : line?.Rate ?? string.Empty;
        if (total.Length > 0) Figure(new Rect(cx, cy, inner, row), total, line != null && Colours ? Ink(colour) : Theme.Text, TextAnchor.MiddleRight);
        cy += row;

        if (graph)
        {
            var area = new Rect(cx, cy, inner, graphHeight);
            if (!s_canvases.TryGetValue("detail", out var canvas)) s_canvases["detail"] = canvas = new SessionCharts.Canvas();
            SessionCharts.Area(area, canvas, HashCode.Combine(s_version, key, s_stamp, "detail"), line!.Fine!, s_duration, line.PaceMax * 1.15d, colour,
                Array.Empty<(double, double)>(), grid: true, line: s_lineWidths[Mathf.Clamp(SessionSettings.HudLine.Value, 0, s_lineWidths.Length - 1)]);
            cy += graphHeight;
            // The time axis, with the peak between its ends.
            Widgets.Paint(new Rect(cx, cy, inner, row), "0:00", s_label!);
            Centred(new Rect(cx, cy, inner, row), s_detailPeak, s_label!);
            Widgets.Paint(new Rect(cx, cy, inner, row), SessionsPage.Clock(s_duration), s_labelRight!);
            cy += row;
        }
        if (progress)
        {
            float thick = Mathf.Max(4f, Mathf.Round(6f * k));
            var bar = new Rect(cx, Mathf.Round(cy + (row - thick) / 2f), inner, thick);
            Theme.Fill(bar, Theme.Values.Control);
            Theme.Fill(new Rect(bar.x, bar.y, Mathf.Max(1f, bar.width * line!.Progress!.Value), bar.height), colour);
            cy += row;
        }
        if (rates)
        {
            // Three columns: a small name over each rate; the one the HUD shows in the figure's colour.
            string[] values = { s_detailRates.Session, s_detailRates.Recent, s_detailRates.Town };
            var names = s_rateNames;
            float column = inner / 3f;
            for (int i = 0; i < 3; i++)
            {
                var at = new Rect(cx + column * i, cy, column, row);
                Widgets.Paint(at, names[i], s_label!);
                // The columns are in the setting's order: whole session, last 10 minutes, time out of town.
                bool chosen = i == SessionSettings.HudRate.Value;
                var valueAt = new Rect(at.x, at.y + row, column, row);
                Color tint = chosen && line != null && Colours ? Ink(colour) : Theme.Text;
                Color previous = s_value!.normal.textColor;
                TextAnchor alignment = s_value.alignment;
                s_value.normal.textColor = tint;
                s_value.alignment = TextAnchor.MiddleLeft;
                Widgets.Paint(valueAt, values[i], s_value);
                s_value.normal.textColor = previous;
                s_value.alignment = alignment;
            }
            cy += row * 2f;
        }
        foreach (var detail in s_detailRows)
        {
            Widgets.Paint(new Rect(cx, cy, inner, row), detail.Name, s_label!);
            if (detail.Value.Length > 0) Figure(new Rect(cx, cy, inner, row), detail.Value, detail.Colour is { } c ? Ink(c) : Theme.Text, TextAnchor.MiddleRight);
            cy += row;
        }
    }

    // ---- Text ----

    // A figure's width: the value style's, or with every digit in the widest digit's cell.
    private static float Measure(string text)
    {
        if (!Fixed) return Width(s_value!, text);
        if (s_fixedWidths.TryGetValue(text, out float known)) return known;
        float width = 0f;
        foreach (char ch in text) width += Glyph(ch).Width;
        return s_fixedWidths[text] = width;
    }

    // A figure in the value style, against the rect's right edge or centred; fixed-width figures draw a cell per digit.
    private static void Figure(Rect r, string text, Color colour, TextAnchor anchor)
    {
        float width = Measure(text);
        float x = anchor == TextAnchor.MiddleRight ? r.xMax - width : r.x + (r.width - width) / 2f;
        Color previous = s_value!.normal.textColor;
        s_value.normal.textColor = colour;
        if (!Fixed) Widgets.Paint(new Rect(x, r.y, width, r.height), Content(text), s_value);
        else
        {
            TextAnchor alignment = s_value.alignment;
            s_value.alignment = TextAnchor.MiddleCenter;
            foreach (char ch in text)
            {
                var (glyph, cell) = Glyph(ch);
                Widgets.Paint(new Rect(x, r.y, cell, r.height), Content(glyph), s_value);
                x += cell;
            }
            s_value.alignment = alignment;
        }
        s_value.normal.textColor = previous;
    }

    private static void Dot(Rect r, Color colour) => Theme.Tinted(r, Theme.Dot, colour);

    // A figure's colour for text. The game draws text colours a step darker than the same colour on a texture (its
    // linear colour space), so text gets the colour's gamma form and reads as the dot and graph beside it do.
    private static Color Ink(Color colour) => colour.gamma;

    // A label centred across the rect, in its style's own colour.
    private static void Centred(Rect r, string text, GUIStyle style)
    {
        TextAnchor alignment = style.alignment;
        style.alignment = TextAnchor.MiddleCenter;
        Widgets.Paint(r, Content(text), style);
        style.alignment = alignment;
    }
}
