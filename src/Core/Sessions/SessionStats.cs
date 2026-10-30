namespace EpochAssist.Core;

/// <summary>
/// Echo runs as records show them: a run starts at a zone record of kind echo, completes at the first echo record after
/// it and ends at the next zone record of town or of another echo. Its own scene again (the game reports an entry twice)
/// and side zones it opens (an echo's cemetery: neither town nor echo) continue it, so a completion there counts.
/// Session totals and the figures builder both feed one, so their echo figures can't disagree.
/// </summary>
internal sealed class EchoRunTracker
{
    /// <summary>The open run's start, its completion and scene; Start null while no run is open.</summary>
    public double? Start { get; private set; }
    public double? Completed { get; private set; }
    public string? Scene { get; private set; }
    /// <summary>The last record fed started a run.</summary>
    public bool Started { get; private set; }

    /// <summary>Feeds one record; returns the run it ended (start, completion, end, scene), or null.</summary>
    public (double Start, double? Completed, double End, string Scene)? Add(SessionRecord record)
    {
        Started = false;
        switch (record.Kind)
        {
            case "echo" when Start != null && Completed == null:
                Completed = record.T;
                return null;
            case "zone":
                string? kind = record.Text("kind");
                bool echo = kind == "echo";
                // The game can report one entry twice (the scene, then its monolith transition), and an echo can open a
                // side zone: both continue the run.
                if (Start != null && ((echo && record.Text("scene") == Scene) || (!echo && kind != "town" && kind != "menu"))) return null;
                (double Start, double? Completed, double End, string Scene)? ended = Start is double start ? (start, Completed, record.T, Scene ?? string.Empty) : null;
                Start = echo ? record.T : null;
                Completed = null;
                Scene = echo ? record.Text("scene") ?? string.Empty : null;
                Started = echo;
                return ended;
            default:
                return null;
        }
    }

    public EchoRunTracker Copy() => new() { Start = Start, Completed = Completed, Scene = Scene };
}

/// <summary>A figure's rates per hour: over the session, out of town and the last 10 minutes; null until shown.</summary>
internal readonly record struct Rates(double? Session, double? OutOfTown, double? Recent);

/// <summary>One echo run with its sums and the pick before it (blank when none).</summary>
internal sealed class EchoRun
{
    public string Scene = string.Empty;
    /// <summary>The echo's name as the game shows it ("Waste Pools"); the scene when unknown.</summary>
    public string Name = string.Empty;
    public double Start;
    public double? Completed, End;
    public long Xp, Gold;
    public int Drops, Deaths;
    public readonly Dictionary<string, long> Favour = new(), Reputation = new();
    public string? Modifier, Reward;
    public long? Stability, Corruption;
    public bool HasPick;

    public double? Clear => Completed is double c ? c - Start : null;

    /// <summary>To its end, or to now while open.</summary>
    public double Total(double now) => (End ?? now) - Start;
}

/// <summary>The echo card's figures: ended runs from totals, the open run added in for display.</summary>
internal sealed record EchoFigures(double? LastClear, double? AverageClear, double? BestClear, double? LastTotal, double? AverageTotal, int Completed, int Left, double? PerHour);

/// <summary>A cumulative series: points at minute buckets (merged by powers of two to at most 120), gap spans and the scale.</summary>
internal sealed record ChartSeries(IReadOnlyList<(double T, double V)> Points, double Max);

/// <summary>The timeline's raw segments (merged by kind) and marks (drops of rare and above, deaths); reduced per drawn width.</summary>
internal sealed record TimelineData(IReadOnlyList<(double Start, double End, string Kind)> Segments, IReadOnlyList<(double T, string Kind)> Marks)
{
    /// <summary>
    /// Segments for a drawn width: one narrower than a pixel joins its neighbour, taking the longer one's kind, then
    /// neighbours of one kind join; the limit doubles until at most cap are left.
    /// </summary>
    public List<(double Start, double End, string Kind)> Drawn(double duration, float width, int cap)
    {
        var drawn = new List<(double Start, double End, string Kind)>();
        if (duration <= 0d || width <= 0f) return drawn;
        // A pixel to start with, so short town stays between echoes still show; wider only when over cap.
        double minSeconds = duration / width;
        for (int pass = 0; pass < 16; pass++)
        {
            drawn.Clear();
            foreach (var segment in Segments)
            {
                if (drawn.Count > 0 && (segment.End - segment.Start < minSeconds || drawn[^1].Kind == segment.Kind || drawn[^1].End - drawn[^1].Start < minSeconds))
                {
                    var last = drawn[^1];
                    string kind = last.End - last.Start >= segment.End - segment.Start ? last.Kind : segment.Kind;
                    drawn[^1] = (last.Start, segment.End, kind);
                }
                else drawn.Add(segment);
            }
            if (drawn.Count <= cap) break;
            minSeconds *= 2d;
        }
        return drawn;
    }

    /// <summary>Marks for a drawn width: marks within 2 px (more when over cap) join, keeping a death, else the highest rarity.</summary>
    public List<(double T, string Kind)> DrawnMarks(double duration, float width, int cap)
    {
        var drawn = new List<(double T, string Kind)>();
        if (duration <= 0d || width <= 0f) return drawn;
        double near = 2d * duration / width;
        for (int pass = 0; pass < 16; pass++)
        {
            drawn.Clear();
            foreach (var mark in Marks)
            {
                if (drawn.Count > 0 && mark.T - drawn[^1].T < near)
                {
                    if (Rank(mark.Kind) > Rank(drawn[^1].Kind)) drawn[^1] = (drawn[^1].T, mark.Kind);
                }
                else drawn.Add(mark);
            }
            if (drawn.Count <= cap) break;
            near *= 2d;
        }
        return drawn;
    }

    private static int Rank(string kind) => kind == "death" ? 100 : Array.IndexOf(SessionStats.MarkRarities, kind);
}

internal sealed class DeathEntry
{
    public double T;
    public string? Zone, Killer;
    public string[]? Types;
}

/// <summary>A drop the loot filter showed.</summary>
internal sealed record ShownDrop(double T, string? Name, string? Base, string Rarity);

internal sealed record PickEntry(double T, string? Modifier, long? Stability, string? Reward, long? Corruption);

internal sealed record LevelFigures(int? StartLevel, int? CurrentLevel, IReadOnlyList<(double T, int Level)> LevelUps, long? XpInLevel, long? Threshold, double? Percent, double? TimeToNext)
{
    /// <summary>Levels gained: the records', or the totals' when more (a cut session's level-ups after its cut).</summary>
    public int Gained { get; init; }
}

internal sealed class FactionFigures
{
    public string Faction = string.Empty;
    public string Name = string.Empty;
    /// <summary>The Weaver's favour, shown as Memory amber.</summary>
    public bool IsAmber;
    public long FavourGained, FavourSpent, ReputationGained;
    public int RankUps;
    public int? Rank;
    /// <summary>The character is in it (live read; left factions keep their rank).</summary>
    public bool Member;
    public double? PercentToNext, TimeToNext;
    public Rates FavourRates, ReputationRates;
}

/// <summary>
/// Every figure the Session tab draws for one session, built from its records and totals: rates, echo runs, shares,
/// chart points, timeline segments, lists, level and faction figures. Built in one pass for a past session
/// (<see cref="For"/>) or kept incrementally for the current one (<see cref="Live"/>, advanced from RecordsSince at most
/// every 0.5 s). Lists are shared with the builder and only appended to, so a snapshot copies nothing.
/// </summary>
internal sealed class SessionStats
{
    public const double RateAfter = 60d, RecentAfter = 600d, RecentWindow = 600d;
    public const int MaxLevel = 100;
    public const int MaxPoints = 120;
    /// <summary>What a chart and the timeline draw at most: points per series, segments and marks.</summary>
    public const int DrawnPoints = 60, DrawnSegments = 80, DrawnMarks = 40;
    public static readonly string[] MarkRarities = { "rare", "exalted", "unique", "set", "legendary" };
    /// <summary>The faction whose favour is memory amber (its FactionID name): the Weaver panel's Memory Amber is its
    /// Favor (game facts GF-104).</summary>
    public const string AmberFaction = "TheWeaver";

    public string Id = string.Empty;
    public bool Live;
    public SessionHeader Header = null!;
    public SessionTotals Totals = null!;
    public double Duration, TownSeconds, OutOfTownSeconds;
    public Rates XpRates, GoldRates, DropRates, DeathRates, EchoRates;
    public EchoFigures Echo = null!;
    public IReadOnlyList<EchoRun> Runs = Array.Empty<EchoRun>();
    public IReadOnlyDictionary<string, int> ShownByRarity = new Dictionary<string, int>();
    public IReadOnlyList<ShownDrop> ShownDrops = Array.Empty<ShownDrop>();
    /// <summary>Some drop was read with a loot filter (shown or hidden).</summary>
    public bool FilterRead;
    public ChartSeries XpChart = null!, GoldChart = null!;
    /// <summary>Cumulative series by figure: xp, gold, drops, deaths, echoes, favour (factions other than the Weaver),
    /// amber and rep.</summary>
    public IReadOnlyDictionary<string, ChartSeries> Charts = new Dictionary<string, ChartSeries>();
    public IReadOnlyList<(double From, double To)> Gaps = Array.Empty<(double, double)>();
    public TimelineData Timeline = null!;
    public IReadOnlyList<DeathEntry> Deaths = Array.Empty<DeathEntry>();
    public IReadOnlyList<PickEntry> Picks = Array.Empty<PickEntry>();
    public LevelFigures Level = null!;
    public IReadOnlyList<FactionFigures> Factions = Array.Empty<FactionFigures>();
    public bool Cut;
    public double? CutAt;
    public string EndKind = "open";
    /// <summary>Bumped on each change; keys the chart textures.</summary>
    public int Version;

    public EchoRun? OpenRun => Live && Runs.Count > 0 && Runs[^1].End == null ? Runs[^1] : null;

    // ---- Sources ----

    /// <summary>A past session's figures (or the current one's, live), built in one pass.</summary>
    public static SessionStats For(SessionView view, bool live = false)
    {
        var builder = new Builder(view.Header);
        foreach (var record in view.Records) builder.Add(record);
        return builder.At(view, live);
    }

    private static Builder? s_live;
    private static SessionStats? s_liveStats;
    private static float s_liveAt = float.MinValue;
    private const float RefreshSeconds = 0.5f;

    /// <summary>Live figure builds since load and their total time (dev `sessions cost`).</summary>
    public static int LiveBuilds;
    public static double LiveBuildMs;

    /// <summary>The current session's figures, refreshed at most every 0.5 s; null with no session. Built only when read.</summary>
    public static SessionStats? LiveStats()
    {
        if (Sessions.Current is not { } view)
        {
            s_live = null;
            s_liveStats = null;
            return null;
        }
        if (s_live == null || !ReferenceEquals(s_live.Header, view.Header))
        {
            s_live = new Builder(view.Header);
            s_liveStats = null;
        }
        if (s_liveStats == null || Clock.Now - s_liveAt >= RefreshSeconds)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var record in view.RecordsSince(s_live.Consumed)) s_live.Add(record);
            s_liveStats = s_live.At(view, live: true);
            s_liveAt = Clock.Now;
            LiveBuilds++;
            LiveBuildMs += (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d / System.Diagnostics.Stopwatch.Frequency;
        }
        return s_liveStats;
    }

    /// <summary>Forgets the live builder (shutdown).</summary>
    public static void Reset()
    {
        s_live = null;
        s_liveStats = null;
        s_liveAt = float.MinValue;
    }

    /// <summary>Consumes records in order and produces a snapshot at a time.</summary>
    internal sealed class Builder
    {
        public Builder(SessionHeader header) => Header = header;

        public readonly SessionHeader Header;
        public int Consumed;

        private readonly EchoRunTracker _tracker = new();
        private readonly List<EchoRun> _runs = new();
        private EchoRun? _open;
        private PickEntry? _pendingPick;
        private long? _lastPickHex;
        private readonly Dictionary<string, int> _shown = new();
        private readonly List<ShownDrop> _shownDrops = new();
        private bool _filterRead;
        private readonly Dictionary<string, Window> _windows = new();
        private readonly Dictionary<string, Buckets> _charts = new(StringComparer.Ordinal);
        private readonly List<(double From, double To)> _gaps = new();
        private readonly List<(double Start, double End, string Kind)> _segments = new();
        private (double Start, string Kind)? _segment;
        private readonly List<(double T, string Kind)> _marks = new();
        private readonly List<DeathEntry> _deaths = new();
        private readonly List<PickEntry> _picks = new();
        private readonly List<(double T, int Level)> _levelUps = new();
        private readonly Dictionary<string, FactionFigures> _factions = new(StringComparer.Ordinal);
        private string? _zoneScene;
        private int _version, _lastTick = -1, _lastConsumed = -1;

        public void Add(SessionRecord r)
        {
            Consumed++;
            if (r.Kind == "test") return;
            if (_tracker.Add(r) is { } ended && _open != null)
            {
                _open.End = ended.End;
                _open = null;
                _pendingPick = null;
            }
            switch (r.Kind)
            {
                case "zone":
                    _zoneScene = ZoneName(r);
                    string kind = r.Text("kind") ?? "other";
                    // A side zone the running echo opened (its cemetery) is what the island's tooltip names the echo after, so
                    // it comes first: "Cemetery of the Erased › Jagged Outskirts".
                    if (_open != null && !_tracker.Started && kind != "echo" && _zoneScene is { } side && !_open.Name.Contains(" › "))
                        _open.Name = $"{side} › {_open.Name}";
                    CloseSegment(r.T);
                    _segment = (r.T, kind);
                    if (_tracker.Started)
                    {
                        _open = new EchoRun { Scene = _tracker.Scene ?? string.Empty, Name = ZoneName(r) ?? _tracker.Scene ?? string.Empty, Start = r.T };
                        if (_pendingPick != null) Attach(_open, _pendingPick);
                        _pendingPick = null;
                        _runs.Add(_open);
                    }
                    break;
                case "echo":
                    if (_open != null && _open.Completed == null) _open.Completed = _tracker.Completed;
                    Window("echoes").Add(r.T, 1d);
                    Chart("echoes").Add(r.T, 1d);
                    break;
                case "xp":
                    long xp = r.Whole("amount") ?? 0;
                    Chart("xp").Add(r.T, xp);
                    Window("xp").Add(r.T, xp);
                    if (_open != null) _open.Xp += xp;
                    break;
                case "gold":
                    long gold = r.Whole("amount") ?? 0;
                    // Spent gold is kept apart and never enters rates or the chart.
                    if (gold <= 0) break;
                    Chart("gold").Add(r.T, gold);
                    Window("gold").Add(r.T, gold);
                    if (_open != null) _open.Gold += gold;
                    break;
                case "drop":
                    string rarity = (r.Text("rarityName") ?? "other").ToLowerInvariant();
                    if (r.Flag("shown") is bool shown)
                    {
                        _filterRead = true;
                        if (shown)
                        {
                            _shown[rarity] = _shown.GetValueOrDefault(rarity) + 1;
                            _shownDrops.Add(new ShownDrop(r.T, r.Text("name"), r.Text("base"), r.Text("rarityName") ?? "Normal"));
                        }
                    }
                    if (Array.IndexOf(MarkRarities, rarity) >= 0) _marks.Add((r.T, rarity));
                    Window("drops").Add(r.T, 1d);
                    Chart("drops").Add(r.T, 1d);
                    if (_open != null) _open.Drops++;
                    break;
                case "died":
                    _deaths.Add(new DeathEntry { T = r.T, Zone = _zoneScene });
                    _marks.Add((r.T, "death"));
                    Window("deaths").Add(r.T, 1d);
                    Chart("deaths").Add(r.T, 1d);
                    if (_open != null) _open.Deaths++;
                    break;
                case "death":
                    if (_deaths.Count > 0 && _deaths[^1] is { Killer: null, Types: null } last)
                    {
                        last.Killer = r.Text("killer");
                        last.Types = r.List("types");
                    }
                    break;
                case "pick":
                    var pick = new PickEntry(r.T, r.Text("modifier"), r.Whole("stability"), r.Text("reward"), r.Whole("corruption"));
                    // The same echo reported again (re-entered from a side zone, or after a reload) is one pick.
                    if (_picks.Count > 0 && r.Whole("hex") is long hex && hex == _lastPickHex && _open != null && _open.HasPick) break;
                    _lastPickHex = r.Whole("hex");
                    _picks.Add(pick);
                    // The pick is raised from the same zone change as the echo's zone record, just after it.
                    if (_open != null && !_open.HasPick && r.T - _open.Start <= 2d) Attach(_open, pick);
                    else _pendingPick = pick;
                    break;
                case "gap":
                    if (r.Number("from") is double from && r.Number("to") is double to && to > from) _gaps.Add((from, to));
                    break;
                case "level":
                    if (r.Whole("level") is long level) _levelUps.Add((r.T, (int)level));
                    break;
                case "favour":
                    if (Faction(r) is { } f)
                    {
                        long amount = r.Whole("amount") ?? 0;
                        if (amount > 0) f.FavourGained += amount;
                        else f.FavourSpent -= amount;
                        if (amount > 0)
                        {
                            Window("favour:" + f.Faction).Add(r.T, amount);
                            Chart(f.IsAmber ? "amber" : "favour").Add(r.T, amount);
                        }
                        if (_open != null && amount > 0) _open.Favour[f.Faction] = _open.Favour.GetValueOrDefault(f.Faction) + amount;
                    }
                    break;
                case "rep":
                    if (Faction(r) is { } rf)
                    {
                        long amount = Math.Max(0, r.Whole("amount") ?? 0);
                        rf.ReputationGained += amount;
                        Window("rep:" + rf.Faction).Add(r.T, amount);
                        Chart("rep").Add(r.T, amount);
                        if (_open != null) _open.Reputation[rf.Faction] = _open.Reputation.GetValueOrDefault(rf.Faction) + amount;
                    }
                    break;
                case "rank":
                    if (Faction(r) is { } kf && r.Whole("rank") is long rank)
                    {
                        // A rank record is a rank rise.
                        kf.RankUps++;
                        kf.Rank = (int)rank;
                    }
                    break;
            }
        }

        private static void Attach(EchoRun run, PickEntry pick)
        {
            run.HasPick = true;
            run.Modifier = pick.Modifier;
            run.Stability = pick.Stability;
            run.Reward = pick.Reward;
            run.Corruption = pick.Corruption;
        }

        private FactionFigures? Faction(SessionRecord r)
        {
            if (r.Text("faction") is not { Length: > 0 } id) return null;
            if (!_factions.TryGetValue(id, out var f))
                _factions[id] = f = new FactionFigures { Faction = id, Name = FactionName(id), IsAmber = id == AmberFaction };
            return f;
        }

        private Buckets Chart(string key)
        {
            if (!_charts.TryGetValue(key, out var b)) _charts[key] = b = new Buckets();
            return b;
        }

        private Window Window(string key)
        {
            if (!_windows.TryGetValue(key, out var w)) _windows[key] = w = new Window();
            return w;
        }

        private void CloseSegment(double t)
        {
            if (_segment is not { } open) return;
            _segment = null;
            if (t <= open.Start) return;
            if (_segments.Count > 0 && _segments[^1].Kind == open.Kind && Math.Abs(_segments[^1].End - open.Start) < 0.01d)
                _segments[^1] = (_segments[^1].Start, t, open.Kind);
            else _segments.Add((open.Start, t, open.Kind));
        }

        /// <summary>A snapshot at the view's duration (live: now).</summary>
        public SessionStats At(SessionView view, bool live)
        {
            var totals = view.Totals;
            double now = totals.Duration;
            // Charts and the timeline grow with time too, so the version moves every 5 s even without records.
            int tick = (int)(now / 5d);
            if (Consumed != _lastConsumed || tick != _lastTick) _version++;
            _lastConsumed = Consumed;
            _lastTick = tick;

            double town = totals.ZoneSeconds.GetValueOrDefault("town"), outOfTown = Math.Max(0d, now - town);
            var stats = new SessionStats
            {
                Id = view.Header.Id, Live = live, Header = view.Header, Totals = totals, Duration = now, TownSeconds = town, OutOfTownSeconds = outOfTown,
                Runs = _runs, ShownByRarity = _shown, ShownDrops = _shownDrops, FilterRead = _filterRead, Gaps = _gaps, Deaths = _deaths, Picks = _picks, Cut = view.Cut, EndKind = view.EndKind, Version = _version,
            };
            stats.CutAt = view.Cut && view.Records.Count > 0 ? view.Records[^1].T : null;
            stats.XpRates = RatesOf(totals.Xp, "xp", now, outOfTown, live);
            stats.GoldRates = RatesOf(totals.GoldGained, "gold", now, outOfTown, live);
            stats.DropRates = RatesOf(totals.DropCount, "drops", now, outOfTown, live);
            stats.DeathRates = RatesOf(totals.Deaths, "deaths", now, outOfTown, live);
            stats.EchoRates = RatesOf(EchoCount(totals), "echoes", now, outOfTown, live);
            // A past session's open run ends with it, or at the cut when its records stop there.
            if (!live && _open != null) _open.End ??= view.Cut && view.Records.Count > 0 ? view.Records[^1].T : now;
            stats.Echo = EchoOf(totals, now, live);
            var charts = new Dictionary<string, ChartSeries>(StringComparer.Ordinal);
            foreach (var (key, buckets) in _charts) charts[key] = buckets.Series(now);
            stats.Charts = charts;
            stats.XpChart = Chart("xp").Series(now);
            stats.GoldChart = Chart("gold").Series(now);
            var segments = new List<(double, double, string)>(_segments);
            if (_segment is { } open && now > open.Start)
            {
                if (segments.Count > 0 && segments[^1].Item3 == open.Kind && Math.Abs(segments[^1].Item2 - open.Start) < 0.01d) segments[^1] = (segments[^1].Item1, now, open.Kind);
                else segments.Add((open.Start, now, open.Kind));
            }
            stats.Timeline = new TimelineData(segments, _marks);
            stats.Level = LevelOf(totals, live, stats.XpRates);
            stats.Factions = FactionsOf(totals, now, outOfTown, live);
            return stats;
        }

        private static int EchoCount(SessionTotals t) => t.EchoClearCount > 0 ? t.EchoClearCount : t.Echoes;

        private Rates RatesOf(double amount, string key, double now, double outOfTown, bool live)
        {
            double? session = now >= RateAfter ? amount * 3600d / now : null;
            double? away = outOfTown >= RateAfter ? amount * 3600d / outOfTown : null;
            double? recent = live && now >= RecentAfter ? (_windows.TryGetValue(key, out var w) ? w.Sum(now) : 0d) * 3600d / RecentWindow : null;
            return new Rates(session, away, recent);
        }

        private EchoFigures EchoOf(SessionTotals t, double now, bool live)
        {
            int clearCount = t.EchoClearCount, totalCount = t.EchoTotalCount, left = t.EchoesLeft;
            double clearSum = t.EchoClearSum, totalSum = t.EchoTotalSum;
            // Sessions from before the totals kept echo sums: from the runs the records show.
            if (totalCount == 0 && _runs.Count > 0)
            {
                foreach (var run in _runs)
                {
                    if (run.End == null && live) continue;
                    totalCount++;
                    totalSum += run.Total(now);
                    if (run.Clear is double c)
                    {
                        clearCount++;
                        clearSum += c;
                    }
                    else left++;
                }
            }
            double? lastClear = null, best = null, lastTotal = null;
            for (int i = _runs.Count - 1; i >= 0; i--)
            {
                var run = _runs[i];
                if (run.Clear is double c)
                {
                    lastClear ??= c;
                    best = best is double b ? Math.Min(b, c) : c;
                }
                lastTotal ??= run.Total(now);
            }
            int completed = clearCount > 0 ? clearCount : t.Echoes;
            return new EchoFigures(lastClear, clearCount > 0 ? clearSum / clearCount : null, best, lastTotal, totalCount > 0 ? totalSum / totalCount : null,
                completed, left, now >= RateAfter ? completed * 3600d / now : null);
        }

        private LevelFigures LevelOf(SessionTotals totals, bool live, Rates xpRates)
        {
            int? start = totals.StartLevel ?? Header.StartLevel;
            int? current = _levelUps.Count > 0 ? _levelUps[^1].Level : start;
            if (start is int s && _levelUps.Count == 0 && totals.LevelUps > 0) current = s + totals.LevelUps;
            long? xpIn = null, threshold = null;
            double? percent = null, toNext = null;
            // At the level cap there is no next level to show progress to.
            if (live && LevelProgress?.Invoke() is { } progress && (progress.Level ?? current ?? 0) < MaxLevel)
            {
                xpIn = progress.Xp;
                threshold = progress.Threshold;
                percent = progress.Threshold > 0 ? Math.Clamp(progress.Xp * 100d / progress.Threshold, 0d, 100d) : null;
                double? rate = xpRates.Recent ?? xpRates.Session;
                if (rate is double r && r > 0d && progress.Threshold > progress.Xp) toNext = (progress.Threshold - progress.Xp) * 3600d / r;
                if (progress.Level is int level) current = level;
            }
            return new LevelFigures(start, current, _levelUps, xpIn, threshold, percent, toNext) { Gained = Math.Max(_levelUps.Count, totals.LevelUps) };
        }

        private List<FactionFigures> FactionsOf(SessionTotals totals, double now, double outOfTown, bool live)
        {
            var list = new List<FactionFigures>();
            // Totals hold the sums for a cut session; records give the rest.
            foreach (var (id, sums) in totals.Factions)
            {
                if (!_factions.TryGetValue(id, out var f)) _factions[id] = f = new FactionFigures { Faction = id, Name = FactionName(id), IsAmber = id == AmberFaction };
                f.FavourGained = Math.Max(f.FavourGained, sums.FavourGained);
                f.FavourSpent = Math.Max(f.FavourSpent, sums.FavourSpent);
                f.ReputationGained = Math.Max(f.ReputationGained, sums.ReputationGained);
                f.RankUps = Math.Max(f.RankUps, sums.RankUps);
            }
            // Live, factions with a rank show from the start, before anything is gained.
            if (live && LiveFactionIds?.Invoke() is { } ids)
            {
                foreach (string id in ids)
                    if (!_factions.ContainsKey(id)) _factions[id] = new FactionFigures { Faction = id, Name = FactionName(id), IsAmber = id == AmberFaction };
            }
            foreach (var f in _factions.Values)
            {
                f.FavourRates = RatesOf(f.FavourGained, "favour:" + f.Faction, now, outOfTown, live);
                f.ReputationRates = RatesOf(f.ReputationGained, "rep:" + f.Faction, now, outOfTown, live);
                f.PercentToNext = null;
                f.TimeToNext = null;
                if (live && FactionProgress?.Invoke(f.Faction) is { } progress)
                {
                    f.Rank = progress.Rank;
                    f.Member = progress.Member;
                    f.PercentToNext = progress.Percent;
                    double? rate = f.ReputationRates.Recent ?? f.ReputationRates.Session;
                    if (progress.ToNext is long left && rate is double r && r > 0d) f.TimeToNext = left * 3600d / r;
                }
                list.Add(f);
            }
            list.Sort((a, b) => a.IsAmber != b.IsAmber ? (a.IsAmber ? 1 : -1) : string.CompareOrdinal(a.Name, b.Name));
            return list;
        }
    }

    /// <summary>A zone record's name: the one kept with it, else the game's name for its scene (records from before names
    /// were kept), else the scene.</summary>
    public static string? ZoneName(SessionRecord r) =>
        r.Text("name") ?? (r.Text("scene") is { } scene ? GameData.SceneNamesOf(scene).Display ?? scene : null);

    /// <summary>The level read for live figures (xp in level, threshold, level), or null; set by the player model.</summary>
    public static Func<(long Xp, long Threshold, int? Level)?>? LevelProgress { get; set; }

    /// <summary>A faction's live rank, percent to the next and reputation left (null at the top rank), or null; set by the
    /// faction producer.</summary>
    public static Func<string, (int Rank, double? Percent, long? ToNext, bool Member)?>? FactionProgress { get; set; }

    // Each faction's colours as its panels draw them (game facts GF-105): favour as its favour gem, reputation as its rank
    // bar and banner. The Woven's favour is memory amber, a role of its own.
    private static readonly Dictionary<string, (UnityEngine.Color Favour, UnityEngine.Color Reputation)> s_factionColours = new(StringComparer.Ordinal)
    {
        ["CircleOfFortune"] = (Hex(0x7129A2), Hex(0x3D2E87)), ["MerchantsGuild"] = (Hex(0x7B2B1E), Hex(0x733122)),
        ["TheWeaver"] = (Hex(0xD8D343), Hex(0x376762)), ["ForgottenKnights"] = (Hex(0x6F1E65), Hex(0x6F1E65)),
    };

    /// <summary>A faction's favour colour (its favour gem), or null for a faction not known.</summary>
    public static UnityEngine.Color? FavourColour(string faction) => s_factionColours.TryGetValue(faction, out var c) ? c.Favour : null;

    /// <summary>A faction's reputation colour (its rank bar and banner), or null for a faction not known.</summary>
    public static UnityEngine.Color? ReputationColour(string faction) => s_factionColours.TryGetValue(faction, out var c) ? c.Reputation : null;

    private static UnityEngine.Color Hex(int rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

    /// <summary>The factions the producer read with a rank or favour (live), or null; set by the faction producer.</summary>
    public static Func<IReadOnlyCollection<string>>? LiveFactionIds { get; set; }

    /// <summary>Display names for faction ids as the game's panels write them (its faction data holds none); an id not
    /// here is split into words.</summary>
    public static readonly Dictionary<string, string> FactionNames = new(StringComparer.Ordinal)
    {
        ["CircleOfFortune"] = "Circle of Fortune", ["MerchantsGuild"] = "Merchant's Guild", ["ForgottenKnights"] = "Forgotten Knights", ["TheWeaver"] = "The Woven",
    };

    public static string FactionName(string id)
    {
        if (FactionNames.TryGetValue(id, out var name)) return name;
        var words = new System.Text.StringBuilder(id.Length + 4);
        for (int i = 0; i < id.Length; i++)
        {
            if (i > 0 && char.IsUpper(id[i]) && !char.IsUpper(id[i - 1])) words.Append(' ');
            words.Append(i > 0 && words.Length > 0 && words[^1] == ' ' ? char.ToLowerInvariant(id[i]) : id[i]);
        }
        return words.ToString();
    }

    // Amounts with their times inside the last 10 minutes, dropped from the front as time moves, so no scan.
    private sealed class Window
    {
        private readonly Queue<(double T, double V)> _items = new();
        private double _sum;

        public void Add(double t, double v)
        {
            _items.Enqueue((t, v));
            _sum += v;
        }

        public double Sum(double now)
        {
            while (_items.Count > 0 && _items.Peek().T < now - RecentWindow) _sum -= _items.Dequeue().V;
            return Math.Max(0d, _sum);
        }
    }

    // Cumulative amounts per bucket of session time: a minute, doubled whenever there would be more than 120 buckets.
    private sealed class Buckets
    {
        private readonly List<double> _cumulative = new();
        private double _seconds = 60d, _total;

        public void Add(double t, double amount)
        {
            _total += amount;
            int index = (int)(t / _seconds);
            Extend(index);
            for (int i = index; i < _cumulative.Count; i++) _cumulative[i] = _total;
        }

        private void Extend(int index)
        {
            while (index >= MaxPoints)
            {
                // Merge in pairs: a bucket's cumulative value is its later half's.
                var merged = new List<double>((_cumulative.Count + 1) / 2);
                for (int i = 0; i < _cumulative.Count; i += 2) merged.Add(_cumulative[Math.Min(i + 1, _cumulative.Count - 1)]);
                _cumulative.Clear();
                _cumulative.AddRange(merged);
                _seconds *= 2d;
                index /= 2;
            }
            double last = _cumulative.Count > 0 ? _cumulative[^1] : 0d;
            while (_cumulative.Count <= index) _cumulative.Add(last);
        }

        public ChartSeries Series(double now)
        {
            int end = (int)(now / _seconds);
            if (end >= MaxPoints)
            {
                Extend(end);
                end = (int)(now / _seconds);
            }
            var points = new List<(double, double)>(Math.Min(MaxPoints, end + 2)) { (0d, 0d) };
            double last = 0d;
            for (int i = 0; i <= end; i++)
            {
                last = i < _cumulative.Count ? _cumulative[i] : last;
                points.Add((Math.Min((i + 1) * _seconds, now), last));
            }
            return new ChartSeries(points, _total);
        }
    }
}
