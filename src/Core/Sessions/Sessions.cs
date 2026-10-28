using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace EpochAssist.Core;

/// <summary>A record was kept in the current session: its index in Sessions.Current.Records and its kind.</summary>
internal readonly record struct SessionRecordKept(int Index, string Kind);

/// <summary>A session as features read it: header, totals (live for the current one), records and how it ended
/// ("open" while running).</summary>
internal sealed class SessionView
{
    private readonly List<SessionRecord> _records;
    private readonly Func<SessionTotals> _totals;
    private readonly Func<string> _endKind;
    private readonly Func<bool> _cut;

    internal SessionView(SessionHeader header, List<SessionRecord> records, Func<SessionTotals> totals, Func<string> endKind, Func<bool> cut)
    {
        Header = header;
        _records = records;
        _totals = totals;
        _endKind = endKind;
        _cut = cut;
    }

    public SessionHeader Header { get; }
    public SessionTotals Totals => _totals();
    /// <summary>Append-only; for the current session capped by the per-session limit.</summary>
    public IReadOnlyList<SessionRecord> Records => _records;
    public string EndKind => _endKind();
    /// <summary>Reached its size limit: records stop at the cut, totals go on.</summary>
    public bool Cut => _cut();

    public IEnumerable<SessionRecord> RecordsSince(int index)
    {
        for (int i = Math.Max(0, index); i < _records.Count; i++) yield return _records[i];
    }
}

/// <summary>
/// Session history: each character's play session recorded from the bus into its own file
/// (UserData/EpochAssist/sessions, contracts/session-format.md), and read back for features, the page and exports.
/// A session starts when a character is in a zone with history on, and ends on leaving to a menu scene, switching
/// history off or quitting. Records are batched and handed to the background writer every 10 s; a dev reload or a
/// fault leaves the file open and the next start in the same game process resumes it, while an open file from an
/// earlier process (a crash) is closed as ended unexpectedly. Reads the bus only: no game members, no patches, and never
/// hits.
/// </summary>
internal static class Sessions
{
    public static readonly string[] Captured = { "zone", "died", "xp", "gold", "drop", "death", "echo", "pick", "level", "favour", "rep", "rank" };
    private const float FlushSeconds = 10f;
    private const float IndexSeconds = 5f;
    private const long MegaByte = 1024 * 1024;
    // Scenes the game passes through between zones and on the way to character select; they read as combat scenes,
    // UnloadScene even with the player in it (game facts GF-66), so they neither start a session nor count as a zone.
    private static readonly string[] s_transitScenes = { "UnloadScene", "TransitionScene" };

    // The record kinds each event producer gives, for the header's missing list.
    private static readonly Dictionary<string, string[]> s_producerKinds = new()
    {
        ["xp"] = new[] { "xp" }, ["gold"] = new[] { "gold" }, ["drops"] = new[] { "drop" }, ["death cause"] = new[] { "death" }, ["echo"] = new[] { "echo" },
        ["monolith"] = new[] { "pick" }, ["level"] = new[] { "level" }, ["factions"] = new[] { "favour", "rep", "rank" },
    };

    /// <summary>The session being recorded: what is written so far and the batch not yet handed to the writer.</summary>
    private sealed class Live
    {
        public Live(SessionHeader header) => Header = header;

        public readonly SessionHeader Header;
        public readonly List<SessionRecord> Records = new();
        public SessionTotals Totals = new();
        public readonly StringBuilder Batch = new();
        public string? Path;
        public bool HeaderWritten;
        // Bytes written and batched; at SessionMaxMB the session is cut to totals.
        public long Bytes;
        public bool Cut, TotalsDirty;
        public double LastT;
        public SessionView? View;
        // Keys of the items kept as drops, so the same item dropped again isn't counted twice.
        public readonly HashSet<string> DropKeys = new();
    }

    private static Feature? s_feature;
    private static Live? s_live;
    // A shutdown in this load left a session open, so the next start is a restart rather than a reload.
    private static bool s_stoppedOpen;
    private static readonly Lazy<string> s_process = new(() => Process.GetCurrentProcess().StartTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

    // Past sessions by file, refreshed when the folder's files change; problem files are unreadable ones and files
    // that aren't sessions by name.
    private static readonly Dictionary<string, (long Bytes, DateTime Written, SessionIndexEntry Entry)> s_entries = new(StringComparer.OrdinalIgnoreCase);
    private static List<SessionIndexEntry>? s_index;
    private static List<string> s_problemFiles = new();
    private static float s_indexAt = float.MinValue;

    /// <summary>History is on and a session is running.</summary>
    public static bool Recording => s_live != null && SessionSettings.History.Value;

    /// <summary>The session being recorded, or null.</summary>
    public static SessionView? Current
    {
        get
        {
            var live = s_live;
            if (live == null) return null;
            return live.View ??= new SessionView(live.Header, live.Records, () => live.Totals.At(Now(live)), () => "open", () => live.Cut);
        }
    }

    /// <summary>Folder problems: unreadable sessions and files that aren't sessions; never deleted.</summary>
    public static IReadOnlyList<string> ProblemFiles
    {
        get
        {
            List();
            return s_problemFiles;
        }
    }

    // ---- Lifecycle (the sessions feature) ----

    internal static void Start(Feature feature)
    {
        s_feature = feature;
        SessionWriter.Start();
        Recover();
        foreach (var subscribe in s_subscriptions) subscribe(feature);
        Scheduler.Every(feature, "sessions flush", FlushSeconds, () => Flush(force: false));
        RotateLater();
    }

    internal static void Update()
    {
        SessionWriter.DrainMessages();
        if (!SessionSettings.History.Value)
        {
            if (s_live != null) End("normal");
            return;
        }
        if (s_live == null && GameState.InGame && Array.IndexOf(s_transitScenes, GameState.Scene) < 0 && !GameState.Loading) Begin();
    }

    /// <summary>Shutdown without a quit (dev reload, fault, host stop): flushed and left open to resume.</summary>
    internal static void Stop()
    {
        if (s_live != null)
        {
            Flush(force: true);
            s_stoppedOpen = true;
        }
        SessionWriter.Flush(wait: true);
        SessionWriter.Stop();
        s_live = null;
        s_feature = null;
        s_index = null;
        s_entries.Clear();
        SessionStats.Reset();
        SessionTrends.Reset();
    }

    /// <summary>Quit (Runtime.Save): the session ends normally and is written before the game closes.</summary>
    internal static void EndForQuit()
    {
        if (s_live == null) return;
        End("normal");
        SessionWriter.Flush(wait: true);
    }

    private static readonly IReadOnlyDictionary<string, object> NoFields = new Dictionary<string, object>();

    // One per record kind, owned by the feature (the host ends them when it stops). Never HitTaken.
    private static readonly Action<Feature>[] s_subscriptions =
    {
        f => Bus.Subscribe<ZoneChange>(f, OnZone),
        f => Bus.Subscribe<PlayerDied>(f, _ => Keep("died", NoFields)),
        f => Bus.Subscribe<XpGained>(f, e => Keep("xp", Fields(("amount", e.Amount), ("total", e.Total)))),
        f => Bus.Subscribe<GoldChanged>(f, e => Keep("gold", Fields(("amount", (long)e.Amount), ("total", (long)e.Total)))),
        f => Bus.Subscribe<ItemDropped>(f, OnDrop),
        f => Bus.Subscribe<DeathCause>(f, e => Keep("death", Fields(("killer", e.Killer), ("types", e.Types), ("text", e.Text)))),
        f => Bus.Subscribe<EchoCompleted>(f, e => Keep("echo", Fields(("scene", e.Scene)))),
        f => Bus.Subscribe<MonolithPick>(f, e => Keep("pick", Fields(("modifier", e.Modifier), ("stability", (long)e.StabilityReward), ("hex", (long)e.HexIndex), ("reward", e.Reward),
            ("corruption", e.Corruption is int c ? (long)c : null)))),
        f => Bus.Subscribe<LevelUp>(f, e => Keep("level", Fields(("level", (long)e.Level)))),
        f => Bus.Subscribe<FavourChanged>(f, e => Keep("favour", Fields(("faction", e.Faction), ("amount", (long)e.Amount), ("total", (long)e.Total)))),
        f => Bus.Subscribe<ReputationGained>(f, e => Keep("rep", Fields(("faction", e.Faction), ("amount", (long)e.Amount), ("total", (long)e.Total)))),
        f => Bus.Subscribe<RankChanged>(f, e => Keep("rank", Fields(("faction", e.Faction), ("rank", (long)e.Rank)))),
    };

    // ---- Recording ----

    private static Dictionary<string, object> Fields(params (string Name, object? Value)[] fields)
    {
        var map = new Dictionary<string, object>(fields.Length);
        foreach (var (name, value) in fields)
            if (value != null) map[name] = value;
        return map;
    }

    private static double Now(Live live) => Math.Max(live.LastT, SessionFiles.Seconds(Clock.Now - live.Header.ClockStart));

    private static void Begin()
    {
        var header = new SessionHeader
        {
            Process = s_process.Value, ClockStart = Clock.Now, Start = DateTimeOffset.Now,
            ModVersion = BuildInfo.Version, Build = Runtime.BuildId, GameVersion = GameVersion(), Captured = Captured,
        };
        s_live = new Live(header);
        // The zone the character is in: its change came before the character appeared.
        Keep("zone", Fields(("scene", GameState.Scene), ("kind", ZoneKind(GameState.Scene, GameState.Kind, false)), ("monolith", false), ("name", ZoneName(GameState.Scene))));
        Log.Info($"Sessions: recording ({ModeText()})");
    }

    // The same item dropped again (by key) is kept once per session, across reloads too.
    private static void OnDrop(ItemDropped e)
    {
        if (s_live is not { } live) return;
        if (e.Key != null && !live.DropKeys.Add(e.Key)) return;
        Keep("drop", Fields(("name", e.Name), ("base", e.BaseName), ("rarity", (long)e.Rarity), ("rarityName", e.RarityName), ("id", (long)e.Id),
            ("shown", e.Shown), ("rule", e.Rule is int rule ? (long)rule : null), ("key", e.Key)));
    }

    private static void OnZone(ZoneChange change)
    {
        if (s_live == null || Array.IndexOf(s_transitScenes, change.Scene) >= 0) return;
        if (change.Kind == SceneKind.Menu)
        {
            End("normal");
            return;
        }
        Keep("zone", Fields(("scene", change.Scene), ("kind", ZoneKind(change.Scene, change.Kind, change.Monolith)), ("monolith", change.Monolith),
            ("name", ZoneName(change.Scene))));
    }

    // The zone's name as the game shows it ("Waste Pools"), kept with its record; absent when the game data can't say.
    private static string? ZoneName(string scene) => GameData.SceneNamesOf(scene).Display;

    /// <summary>
    /// Zone kinds for totals (game facts GF-67): town for non-combat scenes (the End of Time, the monolith hub), echo
    /// for monolith zones (a monolith transition, or a combat scene named M_…, as the monolith producer tells them),
    /// dungeon for combat scenes named Dun… (Dun1Q10), other for the rest (campaign zones). The record keeps the scene,
    /// so a later build can tell them apart again.
    /// </summary>
    internal static string ZoneKind(string scene, SceneKind kind, bool monolith) => kind switch
    {
        SceneKind.Menu => "menu",
        SceneKind.NonCombat => "town",
        SceneKind.Combat when monolith || scene.StartsWith("M_", StringComparison.Ordinal) => "echo",
        SceneKind.Combat when scene.StartsWith("Dun", StringComparison.Ordinal) => "dungeon",
        _ => "other",
    };

    /// <summary>Keeps one record in the current session: totals, the batch and Records, then SessionRecordKept. Past the
    /// size limit only the totals.</summary>
    internal static void Keep(string kind, IReadOnlyDictionary<string, object> fields)
    {
        var live = s_live;
        if (live == null) return;
        var record = new SessionRecord(Now(live), kind, fields);
        live.LastT = record.T;
        if (kind != "test") live.Totals.Add(record);
        if (live.Cut)
        {
            live.TotalsDirty = true;
            return;
        }
        string line = SessionFiles.RecordLine(record) + "\n";
        int size = Encoding.UTF8.GetByteCount(line);
        if (live.Bytes + size > MaxBytes)
        {
            live.Cut = live.TotalsDirty = true;
            Log.Info($"Sessions: this session reached {SessionSettings.MaxMB.Value:F0} MB; it keeps its totals but no more detail");
            return;
        }
        live.Bytes += size;
        live.Batch.Append(line);
        live.Records.Add(record);
        Bus.Raise(new SessionRecordKept(live.Records.Count - 1, kind));
    }

    private static long MaxBytes => (long)SessionSettings.MaxMB.Value * MegaByte;

    /// <summary>
    /// Hands the batch to the writer. The header goes with the first batch, once the character is read (the profiles
    /// read it within their reading window), so the file is named for it; force writes it without (end, shutdown).
    /// After the cut every flush with new events writes a totals line.
    /// </summary>
    private static void Flush(bool force)
    {
        var live = s_live;
        if (live == null) return;
        if (!live.HeaderWritten)
        {
            if (!force && Profiles.State == CharacterState.Reading) return;
            FillHeader(live.Header);
            live.Header.Id = SessionFiles.FreeId(SessionFiles.Stem(live.Header.Start, live.Header.Character));
            live.Path = SessionFiles.PathOf(live.Header.Id);
            string header = SessionFiles.HeaderLine(live.Header) + "\n";
            live.Batch.Insert(0, header);
            live.Bytes += Encoding.UTF8.GetByteCount(header);
            live.HeaderWritten = true;
        }
        if (live.Cut && live.TotalsDirty)
        {
            double t = Now(live);
            live.Batch.Append(SessionFiles.TotalsLine(t, live.Totals.At(t))).Append('\n');
            live.TotalsDirty = false;
        }
        if (live.Batch.Length == 0) return;
        SessionWriter.Append(live.Path!, live.Batch.ToString());
        live.Batch.Clear();
    }

    private static void FillHeader(SessionHeader header)
    {
        if (Profiles.Key is { } key)
        {
            header.Character = key.Id.Length > 0 ? new SessionCharacter(key.Mode, null, null, key.Id) : new SessionCharacter(key.Mode, key.Slot, key.Name, null);
        }
        var events = FeatureHost.Find("events");
        bool? online = Player.Online;
        header.Missing = GameEvents.Producers
            .Where(p => s_producerKinds.ContainsKey(p.Part) && (events == null || events.State != FeatureState.Running || !events.Has(p.Group) || (online is bool mode && !p.Supports(mode))))
            .SelectMany(p => s_producerKinds[p.Part]).ToArray();
        header.StartLevel ??= PlayerModel.Level;
        if (s_live?.Header == header) s_live.Totals.StartLevel ??= header.StartLevel;
    }

    private static void End(string kind)
    {
        var live = s_live;
        if (live == null) return;
        double t = Now(live);
        Flush(force: true);
        string endKind = live.Cut ? "cut" : kind;
        var totals = live.Totals.At(t, ended: true);
        SessionWriter.Append(live.Path!, SessionFiles.EndLine(new SessionEnd(endKind, t, totals)) + "\n");
        s_live = null;
        s_indexAt = float.MinValue;
        Log.Info($"Sessions: session ended ({endKind}), {FormatDuration(t)}, {live.Records.Count} records");
        RotateLater();
    }

    /// <summary>
    /// Ends the session as split and starts the next one at the same moment, in the same zone (New session, `sessions
    /// split`). False with the reason when history is off or no session runs.
    /// </summary>
    public static bool Split(out string? reason, out string? ended)
    {
        reason = ended = null;
        if (!SessionSettings.History.Value) reason = "history is off";
        else if (s_live == null) reason = "no session is running";
        if (reason != null) return false;
        ended = s_live!.Header.Id.Length > 0 ? Label(s_live.Header.Id) : "the session";
        End("split");
        Begin();
        return true;
    }

#if DEV
    /// <summary>Dev only (`sessions crash`): stops writing the current session without an end line, as a kill would.
    /// The file stays open; the next start in a new process closes it.</summary>
    internal static bool Crash()
    {
        if (s_live == null) return false;
        Flush(force: true);
        SessionWriter.Flush(wait: true);
        s_live = null;
        s_indexAt = float.MinValue;
        return true;
    }

    /// <summary>Dev only: the current session's file, or null.</summary>
    internal static string? CurrentPath => s_live?.Path;
#endif

    private static string GameVersion()
    {
        try
        {
            return UnityEngine.Application.version ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    // ---- Resume, recovery and rotation ----

    /// <summary>
    /// At start: an open session of this game process (a dev reload or a restart) is resumed, with a gap line for the
    /// time without records; open sessions of earlier processes are closed as ended unexpectedly, on the writer.
    /// </summary>
    private static void Recover()
    {
        SessionIndexEntry? resume = null;
        var closing = new List<string>();
        foreach (string path in SessionFiles.Files())
        {
            var entry = SessionFiles.ReadIndexEntry(path);
            if (!entry.Readable || entry.EndKind != "open" || entry.Header == null) continue;
            if (entry.Header.Process == s_process.Value)
            {
                // The newest of this process's open files; an older one (a dev `sessions crash`) waits for the next process.
                resume = entry;
            }
            else
            {
                closing.Add(path);
            }
        }
        if (closing.Count > 0)
        {
            SessionWriter.Post(() =>
            {
                foreach (string path in closing)
                {
                    if (SessionFiles.CloseUnexpected(path, out string reason)) SessionWriter.Tell($"Sessions: closed a session that ended unexpectedly ({Label(Path.GetFileNameWithoutExtension(path))})");
                    else SessionWriter.Tell($"Sessions: couldn't close {Label(Path.GetFileNameWithoutExtension(path))}: {reason}");
                }
            });
        }
        if (resume != null) Resume(Path.Combine(SessionFiles.Dir, resume.File));
    }

    private static void Resume(string path)
    {
        var file = SessionFiles.Read(path);
        if (file.Header == null || file.End != null) return;
        var live = new Live(file.Header) { Path = path, HeaderWritten = true, Bytes = file.Bytes, Cut = file.LastTotals != null };
        live.Records.AddRange(file.Records);
        foreach (var record in file.Records)
            if (record.Kind == "drop" && record.Text("key") is { } key) live.DropKeys.Add(key);
        foreach (var record in file.Records) live.Totals.Add(record);
        double last = file.Records.Count > 0 ? file.Records.Max(r => r.T) : 0d;
        if (file.LastTotals != null)
        {
            // Cut: totals come from its last totals line; its zone records stopped at the cut.
            live.Totals = file.LastTotals;
            last = Math.Max(last, file.LastTotals.Duration);
        }
        live.LastT = last;
        s_live = live;
        double now = Now(live);
        if (live.Cut) live.Totals.Continue(ZoneKind(GameState.Scene, GameState.Kind, false), now);
        string reason = s_stoppedOpen ? "restart" : "reload";
        s_stoppedOpen = false;
        Keep("gap", Fields(("from", SessionFiles.Seconds(last)), ("to", now), ("reason", reason)));
        Log.Info($"Sessions: resumed after a {reason} ({ModeText()}), {live.Records.Count} records");
    }

    private static void RotateLater()
    {
        int count = (int)SessionSettings.MaxCount.Value;
        long bytes = (long)SessionSettings.MaxTotalMB.Value * MegaByte;
        string? current = s_live?.Path;
        SessionWriter.Post(() =>
        {
            foreach (string removed in SessionFiles.Rotate(count, bytes, current))
                SessionWriter.Tell($"Sessions: removed old session {Label(Path.GetFileNameWithoutExtension(removed))}");
        });
        s_indexAt = float.MinValue;
    }

    /// <summary>Dev only: rotation now, on the caller; the file names removed.</summary>
    internal static List<string> RotateNow()
    {
        SessionWriter.Flush(wait: true);
        var removed = SessionFiles.Rotate((int)SessionSettings.MaxCount.Value, (long)SessionSettings.MaxTotalMB.Value * MegaByte, s_live?.Path);
        s_indexAt = float.MinValue;
        return removed;
    }

    // ---- Reading ----

    /// <summary>Past sessions and the current one, newest first; built from each file's first and last lines, cached
    /// and refreshed when the folder's files change (checked at most every 5 s, or after our own changes).</summary>
    public static IReadOnlyList<SessionIndexEntry> List()
    {
        if (s_index != null && Clock.Now - s_indexAt < IndexSeconds) return WithCurrent(s_index);
        var index = new List<SessionIndexEntry>();
        var problems = new List<string>();
        bool listed = false;
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in SessionFiles.Files())
            {
                if (s_live?.Path is { } current && string.Equals(current, path, StringComparison.OrdinalIgnoreCase)) continue;
                seen.Add(path);
                var info = new FileInfo(path);
                if (!s_entries.TryGetValue(path, out var cached) || cached.Bytes != info.Length || cached.Written != info.LastWriteTimeUtc)
                    s_entries[path] = cached = (info.Length, info.LastWriteTimeUtc, SessionFiles.ReadIndexEntry(path));
                if (cached.Entry.Readable) index.Add(cached.Entry);
                else problems.Add($"{cached.Entry.File}: {cached.Entry.Problem}");
            }
            foreach (string gone in s_entries.Keys.Where(k => !seen.Contains(k)).ToList()) s_entries.Remove(gone);
            problems.AddRange(SessionFiles.OtherFiles().Select(name => $"{name}: not a session file"));
            listed = true;
        }
        catch (Exception e)
        {
            Log.WarnThrottled("sessions:list", $"Sessions couldn't be listed: {e.Message}");
        }
        index.Sort((a, b) => string.CompareOrdinal(b.Id, a.Id));
        // Only a full listing prunes left-out ids, so a folder that can't be read keeps them.
        if (listed) SessionTrends.Prune(index.Select(e => e.Id).Append(s_live?.Header.Id ?? string.Empty));
        s_index = index;
        s_problemFiles = problems;
        s_indexAt = Clock.Now;
        return WithCurrent(index);
    }

    private static IReadOnlyList<SessionIndexEntry> WithCurrent(List<SessionIndexEntry> past)
    {
        var live = s_live;
        if (live == null) return past;
        double t = Now(live);
        var entry = new SessionIndexEntry(live.Header.Id.Length > 0 ? live.Header.Id : "(current)", live.Path != null ? Path.GetFileName(live.Path) : string.Empty,
            live.Header.Start, live.Header.CharacterLabel, live.Header.Mode, "open", t, live.Totals.At(t), live.Bytes, true, null) { Header = live.Header };
        var list = new List<SessionIndexEntry>(past.Count + 1) { entry };
        list.AddRange(past);
        return list;
    }

    /// <summary>
    /// Deletes one past session's file, on the writer (waited for, so lists don't show it again). Refuses the current
    /// session, a name that isn't a session's, and while an export runs. Its left-out mark goes with it.
    /// </summary>
    public static bool Delete(string id, out string? reason)
    {
        reason = null;
        string file = id + ".jsonl";
        if (s_live != null && s_live.Header.Id == id) reason = "it is the current session";
        else if (!SessionFiles.NamePattern.IsMatch(file)) reason = "that isn't a session's name";
        else if (!File.Exists(SessionFiles.PathOf(id))) reason = "no such session";
        else if (SessionExport.Running) reason = "an export is running; try again when it finishes";
        if (reason != null) return false;
        string path = SessionFiles.PathOf(id);
        string? failed = null;
        SessionWriter.Post(() =>
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e)
            {
                failed = e.Message;
            }
        });
        SessionWriter.Flush(wait: true);
        s_indexAt = float.MinValue;
        if (failed != null)
        {
            reason = Diagnostics.Scrub(failed);
            return false;
        }
        SessionTrends.Set(id, false);
        Log.Info($"Sessions: deleted {Label(id)}");
        return true;
    }

    /// <summary>A session's records by id, read in full on demand (the current one from memory); null when unreadable.</summary>
    public static SessionView? Read(string id)
    {
        var live = s_live;
        if (live != null && live.Header.Id == id) return Current;
        string file = id + ".jsonl";
        if (!SessionFiles.NamePattern.IsMatch(file)) return null;
        var read = SessionFiles.Read(Path.Combine(SessionFiles.Dir, file));
        if (read.Header == null) return null;
        SessionTotals totals;
        if (read.End != null) totals = read.End.Totals;
        else
        {
            var rebuilt = new SessionTotals();
            foreach (var record in read.Records) rebuilt.Add(record);
            totals = read.LastTotals ?? rebuilt.At(read.Records.Count > 0 ? read.Records.Max(r => r.T) : 0d);
        }
        string end = read.End != null ? SessionFiles.KnownEnd(read.End.Kind) : "open";
        bool cut = read.LastTotals != null || read.End?.Kind == "cut";
        return new SessionView(read.Header, read.Records, () => totals, () => end, () => cut);
    }

    /// <summary>The page's, the command's and Diagnostics' line: mode and slot only, never a character's name.</summary>
    public static string StatusText() => $"{NowText()}; {HistoryText()}";

    /// <summary>"recording 0:42:10 (offline, slot 1), 312 records", "no session" or "off".</summary>
    public static string NowText()
    {
        if (!SessionSettings.History.Value) return "off";
        if (s_live is not { } live) return "no session";
        return $"recording {FormatDuration(Now(live))} ({ModeText()}), {live.Records.Count} records{(live.Cut ? ", cut to totals" : string.Empty)}";
    }

    /// <summary>"37 kept, 4.1 MB; problem files 0".</summary>
    public static string HistoryText()
    {
        var list = List();
        double mb = list.Sum(e => e.Bytes) / (double)MegaByte;
        return $"{list.Count} kept, {mb.ToString("F1", CultureInfo.InvariantCulture)} MB; problem files {s_problemFiles.Count}";
    }

    /// <summary>The current session's character as mode and slot, or why it isn't known yet.</summary>
    private static string ModeText()
    {
        var live = s_live;
        if (live == null) return "no session";
        if (live.Header.Character is { } c) return c.Label;
        if (Profiles.Key is { } key) return key.Label;
        return Player.Online switch { true => "online", false => "offline", _ => "mode not known yet" };
    }

    /// <summary>A session id for the log and lists: its start and mode, without the character's slug.</summary>
    public static string Label(string id)
    {
        string[] parts = id.Split('-');
        return parts.Length >= 3 ? $"{parts[0]}-{parts[1]} {parts[2]}" : "session";
    }

    /// <summary>"0:42:10".</summary>
    public static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0d, seconds));
        return $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}";
    }

    /// <summary>Lines for the feature's Diagnostics details.</summary>
    internal static IEnumerable<string> Notes()
    {
        yield return StatusText();
        if (SessionWriter.LastError is { } error) yield return $"writer error: {error}";
#if DEV
        yield return $"writer thread {(SessionWriter.ThreadAlive ? "alive" : "stopped")}, writer threads in the process {SessionWriter.LiveThreads}";
#endif
    }

    /// <summary>For the export: the current session's batch written first.</summary>
    internal static void FlushForExport() => Flush(force: true);
}
