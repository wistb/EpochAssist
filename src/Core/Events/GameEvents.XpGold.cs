using Il2Cpp;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

internal static partial class GameEvents
{
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
}
