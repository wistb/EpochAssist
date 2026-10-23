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
    private static float s_nextLookupAt;

    public static bool Exists => Object != null;

    public static Actor? Actor
    {
        get
        {
            Refresh();
            return s_actor;
        }
    }

    public static GameObject? Object
    {
        get
        {
            Refresh();
            return s_object;
        }
    }

    public static Transform? Transform => Object?.transform;

    public static void Reset()
    {
        s_actor = null;
        s_object = null;
        s_nextLookupAt = 0f;
    }

    private static void Refresh()
    {
        if (s_object != null) return;
        if (Time.unscaledTime < s_nextLookupAt) return;
        s_nextLookupAt = Time.unscaledTime + RetrySeconds;

        s_actor = PlayerFinder.getPlayerActor();
        s_object = s_actor != null ? s_actor.gameObject : null;
        for (int i = 0; s_object == null && i < s_objectNames.Length; i++)
        {
            s_object = GameObject.Find(s_objectNames[i]);
        }
        if (s_actor == null && s_object != null) s_actor = s_object.GetComponent<Actor>();
    }
}
