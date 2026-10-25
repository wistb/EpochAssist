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
