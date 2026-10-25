using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

internal enum HitKind { Hit, DamageOverTime, Critical, Block, Dodge, Parry }

/// <summary>
/// A hit the player took. Without detail it is one health drop in a frame (Amount only); with detail it is one call of
/// the game's own damage method (offline only), with the ward lost, the attacker, ability and damage types. Unknown
/// fields are null; Kind is null until a game source gives it.
/// </summary>
internal readonly record struct HitTaken(float Amount, float? Ward, string? Source, string? Ability, string[]? Types, HitKind? Kind, bool Detail)
{
    private bool PrintMembers(StringBuilder text)
    {
        text.Append($"Amount = {Amount:0.#}, Ward = {Ward:0.#}, Source = {Source}, Ability = {Ability}, Types = {GameEvents.List(Types)}, Kind = {Kind}, Detail = {Detail}");
        return true;
    }
}

/// <summary>XP the player gained, and the tracker's value after it.</summary>
internal readonly record struct XpGained(long Amount, long Total);

/// <summary>A change of the player's gold (a spend is negative), and the total after it.</summary>
internal readonly record struct GoldChanged(int Amount, int Total);

/// <summary>An item that dropped in this scene (gold piles, potions, tomes and bones aren't items).</summary>
internal readonly record struct ItemDropped(string? Name, string? BaseName, int Rarity, string RarityName, uint Id);

/// <summary>What killed the player, from the death screen's text, after a PlayerDied; all null when it didn't say.</summary>
internal readonly record struct DeathCause(string? Killer, string[]? Types, string? Text)
{
    private bool PrintMembers(StringBuilder text)
    {
        text.Append($"Killer = {Killer}, Types = {GameEvents.List(Types)}, Text = {Text?.Replace("\n", " / ")}");
        return true;
    }
}

/// <summary>The echo's objective was completed.</summary>
internal readonly record struct EchoCompleted(string Scene);

/// <summary>The player entered an echo chosen on the monolith.</summary>
internal readonly record struct MonolithPick(string? Modifier, int StabilityReward, int HexIndex);

/// <summary>
/// What happens to the local player, read from state the game already keeps: one producer per event, each
/// idle (one subscriber count read a frame) while nothing subscribes, each in its own requirement groups so a game
/// update loses only its own event. The first read after a subscription, a scene change or a player change is a
/// baseline that raises nothing. The two hit detail patches are applied only while hits have a subscriber.
/// </summary>
internal static class GameEvents
{
    public const string Hits = "events: hits";
    public const string HitDetailOffline = "events: hit detail (offline)";
    public const string Xp = "events: xp";
    public const string Gold = "events: gold";
    public const string Drops = "events: drops";
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

    /// <summary>The hit detail group patched now (offline only), or null.</summary>
    internal static string? DetailOn => s_feature?.DemandGroupsOn.FirstOrDefault();

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
        s_zoneSubscription = null;
        s_deathSubscription = null;
        s_feature = null;
    }

    /// <summary>The player changed or left (Player.Reset): cached components and every baseline go.</summary>
    internal static void ResetPlayer()
    {
        s_lastHealth = null;
        s_protection = null;
        s_protectionPtr = IntPtr.Zero;
        s_experience = null;
        s_lastXp = null;
        s_goldTracker = null;
        s_lastGold = null;
        s_dropsBaseline = true;
        s_seenDrops.Clear();
        s_echoBaselined = false;
        s_attackerNames.Clear();
    }

    // A new scene is a baseline for every producer; the monolith panel is never kept across one.
    private static void OnSceneChanged()
    {
        s_lastHealth = null;
        s_lastGold = null;
        s_lastXp = null;
        s_dropsBaseline = true;
        s_seenDrops.Clear();
        s_echoBaselined = false;
        s_attackerNames.Clear();
        s_protection = null;
        s_protectionPtr = IntPtr.Zero;
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

    // ---- Hits ----

    private static float? s_lastHealth;
    private static ProtectionClass? s_protection;
    private static IntPtr s_protectionPtr;
    private static float s_healthBefore, s_wardBefore;
    private static readonly Dictionary<IntPtr, string?> s_attackerNames = new();

    private static void UpdateHits(Feature feature, bool? online)
    {
        bool active = Wanted<HitTaken>(feature, s_hits, online);
        // Detail offline only: online the game's hit event never fires for the local player, so online
        // hits are the health edge. The patch is on only while hits are wanted.
        string? detail = active && online == false ? HitDetailOffline : null;
        FeatureHost.SetPatchGroup(feature, HitDetailOffline, detail != null);
        if (active != s_hits.Active)
        {
            s_hits.Active = active;
            s_lastHealth = null;
        }
        if (!active) return;
        if (detail != null) CacheProtection();

        float? health = PlayerModel.Vitals?.Health;
        float? last = s_lastHealth;
        s_lastHealth = health;
        // With detail on and its object found, the game's own calls are the hits, so a drop isn't counted twice.
        if (health is not float now || last is not float before || now >= before) return;
        bool detailLive = detail != null && feature.DemandGroupsOn.Contains(detail) && s_protectionPtr != IntPtr.Zero;
        if (detailLive) return;
        Bus.Raise(new HitTaken(before - now, null, null, null, null, null, false));
    }

    private static void CacheProtection()
    {
        if (Game.Alive(s_protection)) return;
        var actor = Player.Actor;
        s_protection = actor != null ? actor.protection : null;
        s_protectionPtr = Game.Alive(s_protection) ? s_protection!.Pointer : IntPtr.Zero;
    }

    private static void OnApplyDamagePrefix(ProtectionClass protection)
    {
        if (s_protectionPtr == IntPtr.Zero || protection.Pointer != s_protectionPtr) return;
        s_healthBefore = Player.Health is { } health ? health.currentHealth : 0f;
        s_wardBefore = protection.currentWard;
    }

    // The amounts are what the call took from health and ward: its return value can't be read through Harmony.
    private static void OnApplyDamage(ProtectionClass protection, DamageStats? stats, DamageSource? source)
    {
        if (s_protectionPtr == IntPtr.Zero || protection.Pointer != s_protectionPtr) return;
        float health = Player.Health is { } h ? h.currentHealth : s_healthBefore;
        float ward = protection.currentWard;
        string? attacker = null, ability = null;
        if (source != null)
        {
            var info = source.GetDamageSourceInfo();
            var used = info.Item2 ?? info.Item3 ?? info.Item4;
            ability = Game.Alive(used) && !string.IsNullOrEmpty(used!.abilityName) ? used.abilityName : null;
            attacker = AttackerName(info.Item5);
        }
        Bus.Raise(new HitTaken(Math.Max(0f, s_healthBefore - health), Math.Max(0f, s_wardBefore - ward), attacker, ability, DamageTypes(stats), null, true));
    }

    // The nameplate's name, on the attacker's visuals; kept per attacker for the scene.
    private static string? AttackerName(Actor? attacker)
    {
        if (!Game.Alive(attacker)) return null;
        IntPtr key = attacker!.Pointer;
        if (s_attackerNames.TryGetValue(key, out string? name)) return name;
        var info = attacker.GetComponentInChildren<ActorDisplayInformation>(true);
        name = Game.Alive(info) && !string.IsNullOrEmpty(info!.displayName) ? info.displayName : null;
        s_attackerNames[key] = name;
        return name;
    }

    private static readonly string[] s_typeNames = { "physical", "fire", "cold", "lightning", "necrotic", "void", "poison" };

    private static string[]? DamageTypes(DamageStats? stats)
    {
        var damage = stats?.damage;
        if (damage == null) return null;
        List<string>? types = null;
        for (int i = 0; i < damage.Length && i < s_typeNames.Length; i++)
        {
            if (damage[i] > 0f) (types ??= new List<string>()).Add(s_typeNames[i]);
        }
        return types?.ToArray();
    }

    // Postfixes run inside the game's own call: they never throw into it.
    [HarmonyPatch(typeof(ProtectionClass), nameof(ProtectionClass.ApplyDamage), new[] { typeof(DamageStats), typeof(DamageSource), typeof(float), typeof(Actor), typeof(bool) })]
    private static class ApplyDamage_Patch
    {
        private static void Prefix(ProtectionClass __instance)
        {
            try
            {
                OnApplyDamagePrefix(__instance);
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("events:hits", $"Hit detail read failed: {e.Message}", 30);
            }
        }

        private static void Postfix(ProtectionClass __instance, DamageStats __0, DamageSource __1)
        {
            try
            {
                OnApplyDamage(__instance, __0, __1);
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("events:hits", $"Hit detail read failed: {e.Message}", 30);
            }
        }
    }

    // ---- XP and gold ----

    private static ExperienceTracker? s_experience;
    private static GoldTracker? s_goldTracker;
    private static long? s_lastXp;
    private static int s_lastLevel;
    private static long s_lastNextXp;
    private static int? s_lastGold;

    private static void UpdateXp(Feature feature, bool? online)
    {
        bool active = Wanted<XpGained>(feature, s_xp, online);
        if (active != s_xp.Active)
        {
            s_xp.Active = active;
            s_lastXp = null;
        }
        if (!active) return;
        if (!Game.Alive(s_experience))
        {
            var player = Player.Object;
            s_experience = player != null ? player.GetComponent<ExperienceTracker>() : null;
            if (!Game.Alive(s_experience)) return;
        }
        long xp = s_experience!.CurrentExperience;
        int level = s_experience.CurrentLevel;
        long next = s_experience.NextLevelExperience;
        long? last = s_lastXp;
        int lastLevel = s_lastLevel;
        long lastNext = s_lastNextXp;
        s_lastXp = xp;
        s_lastLevel = level;
        s_lastNextXp = next;
        if (last is not long before) return;
        // CurrentExperience is likely per level: across a level-up the gain is what the old level had left plus
        // the new value. If it turns out cumulative, it simply rises.
        long amount = level > lastLevel && xp < before ? lastNext - before + xp : xp - before;
        if (amount > 0) Bus.Raise(new XpGained(amount, xp));
    }

    private static void UpdateGold(Feature feature, bool? online)
    {
        bool active = Wanted<GoldChanged>(feature, s_gold, online);
        if (active != s_gold.Active)
        {
            s_gold.Active = active;
            s_lastGold = null;
        }
        if (!active) return;
        if (!Game.Alive(s_goldTracker))
        {
            var player = Player.Object;
            s_goldTracker = player != null ? player.GetComponent<GoldTracker>() : null;
            if (!Game.Alive(s_goldTracker)) return;
        }
        int gold = s_goldTracker!.value;
        int? last = s_lastGold;
        s_lastGold = gold;
        if (last is int before && gold != before) Bus.Raise(new GoldChanged(gold - before, gold));
    }

    // ---- Drops ----

    private static Job? s_dropScan;
    private static bool s_dropsBaseline = true;
    private static readonly HashSet<uint> s_seenDrops = new();

    private static void UpdateDrops(Feature feature, bool? online)
    {
        bool active = Wanted<ItemDropped>(feature, s_drops, online);
        if (active == s_drops.Active) return;
        s_drops.Active = active;
        s_dropsBaseline = true;
        s_seenDrops.Clear();
        s_dropScan?.Cancel();
        s_dropScan = active ? Scheduler.Every(feature, "drops scan", DropScanSeconds, ScanDrops, startNow: true) : null;
    }

    // The game lists the item visuals on the ground itself: no scene search.
    private static void ScanDrops()
    {
        if (!Player.Exists) return;
        var all = GroundItemVisuals.all;
        if (all == null) return;
        bool baseline = s_dropsBaseline;
        s_dropsBaseline = false;
        for (int i = 0; i < all.Count; i++)
        {
            var visual = all[i];
            if (!Game.Alive(visual)) continue;
            var item = visual.itemData;
            if (item == null || !s_seenDrops.Add(visual.id) || baseline) continue;
            var entry = GameData.Item(item);
            Bus.Raise(new ItemDropped(entry.Name ?? NullIfEmpty(item.FullName), entry.BaseName, item.rarity, RarityName(item), visual.id));
        }
    }

    // The rarity byte is the affix count below exalted, so the name comes from the game's own checks.
    private static string RarityName(ItemDataUnpacked item) =>
        item.isLegendary() ? "Legendary" : item.isSet() ? "Set" : item.isUnique() ? "Unique" : item.isExaltedItem() ? "Exalted"
            : item.isRare() ? "Rare" : item.isMagic() ? "Magic" : "Normal";

    private static string? NullIfEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    // ---- Death cause ----

    private static Subscription? s_deathSubscription;
    private static string? s_deathTextBefore;
    private static float s_deathAt = -1f;

    private static void UpdateDeath(Feature feature, bool? online)
    {
        bool active = Wanted<DeathCause>(feature, s_death, online);
        if (active != s_death.Active)
        {
            s_death.Active = active;
            s_deathAt = -1f;
            s_deathSubscription?.Dispose();
            s_deathSubscription = null;
            if (active)
            {
                // The text changes only at a death, so it is read here and after each cause, not every frame.
                s_deathTextBefore = DeathInformationText.mostRecentDeathInfo;
                s_deathSubscription = Bus.Subscribe<PlayerDied>(feature, _ => s_deathAt = Clock.Now);
            }
        }
        if (!active || s_deathAt < 0f) return;

        string? text = DeathInformationText.mostRecentDeathInfo;
        bool changed = !string.Equals(text, s_deathTextBefore, StringComparison.Ordinal) && !string.IsNullOrEmpty(text);
        if (!changed && Clock.Now - s_deathAt < DeathTextSeconds) return;
        s_deathAt = -1f;
        if (!changed)
        {
            Bus.Raise(new DeathCause(null, null, null));
            return;
        }
        s_deathTextBefore = text;
        Bus.Raise(ParseDeath(text!));
    }

    private static readonly Regex s_tags = new("<[^>]*>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] s_typeSeparators = { ", ", " and " };

    // "You have been slain by a Melee Attack from an Imperial Assassin\nKilling Blow Damage Types: Necrotic and
    // Physical\n…": the killer after the first line's last " from " (and its a/an), the types after
    // "Damage Type(s): ".
    internal static DeathCause ParseDeath(string raw)
    {
        string text = s_tags.Replace(raw, string.Empty).Trim();
        string[] lines = text.Split('\n');
        string first = lines[0];
        // Unique enemies have no article ("…a Meteorite from Radamasa the Cursed").
        int from = first.LastIndexOf(" from ", StringComparison.Ordinal);
        string? killer = null;
        if (from >= 0)
        {
            string rest = first[(from + " from ".Length)..].Trim();
            if (rest.StartsWith("an ", StringComparison.Ordinal)) rest = rest[3..];
            else if (rest.StartsWith("a ", StringComparison.Ordinal)) rest = rest[2..];
            killer = NullIfEmpty(rest);
        }
        string[]? types = null;
        foreach (string line in lines)
        {
            int at = line.IndexOf("Damage Types: ", StringComparison.Ordinal);
            int length = "Damage Types: ".Length;
            if (at < 0)
            {
                at = line.IndexOf("Damage Type: ", StringComparison.Ordinal);
                length = "Damage Type: ".Length;
            }
            if (at < 0) continue;
            types = line[(at + length)..].Split(s_typeSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t.ToLowerInvariant()).ToArray();
            if (types.Length == 0) types = null;
            break;
        }
        return new DeathCause(killer, types, NullIfEmpty(text));
    }

    // ---- Echo completion ----

    private static bool s_echoBaselined, s_lastObjective;

    private static void UpdateEcho(Feature feature, bool? online)
    {
        bool active = Wanted<EchoCompleted>(feature, s_echo, online) && GameState.Kind == SceneKind.Combat;
        if (active != s_echo.Active)
        {
            s_echo.Active = active;
            s_echoBaselined = false;
        }
        if (!active) return;
        // Online instanceExistsAndInitialised stays False in an echo, so the live instance itself is the check.
        bool complete = MonolithZoneManager.instance is { } manager && Game.Alive(manager) && manager.objectiveComplete;
        bool was = s_lastObjective, baselined = s_echoBaselined;
        s_lastObjective = complete;
        s_echoBaselined = true;
        if (baselined && complete && !was) Bus.Raise(new EchoCompleted(GameState.Scene));
    }

    // ---- Monolith picks ----

    private static Subscription? s_zoneSubscription;
    private static (int Timeline, int Hex)? s_lastPick;

    private static void UpdateMonolith(Feature feature, bool? online)
    {
        bool active = Wanted<MonolithPick>(feature, s_monolith, online);
        if (active == s_monolith.Active) return;
        s_monolith.Active = active;
        s_zoneSubscription?.Dispose();
        s_zoneSubscription = active ? Bus.Subscribe<ZoneChange>(feature, OnZoneChange) : null;
    }

    // Echo scenes are combat scenes named M_… (the hub and the rest zone aren't combat); an echo can also start inside
    // the scene (a monolith transition). The panel is found again each time, inactive objects included.
    private static void OnZoneChange(ZoneChange change)
    {
        if (change.Kind != SceneKind.Combat || !(change.Monolith || change.Scene.StartsWith("M_", StringComparison.Ordinal))) return;
        var panel = UnityEngine.Object.FindObjectOfType(Il2CppInterop.Runtime.Il2CppType.Of<MonolithTimelinePanelManager>(), true)?.TryCast<MonolithTimelinePanelManager>();
        if (!Game.Alive(panel)) return;
        // Online the island's hasEnteredEcho stays False in the echo, so the entry itself is the sign; the key keeps a
        // second entry into the same echo from being reported again.
        var island = panel!.LastSelectedIsland;
        if (island == null) return;
        var key = ((int)panel.timelineID, island.hexIndex);
        if (s_lastPick == key) return;
        s_lastPick = key;
        var mod = island.mod;
        string? modifier = mod != null ? GameData.MonsterMod(mod).Name : null;
        Bus.Raise(new MonolithPick(modifier, island.stabilityReward, island.hexIndex));
    }
}

internal sealed class GameEventsFeature : Feature
{
    public override string Id => "events";
    public override string Name => "Game events";
    public override string[] Needs => new[] { "game", "player", "data" };

    public override PartNeed[] Parts => new[]
    {
        Part("player", "vitals", GameEvents.Hits),
        Part("data", "items", GameEvents.Drops),
        Part("data", "monster mods", GameEvents.Monolith),
    };

    // Every group has a member of its own, since Compat knows only groups that requirements or patch groups name.
    public override Requirement[] Requirements => new[]
    {
        Need<BaseHealth>(nameof(BaseHealth.currentHealth), GameEvents.Hits),
        Need<Actor>(nameof(Actor.protection), GameEvents.HitDetailOffline),
        Need<ProtectionClass>(nameof(ProtectionClass.currentWard), GameEvents.HitDetailOffline),
        Need<DamageStats>(nameof(DamageStats.damage), GameEvents.HitDetailOffline),
        Need<DamageSource>(nameof(DamageSource.GetDamageSourceInfo), GameEvents.HitDetailOffline),
        Need<Ability>(nameof(Ability.abilityName), GameEvents.HitDetailOffline),
        Need<ActorDisplayInformation>(nameof(ActorDisplayInformation.displayName), GameEvents.HitDetailOffline),
        Need<ExperienceTracker>(nameof(ExperienceTracker.CurrentExperience), GameEvents.Xp),
        Need<ExperienceTracker>(nameof(ExperienceTracker.CurrentLevel), GameEvents.Xp),
        Need<ExperienceTracker>(nameof(ExperienceTracker.NextLevelExperience), GameEvents.Xp),
        Need<GoldTracker>(nameof(GoldTracker.value), GameEvents.Gold),
        Need<GroundItemVisuals>(nameof(GroundItemVisuals.all), GameEvents.Drops),
        Need<GroundItemVisuals>(nameof(GroundItemVisuals.itemData), GameEvents.Drops),
        Need<GroundItemVisuals>(nameof(GroundItemVisuals.id), GameEvents.Drops),
        Need<ItemData>(nameof(ItemData.rarity), GameEvents.Drops),
        Need<ItemData>(nameof(ItemData.isLegendary), GameEvents.Drops),
        Need<ItemData>(nameof(ItemData.isSet), GameEvents.Drops),
        Need<ItemData>(nameof(ItemData.isUnique), GameEvents.Drops),
        Need<ItemData>(nameof(ItemData.isExaltedItem), GameEvents.Drops),
        Need<ItemData>(nameof(ItemData.isRare), GameEvents.Drops),
        Need<ItemData>(nameof(ItemData.isMagic), GameEvents.Drops),
        Need(typeof(DeathInformationText), nameof(DeathInformationText.mostRecentDeathInfo), GameEvents.Death),
        Need(typeof(MonolithZoneManager), nameof(MonolithZoneManager.instance), GameEvents.Echo),
        Need<MonolithZoneManager>(nameof(MonolithZoneManager.objectiveComplete), GameEvents.Echo),
        Need<MonolithTimelinePanelManager>(nameof(MonolithTimelinePanelManager.LastSelectedIsland), GameEvents.Monolith),
        Need<MonolithTimelinePanelManager>(nameof(MonolithTimelinePanelManager.timelineID), GameEvents.Monolith),
        Need<EchoWebIsland>(nameof(EchoWebIsland.mod), GameEvents.Monolith),
        Need<EchoWebIsland>(nameof(EchoWebIsland.stabilityReward), GameEvents.Monolith),
        Need<EchoWebIsland>(nameof(EchoWebIsland.hexIndex), GameEvents.Monolith),
    };

    public override Type[] PatchClasses => NestedPatches(typeof(GameEvents));

    // The hit detail patch is applied only while hits have a subscriber, offline.
    public override IReadOnlyDictionary<Type, string> PatchGroups => s_patchGroups ??= PatchClasses.ToDictionary(t => t, _ => GameEvents.HitDetailOffline);
    private static IReadOnlyDictionary<Type, string>? s_patchGroups;

    public override string[] DemandGroups => s_demandGroups;
    private static readonly string[] s_demandGroups = { GameEvents.HitDetailOffline };

    public override IEnumerable<string> DiagnosticNotes => GameEvents.Notes();

    public override void Start() => GameEvents.Start(this);
    public override void Update() => GameEvents.Update();
    public override void Shutdown() => GameEvents.Stop();
}
