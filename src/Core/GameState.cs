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
    private static int s_sceneHandle;

    /// <summary>Off when the game lacks the chat focus flag (set by GameStateFeature).</summary>
    public static bool CanReadChat = true;

    /// <summary>Off when the game lacks the loading screen flag (set by GameStateFeature).</summary>
    public static bool CanReadLoading = true;

    /// <summary>Off when the game lacks the pause menu flag (set by GameStateFeature).</summary>
    public static bool CanReadPauseMenu = true;

    /// <summary>One of the mod's own text fields has focus (set by the menu).</summary>
    public static bool UiTyping;

    public static string Scene { get; private set; } = string.Empty;

    /// <summary>The active scene's name right now, which may be ahead of Scene until the next update.</summary>
    public static string ActiveSceneName => UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? string.Empty;
    public static SceneKind Kind { get; private set; }

    /// <summary>A character is loaded and the scene is a playable zone.</summary>
    public static bool InGame => Kind is SceneKind.NonCombat or SceneKind.Combat && Player.Exists;

    /// <summary>Null until the player is found.</summary>
    public static bool? Online => Player.Online;

    /// <summary>The player's health and maximum, or null without a player.</summary>
    public static (float Current, float Max)? Health
    {
        get
        {
            try
            {
                PlayerHealth? health = Player.Health;
                return health != null ? (health.currentHealth, health.maxHealth) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

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

    /// <summary>The game's loading screen is up; false when it can't be read.</summary>
    public static bool Loading
    {
        get
        {
            if (!CanReadLoading) return false;
            try
            {
                return Il2CppLE.UI.LoadingScreen.IsVisible;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>The game's own pause menu (Esc) is open; false when it can't be read.</summary>
    public static bool GamePaused
    {
        get
        {
            if (!CanReadPauseMenu) return false;
            try
            {
                return Game.UI?.menuOpen ?? false;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// A text box has focus, so keys belong to it. Tested in game: the game's input-field flag covers fields like the
    /// stash search but not the chat box, which has its own flag, and neither sees the mod's own fields.
    /// </summary>
    public static bool Typing => UiTyping || GameFieldActive || ChatFocused;

    /// <summary>A game text field or the chat box has focus (not one of the mod's own fields).</summary>
    public static bool GameTyping => GameFieldActive || ChatFocused;

    private static bool GameFieldActive
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

    private static bool ChatFocused
    {
        get
        {
            if (!CanReadChat) return false;
            try
            {
                return Game.UI?.IsChatInputFocused ?? false;
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
        // The active scene can change with no scene load or unload event (entering an echo from the monolith hub,
        // walking from a rest zone into the hub), so it is checked every frame, by handle to keep it cheap.
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle != s_sceneHandle) OnSceneChanged();

        bool hasPlayer = Player.Exists;
        if (hasPlayer && !s_hadPlayer) Raise(PlayerSpawned, nameof(PlayerSpawned));
        s_hadPlayer = hasPlayer;

        bool alive = hasPlayer && PlayerAlive;
        if (s_wasAlive && !alive && hasPlayer) Raise(PlayerDied, nameof(PlayerDied));
        s_wasAlive = alive;
    }

    public static void OnSceneChanged()
    {
        var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        s_sceneHandle = active.handle;
        string scene = active.name ?? string.Empty;
        if (string.Equals(scene, Scene, StringComparison.Ordinal)) return;

        Player.Reset();
        Scene = scene;
        Kind = Classify(scene);
        s_hadPlayer = false;
        s_wasAlive = false;
        RaiseZone(new ZoneChange(Scene, Kind, Monolith: false));
    }

    // Subscribers and the scene are kept: features unsubscribe in their own Shutdown, and a restart of this feature must
    // neither cut them off nor report the zone the player is still in as a new one.
    public static void Reset()
    {
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
    // Chat focus is optional: without it, typing in chat is missed but everything else keeps working.
    private const string ChatTyping = "chat typing";
    // Optional too: without them automation doesn't pause for that one condition.
    private const string LoadingGroup = "loading";
    private const string PauseMenu = "pause menu";

    public override Type[] PatchClasses => NestedPatches(typeof(GameState));
    public override Requirement[] Requirements => new[]
    {
        Need<SceneList>(nameof(SceneList.IsNonCombatZone)),
        Need(typeof(PlayerFinder), nameof(PlayerFinder.getPlayerActor)),
        Need(typeof(PlayerFinder), nameof(PlayerFinder.getLocalTreeData)),
        Need<PlayerHealth>(nameof(PlayerHealth.currentHealth)),
        Need<EpochInputManager>(nameof(EpochInputManager.IsInputFieldActive)),
        Need<UIBase>(nameof(UIBase.instance), ChatTyping),
        Need<UIBase>(nameof(UIBase.IsChatInputFocused), ChatTyping),
        Need(typeof(Il2CppLE.UI.LoadingScreen), nameof(Il2CppLE.UI.LoadingScreen.IsVisible), LoadingGroup),
        Need<UIBase>(nameof(UIBase.instance), PauseMenu),
        Need<UIBase>(nameof(UIBase.menuOpen), PauseMenu),
    };

    public override void Start()
    {
        GameState.CanReadChat = Has(ChatTyping);
        GameState.CanReadLoading = Has(LoadingGroup);
        GameState.CanReadPauseMenu = Has(PauseMenu);
        GameState.OnSceneChanged();
    }

    public override void Update() => GameState.Update();
    public override void OnSceneChanged() => GameState.OnSceneChanged();
    public override void Shutdown() => GameState.Reset();
}
