using Il2Cpp;
using UnityEngine;

namespace EpochAssist.Core;

/// <summary>
/// Where the world model finds things in each mode. Enemies and other players come from the actor
/// manager's per-alignment lists, which hold the whole known population, active or not, and no scenery.
/// Other players' identity members are never named, read or formatted here.
/// </summary>
internal static class WorldSources
{
    // AutoCast's rule, kept for parity: these alignments are enemies.
    private const string HostileAlignmentName = "Evil";
    private const string HostileNeutralAlignmentName = "HostileNeutral";

    private static readonly HashSet<IntPtr> s_objectives = new();
    private static readonly List<DataEntry> s_noMods = new();

    public static Requirement[] Requirements => new[]
    {
        Need<ActorManager>(nameof(ActorManager.instance), World.Enemies),
        Need<ActorManager>(nameof(ActorManager.visuals), World.Enemies),
        Need<ActorManager.ActorVisualsList>(nameof(ActorManager.ActorVisualsList.alignment), World.Enemies),
        Need<ActorManager.ActorVisualsList>(nameof(ActorManager.ActorVisualsList.visuals), World.Enemies),
        Need<ActorVisuals>(nameof(ActorVisuals.dead), World.Enemies),
        Need<ActorVisuals>(nameof(ActorVisuals.isPlayer), World.Enemies),
        Need<ActorVisuals>(nameof(ActorVisuals.actorSync), World.Enemies),
        Need<ActorDisplayInformation>(nameof(ActorDisplayInformation.actorClass), World.Enemies),
        Need<ActorDisplayInformation>(nameof(ActorDisplayInformation.displayName), World.Enemies),

        Need<Actor>(nameof(Actor.health), World.EnemyHealthOffline),
        Need<BaseHealth>(nameof(BaseHealth.currentHealth), World.EnemyHealthOffline),
        Need<BaseHealth>(nameof(BaseHealth.maxHealth), World.EnemyHealthOffline),
        Need<Actor>(nameof(Actor.protection), World.EnemyHealthOffline),
        Need<ProtectionClass>(nameof(ProtectionClass.currentWard), World.EnemyHealthOffline),

        Need<ActorSync>(nameof(ActorSync.healthRatio), World.EnemyHealthOnline),
        Need<ActorSync>(nameof(ActorSync.SyncedMaxHealth), World.EnemyHealthOnline),
        Need<ActorSync>(nameof(ActorSync.SyncedCurrentWard), World.EnemyHealthOnline),

        Need<ActorDisplayInformation>(nameof(ActorDisplayInformation.appliedMods), World.MonsterMods),

        Need<MonolithZoneManager>(nameof(MonolithZoneManager.instance), World.ObjectivesOffline),
        Need<MonolithZoneManager>(nameof(MonolithZoneManager.objectiveEnemies), World.ObjectivesOffline),
        Need<Actor>(nameof(Actor.dying), World.ObjectivesOffline),

        Need<ActorSync>(nameof(ActorSync.IsLocalPlayer), World.PlayersOnline),
        Need<ActorSync>(nameof(ActorSync.IsDead), World.PlayersOnline),
        Need<ActorSync>(nameof(ActorSync.healthRatio), World.PlayersOnline),
        Need<ActorSync>(nameof(ActorSync.SyncedMaxHealth), World.PlayersOnline),
        Need<ActorSync>(nameof(ActorSync.SyncedCurrentWard), World.PlayersOnline),

        Need<ShrinesManager>(nameof(ShrinesManager.ManagersByScene), World.ShrinesOffline),
        Need<ShrinesManager>(nameof(ShrinesManager._allShrineObjects), World.ShrinesOffline),
        Need<ConditionHandler>(nameof(ConditionHandler.triggered), World.ShrinesOffline),
        Need<DisplayInformation>(nameof(DisplayInformation.displayName), World.ShrinesOffline),

        Need<ShrineSync>(nameof(ShrineSync.ShrineObject), World.ShrinesOnline),
        Need<ShrineSync>(nameof(ShrineSync.conditionHandler), World.ShrinesOnline),
        Need<ConditionHandler>(nameof(ConditionHandler.triggered), World.ShrinesOnline),

        Need<ChestPlacementManager>(nameof(ChestPlacementManager.chestPlacementManagers), World.ChestsOffline),
        Need<ConditionHandler>(nameof(ConditionHandler.triggered), World.ChestsOffline),

        Need<ChestSync>(nameof(ChestSync.chestObject), World.ChestsOnline),
        Need<ChestSync>(nameof(ChestSync.conditionHandler), World.ChestsOnline),
        Need<ConditionHandler>(nameof(ConditionHandler.triggered), World.ChestsOnline),
    };

    private static Requirement Need<T>(string member, string group) => new(typeof(T), member, group);

    /// <summary>Lists every known object to the world index (World.Known, World.Add), once per discovery.</summary>
    public static void Discover(int stamp)
    {
        bool online = PlayerModel.Online == true;
        if (World.Has("enemies")) DiscoverActors(stamp, online);
        if (online)
        {
            if (World.Has("shrines")) DiscoverOnline<ShrineSync>(stamp, WorldKind.Shrine, ref s_nextShrineScan, 0f);
            if (World.Has("chests")) DiscoverOnline<ChestSync>(stamp, WorldKind.Chest, ref s_nextChestScan, OnlineScanSeconds / 2f);
        }
        else
        {
            if (World.Has("shrines")) DiscoverShrinesOffline(stamp);
            if (World.Has("chests")) DiscoverChestsOffline(stamp);
        }
    }

    private static void DiscoverActors(int stamp, bool online)
    {
        ActorManager? manager = Game.Actors;
        if (manager == null) return;
        bool players = online && World.Has("players");
        bool mods = World.Has("monster mods");
        LoadObjectives(online);
        var lists = manager.visuals;
        if (lists == null) return;
        foreach (var list in lists)
        {
            if (list == null) continue;
            string? alignment = list.alignment?.name;
            bool hostile = alignment == HostileAlignmentName || alignment == HostileNeutralAlignmentName;
            var visuals = list.visuals;
            if (visuals == null) continue;
            for (int i = 0; i < visuals.Count; i++)
            {
                ActorVisuals? visual = visuals[i];
                if (visual == null) continue;
                if (hostile && !visual.isPlayer)
                {
                    if (!World.Known(visual.Pointer, stamp)) World.Add(visual.Pointer, stamp, MakeEnemy(visual, online, mods));
                }
                else if (players && visual.isPlayer)
                {
                    // Only the sync object's health, ward and death are read; the local player is left out.
                    PlayerActorSync? sync = visual.actorSync?.TryCast<PlayerActorSync>();
                    if (sync != null && !sync.IsLocalPlayer && !World.Known(visual.Pointer, stamp)) World.Add(visual.Pointer, stamp, new WorldEntry { Kind = WorldKind.Player, Visuals = visual, Sync = sync });
                }
            }
        }
    }

    private static WorldEntry MakeEnemy(ActorVisuals visual, bool online, bool readMods)
    {
        var entry = new WorldEntry { Kind = WorldKind.Enemy, Visuals = visual, Mods = s_noMods };
        if (online) entry.Sync = visual.actorSync;
        // Far actors are inactive, which GetComponentInParent skips unless asked.
        else entry.Actor = visual.GetComponentInParent<Actor>(true);
        // The display information sits on the visuals object, not the actor.
        ActorDisplayInformation? info = visual.GetComponent<ActorDisplayInformation>();
        if (Game.Alive(info))
        {
            entry.Name = string.IsNullOrEmpty(info!.displayName) ? null : info.displayName;
            entry.Rarity = (Rarity)(int)info.actorClass;
            if (readMods) entry.Mods = Mods(info);
        }
        entry.Objective = online || !World.Has("objectives") ? null : Game.Alive(entry.Actor) && Game.Alive(entry.Actor!.dying) && s_objectives.Contains(entry.Actor.dying.Pointer);
        return entry;
    }

    private static List<DataEntry> Mods(ActorDisplayInformation info)
    {
        var applied = info.appliedMods;
        if (applied == null || applied.Count == 0) return s_noMods;
        var mods = new List<DataEntry>(applied.Count);
        for (int i = 0; i < applied.Count; i++)
        {
            if (applied[i] != null) mods.Add(GameData.MonsterMod(applied[i]));
        }
        return mods;
    }

    // Offline the echo's objective enemies are listed by their Dying state; online the client doesn't know them.
    private static void LoadObjectives(bool online)
    {
        s_objectives.Clear();
        if (online || !World.Has("objectives")) return;
        try
        {
            MonolithZoneManager? zone = MonolithZoneManager.instance;
            var objectives = Game.Alive(zone) ? zone!.objectiveEnemies : null;
            for (int i = 0; objectives != null && i < objectives.Count; i++)
            {
                if (objectives[i] != null) s_objectives.Add(objectives[i].Pointer);
            }
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("world:objectives", $"World model objectives read failed: {e.Message}", 30);
        }
    }

    private static void DiscoverShrinesOffline(int stamp)
    {
        var managers = ShrinesManager.ManagersByScene;
        if (managers == null) return;
        foreach (var pair in managers)
        {
            ShrinesManager? manager = pair.Value;
            if (!Game.Alive(manager)) continue;
            var shrines = manager!._allShrineObjects;
            for (int i = 0; shrines != null && i < shrines.Count; i++)
            {
                GameObject? shrine = shrines[i];
                if (Game.Alive(shrine) && !World.Known(shrine!.Pointer, stamp)) World.Add(shrine!.Pointer, stamp, MakeObject(WorldKind.Shrine, shrine, shrine.GetComponent<ConditionHandler>()));
            }
        }
    }

    // Chests are the click handlers under each scene's chest placement manager.
    private static void DiscoverChestsOffline(int stamp)
    {
        var managers = ChestPlacementManager.chestPlacementManagers;
        if (managers == null) return;
        foreach (var pair in managers)
        {
            ChestPlacementManager? manager = pair.Value;
            if (!Game.Alive(manager)) continue;
            Transform parent = manager!.transform;
            for (int i = 0; i < parent.childCount; i++)
            {
                GameObject chest = parent.GetChild(i).gameObject;
                ConditionHandler? handler = chest.GetComponent<ConditionHandler>();
                if (Game.Alive(handler) && !World.Known(chest.Pointer, stamp)) World.Add(chest.Pointer, stamp, MakeObject(WorldKind.Chest, chest, handler));
            }
        }
    }

    // FindObjectsOfType walks the whole scene (about 17 ms each online), so it runs only while a reader asks for shrines
    // or chests: then at the first discoveries of a scene and every 10 s, the two kinds 5 s apart. Otherwise, and
    // between searches, their entries are kept as they are.
    private const float OnlineScanSeconds = 10f;
    private const float SceneStartScanSeconds = 3f;
    private static float s_nextShrineScan, s_nextChestScan;
    private static int s_scanScene;
    private static float s_sceneSeenAt;
    private static bool s_objectsIdle = true;

    // Online shrines and chests are sync objects whose world object is made separately (the proxy sits at the origin).
    private static void DiscoverOnline<T>(int stamp, WorldKind kind, ref float nextScan, float offset) where T : Component
    {
        float now = Time.unscaledTime;
        if (World.SceneHandle != s_scanScene)
        {
            s_scanScene = World.SceneHandle;
            s_sceneSeenAt = now;
            s_nextShrineScan = s_nextChestScan = 0f;
        }
        if (!World.ObjectsWanted)
        {
            s_objectsIdle = true;
            World.Keep(kind, stamp);
            return;
        }
        // A reader just started asking: search now rather than at the next slot.
        if (s_objectsIdle)
        {
            s_objectsIdle = false;
            s_nextShrineScan = s_nextChestScan = 0f;
        }
        // The client places them shortly after the scene starts, so the first few discoveries all search.
        bool sceneStart = now - s_sceneSeenAt < SceneStartScanSeconds;
        if (!sceneStart && now < nextScan)
        {
            World.Keep(kind, stamp);
            return;
        }
        nextScan = now + OnlineScanSeconds + (nextScan == 0f ? offset : 0f);

        var found = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<T>());
        if (found == null) return;
        foreach (var item in found)
        {
            if (item == null) continue;
            if (kind == WorldKind.Shrine && item.TryCast<ShrineSync>() is { } shrine)
            {
                if (!World.Known(shrine.Pointer, stamp)) World.Add(shrine.Pointer, stamp, new WorldEntry { Kind = kind, ShrineSync = shrine, Handler = shrine.conditionHandler });
            }
            else if (kind == WorldKind.Chest && item.TryCast<ChestSync>() is { } chest)
            {
                if (!World.Known(chest.Pointer, stamp)) World.Add(chest.Pointer, stamp, new WorldEntry { Kind = kind, Object = chest.chestObject, Handler = chest.conditionHandler });
            }
        }
    }

    private static WorldEntry MakeObject(WorldKind kind, GameObject obj, ConditionHandler? handler) =>
        new() { Kind = kind, Object = obj, Handler = handler, Name = ObjectName(obj) };

    private static string? ObjectName(GameObject obj)
    {
        DisplayInformation? info = obj.GetComponent<DisplayInformation>();
        if (Game.Alive(info) && !string.IsNullOrEmpty(info!.displayName)) return info.displayName;
        string name = obj.name;
        int clone = name.IndexOf(" (Clone)", StringComparison.Ordinal);
        return clone > 0 ? name[..clone] : name;
    }

    /// <summary>Reads an entry's changing values: position, active, dead, health and ward, or used.</summary>
    public static void Refresh(WorldEntry entry)
    {
        switch (entry.Kind)
        {
            case WorldKind.Enemy:
            case WorldKind.Player:
                RefreshActor(entry);
                break;
            default:
                RefreshObject(entry);
                break;
        }
    }

    private static void RefreshActor(WorldEntry entry)
    {
        ActorVisuals? visual = entry.Visuals;
        if (!Game.Alive(visual))
        {
            entry.Drop();
            return;
        }
        if (entry.Object == null)
        {
            entry.Transform = visual!.transform;
            entry.Object = visual.gameObject;
        }
        entry.Position = entry.Transform!.position;
        entry.Active = entry.Object.activeInHierarchy;
        entry.Dead = visual!.dead;

        if (entry.Sync != null)
        {
            if (!Game.Alive(entry.Sync))
            {
                entry.Drop();
                return;
            }
            if (entry.Kind == WorldKind.Player || World.Has("enemy health"))
            {
                float max = entry.Sync!.SyncedMaxHealth;
                entry.MaxHealth = max;
                entry.HealthFraction = entry.Sync.healthRatio / 255f;
                entry.Health = entry.HealthFraction * max;
                entry.Ward = entry.Sync.SyncedCurrentWard;
                if (entry.Kind == WorldKind.Player) entry.Dead = entry.Sync.IsDead;
            }
        }
        else if (Game.Alive(entry.Actor) && World.Has("enemy health"))
        {
            if (entry.HealthSource == null)
            {
                entry.HealthSource = entry.Actor!.health;
                entry.WardSource = entry.Actor.protection;
            }
            BaseHealth? health = entry.HealthSource;
            if (health != null && !health.WasCollected)
            {
                float current = health.currentHealth, max = health.maxHealth;
                entry.Health = current;
                entry.MaxHealth = max;
                entry.HealthFraction = max > 0f ? current / max : null;
            }
            ProtectionClass? protection = entry.WardSource;
            entry.Ward = protection != null && !protection.WasCollected ? protection.currentWard : null;
        }
    }

    private static void RefreshObject(WorldEntry entry)
    {
        if (entry.ShrineSync != null)
        {
            if (!Game.Alive(entry.ShrineSync))
            {
                entry.Drop();
                return;
            }
            // The shrine's world object can be made after the sync object.
            if (!Game.Alive(entry.Object))
            {
                entry.Object = entry.ShrineSync!.ShrineObject;
                if (Game.Alive(entry.Object)) entry.Name = ObjectName(entry.Object!);
            }
            if (!Game.Alive(entry.Handler)) entry.Handler = entry.ShrineSync!.conditionHandler;
        }
        if (!Game.Alive(entry.Object))
        {
            // Online the object may not exist yet; offline it is gone.
            if (entry.ShrineSync == null) entry.Drop();
            return;
        }
        entry.Position = entry.Object!.transform.position;
        entry.Active = entry.Object.activeInHierarchy;
        entry.Used = Game.Alive(entry.Handler) ? entry.Handler!.triggered : null;
        if (entry.Name == null) entry.Name = ObjectName(entry.Object);
    }
}
