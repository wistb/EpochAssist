using Il2Cpp;

namespace EpochAssist.Core;

internal static partial class PlayerModel
{
    private static void FillVitals(Vitals vitals)
    {
        PlayerHealth health = Player.Health ?? throw new InvalidOperationException("no PlayerHealth");
        PlayerMana mana = Component(ref s_mana) ?? throw new InvalidOperationException("no PlayerMana");
        vitals.Health = health.currentHealth;
        vitals.MaxHealth = health.maxHealth;
        vitals.HealthFraction = health.getHealthPercent();
        vitals.Mana = mana.currentMana;
        vitals.MaxMana = mana.maxMana;
        vitals.Ward = ReadWard();
    }

    // Offline the actor's protection; online the player object's ward holder.
    private static float? ReadWard()
    {
        if (!Has("ward")) return null;
        try
        {
            if (Online != true)
            {
                ProtectionClass? protection = Player.Actor?.protection;
                return Game.Alive(protection) ? protection!.currentWard : null;
            }
            return Component(ref s_ward) is { } ward ? ward.currentWard : null;
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("player:ward", $"Player model ward read failed: {e.Message}", 30);
            return null;
        }
    }
}
