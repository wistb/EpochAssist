using Il2Cpp;
using UnityEngine;

namespace EpochAssist.Core;

internal static partial class PlayerModel
{
    public const string ClassGroup = "player: class";
    public const string LevelGroup = "player: level";

    private static int s_classFrame = -1;
    private static int? s_classId;
    private static LocalTreeData? s_tree;

    /// <summary>
    /// The character's base class id (the game's CharacterClassID: 4 is Rogue, GF-77), or null when it can't be read:
    /// the player's tree data's class, in both modes.
    /// </summary>
    public static int? ClassId
    {
        get
        {
            if (!Exists) return null;
            int frame = Time.frameCount;
            if (frame == s_classFrame) return s_classId;
            s_classFrame = frame;
            s_classId = null;
            try
            {
                if (HasGroup(ClassGroup) && Component(ref s_tree)?.characterClass is { } characterClass && Game.Alive(characterClass))
                {
                    s_classId = (int)characterClass.classID;
                }
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("player:class", $"Player model class read failed: {e.Message}", 30);
            }
            return s_classId;
        }
    }

    /// <summary>The character's level from the player's tree data (both modes), or null when it can't be read.</summary>
    public static int? Level
    {
        get
        {
            if (!Exists || !HasGroup(LevelGroup)) return null;
            try
            {
                return Component(ref s_tree) is { } tree && tree.characterLevel > 0 ? tree.characterLevel : null;
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("player:level", $"Player model level read failed: {e.Message}", 30);
                return null;
            }
        }
    }

    /// <summary>
    /// What one health potion restores before gear and passives: 50 + 10 × level, as the game's potion text says; null
    /// without a level. Increased potion healing isn't counted, so the real heal can be larger.
    /// </summary>
    public static float? PotionHeal => Level is int level ? 50f + 10f * level : null;

    /// <summary>The character's base class name as the library stores it ("Rogue"), or null.</summary>
    public static string? ClassName => ClassId is int id ? GameData.ClassName(id) : null;
}
