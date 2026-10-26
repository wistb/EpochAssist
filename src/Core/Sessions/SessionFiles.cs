using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MelonLoader.Utils;

namespace EpochAssist.Core;

/// <summary>The character a session belongs to: mode, slot and name offline, the game's id online. The name and id are
/// the player's own, kept in their session files and exports only, never in logs, diagnostics or reports.</summary>
internal sealed record SessionCharacter(string Mode, int? Slot, string? Name, string? Id)
{
    /// <summary>For lists, Diagnostics and the log: the mode, and the slot where there is one.</summary>
    public string Label => Slot is int slot ? $"{Mode}, slot {slot}" : Mode;
}

/// <summary>A session file's first line (format contract: contracts/session-format.md).</summary>
internal sealed class SessionHeader
{
    /// <summary>The file name without .jsonl; set at the first write, when the character is known.</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>The game process's start time, as crash reports write it: a resume (same process) or a crash (another).</summary>
    public string Process { get; init; } = string.Empty;
    public double ClockStart { get; init; }
    public DateTimeOffset Start { get; init; }
    /// <summary>Null when the character couldn't be read.</summary>
    public SessionCharacter? Character { get; set; }
    public string ModVersion { get; init; } = string.Empty;
    public string Build { get; init; } = string.Empty;
    public string GameVersion { get; init; } = string.Empty;
    public IReadOnlyList<string> Captured { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Missing { get; set; } = Array.Empty<string>();
    /// <summary>Written by the dev `sessions fill`.</summary>
    public bool Test { get; init; }

    public string Mode => Character?.Mode ?? "unknown";
    public string CharacterLabel => Character?.Label ?? "unknown";
}

/// <summary>
/// One line of a session: its session time, kind and fields (string → long, double, string, bool or string array), so a
/// new kind needs no new type. Kinds from newer builds are kept as they are; totals skip them.
/// </summary>
internal sealed class SessionRecord
{
    public SessionRecord(double t, string kind, IReadOnlyDictionary<string, object> fields)
    {
        T = t;
        Kind = kind;
        Fields = fields;
    }

    public double T { get; }
    public string Kind { get; }
    public IReadOnlyDictionary<string, object> Fields { get; }

    public string? Text(string field) => Fields.TryGetValue(field, out var value) ? value as string : null;
    public long? Whole(string field) => Fields.TryGetValue(field, out var value) ? value switch { long l => l, int i => i, double d => (long)d, _ => null } : null;
    public double? Number(string field) => Fields.TryGetValue(field, out var value) ? value switch { long l => l, int i => i, double d => d, _ => null } : null;
    public bool? Flag(string field) => Fields.TryGetValue(field, out var value) && value is bool b ? b : null;
    public string[]? List(string field) => Fields.TryGetValue(field, out var value) ? value as string[] : null;
}

/// <summary>
/// A session's totals: kept live while it records, written in `totals` and `end` lines, rebuilt from records when a
/// crashed session is closed. Zone seconds run from each zone record to the next (or the end).
/// </summary>
internal sealed class SessionTotals
{
    public static readonly string[] DropRarities = { "normal", "magic", "rare", "exalted", "unique", "set", "legendary" };
    public static readonly string[] ZoneKinds = { "town", "echo", "dungeon", "other" };

    public double Duration;
    public long Xp, GoldGained, GoldSpent;
    public readonly Dictionary<string, int> Drops = new();
    public int Deaths, Echoes, Picks;
    /// <summary>Drops the player's loot filter showed (drops recorded before the filter was read count in neither).</summary>
    public int DropsShown;
    public readonly Dictionary<string, double> ZoneSeconds = ZoneKinds.ToDictionary(k => k, _ => 0d);

    // The zone the player is in and since when (session seconds), so its time counts up to now.
    private string? _zone;
    private double _zoneSince;

    public string? Zone => _zone;

    public void Add(SessionRecord record)
    {
        switch (record.Kind)
        {
            case "zone":
                CloseZone(record.T);
                _zone = record.Text("kind");
                _zoneSince = record.T;
                break;
            case "died": Deaths++; break;
            case "xp": Xp += record.Whole("amount") ?? 0; break;
            case "gold":
                long amount = record.Whole("amount") ?? 0;
                if (amount > 0) GoldGained += amount;
                else GoldSpent -= amount;
                break;
            case "drop":
                string rarity = (record.Text("rarityName") ?? "other").ToLowerInvariant();
                Drops[rarity] = Drops.TryGetValue(rarity, out int n) ? n + 1 : 1;
                if (record.Flag("shown") == true) DropsShown++;
                break;
            case "echo": Echoes++; break;
            case "pick": Picks++; break;
        }
        if (record.Kind != "test") Duration = Math.Max(Duration, record.T);
    }

    /// <summary>Continues counting the zone the player is in (a resume after a cut, whose zone records weren't kept).</summary>
    public void Continue(string zone, double since)
    {
        _zone = zone;
        _zoneSince = since;
    }

    /// <summary>A copy with the current zone counted up to t, and t as the duration.</summary>
    public SessionTotals At(double t)
    {
        var copy = new SessionTotals { Duration = Math.Max(Duration, t), Xp = Xp, GoldGained = GoldGained, GoldSpent = GoldSpent, Deaths = Deaths, Echoes = Echoes, Picks = Picks, DropsShown = DropsShown };
        foreach (var (k, v) in Drops) copy.Drops[k] = v;
        foreach (var (k, v) in ZoneSeconds) copy.ZoneSeconds[k] = v;
        if (_zone != null && t > _zoneSince) copy.ZoneSeconds[_zone] = copy.ZoneSeconds.GetValueOrDefault(_zone) + (t - _zoneSince);
        return copy;
    }

    public int DropsOf(string rarity) => Drops.GetValueOrDefault(rarity);
    public int DropsOther => Drops.Where(d => Array.IndexOf(DropRarities, d.Key) < 0).Sum(d => d.Value);
    public int DropCount => Drops.Values.Sum();

    private void CloseZone(double t)
    {
        if (_zone != null && t > _zoneSince) ZoneSeconds[_zone] = ZoneSeconds.GetValueOrDefault(_zone) + (t - _zoneSince);
        _zone = null;
    }

    public void Write(Utf8JsonWriter json)
    {
        json.WriteStartObject();
        json.WriteNumber("duration", SessionFiles.Seconds(Duration));
        json.WriteNumber("xp", Xp);
        json.WriteNumber("goldGained", GoldGained);
        json.WriteNumber("goldSpent", GoldSpent);
        json.WriteStartObject("drops");
        foreach (var (rarity, count) in Drops) json.WriteNumber(rarity, count);
        json.WriteEndObject();
        json.WriteNumber("dropsShown", DropsShown);
        json.WriteNumber("deaths", Deaths);
        json.WriteNumber("echoes", Echoes);
        json.WriteNumber("picks", Picks);
        json.WriteStartObject("zoneSeconds");
        foreach (var (zone, seconds) in ZoneSeconds) json.WriteNumber(zone, Math.Round(seconds, 1));
        json.WriteEndObject();
        json.WriteEndObject();
    }

    public static SessionTotals Read(JsonElement e)
    {
        var totals = new SessionTotals
        {
            Duration = Num(e, "duration"), Xp = (long)Num(e, "xp"), GoldGained = (long)Num(e, "goldGained"), GoldSpent = (long)Num(e, "goldSpent"),
            Deaths = (int)Num(e, "deaths"), Echoes = (int)Num(e, "echoes"), Picks = (int)Num(e, "picks"), DropsShown = (int)Num(e, "dropsShown"),
        };
        if (e.TryGetProperty("drops", out var drops) && drops.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in drops.EnumerateObject())
                if (p.Value.TryGetInt32(out int n)) totals.Drops[p.Name] = n;
        }
        if (e.TryGetProperty("zoneSeconds", out var zones) && zones.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in zones.EnumerateObject())
                if (p.Value.TryGetDouble(out double s)) totals.ZoneSeconds[p.Name] = s;
        }
        return totals;
    }

    private static double Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetDouble(out double d) ? d : 0d;
}

/// <summary>A session's end line: normal, unexpected (closed at the next start) or cut (reached its size limit).</summary>
internal sealed record SessionEnd(string Kind, double T, SessionTotals Totals);

/// <summary>A session file read in full: header, records (gap and unknown kinds included, totals lines not), the last
/// totals line, the end line, damaged lines, or the problem that kept it from reading.</summary>
internal sealed class SessionFile
{
    public SessionHeader? Header;
    public readonly List<SessionRecord> Records = new();
    public SessionTotals? LastTotals;
    public SessionEnd? End;
    public int Damaged;
    public string? Problem;
    public long Bytes;
}

/// <summary>A past or current session as lists show it, from its first and last lines only.</summary>
internal sealed record SessionIndexEntry(string Id, string File, DateTimeOffset? Start, string Character, string Mode, string EndKind, double Duration,
    SessionTotals? Totals, long Bytes, bool Readable, string? Problem)
{
    public SessionHeader? Header { get; init; }
}

/// <summary>
/// Session files on disk (format 1, contracts/session-format.md): the folder, names, lines written and read, the index of
/// past sessions, closing crashed ones and rotation. Files are only appended to; rotation deletes whole files of ours.
/// Every number is written in the invariant culture.
/// </summary>
internal static class SessionFiles
{
    public const string Format = "epoch-assist-session";
    public const int Version = 1;
    private const int MaxSlug = 40;
    private const int TailBytes = 16 * 1024;

    public static readonly Regex NamePattern = new(@"^\d{8}-\d{6}-(offline|online|unknown)-[a-z0-9-]{1,48}\.jsonl$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly UTF8Encoding s_utf8 = new(false);
    private static readonly JsonWriterOptions s_writeOptions = new() { Indented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    [ThreadStatic] private static ArrayBufferWriter<byte>? t_buffer;

    public static string Dir => Path.Combine(MelonEnvironment.UserDataDirectory, "EpochAssist", "sessions");

    /// <summary>Session seconds as stored: 0.01 s resolution.</summary>
    public static double Seconds(double t) => Math.Round(t, 2);

    // ---- Names ----

    /// <summary>"20261026-143210-offline-1-c30": the start's local time, the mode and the character's slug.</summary>
    public static string Stem(DateTimeOffset start, SessionCharacter? character)
    {
        string mode = character?.Mode ?? "unknown";
        string slug = character == null ? "unknown"
            : character.Id is { Length: > 0 } id ? Profiles.Slug(id, MaxSlug, "unknown")
            : $"{character.Slot ?? 0}-{Profiles.Slug(character.Name ?? string.Empty, MaxSlug, "unnamed")}";
        return $"{start.LocalDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{mode}-{slug}";
    }

    /// <summary>The stem, or the stem with -2, -3, … when a file already has it.</summary>
    public static string FreeId(string stem)
    {
        string id = stem;
        for (int n = 2; File.Exists(Path.Combine(Dir, id + ".jsonl")); n++) id = $"{stem}-{n}";
        return id;
    }

    public static string PathOf(string id) => Path.Combine(Dir, id + ".jsonl");

    /// <summary>The folder's files that match the session name pattern, oldest first by name.</summary>
    public static List<string> Files()
    {
        if (!Directory.Exists(Dir)) return new List<string>();
        return Directory.GetFiles(Dir, "*.jsonl").Where(p => NamePattern.IsMatch(Path.GetFileName(p)))
            .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal).ToList();
    }

    /// <summary>Files in the folder that aren't sessions of ours by name (never touched).</summary>
    public static List<string> OtherFiles()
    {
        if (!Directory.Exists(Dir)) return new List<string>();
        return Directory.GetFiles(Dir).Where(p => !NamePattern.IsMatch(Path.GetFileName(p))).Select(p => Path.GetFileName(p)).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    // ---- Writing lines ----

    /// <summary>One JSON line (no line end), written with the given writer callback.</summary>
    public static string Line(Action<Utf8JsonWriter> write)
    {
        var buffer = t_buffer ??= new ArrayBufferWriter<byte>(512);
        buffer.Clear();
        using (var json = new Utf8JsonWriter(buffer, s_writeOptions)) write(json);
        return s_utf8.GetString(buffer.WrittenSpan);
    }

    public static string HeaderLine(SessionHeader h) => Line(json =>
    {
        json.WriteStartObject();
        json.WriteString("format", Format);
        json.WriteNumber("version", Version);
        json.WriteString("id", h.Id);
        json.WriteString("process", h.Process);
        json.WriteNumber("clockStart", Seconds(h.ClockStart));
        json.WriteString("start", h.Start.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
        if (h.Character is { } c)
        {
            json.WriteStartObject("character");
            json.WriteString("mode", c.Mode);
            if (c.Slot is int slot) json.WriteNumber("slot", slot);
            if (c.Name != null) json.WriteString("name", c.Name);
            if (c.Id != null) json.WriteString("id", c.Id);
            json.WriteEndObject();
        }
        else
        {
            json.WriteNull("character");
        }
        json.WriteString("modVersion", h.ModVersion);
        json.WriteString("build", h.Build);
        json.WriteString("gameVersion", h.GameVersion);
        WriteList(json, "captured", h.Captured);
        WriteList(json, "missing", h.Missing);
        if (h.Test) json.WriteBoolean("test", true);
        json.WriteEndObject();
    });

    public static string RecordLine(SessionRecord record) => Line(json =>
    {
        json.WriteStartObject();
        json.WriteNumber("t", Seconds(record.T));
        json.WriteString("k", record.Kind);
        foreach (var (name, value) in record.Fields) WriteField(json, name, value);
        json.WriteEndObject();
    });

    public static string TotalsLine(double t, SessionTotals totals) => Line(json =>
    {
        json.WriteStartObject();
        json.WriteNumber("t", Seconds(t));
        json.WriteString("k", "totals");
        json.WritePropertyName("totals");
        totals.Write(json);
        json.WriteEndObject();
    });

    public static string EndLine(SessionEnd end) => Line(json =>
    {
        json.WriteStartObject();
        json.WriteNumber("t", Seconds(end.T));
        json.WriteString("k", "end");
        json.WriteString("kind", end.Kind);
        json.WritePropertyName("totals");
        end.Totals.Write(json);
        json.WriteEndObject();
    });

    // Null fields are left out.
    private static void WriteField(Utf8JsonWriter json, string name, object? value)
    {
        switch (value)
        {
            case null: break;
            case string s: json.WriteString(name, s); break;
            case bool b: json.WriteBoolean(name, b); break;
            case int i: json.WriteNumber(name, i); break;
            case long l: json.WriteNumber(name, l); break;
            case uint u: json.WriteNumber(name, u); break;
            case double d: json.WriteNumber(name, d); break;
            case float f: json.WriteNumber(name, (double)f); break;
            case string[] list: WriteList(json, name, list); break;
            default: json.WriteString(name, Convert.ToString(value, CultureInfo.InvariantCulture)); break;
        }
    }

    private static void WriteList(Utf8JsonWriter json, string name, IEnumerable<string> items)
    {
        json.WriteStartArray(name);
        foreach (string item in items) json.WriteStringValue(item);
        json.WriteEndArray();
    }

    // ---- Reading ----

    /// <summary>A header line, or null with the problem ("not a session", "newer version").</summary>
    public static SessionHeader? ParseHeader(string? line, out string? problem)
    {
        problem = null;
        try
        {
            if (string.IsNullOrWhiteSpace(line)) throw new JsonException("empty");
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object || Str(e, "format") != Format)
            {
                problem = "not a session";
                return null;
            }
            int version = e.TryGetProperty("version", out var v) && v.TryGetInt32(out int n) ? n : 0;
            if (version > Version)
            {
                problem = $"newer version ({version})";
                return null;
            }
            SessionCharacter? character = null;
            if (e.TryGetProperty("character", out var c) && c.ValueKind == JsonValueKind.Object)
                character = new SessionCharacter(Str(c, "mode") ?? "unknown", c.TryGetProperty("slot", out var s) && s.TryGetInt32(out int slot) ? slot : null, Str(c, "name"), Str(c, "id"));
            return new SessionHeader
            {
                Id = Str(e, "id") ?? string.Empty,
                Process = Str(e, "process") ?? string.Empty,
                ClockStart = e.TryGetProperty("clockStart", out var cs) && cs.TryGetDouble(out double clock) ? clock : 0d,
                Start = DateTimeOffset.TryParse(Str(e, "start"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ? start : default,
                Character = character,
                ModVersion = Str(e, "modVersion") ?? string.Empty,
                Build = Str(e, "build") ?? string.Empty,
                GameVersion = Str(e, "gameVersion") ?? string.Empty,
                Captured = Strings(e, "captured") ?? Array.Empty<string>(),
                Missing = Strings(e, "missing") ?? Array.Empty<string>(),
                Test = e.TryGetProperty("test", out var t) && t.ValueKind == JsonValueKind.True,
            };
        }
        catch (Exception)
        {
            problem = "not a session";
            return null;
        }
    }

    /// <summary>Reads a whole session file; never throws (an I/O failure is the problem).</summary>
    public static SessionFile Read(string path)
    {
        var file = new SessionFile();
        try
        {
            file.Bytes = new FileInfo(path).Length;
            var lines = File.ReadAllLines(path, s_utf8);
            if (lines.Length == 0)
            {
                file.Problem = "empty";
                return file;
            }
            file.Header = ParseHeader(lines[0], out file.Problem);
            if (file.Header == null) return file;
            int last = lines.Length - 1;
            while (last > 0 && lines[last].Length == 0) last--;
            for (int i = 1; i <= last; i++)
            {
                if (lines[i].Length == 0) continue;
                if (!ParseLine(lines[i], file))
                {
                    // A torn last line is what a kill leaves; anywhere else the session is damaged but still read.
                    if (i != last) file.Damaged++;
                }
            }
        }
        catch (Exception e)
        {
            file.Problem = $"can't be read: {e.Message}";
        }
        return file;
    }

    // One record, totals or end line into the file; false when it doesn't parse.
    private static bool ParseLine(string line, SessionFile file)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object || Str(e, "k") is not { } kind) return false;
            double t = e.TryGetProperty("t", out var tv) && tv.TryGetDouble(out double d) ? d : 0d;
            switch (kind)
            {
                case "end":
                    var totals = e.TryGetProperty("totals", out var et) && et.ValueKind == JsonValueKind.Object ? SessionTotals.Read(et) : new SessionTotals();
                    file.End = new SessionEnd(Str(e, "kind") ?? "normal", t, totals);
                    return true;
                case "totals":
                    if (e.TryGetProperty("totals", out var tt) && tt.ValueKind == JsonValueKind.Object) file.LastTotals = SessionTotals.Read(tt);
                    return true;
            }
            var fields = new Dictionary<string, object>();
            foreach (var p in e.EnumerateObject())
            {
                if (p.Name is "t" or "k") continue;
                if (Value(p.Value) is { } value) fields[p.Name] = value;
            }
            file.Records.Add(new SessionRecord(t, kind, fields));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static object? Value(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => e.TryGetInt64(out long l) ? l : e.GetDouble(),
        JsonValueKind.Array => e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? string.Empty : x.ToString()).ToArray(),
        _ => null,
    };

    /// <summary>
    /// A session for lists, from its first line and the end of the file only (seeking from the end), so hundreds of
    /// sessions list quickly. Never throws.
    /// </summary>
    public static SessionIndexEntry ReadIndexEntry(string path)
    {
        string file = Path.GetFileName(path), id = Path.GetFileNameWithoutExtension(path);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long bytes = stream.Length;
            string? first;
            using (var reader = new StreamReader(stream, s_utf8, false, 4096, leaveOpen: true)) first = reader.ReadLine();
            var header = ParseHeader(first, out string? problem);
            if (header == null) return new SessionIndexEntry(id, file, null, "unknown", "unknown", "unreadable", 0d, null, bytes, false, problem);

            // The last whole lines: the end line once ended, else the last record (a torn last line is skipped).
            long from = Math.Max(0, bytes - TailBytes);
            stream.Seek(from, SeekOrigin.Begin);
            var tail = new byte[bytes - from];
            int read = 0;
            while (read < tail.Length && stream.Read(tail, read, tail.Length - read) is int n and > 0) read += n;
            var lines = s_utf8.GetString(tail, 0, read).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            SessionEnd? ended = null;
            SessionTotals? totals = null;
            double lastT = 0d;
            // The first line of a cut-off tail may be part of a line, so it isn't read.
            for (int i = lines.Length - 1; i >= Math.Max(from > 0 ? 1 : 0, lines.Length - 4); i--)
            {
                var probe = new SessionFile();
                if (!ParseLine(lines[i], probe)) continue;
                ended = probe.End;
                totals = probe.End?.Totals ?? probe.LastTotals;
                lastT = probe.End?.T ?? (probe.Records.Count > 0 ? probe.Records[0].T : totals?.Duration ?? 0d);
                break;
            }
            return new SessionIndexEntry(header.Id.Length > 0 ? header.Id : id, file, header.Start, header.CharacterLabel, header.Mode,
                ended != null ? KnownEnd(ended.Kind) : "open", lastT, totals, bytes, true, null) { Header = header };
        }
        catch (Exception e)
        {
            return new SessionIndexEntry(id, file, null, "unknown", "unknown", "unreadable", 0d, null, 0, false, $"can't be read: {e.Message}");
        }
    }

    /// <summary>An end kind a later build added reads as normal.</summary>
    public static string KnownEnd(string kind) => kind is "normal" or "unexpected" or "cut" ? kind : "normal";

    // ---- Recovery and rotation ----

    /// <summary>
    /// Closes an open session from another game process (a crash or a kill): totals rebuilt from its records, or its last
    /// totals line when it was cut, and an end line of kind unexpected at its last record's time, appended. False with
    /// the reason when it couldn't be.
    /// </summary>
    public static bool CloseUnexpected(string path, out string reason)
    {
        var file = Read(path);
        if (file.Header == null || file.End != null)
        {
            reason = file.Problem ?? "already ended";
            return false;
        }
        var totals = new SessionTotals();
        foreach (var record in file.Records) totals.Add(record);
        // A cut session's records stop at the cut; its totals lines go on.
        double last = Math.Max(file.Records.Count > 0 ? file.Records.Max(r => r.T) : 0d, file.LastTotals?.Duration ?? 0d);
        var closed = file.LastTotals ?? totals.At(last);
        try
        {
            Append(path, EndLine(new SessionEnd("unexpected", last, closed)) + "\n", tornGuard: true);
            reason = string.Empty;
            return true;
        }
        catch (Exception e)
        {
            reason = e.Message;
            return false;
        }
    }

    /// <summary>Appends text, UTF-8, creating the folder and file; with tornGuard a file not ending in a line end gets
    /// one first, so a torn last line stays a line of its own.</summary>
    public static void Append(string path, string text, bool tornGuard = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        if (tornGuard && stream.Length > 0 && !EndsWithNewline(path)) text = "\n" + text;
        var bytes = s_utf8.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(true);
    }

    private static bool EndsWithNewline(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0) return true;
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() == '\n';
    }

    /// <summary>
    /// Deletes the oldest readable sessions of ours until at most maxCount are kept and they take at most maxBytes.
    /// Never the current session, a file it can't read as ours, or a file not matching the name pattern. Returns the
    /// file names removed.
    /// </summary>
    public static List<string> Rotate(int maxCount, long maxBytes, string? currentPath)
    {
        var removed = new List<string>();
        var ours = new List<(string Path, long Bytes)>();
        foreach (string path in Files())
        {
            if (currentPath != null && string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                ours.Add((path, -1));
                continue;
            }
            string? first;
            long bytes;
            try
            {
                bytes = new FileInfo(path).Length;
                using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), s_utf8);
                first = reader.ReadLine();
            }
            catch (Exception)
            {
                continue;
            }
            if (ParseHeader(first, out _) != null) ours.Add((path, bytes));
        }
        int count = ours.Count;
        long total = ours.Sum(o => Math.Max(0, o.Bytes)) + (currentPath != null && File.Exists(currentPath) ? new FileInfo(currentPath).Length : 0);
        foreach (var (path, bytes) in ours)
        {
            if (count <= maxCount && total <= maxBytes) break;
            if (bytes < 0) continue;
            try
            {
                File.Delete(path);
                removed.Add(Path.GetFileName(path));
                count--;
                total -= bytes;
            }
            catch (Exception)
            {
                // Left for the next rotation.
            }
        }
        return removed;
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string[]? Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : null;
}
