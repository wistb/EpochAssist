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

    private static string Value(object value) => value switch
    {
        string[] list => "[" + string.Join(", ", list) + "]",
        double d => d.ToString("0.##", s_inv),
        _ => Convert.ToString(value, s_inv) ?? string.Empty,
    };

    // sessions fill <count> <kb each> [days back]: test sessions (marked test in the header) with back-dated names, the
    // oldest first, for the rotation checks. They go in the real history folder; back it up first.
    private static CommandResult Fill(CommandCall call)
    {
        if (!int.TryParse(call.Lower(2), out int count) || count < 1 || count > 2000 || !int.TryParse(call.Lower(3), out int kb) || kb < 1 || kb > 20000)
            return CommandResult.Error("usage: sessions fill <count 1-2000> <kb each 1-20000> [days back]");
        int days = int.TryParse(call.Lower(4), out int d) && d >= 0 ? d : 30;
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
