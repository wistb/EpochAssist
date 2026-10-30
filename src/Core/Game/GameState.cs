using HarmonyLib;
using Il2Cpp;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

internal enum SceneKind { None, Menu, NonCombat, Combat }

/// <summary>A zone change: a new scene, or a monolith echo transition inside one (which loads no scene).</summary>
internal readonly record struct ZoneChange(string Scene, SceneKind Kind, bool Monolith);

/// <summary>The player appeared (a character entered a zone).</summary>
internal readonly record struct PlayerSpawned;

/// <summary>The player's health went from above 0 to 0 while in game.</summary>
internal readonly record struct PlayerDied;

/// <summary>
/// What the game is doing right now, and events on the bus when it changes. Runs first, so features see fresh state.
/// Subscribe through the bus in a feature's Start; the host removes the subscriptions when the feature stops.
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

    /// <summary>The frame of the last scene change (the host skips that second in the budget check); -1 before one.</summary>
    internal static int SceneChangedAt { get; private set; } = -1;

    /// <summary>The active scene's name right now, which may be ahead of Scene until the next update.</summary>
    public static string ActiveSceneName => UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? string.Empty;
    public static SceneKind Kind { get; private set; }

    /// <summary>A zone's kind for session records and macro zone conditions: menu, town, echo, dungeon or other.</summary>
    public static string ZoneKind(string scene, SceneKind kind, bool monolith) => kind switch
    {
        SceneKind.Menu => "menu",
        SceneKind.NonCombat => "town",
        SceneKind.Combat when monolith || scene.StartsWith("M_", StringComparison.Ordinal) => "echo",
        SceneKind.Combat when scene.StartsWith("Dun", StringComparison.Ordinal) => "dungeon",
        _ => "other",
    };

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

    public static bool CanReadCorruption = true;

    /// <summary>The echo's corruption (GF-75: the monolith's current corruption), or null when it can't be read.</summary>
    public static int? EchoCorruption
    {
        get
        {
            if (!CanReadCorruption) return null;
            try
            {
                return Il2CppLE.Gameplay.Monolith.MonolithGameplayManager.CurrentCorruption;
            }
            catch (Exception)
            {
                return null;
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

    public static void Update()
    {
        // The active scene can change with no scene load or unload event (entering an echo from the monolith hub,
        // walking from a rest zone into the hub), so it is checked every frame, by handle to keep it cheap.
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle != s_sceneHandle) OnSceneChanged();

        bool hasPlayer = Player.Exists;
        if (hasPlayer && !s_hadPlayer) Bus.Raise(new PlayerSpawned());
        s_hadPlayer = hasPlayer;

        bool alive = hasPlayer && PlayerAlive;
        if (s_wasAlive && !alive && hasPlayer) Bus.Raise(new PlayerDied());
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
        SceneChangedAt = UnityEngine.Time.frameCount;
        Bus.Raise(new ZoneChange(Scene, Kind, Monolith: false));
    }

    // The scene is kept: a restart of this feature must not report the zone the player is still in as a new one. Its
    // subscribers are on the bus, which a restart of this feature doesn't touch.
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

    private static void OnMonolithTransition() => Bus.Raise(new ZoneChange(Scene, Kind, Monolith: true));

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
