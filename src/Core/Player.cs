using Il2Cpp;
using UnityEngine;

namespace EpochAssist.Core;

// Cached local player, looked up again after a scene change or when it goes missing.
internal static class Player
{
    private const float RetrySeconds = 0.5f;
    private static readonly string[] s_objectNames = { "MainPlayer(Clone)", "Local Player(Clone)" };

    private static Actor? s_actor;
    private static GameObject? s_object;
    private static Cooldown s_lookup;
    private static PlayerHealth? s_health;

    public static bool Exists => Object != null;

    /// <summary>True online, false offline, null until the player is found (the two modes spawn differently named objects).</summary>
    public static bool? Online { get; private set; }

    // A cached object can be destroyed before the next lookup is due (a scene transition), so only live ones are handed out.
    public static Actor? Actor
    {
        get
        {
            Refresh();
            return Game.Alive(s_actor) ? s_actor : null;
        }
    }

    public static GameObject? Object
    {
        get
        {
            Refresh();
            return Game.Alive(s_object) ? s_object : null;
        }
    }

    public static Transform? Transform => Object?.transform;

    /// <summary>The player's health component (Actor.health is not it online).</summary>
    public static PlayerHealth? Health
    {
        get
        {
            GameObject? obj = Object;
            if (obj == null) return null;
            if (!Game.Alive(s_health)) s_health = obj.GetComponent<PlayerHealth>();
            return Game.Alive(s_health) ? s_health : null;
        }
    }

    public static void Reset()
    {
        s_actor = null;
        s_object = null;
        s_health = null;
        s_lookup.Clear();
        Online = null;
        // Every scene change and character change comes through here, so the model never keeps the old player's values.
        PlayerModel.Reset();
        GameEvents.ResetPlayer();
    }

    private static void Refresh()
    {
        if (s_object != null) return;
        if (!s_lookup.Ready) return;
        s_lookup.Start(RetrySeconds);

        s_actor = PlayerFinder.getPlayerActor();
        s_object = s_actor != null ? s_actor.gameObject : null;
        for (int i = 0; s_object == null && i < s_objectNames.Length; i++)
        {
            s_object = GameObject.Find(s_objectNames[i]);
        }
        if (s_actor == null && s_object != null) s_actor = s_object.GetComponent<Actor>();
        if (s_object != null)
        {
            string name = s_object.name;
            Online = name == "Local Player(Clone)" ? true : name == "MainPlayer(Clone)" ? false : null;
        }
    }
}
