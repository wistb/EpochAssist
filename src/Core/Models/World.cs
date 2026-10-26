using System.Diagnostics;
using UnityEngine;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

/// <summary>
/// Everything the client knows around the player: enemies, other players, shrines and chests, one entry shape in both
/// modes. Discovery adds and removes entries on a scene change and then once a second; each frame hot entries (active
/// or near) are refreshed, then cold ones in turn within a time budget. It runs only while a running feature needs
/// "world" or a dev command woke it. Nothing from an earlier scene is ever returned.
/// </summary>
internal static class World
{
    public const string Enemies = "world: enemies";
    public const string EnemyHealthOffline = "world: enemy health (offline)";
    public const string EnemyHealthOnline = "world: enemy health (online)";
    public const string MonsterMods = "world: monster mods";
    public const string ObjectivesOffline = "world: objectives (offline)";
    public const string PlayersOnline = "world: players (online)";
    public const string ShrinesOffline = "world: shrines (offline)";
    public const string ShrinesOnline = "world: shrines (online)";
    public const string ChestsOffline = "world: chests (offline)";
    public const string ChestsOnline = "world: chests (online)";
    public const string EnemyAilments = "world: enemy ailments";

    /// <summary>Active entries and entries this close are refreshed every frame (AutoCast's largest aim range).</summary>
    public const float NearRadius = 60f;
    /// <summary>The refresh's time per frame: hot entries, then cold ones while it lasts (not Feature.BudgetMs).</summary>
    private const double RefreshSliceMs = 0.5;
    private const float DiscoverySeconds = 1f;
    private const float StaleSeconds = 1f;
    // Cold entries are refreshed when they are about this old, so the budget isn't spent on fresh ones.
    private const float ColdRefreshSeconds = 0.9f;

    private static readonly List<WorldEntry> s_entries = new();
    private static readonly Dictionary<IntPtr, WorldEntry> s_byKey = new();
    private static readonly Dictionary<string, List<string>> s_partGroups = new();
    private static Feature? s_feature;
    private static int s_sceneHandle;
    private static int s_stamp;
    private static int s_cursor;
    private static float s_awakeUntil;
    private static int s_overrunSecond = -1;
    private static int s_overrunsThis, s_overrunsLast;
    private static double s_discoveryMs, s_slowestMs;
    private static string s_slowestCause = string.Empty;
    private static Job? s_discovery;
    // This frame's refresh time so far (discovery, hot entries, cold steps), and whether the hot entries overran.
    private static int s_spentFrame = -1, s_coldFrame = -1;
    private static long s_spentTicks;
    private static bool s_discoveredThisFrame, s_hotOverran;
    private static int s_frameCollections, s_coldVisited;

    /// <summary>The active scene the index was built for.</summary>
    public static int SceneHandle => s_sceneHandle;

    /// <summary>Unscaled time of the last discovery; 0 before the first.</summary>
    public static float BuiltAt { get; private set; }

    /// <summary>Refresh time per frame over the last second.</summary>
    public static CostWindow Refresh { get; } = new();

    /// <summary>The entries of the current scene; empty with no player or while the model isn't running.</summary>
    public static IReadOnlyList<WorldEntry> Entries
    {
        get
        {
            CheckScene();
            return s_entries;
        }
    }

    public static bool Running => s_feature?.State == FeatureState.Running;

    /// <summary>True when the part's group for the current mode is present.</summary>
    public static bool Has(string part) => Running && MissingGroup(part) == null;

    /// <summary>The missing group that turns the part off in the current mode, or null.</summary>
    public static string? MissingGroup(string part)
    {
        var feature = s_feature;
        if (feature == null) return $"world: {part}";
        if (!s_partGroups.TryGetValue(part, out var groups)) s_partGroups[part] = groups = Compat.PartGroups(feature, part);
        if (groups.Count == 0) return $"world: {part}";
        return Compat.MissingPartGroup(feature, groups, PlayerModel.Online == true);
    }

    // Online, shrines and chests are found by a search of the whole scene, so it runs only while a reader asks for them.
    private static float s_objectsWantedUntil;
    private const float ObjectsWantedSeconds = 5f;

    /// <summary>For the sources: a query asked for shrines or chests (or every kind) in the last few seconds.</summary>
    internal static bool ObjectsWanted => Time.unscaledTime < s_objectsWantedUntil;

    /// <summary>A reader is about to look at entries of this kind (null: every kind); shrines and chests are searched for
    /// online only while someone asks.</summary>
    internal static void Want(WorldKind? kind)
    {
        if (kind is null or WorldKind.Shrine or WorldKind.Chest) s_objectsWantedUntil = Time.unscaledTime + ObjectsWantedSeconds;
    }

    /// <summary>Keeps the model refreshing for a while with no feature reading it (dev commands); a stale index is
    /// brought up to date at once.</summary>
    public static void WakeFor(float seconds)
    {
        s_awakeUntil = Math.Max(s_awakeUntil, Time.unscaledTime + seconds);
        Want(null);
        if (!Running) return;
        CheckScene();
        if (s_discovery is { } discovery && Time.unscaledTime - BuiltAt > discovery.Interval)
        {
            discovery.RunNow();
            RefreshAll();
        }
    }

    public static WorldCost Cost
    {
        get
        {
            int hot = 0, cold = 0, stale = 0;
            foreach (var entry in Entries)
            {
                if (entry.Gone) continue;
                if (entry.Hot) hot++;
                else cold++;
                if (entry.Age > StaleSeconds) stale++;
            }
            int second = (int)Time.unscaledTime;
            int overruns = second == s_overrunSecond ? s_overrunsThis + s_overrunsLast : second == s_overrunSecond + 1 ? s_overrunsThis : 0;
            return new WorldCost(Refresh.AverageMs, Refresh.WorstMs, overruns, hot, cold, stale)
            {
                DiscoveryMs = s_discoveryMs, SlowestMs = s_slowestMs, SlowestCause = s_slowestCause,
            };
        }
    }

    /// <summary>The nearest entry matching the query, or null.</summary>
    public static WorldEntry? Nearest(in WorldQuery query)
    {
        Want(query.Kind);
        WorldEntry? nearest = null;
        foreach (var entry in Entries)
        {
            if (Matches(entry, query) && (nearest == null || entry.Distance < nearest.Distance)) nearest = entry;
        }
        return nearest;
    }

    /// <summary>Fills the caller's list (cleared first) with the matching entries; returns how many.</summary>
    public static int InRange(in WorldQuery query, List<WorldEntry> into)
    {
        Want(query.Kind);
        into.Clear();
        foreach (var entry in Entries)
        {
            if (Matches(entry, query)) into.Add(entry);
        }
        return into.Count;
    }

    /// <summary>
    /// How many of an ailment an enemy shows (GF-74: its visuals' ailment counters, the same in both modes), 0 when it
    /// shows none; null when it can't be read (the group missing, the enemy gone).
    /// </summary>
    public static int? AilmentStacks(WorldEntry entry, int ailmentId)
    {
        if (!Has("enemy ailments") || entry.Gone || !Game.Alive(entry.Visuals)) return null;
        try
        {
            var list = entry.Visuals!.ailmentVFXCreator?.ailmentVisuals;
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
            {
                var counter = list[i];
                if (counter != null && (int)counter.getAilmentID() == ailmentId) return (int)MathF.Round(counter.getCount());
            }
            return 0;
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("world:ailments", $"Enemy ailment read failed: {e.Message}", 30);
            return null;
        }
    }

    /// <summary>Other players within a range of the player (the world's player entries, alive or not).</summary>
    public static int PlayersWithin(float range)
    {
        Want(WorldKind.Player);
        int count = 0;
        var entries = Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Kind == WorldKind.Player && !entry.Gone && entry.Distance <= range) count++;
        }
        return count;
    }

    public static int Count(in WorldQuery query)
    {
        Want(query.Kind);
        int count = 0;
        foreach (var entry in Entries)
        {
            if (Matches(entry, query)) count++;
        }
        return count;
    }

    /// <summary>The matching enemies weighed by rarity (entries without a rarity weigh as normal).</summary>
    public static float Weight(in WorldQuery query, Func<Rarity, float> weight)
    {
        Want(query.Kind);
        float total = 0f;
        foreach (var entry in Entries)
        {
            if (Matches(entry, query)) total += weight(entry.Rarity ?? Core.Rarity.Normal);
        }
        return total;
    }

    private static bool Matches(WorldEntry entry, in WorldQuery query)
    {
        if (entry.Gone) return false;
        if (query.Kind is { } kind && entry.Kind != kind) return false;
        if ((query.Alive ?? entry.Kind is WorldKind.Enemy or WorldKind.Player) && entry.Dead) return false;
        if (query.Active && !entry.Active) return false;
        if (query.MinRarity > Core.Rarity.Normal && (entry.Rarity ?? Core.Rarity.Normal) < query.MinRarity) return false;
        if (query.Objective is { } objective && entry.Objective != objective) return false;
        if (query.Used is { } used && entry.Used != used) return false;
        if (query.Range is { } range && entry.Distance > range) return false;
        return true;
    }

    internal static void Start(Feature feature)
    {
        s_feature = feature;
        s_partGroups.Clear();
        Refresh.Reset();
        s_slowestMs = 0d;
        s_slowestCause = string.Empty;
        Clear();
        s_discovery = Scheduler.Every(feature, "discovery", DiscoverySeconds, DiscoverJob, startNow: true);
        Scheduler.Spread(feature, "cold refresh", ColdSlice, ColdStep);
    }

    internal static void Stop()
    {
        Clear();
        s_feature = null;
        s_discovery = null;
        s_awakeUntil = 0f;
    }

    /// <summary>Empties the index; every entry is marked gone first, so a reader holding one sees it. Discovery runs
    /// again at the next job pass.</summary>
    internal static void Clear() => Clear(discoverSoon: true);

    private static void Clear(bool discoverSoon)
    {
        foreach (var entry in s_entries) entry.Drop();
        s_entries.Clear();
        s_byKey.Clear();
        s_cursor = 0;
        BuiltAt = 0f;
        s_sceneHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
        if (discoverSoon) s_discovery?.Due();
    }

    // The active scene can change without a load event: a new handle empties the index before anything reads it.
    // Inside the discovery job the index is rebuilt right after, so it isn't made due again.
    private static void CheckScene(bool discoverSoon = true)
    {
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle != s_sceneHandle) Clear(discoverSoon);
    }

    private static bool Awake
    {
        get
        {
            if (Time.unscaledTime < s_awakeUntil) return true;
            foreach (var feature in FeatureHost.All)
            {
                if (feature.State == FeatureState.Running && Array.IndexOf(feature.Needs, "world") >= 0) return true;
            }
            return false;
        }
    }

    // The discovery job (every second, before the update): adds and drops entries.
    private static void DiscoverJob()
    {
        if (!Awake) return;
        CheckScene(discoverSoon: false);
        if (!Player.Exists) return;
        long start = Stopwatch.GetTimestamp();
        BeginFrame();
        s_discoveredThisFrame = true;
        try
        {
            Discover();
        }
        finally
        {
            long spent = Stopwatch.GetTimestamp() - start;
            s_discoveryMs = FrameTimes.Ms(spent);
            Spend(spent);
        }
    }

    // Hot entries every frame, even past the slice (counted as an overrun); the cold ones are the spread job's.
    internal static void Update()
    {
        if (!Awake) return;
        CheckScene();
        if (!Player.Exists)
        {
            if (s_entries.Count > 0) Clear();
            return;
        }
        long start = Stopwatch.GetTimestamp();
        BeginFrame();
        try
        {
            RefreshHot();
        }
        finally
        {
            Spend(Stopwatch.GetTimestamp() - start);
            s_hotOverran = FrameTimes.Ms(s_spentTicks) > RefreshSliceMs;
            if (s_hotOverran) CountOverrun();
        }
    }

    private static void BeginFrame()
    {
        int frame = Time.frameCount;
        if (frame == s_spentFrame) return;
        s_spentFrame = frame;
        s_spentTicks = 0;
        s_discoveredThisFrame = false;
        s_hotOverran = false;
        s_frameCollections = GC.CollectionCount(0);
    }

    // The refresh cost window covers discovery, hot entries and cold steps, as one frame's total.
    private static void Spend(long ticks)
    {
        Refresh.Add(ticks);
        s_spentTicks += ticks;
        double ms = FrameTimes.Ms(s_spentTicks);
        if (ms > s_slowestMs)
        {
            s_slowestMs = ms;
            s_slowestCause = (s_discoveredThisFrame ? "discovery" : "refresh") + (GC.CollectionCount(0) != s_frameCollections ? ", gc" : string.Empty);
        }
    }

    // What the refresh slice has left after this frame's discovery and hot entries; nothing after an overrun, without
    // a player, or while asleep.
    private static double ColdSlice()
    {
        if (s_spentFrame != Time.frameCount || s_hotOverran || !Awake || !Player.Exists) return 0d;
        return Math.Max(0d, RefreshSliceMs - FrameTimes.Ms(s_spentTicks));
    }

    // One cold entry that is due, in turn; false once every entry was looked at this frame.
    private static bool ColdStep()
    {
        int frame = Time.frameCount;
        if (frame != s_coldFrame)
        {
            s_coldFrame = frame;
            s_coldVisited = 0;
        }
        long start = Stopwatch.GetTimestamp();
        try
        {
            int count = s_entries.Count;
            float due = Time.unscaledTime - ColdRefreshSeconds;
            Vector3? player = null;
            while (s_coldVisited < count)
            {
                s_coldVisited++;
                if (s_cursor >= count) s_cursor = 0;
                var entry = s_entries[s_cursor++];
                if (entry.Hot || entry.Gone || entry.RefreshedTime > due) continue;
                player ??= PlayerPosition();
                RefreshEntry(entry, player);
                return s_coldVisited < count;
            }
            return false;
        }
        finally
        {
            Spend(Stopwatch.GetTimestamp() - start);
        }
    }

    // Adds what the sources list and wasn't known, drops what they no longer list.
    private static void Discover()
    {
        BuiltAt = Time.unscaledTime;
        int stamp = ++s_stamp;
        WorldSources.Discover(stamp);
        for (int i = s_entries.Count - 1; i >= 0; i--)
        {
            var entry = s_entries[i];
            if (entry.SeenStamp == stamp && !entry.Gone) continue;
            s_byKey.Remove(entry.Key);
            entry.Drop();
            s_entries.RemoveAt(i);
        }
        if (s_cursor >= s_entries.Count) s_cursor = 0;
    }

    /// <summary>For the sources: true (and marked seen) when the key already has an entry.</summary>
    internal static bool Known(IntPtr key, int stamp)
    {
        if (!s_byKey.TryGetValue(key, out var entry)) return false;
        entry.SeenStamp = stamp;
        return true;
    }

    /// <summary>For the sources: keeps every entry of a kind this discovery without looking for new ones.</summary>
    internal static void Keep(WorldKind kind, int stamp)
    {
        foreach (var entry in s_entries)
        {
            if (entry.Kind == kind) entry.SeenStamp = stamp;
        }
    }

    /// <summary>For the sources: adds a new entry and refreshes it once.</summary>
    internal static void Add(IntPtr key, int stamp, WorldEntry entry)
    {
        entry.Key = key;
        entry.SeenStamp = stamp;
        s_byKey[key] = entry;
        s_entries.Add(entry);
        RefreshEntry(entry, PlayerPosition());
    }

    private static Vector3? PlayerPosition()
    {
        Transform? player = Player.Transform;
        return player != null ? player.position : null;
    }

    private static void RefreshHot()
    {
        Vector3? player = PlayerPosition();
        foreach (var entry in s_entries)
        {
            if (entry.Hot && !entry.Gone) RefreshEntry(entry, player);
        }
    }

    private static void RefreshAll()
    {
        Vector3? player = PlayerPosition();
        foreach (var entry in s_entries)
        {
            if (!entry.Gone) RefreshEntry(entry, player);
        }
    }

    private static void RefreshEntry(WorldEntry entry, Vector3? player)
    {
        try
        {
            WorldSources.Refresh(entry);
        }
        catch (Exception e)
        {
            Log.ErrorThrottled($"world:{entry.Kind}", $"World model {entry.Kind.ToString().ToLowerInvariant()} read failed: {e.Message}", 30);
        }
        if (player is Vector3 p && !entry.Gone) entry.Distance = Vector3.Distance(entry.Position, p);
        entry.Hot = entry.Active || entry.Distance <= NearRadius;
        entry.RefreshedAt = Time.frameCount;
        entry.RefreshedTime = Time.unscaledTime;
    }

    private static void CountOverrun()
    {
        int second = (int)Time.unscaledTime;
        if (second != s_overrunSecond)
        {
            s_overrunsLast = second == s_overrunSecond + 1 ? s_overrunsThis : 0;
            s_overrunsThis = 0;
            s_overrunSecond = second;
        }
        s_overrunsThis++;
    }
}
