using System.Globalization;
using System.Text;

namespace EpochAssist.Core;

/// <summary>Which sessions an export takes: the current one, those started in the last N days (the current one
/// included), or all.</summary>
internal readonly record struct ExportRange(string Kind, int Days = 0)
{
    public static readonly ExportRange Current = new("current"), All = new("all");
    public static ExportRange LastDays(int days) => new("days", days);

    public string Text => Kind == "days" ? $"last {Days} days" : Kind == "current" ? "current session" : "all";
}

/// <summary>An export's outcome: the two file names and their row counts and the sessions skipped as unreadable, or the error.</summary>
internal sealed record ExportResult(string? SessionsFile, string? RecordsFile, int SessionRows, int RecordRows, int Skipped, string? Error)
{
    public bool Ok => Error == null;

    /// <summary>"exported 37 sessions: exports/sessions-….csv (37 rows), exports/records-….csv (11204 rows)".</summary>
    public string Text => Ok
        ? $"exported {SessionRows} sessions: exports/{SessionsFile} ({SessionRows} rows), exports/{RecordsFile} ({RecordRows} rows)" + (Skipped > 0 ? $"; {Skipped} unreadable skipped" : string.Empty)
        : $"export failed: {Error}";
}

/// <summary>
/// Session history as two CSV files in UserData/EpochAssist/exports (contracts/csv-export.md): one row per session and
/// one per record. UTF-8 with a byte order mark, CRLF rows, comma separated with RFC 4180 quoting, numbers in the
/// invariant culture and local times as yyyy-MM-dd HH:mm:ss, which Excel, LibreOffice Calc and Google Sheets read.
/// The current session's batch is written first, and the export runs on the session writer's thread after it.
/// </summary>
internal static class SessionExport
{
    private static readonly string[] s_sessionColumns =
    {
        "session", "character", "mode", "start", "end", "end_kind", "duration_s", "xp", "gold_gained", "gold_spent",
        "drops_normal", "drops_magic", "drops_rare", "drops_exalted", "drops_unique", "drops_set", "drops_legendary", "drops_other",
        "deaths", "echoes", "picks", "town_s", "echo_s", "dungeon_s", "other_s", "mod_version", "game_version", "missing", "drops_shown",
        "start_level", "end_level", "levels_gained", "echoes_completed", "echoes_left", "echo_clear_avg_s", "echo_total_avg_s",
        "favour_gained", "favour_used", "reputation_gained", "rank_ups", "amber_gained", "amber_used", "factions", "left_out",
    };
    private static readonly string[] s_recordColumns =
    {
        "session", "time", "session_s", "kind", "amount", "total", "name", "base", "rarity", "scene", "zone_kind", "monolith",
        "killer", "types", "text", "modifier", "stability", "hex", "shown", "faction",
    };
    private static readonly UTF8Encoding s_bomUtf8 = new(true);

    private static volatile ExportResult? s_finished;
    private static bool s_running;

    /// <summary>An export is writing; deleting a session waits for it.</summary>
    public static bool Running => s_running;

    /// <summary>The page's range: 0 current session, 1 last 7 days, 2 last 30 days, 3 all (runtime only, not stored).</summary>
    public static int PageRange { get; set; } = 3;
    public static readonly string[] PageRanges = { "Current session", "Last 7 days", "Last 30 days", "All" };

    /// <summary>The last export's result on the page, or empty.</summary>
    public static string LastText { get; private set; } = string.Empty;
    public static bool LastOk { get; private set; } = true;

    public static ExportRange RangeOf(int page) => page switch { 0 => ExportRange.Current, 1 => ExportRange.LastDays(7), 2 => ExportRange.LastDays(30), _ => ExportRange.All };

    /// <summary>Starts an export; the result arrives through onDone on the main thread (Poll), and as a toast.</summary>
    public static bool Start(ExportRange range, Action<ExportResult>? onDone = null)
    {
        if (s_running) return false;
        s_running = true;
        s_onDone = onDone;
        Sessions.FlushForExport();
        // The open session's live totals, taken here on the main thread; its file has records but no totals yet.
        var current = Sessions.Current;
        string? currentId = current?.Header.Id;
        var currentTotals = current?.Totals;
        var leftOut = new HashSet<string>(SessionTrends.All, StringComparer.Ordinal);
        SessionWriter.Post(() => s_finished = Write(range, currentId, currentTotals, leftOut));
        return true;
    }

    private static Action<ExportResult>? s_onDone;

    /// <summary>Main thread, once a frame: hands a finished export to its caller, the page line and a toast.</summary>
    internal static void Poll()
    {
        var result = s_finished;
        if (result == null) return;
        s_finished = null;
        // Paths in an error become UserData/EpochAssist/... or their last part, as everywhere the player can see.
        if (result.Error != null) result = result with { Error = Diagnostics.Scrub(result.Error) };
        s_running = false;
        LastText = result.Ok ? $"{result.SessionsFile}, {result.RecordsFile} ({result.SessionRows} sessions, {result.RecordRows} records)" : result.Error!;
        LastOk = result.Ok;
        if (result.Ok) Log.Info($"Sessions {result.Text}");
        else Log.Warn($"Sessions {result.Text}");
        Toasts.Show("sessions", result.Ok ? $"Sessions exported: {result.SessionsFile} and {result.RecordsFile}." : $"Sessions export failed: {result.Error}", result.Ok ? ToastKind.Info : ToastKind.Warning);
        var done = s_onDone;
        s_onDone = null;
        done?.Invoke(result);
    }

    /// <summary>Drops a pending export's callback (the feature stopped); the writer still finishes the files.</summary>
    internal static void Reset()
    {
        s_onDone = null;
        s_running = false;
        s_finished = null;
    }

    // ---- Writing (the writer's thread) ----

    private static ExportResult Write(ExportRange range, string? currentId, SessionTotals? currentTotals, HashSet<string> leftOut)
    {
        var sessions = new List<(string Id, SessionFile File)>();
        int skipped = 0;
        try
        {
            var since = range.Kind == "days" ? DateTimeOffset.Now.AddDays(-range.Days) : DateTimeOffset.MinValue;
            foreach (string path in SessionFiles.Files())
            {
                string id = Path.GetFileNameWithoutExtension(path);
                if (range.Kind == "current" && id != currentId) continue;
                var file = SessionFiles.Read(path);
                if (file.Header == null)
                {
                    skipped++;
                    continue;
                }
                if (file.Header.Test) continue;
                if (range.Kind == "days" && file.Header.Start < since && id != currentId) continue;
                sessions.Add((id, file));
            }
        }
        catch (Exception e)
        {
            return new ExportResult(null, null, 0, 0, skipped, $"history can't be read: {e.Message}");
        }

        var sessionRows = new StringBuilder();
        var recordRows = new StringBuilder();
        Row(sessionRows, s_sessionColumns);
        Row(recordRows, s_recordColumns);
        int records = 0;
        foreach (var (id, file) in sessions)
        {
            SessionRow(sessionRows, id, file, id == currentId ? currentTotals : null, leftOut.Contains(id));
            foreach (var record in file.Records)
            {
                if (record.Kind is "test" or "totals") continue;
                RecordRow(recordRows, id, file.Header!, record);
                records++;
            }
        }

        try
        {
            Directory.CreateDirectory(ProfileTransfer.Dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture), suffix = string.Empty;
            for (int n = 2; File.Exists(Path.Combine(ProfileTransfer.Dir, $"sessions-{stamp}{suffix}.csv")) || File.Exists(Path.Combine(ProfileTransfer.Dir, $"records-{stamp}{suffix}.csv")); n++)
                suffix = $"-{n}";
            string sessionsFile = $"sessions-{stamp}{suffix}.csv", recordsFile = $"records-{stamp}{suffix}.csv";
            Save(Path.Combine(ProfileTransfer.Dir, sessionsFile), sessionRows.ToString());
            Save(Path.Combine(ProfileTransfer.Dir, recordsFile), recordRows.ToString());
            return new ExportResult(sessionsFile, recordsFile, sessions.Count, records, skipped, null);
        }
        catch (Exception e)
        {
            return new ExportResult(null, null, 0, 0, skipped, $"folder can't be written: {e.Message}");
        }
    }

    // Written beside the target first and moved into place without overwriting, so a file is never half written.
    private static void Save(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, s_bomUtf8);
        File.Move(tmp, path, false);
    }

    private static void SessionRow(StringBuilder csv, string id, SessionFile file, SessionTotals? live, bool leftOut)
    {
        var h = file.Header!;
        SessionTotals totals;
        string endKind, end = string.Empty;
        if (file.End != null)
        {
            totals = file.End.Totals;
            endKind = SessionFiles.KnownEnd(file.End.Kind);
            end = Time(h.Start, file.End.T);
        }
        else
        {
            var rebuilt = new SessionTotals();
            foreach (var record in file.Records) rebuilt.Add(record);
            totals = live ?? file.LastTotals ?? rebuilt.At(file.Records.Count > 0 ? file.Records.Max(r => r.T) : 0d);
            endKind = "open";
        }
        var fields = new List<string>
        {
            id, CharacterText(h.Character), h.Mode, Time(h.Start, 0d), end, endKind, Sec(totals.Duration), Int(totals.Xp), Int(totals.GoldGained), Int(totals.GoldSpent),
        };
        fields.AddRange(SessionTotals.DropRarities.Select(r => Int(totals.DropsOf(r))));
        fields.Add(Int(totals.DropsOther));
        fields.AddRange(new[] { Int(totals.Deaths), Int(totals.Echoes), Int(totals.Picks) });
        fields.AddRange(SessionTotals.ZoneKinds.Select(z => Sec(totals.ZoneSeconds.GetValueOrDefault(z))));
        fields.AddRange(new[] { h.ModVersion, h.GameVersion, string.Join(";", h.Missing), Int(totals.DropsShown) });
        // Added columns (contracts/csv-additions.md): levels, echo runs, factions without and with the Weaver's amber.
        int? startLevel = totals.StartLevel ?? h.StartLevel;
        int completed = totals.EchoClearCount > 0 ? totals.EchoClearCount : totals.Echoes;
        var others = totals.Factions.Where(f => f.Key != SessionStats.AmberFaction).Select(f => f.Value).ToList();
        var amber = totals.Factions.GetValueOrDefault(SessionStats.AmberFaction);
        fields.AddRange(new[]
        {
            startLevel is int start ? Int(start) : string.Empty, startLevel is int s ? Int(s + totals.LevelUps) : string.Empty, Int(totals.LevelUps),
            Int(completed), Int(totals.EchoesLeft),
            totals.EchoClearCount > 0 ? Sec(totals.EchoClearSum / totals.EchoClearCount) : string.Empty,
            totals.EchoTotalCount > 0 ? Sec(totals.EchoTotalSum / totals.EchoTotalCount) : string.Empty,
            Int(others.Sum(f => f.FavourGained)), Int(others.Sum(f => f.FavourSpent)), Int(others.Sum(f => f.ReputationGained)), Int(others.Sum(f => f.RankUps)),
            amber != null ? Int(amber.FavourGained) : string.Empty, amber != null ? Int(amber.FavourSpent) : string.Empty,
            string.Join(";", totals.Factions.Select(f => $"{f.Key}:{f.Value.FavourGained}/{f.Value.FavourSpent}/{f.Value.ReputationGained}/{f.Value.RankUps}")),
            leftOut ? "yes" : string.Empty,
        });
        Row(csv, fields);
    }

    private static void RecordRow(StringBuilder csv, string id, SessionHeader h, SessionRecord r)
    {
        // session, time, session_s, kind, amount, total, name, base, rarity, scene, zone_kind, monolith, killer, types,
        // text, modifier, stability, hex, shown (by the loot filter), faction
        var f = new string[s_recordColumns.Length];
        f[0] = id;
        f[1] = Time(h.Start, r.T);
        f[2] = Sec(r.T);
        f[3] = r.Kind;
        for (int i = 4; i < f.Length; i++) f[i] = string.Empty;
        switch (r.Kind)
        {
            case "zone":
                f[6] = r.Text("name") ?? string.Empty;
                f[9] = r.Text("scene") ?? string.Empty;
                f[10] = r.Text("kind") ?? string.Empty;
                f[11] = r.Flag("monolith") is bool m ? (m ? "true" : "false") : string.Empty;
                break;
            case "death":
                f[12] = r.Text("killer") ?? string.Empty;
                f[13] = r.List("types") is { } types ? string.Join(";", types) : string.Empty;
                f[14] = OneLine(r.Text("text"));
                break;
            case "xp" or "gold":
                f[4] = Num(r.Number("amount"));
                f[5] = Num(r.Number("total"));
                break;
            case "drop":
                f[4] = "1";
                f[6] = r.Text("name") ?? string.Empty;
                f[7] = r.Text("base") ?? string.Empty;
                f[8] = r.Text("rarityName") ?? string.Empty;
                f[18] = r.Flag("shown") is bool shown ? (shown ? "true" : "false") : string.Empty;
                break;
            case "echo":
                f[9] = r.Text("scene") ?? string.Empty;
                break;
            case "pick":
                f[15] = r.Text("modifier") ?? string.Empty;
                f[16] = Num(r.Number("stability"));
                f[17] = Num(r.Number("hex"));
                f[6] = r.Text("reward") ?? string.Empty;
                f[5] = Num(r.Number("corruption"));
                break;
            case "level":
                f[4] = Num(r.Number("level"));
                break;
            case "favour" or "rep":
                f[19] = r.Text("faction") ?? string.Empty;
                f[4] = Num(r.Number("amount"));
                f[5] = Num(r.Number("total"));
                break;
            case "rank":
                f[19] = r.Text("faction") ?? string.Empty;
                f[4] = Num(r.Number("rank"));
                break;
            case "gap":
                f[4] = r.Number("from") is double from && r.Number("to") is double to ? Sec(to - from) : string.Empty;
                f[14] = r.Text("reason") ?? string.Empty;
                break;
        }
        // Records the dev `sessions keep` made are marked, so a spreadsheet can leave them out.
        if (r.Flag("dev") == true && f[14].Length == 0) f[14] = "dev";
        Row(csv, f);
    }

    /// <summary>Offline the character's name; online "online" and the first 8 characters of its id; unknown when unread.</summary>
    private static string CharacterText(SessionCharacter? c) =>
        c == null ? "unknown" : c.Id is { Length: > 0 } id ? $"online {id[..Math.Min(8, id.Length)]}" : c.Name ?? "unknown";

    private static string Time(DateTimeOffset start, double t) =>
        start == default ? string.Empty : start.AddSeconds(t).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Sec(double seconds) => seconds.ToString("0.0", CultureInfo.InvariantCulture);
    private static string Int(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Num(double? value) => value is double d ? d.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;
    private static string OneLine(string? text) => text == null ? string.Empty : text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

    // RFC 4180: a field holding a comma, quote, CR or LF is quoted, with quotes doubled; rows end in CRLF.
    private static void Row(StringBuilder csv, IEnumerable<string> fields)
    {
        bool first = true;
        foreach (string field in fields)
        {
            if (!first) csv.Append(',');
            first = false;
            if (field.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0) csv.Append('"').Append(field.Replace("\"", "\"\"")).Append('"');
            else csv.Append(field);
        }
        csv.Append("\r\n");
    }
}
