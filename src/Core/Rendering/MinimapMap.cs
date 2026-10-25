using UnityEngine;

namespace EpochAssist.Core;

/// <summary>
/// World positions on the game's minimap and back, worked out once a frame while asked for: from the minimap's own
/// fields (its map rect is centred on the player and shows 2 × zoom metres across, turned with the camera's yaw), or,
/// when that is more than 2 pixels off the game's own icons or the fields are missing, fitted to those icons once a
/// second. Nothing while the minimap is missing, inactive or hidden. A read only; never an edit.
/// </summary>
internal static class MinimapMap
{
    private const float FitSeconds = 1f;
    private const float MaxErrorPixels = 2f;

    private static int s_frame = -1;
    private static bool s_ready;
    private static Rect s_rect;
    private static Vector2 s_centre;
    private static Vector3 s_player;
    // screen offset (bottom-left origin) = (A00·dx + A01·dz, A10·dx + A11·dz) for d = world − player.
    private static float s_a00, s_a01, s_a10, s_a11;
    private static bool s_fitted;
    private static float s_fitA00, s_fitA01, s_fitA10, s_fitA11;
    private static Cooldown s_nextFit;

    /// <summary>The minimap is there and visible, and the mapping is known this frame.</summary>
    public static bool Ready
    {
        get
        {
            Compute();
            return s_ready;
        }
    }

    /// <summary>The minimap's map area on screen, top-left origin.</summary>
    public static Rect ScreenRect
    {
        get
        {
            Compute();
            return s_rect;
        }
    }

    /// <summary>"fields" or "fitted".</summary>
    public static string Source => s_fitted ? "fitted" : "fields";

    /// <summary>Pixels per metre along the map's two axes and the map's rotation in degrees, for the dev command.</summary>
    public static (float X, float Y, float Rotation) Scale
    {
        get
        {
            Compute();
            return (new Vector2(s_a00, s_a10).magnitude, new Vector2(s_a01, s_a11).magnitude, Mathf.Atan2(s_a10, s_a00) * Mathf.Rad2Deg);
        }
    }

    /// <summary>Where a world point is on the minimap (top-left origin), or null outside its area or without one.</summary>
    public static Vector2? ToScreen(Vector3 world)
    {
        if (!Ready) return null;
        float dx = world.x - s_player.x, dz = world.z - s_player.z;
        var p = new Vector2(s_centre.x + s_a00 * dx + s_a01 * dz, Screen.height - (s_centre.y + s_a10 * dx + s_a11 * dz));
        return s_rect.Contains(p) ? p : null;
    }

    /// <summary>The world point under a minimap point (top-left origin); y is the player's.</summary>
    public static Vector3? ToWorld(Vector2 screen)
    {
        if (!Ready) return null;
        float det = s_a00 * s_a11 - s_a01 * s_a10;
        if (Mathf.Abs(det) < 1e-6f) return null;
        float ux = screen.x - s_centre.x, uy = Screen.height - screen.y - s_centre.y;
        return new Vector3(s_player.x + (s_a11 * ux - s_a01 * uy) / det, s_player.y, s_player.z + (-s_a10 * ux + s_a00 * uy) / det);
    }

    /// <summary>The game's own icons with their screen point and ours (top-left origin), for the dev check.</summary>
    public static IEnumerable<(string Name, Vector2 Game, Vector2? Ours)> Icons()
    {
        if (!Ready) yield break;
        foreach (var (world, ui, name) in GameIcons())
        {
            float dx = world.x - s_player.x, dz = world.z - s_player.z;
            var ours = new Vector2(s_centre.x + s_a00 * dx + s_a01 * dz, Screen.height - (s_centre.y + s_a10 * dx + s_a11 * dz));
            yield return (name, new Vector2(ui.x, Screen.height - ui.y), ours);
        }
    }

    internal static void Reset()
    {
        s_frame = -1;
        s_ready = false;
        s_fitted = false;
        s_nextFit.Clear();
    }

    private static void Compute()
    {
        int frame = Time.frameCount;
        if (frame == s_frame) return;
        s_frame = frame;
        s_ready = false;
        if (FeatureHost.Find("visuals") is not { State: FeatureState.Running } feature || !feature.Has(VisualsFeature.Minimap)) return;
        try
        {
            ComputeNow();
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("visuals:minimap", $"Minimap mapping failed: {e.Message}", 30);
        }
    }

    private static void ComputeNow()
    {
        var map = Game.Minimap;
        if (!Game.Alive(map) || !map!.gameObject.activeInHierarchy || Player.Transform is not Transform player) return;
        var canvas = map.GetComponentInParent<Canvas>();
        if (canvas == null || !canvas.isActiveAndEnabled || canvas.rootCanvas == null || !canvas.rootCanvas.enabled) return;
        var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
        map.transform.Cast<RectTransform>().GetWorldCorners(corners);
        // An overlay canvas's world space is the screen in pixels (bottom-left origin).
        float left = corners[0].x, bottom = corners[0].y, right = corners[2].x, top = corners[2].y;
        if (right - left < 4f || top - bottom < 4f) return;
        s_rect = new Rect(left, Screen.height - top, right - left, top - bottom);
        s_centre = new Vector2((left + right) / 2f, (bottom + top) / 2f);
        s_player = player.position;
        float zoom = map.mapParameters.z;
        var camera = Camera.main;
        if (zoom > 0f && camera != null)
        {
            float yaw = camera.transform.eulerAngles.y * Mathf.Deg2Rad;
            float sx = s_rect.width / (2f * zoom), sy = s_rect.height / (2f * zoom);
            // Screen right is the camera's right, screen up its forward, both on the ground plane.
            s_a00 = Mathf.Cos(yaw) * sx;
            s_a01 = -Mathf.Sin(yaw) * sx;
            s_a10 = Mathf.Sin(yaw) * sy;
            s_a11 = Mathf.Cos(yaw) * sy;
        }
        if (s_nextFit.Ready)
        {
            s_nextFit.Start(FitSeconds);
            Fit(zoom > 0f && camera != null);
        }
        if (s_fitted)
        {
            s_a00 = s_fitA00;
            s_a01 = s_fitA01;
            s_a10 = s_fitA10;
            s_a11 = s_fitA11;
        }
        else if (zoom <= 0f || camera == null)
        {
            return;
        }
        s_ready = true;
    }

    // Once a second: how far the fields' mapping is off the game's icons (the median of those inside the map; icons for
    // things beyond it, like an echo's objective pulses, are clamped to its edge); past 2 pixels, or without the
    // fields, the mapping is fitted to 3 or more icons by least squares and used until the fields agree again.
    private static void Fit(bool haveFields)
    {
        double sxx = 0, sxz = 0, szz = 0, sux = 0, suz = 0, svx = 0, svz = 0;
        var errors = new List<double>();
        foreach (var (world, ui, _) in GameIcons())
        {
            double dx = world.x - s_player.x, dz = world.z - s_player.z, u = ui.x - s_centre.x, v = ui.y - s_centre.y;
            if (Math.Abs(u) > s_rect.width || Math.Abs(v) > s_rect.height) continue;
            // An icon the fields put outside the map is one the game clamps to the edge: it says nothing of the mapping.
            if (haveFields && (Math.Abs(s_a00 * dx + s_a01 * dz) > s_rect.width / 2 || Math.Abs(s_a10 * dx + s_a11 * dz) > s_rect.height / 2)) continue;
            sxx += dx * dx; sxz += dx * dz; szz += dz * dz;
            sux += u * dx; suz += u * dz; svx += v * dx; svz += v * dz;
            if (haveFields) errors.Add(Math.Sqrt(Math.Pow(s_a00 * dx + s_a01 * dz - u, 2) + Math.Pow(s_a10 * dx + s_a11 * dz - v, 2)));
        }
        if (haveFields && errors.Count > 0)
        {
            errors.Sort();
            if (errors[errors.Count / 2] <= MaxErrorPixels)
            {
                s_fitted = false;
                return;
            }
        }
        if (errors.Count < 3 && haveFields)
        {
            s_fitted = false;
            return;
        }
        double det = sxx * szz - sxz * sxz;
        if (Math.Abs(det) < 1e-6) return;
        s_fitA00 = (float)((sux * szz - suz * sxz) / det);
        s_fitA01 = (float)((suz * sxx - sux * sxz) / det);
        s_fitA10 = (float)((svx * szz - svz * sxz) / det);
        s_fitA11 = (float)((svz * sxx - svx * sxz) / det);
        s_fitted = true;
    }

    // The game's minimap icons: their world point and their UI element's screen point (bottom-left origin).
    private static IEnumerable<(Vector3 World, Vector3 Ui, string Name)> GameIcons()
    {
        var manager = Il2CppLE.UI.Minimap.MinimapIconManager.Instance;
        if (!Game.Alive(manager) || manager.icons == null) yield break;
        foreach (var info in manager.icons)
        {
            if (info == null || !Game.Alive(info.icon) || !Game.Alive(info.ui) || !info.ui.gameObject.activeInHierarchy) continue;
            yield return (info.icon.transform.position, info.ui.transform.position, info.icon.name);
        }
    }
}
