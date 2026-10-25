namespace EpochAssist.Core;

/// <summary>
/// Targets of values the game saves, declared here so they exist without their feature: recovery after a killed game
/// needs them before any feature runs. No release target is saved yet.
/// </summary>
internal static class EditTargets
{
    private static readonly Dictionary<string, object> s_saved = new();
    private static readonly Dictionary<string, Func<string, string, bool, bool?>> s_recover = new();

#if DEV
    /// <summary>
    /// Dev only, to show recovery after a killed game: the minimap zoom as the game saves it in PlayerPrefs on every zoom
    /// click (the minimap's live zoom is rewritten by the game each frame, so only the saved value is changed).
    /// </summary>
    public static readonly EditTarget<float> MinimapZoom = new()
    {
        Name = "minimap: zoom",
        Find = () => Game.Minimap is { } map ? new UnityEngine.Object[] { map } : Array.Empty<UnityEngine.Object>(),
        Read = _ => UnityEngine.PlayerPrefs.GetFloat(Il2Cpp.DMMapZoom.minimapZoomKey, -1f),
        Write = (_, v) =>
        {
            UnityEngine.PlayerPrefs.SetFloat(Il2Cpp.DMMapZoom.minimapZoomKey, v);
            UnityEngine.PlayerPrefs.Save();
        },
        Saved = true,
        Scene = false,
    };

    static EditTargets() => Declare(MinimapZoom);
#endif

    /// <summary>Declares a saved target by its name (dev code declares its test target).</summary>
    public static void Declare<T>(EditTarget<T> target)
    {
        s_saved[target.Name] = target;
        s_recover[target.Name] = (original, written, force) => EditsFile.RecoverTarget(target, original, written, force);
    }

    public static object? Get(string name) => s_saved.TryGetValue(name, out var target) ? target : null;

    /// <summary>Null while the target's object isn't there; true when restored, false when left as it is.</summary>
    internal static bool? Recover(string name, string original, string written, bool force) =>
        s_recover.TryGetValue(name, out var recover) ? recover(original, written, force) : false;
}
