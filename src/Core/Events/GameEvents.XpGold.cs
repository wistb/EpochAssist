using Il2Cpp;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

internal static partial class GameEvents
{
    // ---- XP and gold ----

    private static ExperienceTracker? s_experience;
    private static GoldTracker? s_goldTracker;
    private static long? s_lastXp;
    // The level threshold behind the last value: the lowest NextLevelExperience seen since the last level-up, counting
    // only values above the threshold before it (thresholds grow with level, and the game may update the threshold
    // before or after the value).
    private static long s_threshold = long.MaxValue;
    private static long s_previousThreshold;
    private static int? s_lastGold;
    // The tracker's level at the last read: a level-up is this + 1, whether or not CurrentLevel has changed yet.
    private static int? s_lastLevel;

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
        long next = s_experience.NextLevelExperience;
        long? last = s_lastXp;
        s_lastXp = xp;
        int? levelBefore = s_lastLevel;
        int level = s_experience.CurrentLevel;
        // After a level-up the remembered level stays one ahead until the tracker catches up.
        s_lastLevel = levelBefore is int known && known > level ? known : level;
        if (last is not long before)
        {
            s_previousThreshold = 0;
            s_threshold = next;
            return;
        }
        long amount;
        // CurrentExperience counts within the level (GF-51), so a drop is a level-up, whether or not CurrentLevel has
        // changed yet this frame: the gain is what the old level had left plus the new value.
        if (xp < before)
        {
            long old = s_threshold != long.MaxValue ? s_threshold : next;
            amount = Math.Max(0, old - before) + xp;
            s_previousThreshold = old;
            s_threshold = next > old ? next : long.MaxValue;
            if (feature.Has(s_level.Group) && levelBefore is int before0 && before0 > 0)
            {
                s_lastLevel = before0 + 1;
                Bus.Raise(new LevelUp(before0 + 1));
            }
        }
        else
        {
            amount = xp - before;
            if (next > s_previousThreshold && next < s_threshold) s_threshold = next;
        }
        if (amount > 0) Bus.Raise(new XpGained(amount, xp));
    }

    /// <summary>The live level read (xp within the level, its threshold, the level) while the XP producer runs, else null.</summary>
    private static (long Xp, long Threshold, int? Level)? LevelProgress()
    {
        if (!s_xp.Active || !Game.Alive(s_experience)) return null;
        long threshold = s_threshold != long.MaxValue ? s_threshold : s_experience!.NextLevelExperience;
        return threshold > 0 ? (s_experience!.CurrentExperience, threshold, s_lastLevel) : null;
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
