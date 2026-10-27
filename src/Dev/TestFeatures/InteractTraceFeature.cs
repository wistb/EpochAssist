using System.Globalization;
using EpochAssist.Core;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Dev;

/// <summary>
/// Dev only: logs the game's own click path for shrines, chests and ground pick-ups while `interact trace on` is set,
/// so a player's click shows which methods run, from how far and with which range. The patches change nothing; each
/// sits in its own optional group, so a method the game no longer has stops only its own line.
/// </summary>
internal sealed class InteractTraceFeature : Feature
{
    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;

    /// <summary>The patches log only while this is set.</summary>
    public static bool On;

    public override string Id => "interacttrace";
    public override string Name => "Interact trace";

    public override Type[] PatchClasses => new[]
    {
        typeof(ObjectClickPatch), typeof(TriggerPatch), typeof(ShrineInteractionPatch), typeof(ShrineInitiatePatch),
        typeof(ChestInteractionPatch), typeof(ChestInitiatePatch), typeof(ItemClickedPatch), typeof(ItemRequestPatch),
        typeof(GoldClickedPatch), typeof(PotionClickedPatch), typeof(XpTomeClickedPatch), typeof(FavorTomeClickedPatch),
    };

    public override IReadOnlyDictionary<Type, string> PatchGroups { get; } = new Dictionary<Type, string>
    {
        [typeof(ObjectClickPatch)] = "trace: object click",
        [typeof(TriggerPatch)] = "trace: condition trigger",
        [typeof(ShrineInteractionPatch)] = "trace: shrine sync",
        [typeof(ShrineInitiatePatch)] = "trace: shrine sync initiate",
        [typeof(ChestInteractionPatch)] = "trace: chest sync",
        [typeof(ChestInitiatePatch)] = "trace: chest sync initiate",
        [typeof(ItemClickedPatch)] = "trace: item label click",
        [typeof(ItemRequestPatch)] = "trace: item label pick-up",
        [typeof(GoldClickedPatch)] = "trace: gold click",
        [typeof(PotionClickedPatch)] = "trace: potion click",
        [typeof(XpTomeClickedPatch)] = "trace: xp tome click",
        [typeof(FavorTomeClickedPatch)] = "trace: favour tome click",
    };

    public override void Shutdown() => On = false;

    // One line per call: the method, the object, the player's position and the flat distance to the object, the range
    // the game holds for it, and for item labels whether the label is waiting in the distant pick-up list.
    private static void Trace(string method, Component? target, float? range = null, bool? isClick = null, GroundItemLabel? label = null)
    {
        if (!On) return;
        try
        {
            string name = Game.Alive(target) ? target!.gameObject.name : "?";
            string where = "player=?";
            if (Player.Transform is { } player && Game.Alive(player))
            {
                var p = player.position;
                where = $"player={F(p.x)},{F(p.z)}";
                if (Game.Alive(target))
                {
                    var t = target!.transform.position;
                    where += $" dist={F(Vector2.Distance(new Vector2(p.x, p.z), new Vector2(t.x, t.z)))}";
                }
            }
            string extra = (range is { } r ? $" range={F(r)}" : string.Empty)
                + (isClick is { } c ? $" isClick={c}" : string.Empty)
                + (Game.Alive(label) ? $" distantList={label!.inDistantPickupList}" : string.Empty);
            Log.Info($"trace: {method} {name} {where}{extra}");
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("interacttrace", $"trace: {method} read failed: {e.Message}", 30);
        }
    }

    private static string F(float value) => value.ToString("F2", s_inv);

    [HarmonyPatch(typeof(WorldObjectClickListener), nameof(WorldObjectClickListener.ObjectClick))]
    private static class ObjectClickPatch
    {
        // A prefix: the click may set a walking target, and the line should show where the player stood before it.
        private static void Prefix(WorldObjectClickListener __instance, bool isClick) =>
            Trace("ObjectClick", __instance, Game.Alive(__instance) ? __instance.interactionRange : null, isClick);
    }

    [HarmonyPatch(typeof(ConditionHandler), nameof(ConditionHandler.TryTriggerInteraction))]
    private static class TriggerPatch
    {
        private static void Postfix(ConditionHandler __instance) =>
            Trace(Game.Alive(__instance) ? $"TryTriggerInteraction triggered={__instance.triggered}" : "TryTriggerInteraction", __instance);
    }

    [HarmonyPatch(typeof(ShrineSync), nameof(ShrineSync.OnInteraction))]
    private static class ShrineInteractionPatch
    {
        private static void Postfix(ShrineSync __instance) => Trace("ShrineSync.OnInteraction", __instance);
    }

    [HarmonyPatch(typeof(ShrineSync), nameof(ShrineSync.UseShrineInitiate))]
    private static class ShrineInitiatePatch
    {
        private static void Postfix(ShrineSync __instance) => Trace("ShrineSync.UseShrineInitiate", __instance);
    }

    [HarmonyPatch(typeof(ChestSync), nameof(ChestSync.OnInteraction))]
    private static class ChestInteractionPatch
    {
        private static void Postfix(ChestSync __instance) => Trace("ChestSync.OnInteraction", __instance);
    }

    [HarmonyPatch(typeof(ChestSync), nameof(ChestSync.UseChestInitiate))]
    private static class ChestInitiatePatch
    {
        private static void Postfix(ChestSync __instance) => Trace("ChestSync.UseChestInitiate", __instance);
    }

    [HarmonyPatch(typeof(GroundItemLabel), nameof(GroundItemLabel.ClickedItem))]
    private static class ItemClickedPatch
    {
        // A prefix as well: the click from afar walks, and the distant list is set by the call itself (shown by the
        // pick-up line that follows, or by the label staying in the list).
        private static void Prefix(GroundItemLabel __instance) =>
            Trace("GroundItemLabel.ClickedItem", Game.Alive(__instance) ? __instance.visuals : null, GroundItemLabel.pickupDistance, label: __instance);
    }

    [HarmonyPatch(typeof(GroundItemLabel), nameof(GroundItemLabel.requestPickup))]
    private static class ItemRequestPatch
    {
        private static void Prefix(GroundItemLabel __instance) =>
            Trace("GroundItemLabel.requestPickup", Game.Alive(__instance) ? __instance.visuals : null, GroundItemLabel.pickupDistance, label: __instance);
    }

    [HarmonyPatch(typeof(GoldPickup), nameof(GoldPickup.ClickedItem))]
    private static class GoldClickedPatch
    {
        private static void Prefix(GoldPickup __instance) => Trace("GoldPickup.ClickedItem", __instance);
    }

    [HarmonyPatch(typeof(PotionPickup), nameof(PotionPickup.ClickedItem))]
    private static class PotionClickedPatch
    {
        private static void Prefix(PotionPickup __instance) => Trace("PotionPickup.ClickedItem", __instance);
    }

    // The tome labels don't override the generic label's click, so each instantiation is patched on its own.
    [HarmonyPatch(typeof(NonItemPickupableGroundLabel<GroundXPTomeVisuals>), nameof(NonItemPickupableGroundLabel<GroundXPTomeVisuals>.Clicked))]
    private static class XpTomeClickedPatch
    {
        private static void Prefix(NonItemPickupableGroundLabel<GroundXPTomeVisuals> __instance) =>
            Trace("XpTomeLabel.Clicked", Game.Alive(__instance) ? __instance.visuals : null);
    }

    [HarmonyPatch(typeof(NonItemPickupableGroundLabel<GroundFavorTomeVisuals>), nameof(NonItemPickupableGroundLabel<GroundFavorTomeVisuals>.Clicked))]
    private static class FavorTomeClickedPatch
    {
        private static void Prefix(NonItemPickupableGroundLabel<GroundFavorTomeVisuals> __instance) =>
            Trace("FavorTomeLabel.Clicked", Game.Alive(__instance) ? __instance.visuals : null);
    }
}
