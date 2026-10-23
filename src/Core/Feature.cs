using HarmonyLib;

namespace EpochAssist.Core;

// Unavailable: the game lacks something the feature needs (usually after a game update); it is never started.
internal enum FeatureState { Stopped, Running, Faulted, Unavailable }

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

    /// <summary>Game members the feature relies on beyond its patch targets, checked before it starts.</summary>
    public virtual Requirement[] Requirements => Array.Empty<Requirement>();

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

    protected static Requirement Need<T>(string member, string? group = null) => new(typeof(T), member, group);
    protected static Requirement Need(Type type, string member, string? group = null) => new(type, member, group);

    /// <summary>Patch classes that belong to an optional requirement group; they are skipped when the group is missing.</summary>
    public virtual IReadOnlyDictionary<Type, string> PatchGroups => s_noPatchGroups;
    private static readonly IReadOnlyDictionary<Type, string> s_noPatchGroups = new Dictionary<Type, string>();

    /// <summary>Optional requirement groups the game lacks; the feature runs without them.</summary>
    public HashSet<string> MissingGroups { get; } = new();

    /// <summary>False when an optional requirement group is missing from the game.</summary>
    public bool Has(string group) => !MissingGroups.Contains(group);

    /// <summary>Running, but with patch failures or missing optional groups.</summary>
    public bool Degraded => State == FeatureState.Running && (PatchFailures.Count > 0 || MissingGroups.Count > 0);

    /// <summary>The [HarmonyPatch] classes nested in a type.</summary>
    protected static Type[] NestedPatches(Type owner) =>
        owner.GetNestedTypes(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Where(t => Attribute.IsDefined(t, typeof(HarmonyPatch)))
            .ToArray();

    internal HarmonyLib.Harmony? HarmonyInstance;
    internal readonly Queue<float> RecentErrors = new();
    internal bool SettingsRegistered;
}
