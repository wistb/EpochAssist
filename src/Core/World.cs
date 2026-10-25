using System.Diagnostics;
using UnityEngine;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

internal enum WorldKind { Enemy, Player, Shrine, Chest }

/// <summary>The game's display classes for enemies (DisplayActorClass), lowest first.</summary>
internal enum Rarity { Normal, Magic, Rare, Boss }

/// <summary>
/// One object the client knows in the current scene. Values are as of RefreshedAt; a reader that keeps an entry across
/// frames checks Gone (and Dead) before each use. Other players carry no name or other identity.
/// </summary>
internal sealed class WorldEntry
{
    public WorldKind Kind { get; internal set; }
    /// <summary>Enemies, shrines and chests only; never set for other players.</summary>
    public string? Name { get; internal set; }
    public Vector3 Position { get; internal set; }
    /// <summary>Straight-line metres from the player at the last refresh.</summary>
    public float Distance { get; internal set; }
    public bool Active { get; internal set; }
    /// <summary>The frame of the last refresh.</summary>
    public int RefreshedAt { get; internal set; }
    /// <summary>Unscaled time of the last refresh.</summary>
    public float RefreshedTime { get; internal set; }
    /// <summary>Active or within the near radius at the last refresh, so refreshed every frame.</summary>
    public bool Hot { get; internal set; }
    public Rarity? Rarity { get; internal set; }
    public IReadOnlyList<DataEntry> Mods { get; internal set; } = Array.Empty<DataEntry>();
    public float? Health { get; internal set; }
    public float? MaxHealth { get; internal set; }
    public float? HealthFraction { get; internal set; }
    public float? Ward { get; internal set; }
    public bool Dead { get; internal set; }
    /// <summary>An echo objective; null where the mode doesn't know (online).</summary>
    public bool? Objective { get; internal set; }
    /// <summary>Shrines and chests: used or opened; null where the mode doesn't say.</summary>
    public bool? Used { get; internal set; }
    /// <summary>The object is gone (destroyed, removed at discovery, or from an earlier scene).</summary>
    public bool Gone { get; internal set; }

    /// <summary>Seconds since the last refresh.</summary>
    public float Age => Time.unscaledTime - RefreshedTime;

    // The game objects behind the entry, for the model's own refresh only.
    internal IntPtr Key;
    internal Il2Cpp.ActorVisuals? Visuals;
    internal Il2Cpp.Actor? Actor;
    internal Il2Cpp.ActorSync? Sync;
    internal Il2Cpp.ShrineSync? ShrineSync;
    internal GameObject? Object;
    internal Il2Cpp.ConditionHandler? Handler;
    // Kept from discovery: each getter call makes a new wrapper, and the hot refresh runs every frame.
    internal Transform? Transform;
    internal Il2Cpp.BaseHealth? HealthSource;
    internal Il2Cpp.ProtectionClass? WardSource;
    internal int SeenStamp;

    internal void Drop()
    {
        Gone = true;
        Visuals = null;
        Actor = null;
        Sync = null;
        ShrineSync = null;
        Object = null;
        Handler = null;
        Transform = null;
        HealthSource = null;
        WardSource = null;
    }
}

/// <summary>A filter for the world queries. Alive defaults to true for enemies and players.</summary>
internal readonly struct WorldQuery
{
    public WorldKind? Kind { get; init; }
    public bool? Alive { get; init; }
    /// <summary>Only active entries.</summary>
    public bool Active { get; init; }
    public Rarity MinRarity { get; init; }
    public bool? Objective { get; init; }
    public bool? Used { get; init; }
    /// <summary>Metres from the player; null for the whole zone.</summary>
    public float? Range { get; init; }
}

/// <summary>What keeping the world current cost over the last second, and how old the entries are.</summary>
internal readonly record struct WorldCost(double AverageMs, double WorstMs, int Overruns, int Hot, int Cold, int Stale)
{
    /// <summary>The last discovery's time, the slowest frame since start, and whether a collection ran in it.</summary>
    public double DiscoveryMs { get; init; }
    public double SlowestMs { get; init; }
    public string SlowestCause { get; init; } = string.Empty;
}

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

    /// <summary>Active entries and entries this close are refreshed every frame (AutoCast's largest aim range).</summary>
    public const float NearRadius = 60f;
    private const double BudgetMs = 0.5;
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

    private static void Want(WorldKind? kind)
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
        if (Time.unscaledTime - BuiltAt > DiscoverySeconds)
        {
            Discover();
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
    }

    internal static void Stop()
    {
        Clear();
        s_feature = null;
        s_awakeUntil = 0f;
    }

    /// <summary>Empties the index; every entry is marked gone first, so a reader holding one sees it.</summary>
    internal static void Clear()
    {
        foreach (var entry in s_entries) entry.Drop();
        s_entries.Clear();
        s_byKey.Clear();
        s_cursor = 0;
        BuiltAt = 0f;
        s_sceneHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
    }

    // The active scene can change without a load event: a new handle empties the index before anything reads it.
    private static void CheckScene()
    {
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle != s_sceneHandle) Clear();
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
        int collections = GC.CollectionCount(0);
        bool discovered = false;
        try
        {
            if (Time.unscaledTime - BuiltAt >= DiscoverySeconds)
            {
                discovered = true;
                Discover();
                s_discoveryMs = FrameTimes.Ms(Stopwatch.GetTimestamp() - start);
            }
            RefreshFrame(start);
        }
        finally
        {
            long spent = Stopwatch.GetTimestamp() - start;
            Refresh.Add(spent);
            double ms = FrameTimes.Ms(spent);
            if (ms > s_slowestMs)
            {
                s_slowestMs = ms;
                s_slowestCause = (discovered ? "discovery" : "refresh") + (GC.CollectionCount(0) != collections ? ", gc" : string.Empty);
            }
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

    // Hot entries every frame, even past the budget (counted as an overrun); then cold ones that are due, in turn, while
    // the budget lasts.
    private static void RefreshFrame(long start)
    {
        Vector3? player = PlayerPosition();
        foreach (var entry in s_entries)
        {
            if (entry.Hot && !entry.Gone) RefreshEntry(entry, player);
        }
        long budget = (long)(BudgetMs * Stopwatch.Frequency / 1000d);
        if (Stopwatch.GetTimestamp() - start > budget)
        {
            CountOverrun();
            return;
        }
        int count = s_entries.Count;
        float due = Time.unscaledTime - ColdRefreshSeconds;
        for (int n = 0; n < count; n++)
        {
            if (s_cursor >= count) s_cursor = 0;
            var entry = s_entries[s_cursor++];
            if (entry.Hot || entry.Gone || entry.RefreshedTime > due) continue;
            RefreshEntry(entry, player);
            if (Stopwatch.GetTimestamp() - start > budget) break;
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

internal sealed class WorldModelFeature : Feature
{
    public override string Id => "world";
    public override string Name => "World model";
    public override string[] Needs => new[] { "game", "player" };

    // One group per part, or one per mode where the source differs.
    public override Requirement[] Requirements => WorldSources.Requirements;

    public override void Start() => World.Start(this);
    public override void Update() => World.Update();
    public override void OnSceneChanged() => World.Clear();
    public override void Shutdown() => World.Stop();
}
