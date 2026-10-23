using HarmonyLib;
using Il2Cpp;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

internal enum SceneKind { None, Menu, NonCombat, Combat }

/// <summary>A zone change: a new scene, or a monolith echo transition inside one (which loads no scene).</summary>
internal readonly record struct ZoneChange(string Scene, SceneKind Kind, bool Monolith);

/// <summary>
/// What the game is doing right now, and events when it changes. Runs first, so features see fresh state.
/// Subscribe in a feature's Start and unsubscribe in its Shutdown.
/// </summary>
internal static class GameState
{
    private static readonly string[] s_menuScenes = { "ClientSplash", "PersistentUI", "Login", "CharacterSelectScene" };

    private static bool s_hadPlayer;
    private static bool s_wasAlive;

    public static string Scene { get; private set; } = string.Empty;
    public static SceneKind Kind { get; private set; }

    /// <summary>A character is loaded and the scene is a playable zone.</summary>
    public static bool InGame => Kind is SceneKind.NonCombat or SceneKind.Combat && Player.Exists;

    /// <summary>Null until the player is found.</summary>
    public static bool? Online => Player.Online;

    public static bool PlayerAlive
    {
        get
        {
            try
            {
                PlayerHealth? health = Player.Health;
                return health != null && health.currentHealth > 0f;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>A chat or search box has focus, so keys belong to it.</summary>
    public static bool Typing
    {
        get
        {
            try
            {
                return Game.Input?.IsInputFieldActive ?? false;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public static event Action<ZoneChange>? ZoneChanged;
    public static event Action? PlayerSpawned;
    public static event Action? PlayerDied;

    public static void Update()
    {
        bool hasPlayer = Player.Exists;
        if (hasPlayer && !s_hadPlayer) Raise(PlayerSpawned, nameof(PlayerSpawned));
        s_hadPlayer = hasPlayer;

        bool alive = hasPlayer && PlayerAlive;
        if (s_wasAlive && !alive && hasPlayer) Raise(PlayerDied, nameof(PlayerDied));
        s_wasAlive = alive;
    }

    public static void OnSceneChanged()
    {
        string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? string.Empty;
        if (string.Equals(scene, Scene, StringComparison.Ordinal)) return;

        Player.Reset();
        Scene = scene;
        Kind = Classify(scene);
        s_hadPlayer = false;
        s_wasAlive = false;
        RaiseZone(new ZoneChange(Scene, Kind, Monolith: false));
    }

    public static void Reset()
    {
        ZoneChanged = null;
        PlayerSpawned = null;
        PlayerDied = null;
        Scene = string.Empty;
        Kind = SceneKind.None;
        s_hadPlayer = s_wasAlive = false;
    }

    private static SceneKind Classify(string scene)
    {
        if (scene.Length == 0) return SceneKind.None;
        if (Array.IndexOf(s_menuScenes, scene) >= 0) return SceneKind.Menu;
        try
        {
            return SceneList.IsNonCombatZone(scene) ? SceneKind.NonCombat : SceneKind.Combat;
        }
        catch (Exception)
        {
            return SceneKind.Combat;
        }
    }

    // One subscriber's failure must not stop the others.
    private static void RaiseZone(ZoneChange change)
    {
        if (ZoneChanged == null) return;
        foreach (Action<ZoneChange> handler in ZoneChanged.GetInvocationList())
        {
            try
            {
                handler(change);
            }
            catch (Exception e)
            {
                Log.Error($"ZoneChanged handler failed: {e}");
            }
        }
    }

    private static void Raise(Action? handlers, string name)
    {
        if (handlers == null) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try
            {
                handler();
            }
            catch (Exception e)
            {
                Log.Error($"{name} handler failed: {e}");
            }
        }
    }

    private static void OnMonolithTransition() => RaiseZone(new ZoneChange(Scene, Kind, Monolith: true));

    // Monolith echo transitions don't always surface as a scene change.
    [HarmonyPatch(typeof(MonolithZoneManager), nameof(MonolithZoneManager.initialise))]
    private static class MonolithZoneInitialise_Patch
    {
        private static void Postfix() => OnMonolithTransition();
    }

    [HarmonyPatch(typeof(MonolithRunsManager), nameof(MonolithRunsManager.onRestZoneEnteredAfterEchoCompleted))]
    private static class MonolithRestZoneEntered_Patch
    {
        private static void Postfix() => OnMonolithTransition();
    }
}

internal sealed class GameStateFeature : Feature
{
    public override string Id => "game";
    public override string Name => "Game state";
    public override Type[] PatchClasses => NestedPatches(typeof(GameState));
    public override Requirement[] Requirements => new[]
    {
        Need<SceneList>(nameof(SceneList.IsNonCombatZone)),
        Need(typeof(PlayerFinder), nameof(PlayerFinder.getPlayerActor)),
        Need(typeof(PlayerFinder), nameof(PlayerFinder.getLocalTreeData)),
        Need<PlayerHealth>(nameof(PlayerHealth.currentHealth)),
        Need<EpochInputManager>(nameof(EpochInputManager.IsInputFieldActive)),
    };

    public override void Start() => GameState.OnSceneChanged();
    public override void Update() => GameState.Update();
    public override void OnSceneChanged() => GameState.OnSceneChanged();
    public override void Shutdown() => GameState.Reset();
}
