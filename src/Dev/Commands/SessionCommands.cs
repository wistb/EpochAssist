using System.Globalization;
using EpochAssist.Core;

namespace EpochAssist.Dev;

// The sessions command's dev verbs: show, fill, flood, rotate, crash and writer (registered by the dev bridge feature).
// Output names sessions by start and mode only, never by a character's name.
internal static class SessionCommands
{
    private const int DefaultShow = 20;
    private const int SettleFrames = 2;
    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;

    public static void Register(string owner)
    {
        Commands.AddDevVerb("sessions", "show", owner, Show);
        Commands.AddDevVerb("sessions", "fill", owner, Fill);
        Commands.AddDevVerb("sessions", "flood", owner, Flood);
        Commands.AddDevVerb("sessions", "stats", owner, Stats);
        Commands.AddDevVerb("sessions", "keep", owner, KeepRecord);
        Commands.AddDevVerb("sessions", "cost", owner, Cost);
        Commands.AddDevVerb("sessions", "trends", owner, Trends);
        Commands.AddDevVerb("sessions", "sample", owner, call =>
        {
            // on | off | quiet: the Sample data switch from the bridge; quiet hides the switch and banner (page images).
            switch (call.Lower(2))
            {
                case "on": Ui.SessionsPage.ShowSample = true; Ui.SessionsPage.SampleQuiet = false; break;
                case "quiet": Ui.SessionsPage.ShowSample = true; Ui.SessionsPage.SampleQuiet = true; break;
                case "off": Ui.SessionsPage.ShowSample = false; Ui.SessionsPage.SampleQuiet = false; break;
                default: return CommandResult.Error("usage: sessions sample on|off|quiet");
            }
            Ui.SessionsPage.Current.Reset();
            return CommandResult.Ok($"sessions sample: {call.Lower(2)}");
        });
        Commands.AddDevVerb("sessions", "names", owner, _ =>
        {
            // Each zone scene of the current session with the game's display name and echo name.
            var scenes = Sessions.Current?.Records.Where(r => r.Kind == "zone").Select(r => r.Text("scene") ?? string.Empty).Distinct().ToList() ?? new List<string>();
            var lines = new List<string> { $"ok sessions names: {scenes.Count} scenes" };
            lines.AddRange(scenes.Select(s => GameData.SceneNamesOf(s) is var n ? $"  {s}: display {n.Display ?? "-"}, echo {n.Echo ?? "-"}" : s));
            return new CommandResult(DevOutput.Reply("sessions", lines));
        });
        Commands.AddDevVerb("sessions", "delete", owner, call =>
        {
            string id = Arg(call, 2);
            if (id.Length == 0) return CommandResult.Error("usage: sessions delete <id> [during-export]");
            // during-export: starts an export in the same frame first, as a delete pressed while one runs.
            if (call.Lower(3) == "during-export" && !SessionExport.Start(ExportRange.All)) return CommandResult.Error("sessions delete: an export is already running");
            return Sessions.Delete(id, out string? reason) ? CommandResult.Ok($"sessions delete: deleted {Sessions.Label(id)}; {Sessions.HistoryText()}")
                : CommandResult.Error($"sessions delete: {reason}");
        });
        Commands.AddDevVerb("sessions", "rotate", owner, _ =>
        {
            var removed = Sessions.RotateNow();
            return CommandResult.Ok($"sessions rotate: removed {removed.Count}{(removed.Count > 0 ? ": " + string.Join(", ", removed.Select(r => Sessions.Label(Path.GetFileNameWithoutExtension(r)))) : string.Empty)}; {Sessions.HistoryText()}");
        });
        Commands.AddDevVerb("sessions", "crash", owner, _ =>
            Sessions.Crash() ? CommandResult.Ok("sessions crash: the session stays open without an end line; a new one starts while in game")
                : CommandResult.Error("sessions crash: no session"));
        Commands.AddDevVerb("sessions", "writer", owner, call =>
        {
            switch (call.Lower(2))
            {
                case "sync": SessionWriter.Synchronous = true; break;
                case "thread": SessionWriter.Synchronous = false; break;
                case "": break;
                default: return CommandResult.Error("usage: sessions writer [sync|thread]");
            }
            return CommandResult.Ok($"sessions writer: {(SessionWriter.Synchronous ? "synchronous" : "thread")}, thread {(SessionWriter.ThreadAlive ? "alive" : "stopped")}, "
                + $"writer threads in the process {SessionWriter.LiveThreads}{(SessionWriter.LastError is { } e ? $", last error {e}" : string.Empty)}");
        });
    }

    // sessions show [n]: the current session's header (mode and slot only), totals and last n records.
    private static CommandResult Show(CommandCall call)
    {
        int count = int.TryParse(call.Lower(2), out int n) && n > 0 ? n : DefaultShow;
        if (Sessions.Current is not { } view) return CommandResult.Ok($"sessions show: no session; {Sessions.StatusText()}");
        var h = view.Header;
        var t = view.Totals;
        var lines = new List<string>
        {
            $"ok sessions show: {(h.Id.Length > 0 ? Sessions.Label(h.Id) : "(not written yet)")} {h.CharacterLabel}, {view.Records.Count} records, end {view.EndKind}",
            $"header: process {h.Process}, clockStart {h.ClockStart.ToString("F2", s_inv)}, start {h.Start:yyyy-MM-dd HH:mm:ss zzz}, mod {h.ModVersion} build {h.Build}, game {h.GameVersion}, "
                + $"captured [{string.Join(", ", h.Captured)}], missing [{string.Join(", ", h.Missing)}]",
            $"totals: duration {Sessions.FormatDuration(t.Duration)} ({t.Duration.ToString("F1", s_inv)} s), xp {t.Xp}, gold +{t.GoldGained} -{t.GoldSpent}, "
                + $"drops {t.DropCount} [{string.Join(", ", t.Drops.Select(d => $"{d.Key} {d.Value}"))}], deaths {t.Deaths}, echoes {t.Echoes}, picks {t.Picks}, "
                + $"zones [{string.Join(", ", t.ZoneSeconds.Select(z => $"{z.Key} {z.Value.ToString("F1", s_inv)}"))}]",
            Sessions.StatusText(),
        };
        lines.AddRange(view.RecordsSince(view.Records.Count - count).Select(r =>
            $"  {r.T.ToString("F2", s_inv)} {r.Kind} {string.Join(", ", r.Fields.Select(f => $"{f.Key}={Value(f.Value)}"))}"));
        return new CommandResult(DevOutput.Reply("sessions", lines));
    }

    // sessions stats [current|<id>]: every figure the dashboard shows, as text, for checks against records and the CSV.
    private static CommandResult Stats(CommandCall call)
    {
        string which = Arg(call, 2);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        SessionStats stats;
        if (which.Length == 0 || which.Equals("current", StringComparison.OrdinalIgnoreCase))
        {
            if (Sessions.Current is not { } current) return CommandResult.Error("sessions stats: no session");
            stats = SessionStats.For(current, live: true);
        }
        else
        {
            if (Sessions.Read(which) is not { } view) return CommandResult.Error($"sessions stats: no readable session {which}");
            stats = SessionStats.For(view, live: view.EndKind == "open" && Sessions.Current?.Header.Id == which);
        }
        // Read and build, as opening it on the Past sessions page does.
        var lines = StatsLines(stats);
        lines[0] += $", read and built in {((System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d / System.Diagnostics.Stopwatch.Frequency).ToString("F0", s_inv)} ms";
        return new CommandResult(DevOutput.Reply("sessions", lines));
    }

    internal static List<string> StatsLines(SessionStats s)
    {
        var t = s.Totals;
        var e = s.Echo;
        var lines = new List<string>
        {
            $"ok sessions stats: {(s.Id.Length > 0 ? Sessions.Label(s.Id) : "(not written yet)")} {s.Header.CharacterLabel}, {(s.Live ? "live" : "past")}, end {s.EndKind}, "
                + $"cut {(s.Cut ? $"yes at {F(s.CutAt)}" : "no")}, version {s.Version}",
            $"time: duration {F(s.Duration)} ({Sessions.FormatDuration(s.Duration)}), town {F(s.TownSeconds)}, out of town {F(s.OutOfTownSeconds)}",
            $"xp: {t.Xp}, {R(s.XpRates)}",
            $"gold: gained {t.GoldGained}, spent {t.GoldSpent}, {R(s.GoldRates)}",
            $"drops: {t.DropCount}, {R(s.DropRates)}, shown {t.DropsShown}; "
                + string.Join(", ", SessionTotals.DropRarities.Select(r => $"{r} {t.DropsOf(r)}/{s.ShownByRarity.GetValueOrDefault(r)}")) + $", other {t.DropsOther}",
            $"deaths: {t.Deaths}, {R(s.DeathRates)}, last killer {(s.Deaths.Count > 0 ? s.Deaths[^1].Killer ?? "-" : "-")}",
            $"echoes: completed {e.Completed}, left {e.Left}, clear last {F(e.LastClear)} avg {F(e.AverageClear)} best {F(e.BestClear)}, total last {F(e.LastTotal)} avg {F(e.AverageTotal)}, "
                + $"per hour {F(e.PerHour)}; totals clear {F(t.EchoClearSum)}/{t.EchoClearCount}, total {F(t.EchoTotalSum)}/{t.EchoTotalCount}, left {t.EchoesLeft}",
            $"zones: {string.Join(", ", SessionTotals.ZoneKinds.Select(z => $"{z} {F(t.ZoneSeconds.GetValueOrDefault(z))} ({F(s.Duration > 0 ? t.ZoneSeconds.GetValueOrDefault(z) * 100d / s.Duration : 0d)}%)"))}",
            $"chart: xp {s.XpChart.Points.Count} points max {F(s.XpChart.Max)}, gold {s.GoldChart.Points.Count} points max {F(s.GoldChart.Max)}, gaps {s.Gaps.Count}",
            $"timeline: segments {s.Timeline.Segments.Count} raw, {s.Timeline.Drawn(s.Duration, 600f, SessionStats.DrawnSegments).Count} drawn at 600 px; "
                + $"marks {s.Timeline.Marks.Count} raw, {s.Timeline.DrawnMarks(s.Duration, 600f, SessionStats.DrawnMarks).Count} drawn",
            $"level: start {s.Level.StartLevel?.ToString(s_inv) ?? "-"}, now {s.Level.CurrentLevel?.ToString(s_inv) ?? "-"}, gained {s.Level.Gained}, "
                + $"xp {s.Level.XpInLevel?.ToString(s_inv) ?? "-"}/{s.Level.Threshold?.ToString(s_inv) ?? "-"}, percent {F(s.Level.Percent)}, to next {F(s.Level.TimeToNext)}"
                + (s.Level.LevelUps.Count > 0 ? "; ups " + string.Join(", ", s.Level.LevelUps.Select(u => $"{u.Level} at {F(u.T)}")) : string.Empty),
        };
        foreach (var f in s.Factions)
            lines.Add($"faction {f.Faction} ({f.Name}{(f.IsAmber ? ", amber" : string.Empty)}): favour +{f.FavourGained} -{f.FavourSpent} {R(f.FavourRates)}, "
                + $"rep +{f.ReputationGained} {R(f.ReputationRates)}, rank {f.Rank?.ToString(s_inv) ?? "-"} ups {f.RankUps}, percent {F(f.PercentToNext)}, to next {F(f.TimeToNext)}");
        lines.Add($"runs ({s.Runs.Count}):");
        foreach (var run in s.Runs)
            lines.Add($"  {run.Name} ({run.Scene}) start {F(run.Start)} clear {F(run.Clear)} total {F(run.Total(s.Duration))}{(run.End == null ? " (open)" : string.Empty)}: xp {run.Xp}, gold {run.Gold}, "
                + $"drops {run.Drops}, deaths {run.Deaths}, favour [{Pairs(run.Favour)}], rep [{Pairs(run.Reputation)}], pick {(run.HasPick ? $"{run.Modifier ?? "-"} stability {run.Stability?.ToString(s_inv) ?? "-"} reward {run.Reward ?? "-"} corruption {run.Corruption?.ToString(s_inv) ?? "-"}" : "none")}");
        lines.Add($"deaths ({s.Deaths.Count}), newest first:");
        for (int i = s.Deaths.Count - 1; i >= 0; i--)
            lines.Add($"  {F(s.Deaths[i].T)} {s.Deaths[i].Zone ?? "-"} killer {s.Deaths[i].Killer ?? "-"} types {GameEvents.List(s.Deaths[i].Types)}");
        lines.Add($"picks ({s.Picks.Count}), newest first:");
        for (int i = s.Picks.Count - 1; i >= 0; i--)
            lines.Add($"  {F(s.Picks[i].T)} {s.Picks[i].Modifier ?? "-"} stability {s.Picks[i].Stability?.ToString(s_inv) ?? "-"} reward {s.Picks[i].Reward ?? "-"} corruption {s.Picks[i].Corruption?.ToString(s_inv) ?? "-"}");
        return lines;
    }

    // Argument i (from 1) as typed, or empty: session ids and field values keep their case.
    private static string Arg(CommandCall call, int i) => i >= 1 && i <= call.Args.Length ? call.Args[i - 1] : string.Empty;

    private static string F(double? value) => value is double d ? d.ToString("0.##", s_inv) : "-";

    private static string R(Rates r) => $"per hour {F(r.Session)}, out of town {F(r.OutOfTown)}, last 10 min {F(r.Recent)}";

    private static string Pairs(Dictionary<string, long> map) => string.Join(", ", map.Select(p => $"{p.Key} {p.Value}"));

    // sessions cost [frames]: the menu's frame time while the Session tab draws (the menu feature's average and worst over
    // the last seconds), the chart primitives drawn per frame and the texture redraws over n frames.
    private static CommandResult Cost(CommandCall call)
    {
        int frames = int.TryParse(call.Lower(2), out int n) && n > 0 ? Math.Min(n, 10000) : 120;
        if (FeatureHost.Find("menu") is not { } menu) return CommandResult.Error("sessions cost: no menu feature");
        int start = UnityEngine.Time.frameCount, primitives = Ui.SessionCharts.Primitives, redraws = Ui.SessionCharts.Redraws;
        return new CommandResult("ok sessions cost: measuring")
        {
            Pending = () =>
            {
                int done = UnityEngine.Time.frameCount - start;
                if (done < frames) return null;
                var f = menu.Record.Frames;
                string page = Ui.MenuRoutes.Page?.Path ?? "none";
                return $"ok sessions cost: {done} frames, menu {(Ui.Menu.Visible ? "open" : "closed")} on {page}: avg {f.AverageMs.ToString("F3", s_inv)} ms, worst {f.WorstMs.ToString("F3", s_inv)} ms, "
                    + $"last second {f.LastSecondMs.ToString("F3", s_inv)} ms; chart primitives {((Ui.SessionCharts.Primitives - primitives) / (double)done).ToString("F1", s_inv)} per frame, "
                    + $"texture redraws {Ui.SessionCharts.Redraws - redraws}, chart textures {Ui.SessionCharts.TextureCount}"
                    + (FeatureHost.Find("events") is { } events ? $"; events feature (producers, the faction reader among them) avg {events.Record.Frames.AverageMs.ToString("F3", s_inv)} ms, worst {events.Record.Frames.WorstMs.ToString("F3", s_inv)} ms" : string.Empty);
            },
        };
    }

    // sessions trends [list | out <id> | in <id>]: the sessions left out of trends, as the page's toggle sets them.
    private static CommandResult Trends(CommandCall call)
    {
        string id = Arg(call, 3);
        switch (call.Lower(2))
        {
            case "" or "list":
                return CommandResult.Ok($"sessions trends: {SessionTrends.All.Count} left out{(SessionTrends.All.Count > 0 ? ": " + string.Join(", ", SessionTrends.All.OrderBy(i => i, StringComparer.Ordinal).Select(Sessions.Label)) : string.Empty)}");
            case "out" or "in" when id.Length > 0:
                if (Sessions.List().All(e => e.Id != id)) return CommandResult.Error($"sessions trends: no session {id}");
                SessionTrends.Set(id, call.Lower(2) == "out");
                return CommandResult.Ok($"sessions trends: {Sessions.Label(id)} {(call.Lower(2) == "out" ? "left out" : "back in")}");
            default:
                return CommandResult.Error("usage: sessions trends [list | out <id> | in <id>]");
        }
    }

    // sessions keep <kind> [<field>=<value> …]: a synthetic record in the current session, marked dev, for rare cases.
    private static CommandResult KeepRecord(CommandCall call)
    {
        string kind = Arg(call, 2);
        if (kind.Length == 0) return CommandResult.Error("usage: sessions keep <kind> [<field>=<value> …]");
        if (Sessions.Current is not { } view) return CommandResult.Error("sessions keep: no session");
        var fields = new Dictionary<string, object>();
        for (int i = 3; Arg(call, i) is { Length: > 0 } arg; i++)
        {
            int eq = arg.IndexOf('=');
            if (eq <= 0) return CommandResult.Error($"sessions keep: {arg} isn't field=value");
            string name = arg[..eq], text = arg[(eq + 1)..];
            fields[name] = long.TryParse(text, NumberStyles.Integer, s_inv, out long l) ? l
                : double.TryParse(text, NumberStyles.Float, s_inv, out double d) ? d
                : text is "true" or "false" ? text == "true" : text;
        }
        fields["dev"] = true;
        Sessions.Keep(kind, fields);
        return CommandResult.Ok($"sessions keep: {kind} at {F(view.Records.Count > 0 ? view.Records[^1].T : 0d)}");
    }

    private static string Value(object value) => value switch
    {
        string[] list => "[" + string.Join(", ", list) + "]",
        double d => d.ToString("0.##", s_inv),
        _ => Convert.ToString(value, s_inv) ?? string.Empty,
    };

    // sessions fill <count> <kb each> [days back] [realistic]: test sessions (marked test in the header) with back-dated
    // names, the oldest first, for the rotation checks; realistic ones are shaped like play (zones, echo runs with picks,
    // XP, gold, drops, deaths, favour and level-ups) and listed like real sessions of offline slot 9, for the past
    // sessions page and the trend. They go in the real history folder; back it up first.
    private static CommandResult Fill(CommandCall call)
    {
        if (!int.TryParse(call.Lower(2), out int count) || count < 1 || count > 2000 || !int.TryParse(call.Lower(3), out int kb) || kb < 1 || kb > 20000)
            return CommandResult.Error("usage: sessions fill <count 1-2000> <kb each 1-20000> [days back] [realistic]");
        int days = int.TryParse(call.Lower(4), out int d) && d >= 0 ? d : 30;
        bool realistic = call.Lower(4) == "realistic" || call.Lower(5) == "realistic";
        if (realistic) return FillRealistic(count, kb, days);
        var character = new SessionCharacter("offline", 9, "filltest", null);
        var now = DateTimeOffset.Now;
        string filler = new('x', 900);
        for (int i = 0; i < count; i++)
        {
            var start = now.AddDays(-days).AddMinutes(-(count - i));
            var header = new SessionHeader
            {
                Process = "test", Start = start, Character = character, ModVersion = BuildInfo.Version, Build = Runtime.BuildId, Captured = Sessions.Captured, Test = true,
            };
            header.Id = SessionFiles.FreeId(SessionFiles.Stem(start, character));
            var text = new System.Text.StringBuilder(SessionFiles.HeaderLine(header)).Append('\n');
            double t = 0d;
            var totals = new SessionTotals();
            while (text.Length < kb * 1024)
            {
                t += 1d;
                text.Append(SessionFiles.RecordLine(new SessionRecord(t, "test", new Dictionary<string, object> { ["pad"] = filler }))).Append('\n');
            }
            text.Append(SessionFiles.EndLine(new SessionEnd("normal", t, totals.At(t)))).Append('\n');
            try
            {
                SessionFiles.Append(SessionFiles.PathOf(header.Id), text.ToString());
            }
            catch (Exception e)
            {
                return CommandResult.Error($"sessions fill: stopped after {i}: {e.Message}");
            }
        }
        return CommandResult.Ok($"sessions fill: {count} test sessions of {kb} KB, from {days} days back; {Sessions.HistoryText()}") with { Settle = SettleFrames };
    }

    private static CommandResult FillRealistic(int count, int kb, int days)
    {
        var character = new SessionCharacter("offline", 9, "filltest", null);
        var now = DateTimeOffset.Now;
        var random = new Random(count * 7919 + kb);
        for (int i = 0; i < count; i++)
        {
            // Spread over the days back, oldest first, with a couple a day.
            var start = now.AddDays(-days * (count - i) / (double)Math.Max(1, count)).AddMinutes(-random.Next(0, 600));
            int level = 40 + random.Next(0, 50);
            var header = new SessionHeader
            {
                Process = "test", Start = start, Character = character, ModVersion = BuildInfo.Version, Build = Runtime.BuildId, Captured = Sessions.Captured, StartLevel = level,
            };
            header.Id = SessionFiles.FreeId(SessionFiles.Stem(start, character));
            // About kb each: a record line is ~110 bytes.
            var (records, totals, end) = SessionSamples.Generate(random, level, (_, sofar) => sofar.Count * 110 < kb * 1024);
            var text = new System.Text.StringBuilder(SessionFiles.HeaderLine(header)).Append('\n');
            foreach (var record in records) text.Append(SessionFiles.RecordLine(record)).Append('\n');
            text.Append(SessionFiles.EndLine(new SessionEnd("normal", end, totals.At(end, ended: true)))).Append('\n');
            try
            {
                SessionFiles.Append(SessionFiles.PathOf(header.Id), text.ToString());
            }
            catch (Exception e)
            {
                return CommandResult.Error($"sessions fill: stopped after {i}: {e.Message}");
            }
        }
        return CommandResult.Ok($"sessions fill: {count} realistic sessions of about {kb} KB (offline, slot 9), from {days} days back; {Sessions.HistoryText()}") with { Settle = SettleFrames };
    }

    // sessions flood <n>: n test records into the current session (skipped by totals and export).
    private static CommandResult Flood(CommandCall call)
    {
        if (!int.TryParse(call.Lower(2), out int count) || count < 1 || count > 1_000_000) return CommandResult.Error("usage: sessions flood <n>");
        if (Sessions.Current == null) return CommandResult.Error("sessions flood: no session");
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < count; i++) Sessions.Keep("test", new Dictionary<string, object> { ["n"] = (long)i });
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000d / System.Diagnostics.Stopwatch.Frequency;
        return CommandResult.Ok($"sessions flood: {count} test records in {ms.ToString("F1", s_inv)} ms ({(ms * 1000d / count).ToString("F2", s_inv)} µs each); {Sessions.StatusText()}");
    }
}
