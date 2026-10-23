using HarmonyLib;

namespace EpochAssist.Core;

internal enum FeatureState { Stopped, Running, Faulted }

// Where a feature draws; the host draws layers in this order, so the menu ends up on top.
internal enum GuiLayer { None, World, Hud, Menu }

/// <summary>
/// One unit of the mod run by <see cref="FeatureHost"/>. Override only the hooks you need.
/// Shutdown must undo everything the feature did to the game, so a reload or a fault leaves nothing behind.
/// </summary>
internal abstract class Feature
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public virtual GuiLayer Layer => GuiLayer.None;

    /// <summary>Harmony patch classes owned by this feature, applied on start and removed on stop.</summary>
    public virtual Type[] PatchClasses => Array.Empty<Type>();

    public FeatureState State { get; internal set; }
    public string? LastError { get; internal set; }
    public List<string> PatchFailures { get; } = new();

    /// <summary>Registers settings. Called once per load, before the first start.</summary>
    public virtual void RegisterSettings() { }

    public virtual void Start() { }
    public virtual void Update() { }
    public virtual void OnGUI() { }
    public virtual void OnSceneChanged() { }
    public virtual void Shutdown() { }

    /// <summary>The [HarmonyPatch] classes nested in a type.</summary>
    protected static Type[] NestedPatches(Type owner) =>
        owner.GetNestedTypes(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Where(t => Attribute.IsDefined(t, typeof(HarmonyPatch)))
            .ToArray();

    internal HarmonyLib.Harmony? HarmonyInstance;
    internal readonly Queue<float> RecentErrors = new();
    internal bool SettingsRegistered;
}
