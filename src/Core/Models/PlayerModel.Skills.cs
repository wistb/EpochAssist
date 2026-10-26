using Il2Cpp;
using UnityEngine;

namespace EpochAssist.Core;

internal static partial class PlayerModel
{
    public const string CastSignalOffline = "player: cast signal (offline)";
    public const string CastSignalOnline = "player: cast signal (online)";

    /// <summary>A skill isn't on the bar (a known state).</summary>
    public const int NotOnBar = 0;
    /// <summary>The bar can't be read this frame (unknown).</summary>
    public const int BarUnknown = -1;

    // Skill ids seen on the bar this session, with their names, in the order first seen; kept across characters.
    private static readonly Dictionary<string, string> s_seenSkills = new();
    private static readonly List<string> s_seenOrder = new();

    /// <summary>Skill ids seen on the bar this session (id, name), first seen first.</summary>
    public static IEnumerable<(string Id, string Name)> SeenSkills => s_seenOrder.Select(id => (id, s_seenSkills[id]));

    /// <summary>A skill's name if it was seen on the bar this session, else null.</summary>
    public static string? SeenSkillName(string id) => s_seenSkills.TryGetValue(id, out string? name) ? name : null;

    private static void SeeSkill(string? id, string? name)
    {
        if (id == null || s_seenSkills.ContainsKey(id)) return;
        s_seenSkills[id] = name ?? id;
        s_seenOrder.Add(id);
    }

    /// <summary>
    /// The bar slot (1–5) holding a skill in the current form, NotOnBar when it isn't there, BarUnknown when the bar
    /// can't be read. Allocates nothing.
    /// </summary>
    public static int SlotOf(string abilityId)
    {
        var slots = Slots;
        if (slots == null) return BarUnknown;
        bool unread = false;
        for (int i = 0; i < Dispatcher.SlotCount && i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot == null)
            {
                unread = true;
                continue;
            }
            if (slot.AbilityId == abilityId) return i + 1;
        }
        return unread ? BarUnknown : NotOnBar;
    }

    // ---- Cast signal (GF-72): the player's ability state restarts a timer at every cast and holds the skill cast ----

    // When each skill was last cast (unscaled seconds), by skill id.
    private static readonly Dictionary<string, float> s_castAt = new();
    private static int s_castFrame = -1;
    // The game's time since the last cast at the last read (NaN before the first).
    private static float s_lastCastStart = float.NaN;

    /// <summary>Whether casts can be seen in this mode (offline: the player's ability state; online: the client's).</summary>
    public static bool CastSignal => Exists && Online switch
    {
        false => HasGroup(CastSignalOffline),
        true => HasGroup(CastSignalOnline),
        _ => false,
    };

    /// <summary>
    /// When the skill now in a bar slot was last cast, as seen by the cast signal (read once per frame; the Macros feature
    /// reads it every frame it runs, so no cast between reads is missed). Unscaled seconds; negative infinity for none.
    /// </summary>
    public static float LastCastAt(int slotNumber)
    {
        PollCasts();
        return Slot(slotNumber)?.AbilityId is { } id ? LastCastOf(id) : float.NegativeInfinity;
    }

    /// <summary>When a skill was last cast, as seen by the cast signal; negative infinity for never.</summary>
    public static float LastCastOf(string abilityId)
    {
        PollCasts();
        return s_castAt.TryGetValue(abilityId, out float at) ? at : float.NegativeInfinity;
    }

    private static void PollCasts()
    {
        int frame = Time.frameCount;
        if (frame == s_castFrame || !CastSignal) return;
        s_castFrame = frame;
        try
        {
            float since;
            Ability? ability;
            if (Online == true)
            {
                if (Component(ref s_clientUsing) is not { } client) return;
                since = client.timeSinceCast;
                ability = client.ability;
            }
            else
            {
                if (Component(ref s_using) is not { } usingAbility) return;
                since = usingAbility.timeSinceLastAbilityUseStart;
                ability = usingAbility.ability;
            }
            // The game's timer only goes back down at a cast; the first read just notes where it is.
            bool first = float.IsNaN(s_lastCastStart);
            bool cast = !first && since < s_lastCastStart - 1e-3f;
            s_lastCastStart = since;
            if (!cast || !Game.Alive(ability) || ability!.playerAbilityID is not { Length: > 0 } id) return;
            s_castAt[id] = Clock.Now - since;
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("player:casts", $"Player model cast read failed: {e.Message}", 30);
        }
    }

    private static void ResetCasts()
    {
        s_castAt.Clear();
        s_castFrame = -1;
        s_lastCastStart = float.NaN;
    }
}
