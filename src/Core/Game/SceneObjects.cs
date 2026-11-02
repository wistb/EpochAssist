using UnityEngine;

namespace EpochAssist.Core;

/// <summary>
/// Game components with no instance field (the tooltip manager, the stash window's controls), found by an object
/// search, kept while they live and searched again at most once a second. An active one is preferred.
/// </summary>
internal static class SceneObjects
{
    private const float FindSeconds = 1f;

    private static readonly Dictionary<Type, (UnityEngine.Object? Found, float NextFind)> s_found = new();

    public static T? Find<T>() where T : Component
    {
        s_found.TryGetValue(typeof(T), out var entry);
        if (entry.Found is T found && Game.Alive(found)) return found;
        if (Clock.Now < entry.NextFind) return null;
        T? live = null;
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<T>(), true))
        {
            var t = obj.TryCast<T>();
            if (!Game.Alive(t)) continue;
            live = t;
            if (t!.gameObject.activeInHierarchy) break;
        }
        s_found[typeof(T)] = (live, Clock.Now + FindSeconds);
        return live;
    }

    /// <summary>Drops what was found (a new scene, a stop).</summary>
    public static void Forget() => s_found.Clear();
}
