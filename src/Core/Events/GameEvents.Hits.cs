using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

internal static partial class GameEvents
{
    // ---- Hits ----

    private static float? s_lastHealth;

    // A hit is one health drop in a frame, the same in both modes: online the game's own hit event never fires for the
    // local player (GF-47), so the offline damage method's detail isn't used either.
    private static void UpdateHits(Feature feature, bool? online)
    {
        bool active = Wanted<HitTaken>(feature, s_hits, online);
        if (active != s_hits.Active)
        {
            s_hits.Active = active;
            s_lastHealth = null;
        }
        if (!active) return;

        float? health = PlayerModel.Vitals?.Health;
        float? last = s_lastHealth;
        s_lastHealth = health;
        if (health is not float now || last is not float before || now >= before) return;
        Bus.Raise(new HitTaken(before - now));
    }
}
