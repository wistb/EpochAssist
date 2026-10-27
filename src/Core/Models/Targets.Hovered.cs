using UnityEngine;

namespace EpochAssist.Core;

// The one hovered read every owner uses: the game's own hovered enemy (its top bar's actor) while it shows one, matched
// to a world entry; else the old rule, the entry the cursor ray hits (or a parent of what it hits), else the nearest
// enemy within 2 m of the cursor point. Once per frame.
internal static partial class Targets
{
    private const float HoverFallbackMetres = 2f;

    /// <summary>The cursor point skills aim at and what it hits, once a frame; set by the feature that reads it.</summary>
    public static Func<(Vector3? Point, Transform? Hit)>? Cursor { get; set; }

    /// <summary>Whether the game's hovered read is used (dev builds can turn it off to compare with the old rule).</summary>
    internal static bool UseGameHovered { get; set; } = true;


    private static Il2Cpp.UIActorInfo? s_actorInfo;
    private static int s_actorInfoScene = int.MinValue;
    private static float s_actorInfoSearchedAt = float.NegativeInfinity;
    private static int s_hoverFrame = -1;
    private static WorldEntry? s_hovered;

    /// <summary>The enemy under the cursor, or null: the game's hovered enemy where it reads one, else the old rule.</summary>
    public static WorldEntry? Hovered()
    {
        int frame = Time.frameCount;
        if (frame == s_hoverFrame) return s_hovered;
        s_hoverFrame = frame;
        s_hovered = null;
        try
        {
            if (UseGameHovered && GameHovered(out var entry, out _, out _) && entry != null) return s_hovered = entry;
            var (point, hit) = Cursor?.Invoke() ?? (null, null);
            s_hovered = RuleHovered(point, hit);
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("targets:hovered", $"Hovered enemy read failed: {e.Message}", 30);
        }
        return s_hovered;
    }

    /// <summary>The old rule: the entry the cursor ray hits (or a parent of what it hits), else the nearest enemy within
    /// 2 m of the cursor point.</summary>
    public static WorldEntry? RuleHovered(Vector3? cursor, Transform? hit)
    {
        if (cursor is not Vector3 point) return null;
        var entries = World.Entries;
        WorldEntry? near = null;
        float nearest = HoverFallbackMetres;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (!IsLiveEnemy(entry)) continue;
            if (hit != null && IsOrParent(entry.Transform, hit)) return entry;
            float d = Vector3.Distance(entry.Position, point);
            if (d <= nearest)
            {
                nearest = d;
                near = entry;
            }
        }
        return near;
    }

    /// <summary>
    /// The game's hovered enemy: false when the read isn't there (the group missing, no actor info in this scene);
    /// otherwise whether it shows one now, its object's name, and the world entry it matches (null when none does).
    /// </summary>
    public static bool GameHovered(out WorldEntry? entry, out bool hovering, out string? actorName)
    {
        entry = null;
        hovering = false;
        actorName = null;
        if (!World.Has("hovered enemy") || ActorInfo() is not { } info) return false;
        hovering = info.CurrentlyHovered;
        var actor = info.ActorOnMouse;
        if (!hovering || !Game.Alive(actor)) return true;
        var transform = actor!.transform;
        actorName = transform.gameObject.name;
        var entries = World.Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            if (IsLiveEnemy(entries[i]) && IsOrParent(entries[i].Transform, transform))
            {
                entry = entries[i];
                break;
            }
        }
        return true;
    }

    // The scene's actor info, found once per scene (and again when it is destroyed); a scene without one is searched
    // again once a second, not every frame.
    private static Il2Cpp.UIActorInfo? ActorInfo()
    {
        int scene = World.SceneHandle;
        if (scene == s_actorInfoScene && Game.Alive(s_actorInfo)) return s_actorInfo;
        if (scene == s_actorInfoScene && Time.unscaledTime - s_actorInfoSearchedAt < 1f) return null;
        s_actorInfoScene = scene;
        s_actorInfoSearchedAt = Time.unscaledTime;
        s_actorInfo = UnityEngine.Object.FindObjectOfType(Il2CppInterop.Runtime.Il2CppType.Of<Il2Cpp.UIActorInfo>())?.TryCast<Il2Cpp.UIActorInfo>();
        return Game.Alive(s_actorInfo) ? s_actorInfo : null;
    }

    private static bool IsLiveEnemy(WorldEntry entry) => entry.Kind == WorldKind.Enemy && !entry.Gone && !entry.Dead && entry.Active;

    /// <summary>Whether the entry's transform is the given one or one of its parents (colliders sit on child objects).</summary>
    public static bool IsOrParent(Transform? entry, Transform hit)
    {
        if (!Game.Alive(entry)) return false;
        var t = hit;
        for (int depth = 0; depth < 6 && Game.Alive(t); depth++, t = t!.parent)
        {
            if (t!.Pointer == entry!.Pointer) return true;
        }
        return false;
    }

    // The cached actor info and this frame's read go with the scene and the feature.
    private static void ClearHovered()
    {
        s_actorInfo = null;
        s_actorInfoScene = int.MinValue;
        s_actorInfoSearchedAt = float.NegativeInfinity;
        s_hoverFrame = -1;
        s_hovered = null;
    }
}
