using EpochAssist.Core;
using HarmonyLib;
using Il2CppLE.UI.Minimap;
using UnityEngine;
using S = EpochAssist.Features.MapRevealSettings;

namespace EpochAssist.Features;

// Overlay and corner minimap zoom, and the arrow pan's speed. The game rebuilds the map's scale every update (from its saved overlay zoom while
// the overlay is open, from the corner minimap's own zoom while it is closed), so a postfix writes ours after it. The
// game's zoom setter saves the setting, so it is never called; nothing here is saved.
internal static partial class MapReveal
{
    public const string ZoomGroup = "map: zoom";

    private sealed class ZoomState
    {
        public Edit<float>? IconRange;
        // The game's icon range before ours, read once while no edit holds it.
        public float IconRangeOriginal;
    }

    // Read by the postfix every update: the scale for each map, 0 for the game's own.
    private static float s_overlayScale, s_cornerScale;
    // Read by the pan prefix: the arrow pan's speed, 1 for the game's own.
    private static float s_panSpeed = 1f;
    // The material holds a scale we wrote. The game writes its own scale into the field every update but into the
    // material only when its zoom changes, so ours would stay drawn after we stop.
    private static bool s_materialOurs;

    // The range past which the game draws an icon clamped with an arrow, in world units: a wider map needs a wider range.
    internal static readonly EditTarget<float> IconRangeTarget = new()
    {
        Name = "map: icon range",
        Find = () => One(Il2Cpp.DMMapGlobalConfigs.instance),
        Read = o => o.Cast<Il2Cpp.DMMapGlobalConfigs>().iconDistanceThreshold,
        Write = (o, v) => o.Cast<Il2Cpp.DMMapGlobalConfigs>().iconDistanceThreshold = v,
        Scene = false,
    };

    private static void TickZoom(Run run, bool on)
    {
        bool available = run.Feature.Has(ZoomGroup);
        s_overlayScale = on && available && S.OverlayZoom.Value ? S.OverlayScale.Value : 0f;
        s_cornerScale = on && available && S.CornerZoom.Value ? S.CornerScale.Value : 0f;
        MatchPanSpeed();
        s_panSpeed = on && available ? S.PanSpeed.Value : 1f;
        bool patch = s_overlayScale > 0f || s_cornerScale > 0f || Mathf.Abs(s_panSpeed - 1f) > 1e-3f;
        // Unpatched, the postfix can't put the game's scale back, so it is done here first.
        if (!patch && s_materialOurs && Game.Minimap is { } map && Game.Alive(map)) PushMaterial(map);
        FeatureHost.SetPatchGroup(run.Feature, ZoomGroup, patch);
        HoldIconRange(run, s_overlayScale > 0f && OverlayOpen());
    }

    private static void HoldIconRange(Run run, bool on)
    {
        var zoom = run.Zoom;
        if (!on)
        {
            zoom.IconRange?.Remove();
            zoom.IconRange = null;
            return;
        }
        var configs = Il2Cpp.DMMapGlobalConfigs.instance;
        if (!Game.Alive(configs)) return;
        if (zoom.IconRange is not { Removed: false }) zoom.IconRangeOriginal = configs!.iconDistanceThreshold;
        float value = zoom.IconRangeOriginal * s_overlayScale / GameOverlayScale();
        if (zoom.IconRange is not { Removed: false } edit) zoom.IconRange = Edits.Apply(run.Feature, IconRangeTarget, value);
        else if (Mathf.Abs(edit.Value - value) > 0.5f) edit.Set(value);
    }

    // With Match zoom on, the speed that keeps the arrows' pace on screen at our overlay scale (ours over the game's),
    // written into the locked slider so it shows the value in use.
    private static void MatchPanSpeed()
    {
        if (!S.PanMatchZoom.Value) return;
        float speed = S.PanSpeed.Snap(S.OverlayZoom.Value ? S.OverlayScale.Value / GameOverlayScale() : 1f);
        if (Mathf.Abs(S.PanSpeed.Value - speed) > 1e-3f) S.PanSpeed.Value = speed;
    }

    // The game's own overlay scale: its saved zoom setting (120 to 300). The game's settings throw while they aren't set
    // up (the title screen, a load); the last value read, else the game's default, stands in.
    private static float s_gameOverlayScale = 120f;

    private static float GameOverlayScale()
    {
        try
        {
            float saved = Il2CppLE.Settings.SettingsManager.Gameplay?.OverlayMapZoom?.GetValue() ?? 0f;
            if (saved > 0f) s_gameOverlayScale = saved;
        }
        catch (Exception)
        {
            // Not set up yet: the last value stands.
        }
        return s_gameOverlayScale;
    }

    // The game's own overlay pan, fed the way a held arrow key feeds it (the call scales by the frame's time): about 6
    // matches the game's speed.
    private const float PanInput = 6f;

    /// <summary>While the menu shows a Map page with the overlay open, the arrow keys pan the overlay as they do without
    /// the menu (the menu's lock keeps the game from reading them) and the menu leaves them alone.</summary>
    public static bool ArrowsPan() =>
        s_run != null && S.Enabled.Value && Ui.Menu.Visible && Ui.Menu.Current.StartsWith("combat/map", StringComparison.Ordinal) && OverlayOpen();

    /// <summary>Each frame: the held arrows into the game's pan.</summary>
    public static void PanFrame()
    {
        if (!ArrowsPan()) return;
        float x = (InputState.Key(KeyCode.RightArrow) ? 1f : 0f) - (InputState.Key(KeyCode.LeftArrow) ? 1f : 0f);
        float y = (InputState.Key(KeyCode.UpArrow) ? 1f : 0f) - (InputState.Key(KeyCode.DownArrow) ? 1f : 0f);
        if (x == 0f && y == 0f) return;
        var ui = Il2Cpp.UIBase.instance;
        if (Game.Alive(ui)) ui!.MoveOverlayMap(new Vector2(x, y) * PanInput);
    }

    private static bool OverlayOpen()
    {
        var ui = Il2Cpp.UIBase.instance;
        return Game.Alive(ui) && ui!.overlayMapOpen;
    }

    // Stop and fault unpatch the postfix; the scales go to zero too, so a patch left by any path writes nothing, and the
    // material gets the game's own scale back.
    private static void ZoomOff()
    {
        s_overlayScale = 0f;
        s_cornerScale = 0f;
        s_panSpeed = 1f;
        try
        {
            if (s_materialOurs && Game.Minimap is { } map && Game.Alive(map)) PushMaterial(map);
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("mapreveal: zoom off", $"Map reveal: could not put the map's scale back: {e.Message}", 30);
        }
    }

    private static bool MaterialDiffers(Minimap map)
    {
        var material = map.Material;
        if (material == null) return false;
        var m = material.GetVector(Minimap.shaderMapParameters);
        var p = map.mapParameters;
        return Mathf.Abs(m.x - p.x) > 0.01f || Mathf.Abs(m.y - p.y) > 0.001f || Mathf.Abs(m.z - p.z) > 0.01f;
    }

    // The field's parameters (the game's own, or ours) into the material the map draws with.
    private static void PushMaterial(Minimap map)
    {
        var material = map.Material;
        var p = map.mapParameters;
        if (material != null) material.SetVector(Minimap.shaderMapParameters, new Vector4(p.x, p.y, p.z, p.w));
        s_materialOurs = false;
    }

    private static IEnumerable<string> DescribeZoom(Run run)
    {
        var map = Game.Minimap;
        string scale = Game.Alive(map) ? map!.mapParameters.z.ToString("F0") : "?";
        yield return $"zoom: pan {s_panSpeed:F2}x, overlay {(s_overlayScale > 0f ? s_overlayScale.ToString("F0") : "game's")}, corner {(s_cornerScale > 0f ? s_cornerScale.ToString("F0") : "game's")}, "
            + $"map scale now {scale}, overlay open {OverlayOpen()}, patched {run.Feature.Record.DemandGroupsOn.Contains(ZoomGroup)}, "
            + $"icon range {(run.Zoom.IconRange is { Removed: false } r ? r.Describe() : "game's")}";
    }

    // The arrow pan, the game's and the menu's: its input scaled by the speed setting.
    [HarmonyPatch(typeof(Il2Cpp.UIBase), nameof(Il2Cpp.UIBase.MoveOverlayMap))]
    internal static class PanPatch
    {
        private static void Prefix(ref Vector2 input)
        {
            if (s_panSpeed != 1f) input *= s_panSpeed;
        }
    }

    // The scale for the map shown, after the game wrote its own, in the field and in the material the map draws with.
    [HarmonyPatch(typeof(Minimap), nameof(Minimap.Update))]
    internal static class ZoomPatch
    {
        private static void Postfix(Minimap __instance) => ApplyScale(__instance);
    }

    // The game pushes its own zoom into the map the moment the overlay opens or closes (and on a zoom change), before
    // the map's next update; ours goes back at once, or the frame between draws the map at the game's scale.
    [HarmonyPatch(typeof(Minimap), nameof(Minimap.UpdateOverlayMapZoom))]
    internal static class ZoomPushPatch
    {
        private static void Postfix(Minimap __instance) => ApplyIfOurs(__instance);
    }

    [HarmonyPatch(typeof(Il2Cpp.UIBase), nameof(Il2Cpp.UIBase.OpenOverlayMap))]
    internal static class OverlayOpenPatch
    {
        private static void Postfix() => AfterSwitch();
    }

    // The overlay key opens the overlay by its own path, not through OpenOverlayMap.
    [HarmonyPatch(typeof(Il2Cpp.UIBase), nameof(Il2Cpp.UIBase.OverlayMapKeyDown))]
    internal static class OverlayKeyPatch
    {
        private static void Postfix() => AfterSwitch();
    }

    [HarmonyPatch(typeof(Il2Cpp.UIBase), nameof(Il2Cpp.UIBase.CloseOverlayMapOrRecenter))]
    internal static class OverlayClosePatch
    {
        private static void Postfix() => AfterSwitch();
    }

    private static void AfterSwitch()
    {
        if (Game.Minimap is { } map && Game.Alive(map)) ApplyIfOurs(map);
        // The icon range follows at once too, not at the next job tick.
        if (s_run is { } run) HoldIconRange(run, s_overlayScale > 0f && OverlayOpen());
    }

    // Outside the map's update, only to a map we scale: for the game's own, the field may still hold the other map's
    // scale (or the old zone's) until its next update, which then puts the right one into the material.
    private static void ApplyIfOurs(Minimap map)
    {
        if ((OverlayOpen() ? s_overlayScale : s_cornerScale) > 0f) ApplyScale(map);
    }

    private static void ApplyScale(Minimap map)
    {
        float scale = s_overlayScale <= 0f && s_cornerScale <= 0f ? 0f : OverlayOpen() ? s_overlayScale : s_cornerScale;
        if (scale <= 0f)
        {
            // The game's own values are in the field: into the material whenever it differs (a zone load leaves the
            // old zone's size in the material until the game pushes again, which can draw the map off its place).
            if (s_materialOurs || MaterialDiffers(map)) PushMaterial(map);
            return;
        }
        var parameters = map.mapParameters;
        parameters.z = scale;
        map.mapParameters = parameters;
        PushMaterial(map);
        s_materialOurs = true;
    }
}
