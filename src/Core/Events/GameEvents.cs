using Il2Cpp;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

/// <summary>
/// What happens to the local player, read from state the game already keeps: one producer per event, each
/// idle (one subscriber count read a frame) while nothing subscribes, each in its own requirement groups so a game
/// update loses only its own event. The first read after a subscription, a scene change or a player change is a
/// baseline that raises nothing.
/// </summary>
internal static partial class GameEvents
{
    public const string Hits = "events: hits";
    public const string Xp = "events: xp";
    public const string Gold = "events: gold";
    public const string Drops = "events: drops";
    public const string DropFilter = "events: drop filter";
    public const string DropKey = "events: drop key";
    public const string Death = "events: death cause";
    public const string Echo = "events: echo";
    public const string Monolith = "events: monolith";

    private const float DropScanSeconds = 0.25f;
    private const float DeathTextSeconds = 5f;

    private static Feature? s_feature;
    private static int s_sceneStamp = -2;

    /// <summary>A producer's state, for the dev `events subs` and the Diagnostics notes.</summary>
    internal sealed class Producer
    {
        public Producer(string part, string group, bool offline, bool online)
        {
            Part = part;
            Group = group;
            Offline = offline;
            Online = online;
        }

        public string Part { get; }
        public string Group { get; }
        /// <summary>The modes it works in, from the confirm reads.</summary>
        public bool Offline { get; }
        public bool Online { get; }
        public bool Active { get; internal set; }

        public bool Supports(bool online) => online ? Online : Offline;
    }

    private static readonly Producer s_hits = new("hits", Hits, offline: true, online: true);
    private static readonly Producer s_xp = new("xp", Xp, offline: true, online: true);
    private static readonly Producer s_gold = new("gold", Gold, offline: true, online: true);
    private static readonly Producer s_drops = new("drops", Drops, offline: true, online: true);
    private static readonly Producer s_death = new("death cause", Death, offline: true, online: true);
    private static readonly Producer s_echo = new("echo", Echo, offline: true, online: true);
    private static readonly Producer s_monolith = new("monolith", Monolith, offline: true, online: true);

    internal static readonly Producer[] Producers = { s_hits, s_xp, s_gold, s_drops, s_death, s_echo, s_monolith };

    internal static void Start(Feature feature)
    {
        s_feature = feature;
        s_sceneStamp = GameState.SceneChangedAt;
        ResetPlayer();
    }

    internal static void Stop()
    {
        foreach (var producer in Producers) producer.Active = false;
        ResetPlayer();
        s_dropScan = null;
        s_droppedKeys.Clear();
        s_droppedOrder.Clear();
        s_zoneSubscription = null;
        s_deathSubscription = null;
        s_feature = null;
    }

    /// <summary>The player changed or left (Player.Reset): cached components and every baseline go.</summary>
    internal static void ResetPlayer()
    {
        s_lastHealth = null;
        s_experience = null;
        s_lastXp = null;
        s_goldTracker = null;
        s_lastGold = null;
        s_dropsBaseline = true;
        s_seenDrops.Clear();
        s_pendingDrops.Clear();
        s_echoBaselined = false;
    }

    // A new scene is a baseline for every producer; the monolith panel is never kept across one.
    private static void OnSceneChanged()
    {
        s_lastHealth = null;
        s_lastGold = null;
        s_lastXp = null;
        s_dropsBaseline = true;
        s_seenDrops.Clear();
        s_pendingDrops.Clear();
        s_echoBaselined = false;
    }

    internal static void Update()
    {
        var feature = s_feature;
        if (feature == null) return;
        if (GameState.SceneChangedAt != s_sceneStamp)
        {
            s_sceneStamp = GameState.SceneChangedAt;
            OnSceneChanged();
        }
        bool? online = Player.Online;

        UpdateHits(feature, online);
        UpdateXp(feature, online);
        UpdateGold(feature, online);
        UpdateDrops(feature, online);
        UpdateDeath(feature, online);
        UpdateEcho(feature, online);
        UpdateMonolith(feature, online);
    }

    // Has subscribers, its group is present and the mode (once known) is one it works in.
    private static bool Wanted<T>(Feature feature, Producer producer, bool? online) where T : struct =>
        Bus.HasSubscribers<T>() && feature.Has(producer.Group) && online is bool mode && producer.Supports(mode);

    /// <summary>Diagnostics lines for producers the current mode doesn't support.</summary>
    internal static IEnumerable<string> Notes()
    {
        if (Player.Online is not bool online) yield break;
        foreach (var producer in Producers)
        {
            if (!producer.Supports(online)) yield return $"{producer.Part} not supported {(online ? "online" : "offline")}";
        }
    }

    internal static string List(string[]? items) => items == null ? string.Empty : "[" + string.Join(", ", items) + "]";
}
