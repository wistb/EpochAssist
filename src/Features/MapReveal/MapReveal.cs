using EpochAssist.Core;
using UnityEngine;
using Feature = EpochAssist.Core.Feature;
using S = EpochAssist.Features.MapRevealSettings;

namespace EpochAssist.Features;

/// <summary>
/// Clears the minimap's fog for the whole zone by widening the game's own reveal stamp, shows the game's own icons that
/// it keeps hidden until the player comes near, zooms the overlay and corner maps, and changes how the map is drawn. It only changes how this client
/// draws the map: explored areas, the character save and the game's saved settings are never written.
/// </summary>
internal static partial class MapReveal
{
    public const string FogGroup = "fog";

    private const float JobSeconds = 0.25f;
    // The stamp's radius must reach every corner of the level from any point in it.
    private const float MinRadius = 1000f;
    private const float RadiusMargin = 256f;
    private const float DriftRewriteSeconds = 1f;

    // Everything one run changes. Made in Start, dropped in Shutdown; the zone part is cleared on a new zone map.
    private sealed class Run
    {
        public Feature Feature = null!;
        // The zone map: its pointer identifies it, a change means a new zone.
        public IntPtr Info;
        // The game reuses a scene's zone map object on every visit, so a new scene counts as a new zone too: by handle,
        // which changes even when the same zone loads again.
        public int SceneHandle = int.MinValue;
        public bool Ready;
        public float Radius;
        // The reveal was applied in this zone map; kept after switching off, so the page says the fog comes back later.
        public bool RevealedHere;
        public Edit<float>? RadiusEdit;
        public float NextDriftRewrite;
        public readonly IconState Icons = new();
        public readonly ZoomState Zoom = new();
        public readonly LookState Look = new();
    }

    private static Run? s_run;

    internal static readonly EditTarget<float> RadiusTarget = new()
    {
        // The name the dev reveal test uses too, so the two share one stack.
        Name = "minimap: reveal radius",
        Find = () => One(Game.Minimap),
        Read = o => o.Cast<Il2CppLE.UI.Minimap.Minimap>().RevealRadius,
        Write = (o, v) => o.Cast<Il2CppLE.UI.Minimap.Minimap>().RevealRadius = v,
        Scene = false,
    };

    private static IEnumerable<UnityEngine.Object> One(UnityEngine.Object? obj)
    {
        if (Game.Alive(obj)) yield return obj!;
    }

    public static void Start(Feature feature)
    {
        var run = new Run { Feature = feature };
        s_run = run;
        Scheduler.Every(feature, "zone map", JobSeconds, () => { if (s_run == run) Tick(run); }, startNow: true);
        Hotkeys.MenuArrowsTaken = ArrowsPan;
        Actions.Register(feature.Id, "mapreveal.toggle", "Map reveal", () => S.Enabled.Value = !S.Enabled.Value, () => S.Enabled.Value);
        // The Look page's Game defaults button asks first; the second press sets them.
        Actions.Register(feature.Id, "mapreveal.look.ask", "Ask to set the map look to the game's", () => ConfirmingGameDefaults = true);
        Actions.Register(feature.Id, "mapreveal.look.game", "Map look to the game's", SetGameDefaults);
        Actions.Register(feature.Id, "mapreveal.look.keep", "Keep the map look", () => ConfirmingGameDefaults = false);
    }

    public static void Shutdown()
    {
        ConfirmingGameDefaults = false;
        if (Hotkeys.MenuArrowsTaken == ArrowsPan) Hotkeys.MenuArrowsTaken = null;
        ZoomOff();
        s_run = null;
    }

    /// <summary>A scene switch can bring a new zone map with no load event; read it now rather than at the next tick.</summary>
    public static void OnSceneChanged()
    {
        if (s_run is { } run) Tick(run);
    }

    // Reads the zone map, then holds the reveal, the icons and the zoom to the settings. Edits are removed with their
    // owner, so switching off only removes them; the area already stamped stays until the next zone.
    private static void Tick(Run run)
    {
        try
        {
            ReadZoneMap(run);
            bool on = S.Enabled.Value && run.Feature.Has(FogGroup);
            HoldReveal(run, on && run.Ready);
            TickIcons(run, on && run.Ready);
            TickZoom(run, on);
            TickLook(run, on);
            TickPlayerIcon(run, on);
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("mapreveal: tick", $"Map reveal: zone map read failed: {e.Message}", 30);
        }
    }

    private static void ReadZoneMap(Run run)
    {
        var map = Game.Minimap;
        var info = Game.Alive(map) ? map!.minimapInformation : null;
        IntPtr pointer = Game.Alive(info) ? info!.Pointer : IntPtr.Zero;
        int handle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
        if (pointer != run.Info || handle != run.SceneHandle)
        {
            run.Info = pointer;
            run.SceneHandle = handle;
            run.RevealedHere = false;
            ClearIcons(run);
        }
        run.Ready = false;
        if (pointer == IntPtr.Zero) return;
        var sdf = info!.LiveSdf;
        var size = info.LevelBounds.size;
        if (!Game.Alive(sdf) || sdf.width <= 0 || size.x <= 0f || size.z <= 0f) return;
        run.Ready = true;
        run.Radius = Mathf.Max(MinRadius, Mathf.Sqrt(size.x * size.x + size.z * size.z) + RadiusMargin);
    }

    private static void HoldReveal(Run run, bool on)
    {
        if (!on)
        {
            run.RadiusEdit?.Remove();
            run.RadiusEdit = null;
            return;
        }
        if (run.RadiusEdit is not { Removed: false } edit)
        {
            run.RadiusEdit = Edits.Apply(run.Feature, RadiusTarget, run.Radius);
        }
        else if (Mathf.Abs(edit.Value - run.Radius) > 0.5f)
        {
            edit.Set(run.Radius);
        }
        else if (edit.Drifted && Clock.Now >= run.NextDriftRewrite)
        {
            // Something else wrote the radius (another mod): ours again, at most once a second, one warning per zone.
            run.NextDriftRewrite = Clock.Now + DriftRewriteSeconds;
            edit.Set(run.Radius);
            Log.ErrorThrottled($"mapreveal: drift {run.Info}", $"Map reveal: something else changed the minimap's reveal radius in {GameState.ActiveSceneName}; writing ours again", 600);
        }
        if (!run.RevealedHere)
        {
            run.RevealedHere = true;
            Log.Info($"Map reveal: {GameState.ActiveSceneName} revealed (radius {run.Radius:F0})");
        }
    }

    /// <summary>The status words for the page and the HUD.</summary>
    public static FeatureStatus StatusOf(Feature feature)
    {
        var run = s_run;
        if (run == null) return FeatureStatus.Off(string.Empty);
        if (!feature.Has(FogGroup)) return FeatureStatus.Off("Unavailable: the game changed the minimap");
        if (!S.Enabled.Value) return FeatureStatus.Off(run.RevealedHere ? "Off · fog returns on the next zone" : "Off");
        if (!run.Ready) return new FeatureStatus(StatusState.Waiting, "Waiting for the zone map", null);
        return new FeatureStatus(StatusState.Active, $"Applied · {ShownIcons(run)} icons", null);
    }

    /// <summary>The page's status line.</summary>
    public static (string, StatusState?) StatusText()
    {
        if (s_run?.Feature is not { } feature) return ("Off", StatusState.Off);
        var status = StatusOf(feature);
        return (status.Detail, status.State);
    }

    /// <summary>Lines for the dev bridge: the zone map, the reveal, the icons per kind and the zoom.</summary>
    public static IEnumerable<string> Describe()
    {
        if (s_run is not { } run)
        {
            yield return "not running";
            yield break;
        }
        yield return $"status {StatusText().Item1}, zone map {(run.Info == IntPtr.Zero ? "none" : run.Info.ToString("X"))}, ready {run.Ready}, radius {run.Radius:F0}, revealed here {run.RevealedHere}";
        yield return $"radius edit {(run.RadiusEdit is { Removed: false } r ? r.Describe() : "none")}";
        foreach (string line in DescribeIcons(run)) yield return line;
        foreach (string line in DescribeZoom(run)) yield return line;
        foreach (string line in DescribeLook(run)) yield return line;
    }
}
