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

    /// <summary>Ids of features this one can't act safely without; the host runs it after them, wherever it is
    /// registered. The host starts it only while all of them run, and stops it before any of them stops.</summary>
    public virtual string[] Needs => Array.Empty<string>();

    /// <summary>Ids of features this one runs after each frame, with no effect on start, stop or blocking (use Needs for
    /// that). Ids that aren't registered are ignored.</summary>
    public virtual string[] RunsAfter => Array.Empty<string>();

    /// <summary>Parts of the models (player, world, data) the feature reads, checked before it starts like requirements:
    /// a missing required part makes it unavailable, a missing part with a group turns that group off. Each part's model
    /// must be in Needs.</summary>
    public virtual PartNeed[] Parts => Array.Empty<PartNeed>();

    /// <summary>The feature's place in the menu, or null for none. Read in every state, so a stopped feature keeps it.</summary>
    public virtual MenuEntry? Menu => null;

    /// <summary>The player can turn the whole feature off: it has an entry outside the Settings tab.</summary>
    public bool CanTurnOff => Menu != null && Menu.Category != MenuCategory.Settings;

    /// <summary>A non-null name puts the feature on the HUD pill, showing its Status.</summary>
    public virtual string? StatusName => null;

    /// <summary>What the feature is doing, for the HUD, menu and bridge.</summary>
    public virtual FeatureStatus Status =>
        State == FeatureState.Running ? new FeatureStatus(StatusState.Active, string.Empty, null) : FeatureStatus.Off(LastError ?? string.Empty);

    /// <summary>What the host tracks about this feature; it alone writes it.</summary>
    internal FeatureRecord Record { get; } = new();

    public FeatureState State => Record.State;
    public string? LastError => Record.LastError;
    /// <summary>When LastError was set by an error (local time); null when it isn't one.</summary>
    public DateTime? LastErrorAt => Record.LastErrorAt;
    /// <summary>The need that kept or took this feature out of Running, or null.</summary>
    public string? BlockedBy => Record.BlockedBy;
    public List<string> PatchFailures => Record.PatchFailures;

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
    public bool OverBudget => Record.OverBudget;
    public DateTime? OverSince => Record.OverSince;

    /// <summary>Extra lines for the feature's Diagnostics details (game events: producers the mode doesn't support).</summary>
    public virtual IEnumerable<string> DiagnosticNotes => Array.Empty<string>();

    /// <summary>Optional requirement groups the game lacks; the feature runs without them.</summary>
    public HashSet<string> MissingGroups => Record.MissingGroups;

    /// <summary>False when an optional requirement group is missing from the game.</summary>
    public bool Has(string group) => !MissingGroups.Contains(group);

    /// <summary>Running, but with patch failures or missing optional groups.</summary>
    public bool Degraded => State == FeatureState.Running && (PatchFailures.Count > 0 || MissingGroups.Count > 0);

    /// <summary>The [HarmonyPatch] classes nested in a type.</summary>
    protected static Type[] NestedPatches(Type owner) =>
        owner.GetNestedTypes(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Where(t => Attribute.IsDefined(t, typeof(HarmonyPatch)))
            .ToArray();
}
