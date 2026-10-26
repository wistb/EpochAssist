using Il2Cpp;

namespace EpochAssist.Core;

internal static partial class PlayerModel
{
    // HealthPotion (offline) is a HealthPotionCharges, the component online; each mode reads its own.
    private static void FillPotions(Potions potions)
    {
        potions.Charges = null;
        potions.MaxCharges = null;
        if (!Game.Alive(s_potion)) s_potion = null;
        if (s_potion == null && s_object != null)
        {
            s_potion = Online == true ? s_object.GetComponent<HealthPotionCharges>() : s_object.GetComponent<HealthPotion>();
        }
        if (!Game.Alive(s_potion)) return;
        potions.Charges = s_potion!.currentCharges;
        potions.MaxCharges = s_potion.maxCharges;
    }
}

internal static partial class PlayerModel
{
    public const string GameAutoDrinkGroup = "player: game auto drink";

    private static Il2Cpp.PlayerActorSync? s_actorSync;
    private static int s_autoDrinkFrame = -1;
    private static bool? s_autoDrink;

    /// <summary>
    /// The game's own Auto Drink setting for this character (GF-20: the local PlayerActorSync's AutoDrinkPotions), or
    /// null when it can't be read. The player's sync object sits beside it under the persistent player.
    /// </summary>
    public static bool? GameAutoDrink
    {
        get
        {
            if (!Exists || !HasGroup(GameAutoDrinkGroup)) return null;
            int frame = UnityEngine.Time.frameCount;
            if (frame == s_autoDrinkFrame) return s_autoDrink;
            s_autoDrinkFrame = frame;
            s_autoDrink = null;
            try
            {
                if (!Game.Alive(s_actorSync))
                {
                    var parent = Player.Transform?.parent;
                    s_actorSync = parent != null ? parent.GetComponentInChildren<Il2Cpp.PlayerActorSync>() : null;
                }
                if (Game.Alive(s_actorSync)) s_autoDrink = s_actorSync!.AutoDrinkPotions;
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("player:autodrink", $"Player model auto drink read failed: {e.Message}", 30);
            }
            return s_autoDrink;
        }
    }
}
