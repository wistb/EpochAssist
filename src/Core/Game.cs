using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes;

namespace EpochAssist.Core;

/// <summary>
/// The game singletons the mod uses, each looked up safely: a missing, destroyed or collected object comes back null
/// instead of throwing. Look them up when needed rather than caching them across frames.
/// </summary>
internal static class Game
{
    public static EpochInputManager? Input => Get(() => EpochInputManager.instance);
    public static ActorManager? Actors => Get(() => ActorManager.instance);
    public static UIBase? UI => Get(() => UIBase.instance);
    /// <summary>The game's virtual camera rig (CameraTarget/MainCamera_Virtual).</summary>
    public static CameraManager? Camera => Get(() => CameraManager.instance);
    public static UseAbilityProcessor? AbilityProcessor => Get(() => Input?.useAbilityProcessor);
    public static LocalTreeData? LocalTree => Get(() => PlayerFinder.getLocalTreeData());
    public static DList<AbilityBarIcon>? BarIcons => Get(() => AbilityBarIcon.all);
    /// <summary>The one minimap (the corner minimap and, while open, the overlay map), through its icon manager: an
    /// object search would walk every object.</summary>
    public static Il2CppLE.UI.Minimap.Minimap? Minimap => Get(() => Il2CppLE.UI.Minimap.MinimapIconManager.Instance?.minimap);
    /// <summary>The minimap's zoom buttons and limits, in the same prefab as the minimap.</summary>
    public static DMMapZoom? MinimapZoom => Get(() =>
    {
        var map = Minimap;
        if (map == null) return null;
        var t = map.transform;
        while (t.parent != null && !t.name.StartsWith("Minimap(", StringComparison.Ordinal)) t = t.parent;
        return t.GetComponentInChildren<DMMapZoom>(true);
    });

    /// <summary>True for a live object: not null, not destroyed by Unity, not collected on the Il2Cpp side.</summary>
    public static bool Alive(Il2CppObjectBase? obj)
    {
        if (obj == null || obj.WasCollected) return false;
        return obj is not UnityEngine.Object unity || unity != null;
    }

    private static T? Get<T>(Func<T?> lookup) where T : Il2CppObjectBase
    {
        try
        {
            T? value = lookup();
            return Alive(value) ? value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
