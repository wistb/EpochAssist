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
    public static UseAbilityProcessor? AbilityProcessor => Get(() => Input?.useAbilityProcessor);
    public static LocalTreeData? LocalTree => Get(() => PlayerFinder.getLocalTreeData());
    public static DList<AbilityBarIcon>? BarIcons => Get(() => AbilityBarIcon.all);

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
