using Il2Cpp;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

internal static partial class GameEvents
{
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
        int? corruption = s_feature?.Has(Corruption) == true ? Il2CppLE.Gameplay.Monolith.MonolithGameplayManager.CurrentCorruption : null;
        Bus.Raise(new MonolithPick(modifier, island.stabilityReward, island.hexIndex, s_feature?.Has(MonolithReward) == true ? RewardOf(island) : null, corruption));
    }

    // The island's echo reward as its tooltip names it: the web's entry for the same hex holds the rewards (game facts
    // GF-28; the island's droppableRewardIndex points into a table of the whole web, not this list).
    private static string? RewardOf(EchoWebIsland island)
    {
        if (!island.hasDroppableReward) return null;
        var web = UnityEngine.Object.FindObjectOfType(Il2CppInterop.Runtime.Il2CppType.Of<MonolithEchoWebUI>(), true)?.TryCast<MonolithEchoWebUI>();
        if (!Game.Alive(web) || web!.activeIslands == null) return null;
        foreach (var entry in web.activeIslands)
        {
            if (entry == null || entry.island == null || entry.island.hexIndex != island.hexIndex) continue;
            return entry.rewards is { Count: > 0 } rewards && rewards[0] is { } reward ? reward.title : null;
        }
        return null;
    }
}
