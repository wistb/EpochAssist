using HarmonyLib;

namespace EpochAssist.Core;

// Unavailable: the game lacks something the feature needs (usually after a game update); it is never started.
internal enum FeatureState { Stopped, Running, Faulted, Unavailable }

// Where a feature draws; the host draws layers in this order, so the menu ends up on top.
internal enum GuiLayer { None, World, Hud, Menu }

/// <summary>A feature's page in the menu: sidebar title, place (lower first, ties in registration order) and its body.</summary>
internal sealed record FeaturePage(string Title, int Order, Action Draw)
{
    /// <summary>The feature's master switch, shown in the page header; it must be a quick toggle (it runs that action).</summary>
    public Toggle? Switch { get; init; }
}

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

    /// <summary>Ids of features this one can't act safely without; they must be registered first. The host starts it
    /// only while all of them run, and stops it before any of them stops.</summary>
    public virtual string[] Needs => Array.Empty<string>();

    /// <summary>Parts of the models (player, world, data) the feature reads, checked before it starts like requirements:
    /// a missing required part makes it unavailable, a missing part with a group turns that group off. Each part's model
    /// must be in Needs.</summary>
    public virtual PartNeed[] Parts => Array.Empty<PartNeed>();

    /// <summary>The feature's menu page, or null for none. The body is drawn only while the feature is Running.</summary>
    public virtual FeaturePage? Page => null;

    /// <summary>A non-null name puts the feature on the HUD pill, showing its Status.</summary>
    public virtual string? StatusName => null;

    /// <summary>What the feature is doing, for the HUD, menu and bridge.</summary>
    public virtual FeatureStatus Status =>
        State == FeatureState.Running ? new FeatureStatus(StatusState.Active, string.Empty, null) : FeatureStatus.Off(LastError ?? string.Empty);

    public FeatureState State { get; internal set; }
    public string? LastError { get; internal set; }
    /// <summary>When LastError was set by an error (local time); null when it isn't one.</summary>
    public DateTime? LastErrorAt { get; internal set; }
    /// <summary>The need that kept or took this feature out of Running, or null.</summary>
    public string? BlockedBy { get; internal set; }
    public List<string> PatchFailures { get; } = new();

    /// <summary>Registers settings. Called once per load, before the first start.</summary>
    public virtual void RegisterSettings() { }

    public virtual void Start() { }
    public virtual void Update() { }
    /// <summary>A second pass each frame, after every feature's Update.</summary>
    public virtual void LateUpdate() { }
    public virtual void OnGUI() { }
    public virtual void OnSceneChanged() { }
    public virtual void Shutdown() { }

    protected static Requirement Need<T>(string member, string? group = null) => new(typeof(T), member, group);
    protected static Requirement Need(Type type, string member, string? group = null) => new(type, member, group);
    protected static PartNeed Part(string model, string part, string? group = null) => new(model, part, group);

    /// <summary>Patch classes that belong to an optional requirement group; they are skipped when the group is missing.</summary>
    public virtual IReadOnlyDictionary<Type, string> PatchGroups => s_noPatchGroups;
    private static readonly IReadOnlyDictionary<Type, string> s_noPatchGroups = new Dictionary<Type, string>();

    /// <summary>Patch groups whose classes aren't applied at start; the feature turns them on and off with
    /// FeatureHost.SetPatchGroup while it needs them.</summary>
    public virtual string[] DemandGroups => Array.Empty<string>();

    /// <summary>Frame time the feature should stay under, per second on average; more only when it needs it.</summary>
    public virtual double BudgetMs => 0.3;

    /// <summary>Its average was over BudgetMs for 3 counted seconds in a row (cleared after 3 under), and since when.</summary>
    public bool OverBudget { get; internal set; }
    public DateTime? OverSince { get; internal set; }
    internal int OverStreak, UnderStreak;

    /// <summary>Extra lines for the feature's Diagnostics details (game events: producers the mode doesn't support).</summary>
    public virtual IEnumerable<string> DiagnosticNotes => Array.Empty<string>();

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
    // The DemandGroups the host has patched now.
    internal readonly HashSet<string> DemandGroupsOn = new();
    // The scheduler's jobs owned by this feature; the host drops them when it leaves Running.
    internal readonly List<Job> Jobs = new();
    // The registry's edits owned by this feature; the host removes them when it leaves Running.
    internal readonly List<Edit> Edits = new();
    internal readonly FrameTimes Frames = new();
    internal readonly Queue<float> RecentErrors = new();
    internal bool SettingsRegistered;
}
