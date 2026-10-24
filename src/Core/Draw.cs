using UnityEngine;

namespace EpochAssist.Core;

/// <summary>
/// The drawing layer for OnGUI repaint events: lines, circles, labels and markers at world positions. The host takes
/// the camera once at the start of the World GUI layer and drops it at its end, so features keep no drawing state;
/// with no camera nothing is drawn. Resources are made on first use and freed by Release (reload or quit).
/// </summary>
internal static class Draw
{
    private static Texture2D? s_white;
    private static Camera? s_camera;

    /// <summary>Style for world labels, set by the theme on build and cleared on release; no labels without it.</summary>
    public static GUIStyle? LabelStyle;

    /// <summary>Textures the layer holds now, for the dev bridge.</summary>
    public static int ResourceCount => s_white != null ? 1 : 0;

    public static void Begin() => s_camera = Camera.main;

    public static void End() => s_camera = null;

    public static void Release()
    {
        if (s_white != null) UnityEngine.Object.Destroy(s_white);
        s_white = null;
        LabelStyle = null;
    }

    public static void WorldLine(Vector3 a, Vector3 b, Color color, float width)
    {
        if (ToScreen(a) is not Vector2 sa || ToScreen(b) is not Vector2 sb) return;
        ScreenLine(sa, sb, color, width);
    }

    /// <summary>A flat circle on the ground plane (y of the centre), drawn as a closed line.</summary>
    public static void WorldCircle(Vector3 center, float radius, Color color, float width, int segments = 64)
    {
        if (Camera == null || segments < 3) return;
        Vector3 previous = center + new Vector3(radius, 0f, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float angle = i * (2f * Mathf.PI / segments);
            Vector3 next = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            WorldLine(previous, next, color, width);
            previous = next;
        }
    }

    /// <summary>Text centred on a world point, in the theme's label style tinted with the colour.</summary>
    public static void WorldLabel(Vector3 point, string text, Color color)
    {
        if (LabelStyle == null || ToScreen(point) is not Vector2 at) return;
        var content = new GUIContent(text);
        Vector2 size = LabelStyle.CalcSize(content);
        Color previous = GUI.color;
        GUI.color = color;
        GUI.Label(new Rect(at.x - size.x / 2f, at.y - size.y / 2f, size.x, size.y), content, LabelStyle);
        GUI.color = previous;
    }

    /// <summary>A diamond of the given size in screen pixels, centred on a world point.</summary>
    public static void WorldMarker(Vector3 point, Color color, float size)
    {
        if (ToScreen(point) is not Vector2 at) return;
        float h = size / 2f;
        Vector2 top = at + new Vector2(0f, -h), right = at + new Vector2(h, 0f), bottom = at + new Vector2(0f, h), left = at + new Vector2(-h, 0f);
        ScreenLine(top, right, color, 2f);
        ScreenLine(right, bottom, color, 2f);
        ScreenLine(bottom, left, color, 2f);
        ScreenLine(left, top, color, 2f);
    }

    public static void ScreenLine(Vector2 a, Vector2 b, Color color, float width)
    {
        Vector2 delta = b - a;
        float length = delta.magnitude;
        if (length < 0.5f) return;

        if (s_white == null)
        {
            s_white = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            s_white.SetPixel(0, 0, Color.white);
            s_white.Apply();
        }

        float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
        Matrix4x4 previousMatrix = GUI.matrix;
        Color previousColor = GUI.color;
        GUI.matrix = previousMatrix * Matrix4x4.TRS(new Vector3(a.x, a.y, 0f), Quaternion.Euler(0f, 0f, angle), Vector3.one);
        GUI.color = color;
        GUI.DrawTexture(new Rect(0f, -width / 2f, length, width), s_white);
        GUI.color = previousColor;
        GUI.matrix = previousMatrix;
    }

    // The frame's camera; outside the World layer (no Begin) the main camera, as before.
    private static Camera? Camera => s_camera ?? Camera.main;

    // Screen position with a top-left origin, or null behind the camera or with none.
    private static Vector2? ToScreen(Vector3 world)
    {
        var camera = Camera;
        if (camera == null) return null;
        Vector3 s = camera.WorldToScreenPoint(world);
        return s.z > 0f ? new Vector2(s.x, Screen.height - s.y) : null;
    }
}
