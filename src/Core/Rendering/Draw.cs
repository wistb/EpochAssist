using EpochAssist.Ui;
using UnityEngine;

namespace EpochAssist.Core;

/// <summary>Colours of a world bar; Default takes them from the theme.</summary>
internal readonly record struct BarColours(Color Background, Color Health, Color Ward)
{
    public static BarColours Default => new(new Color(Theme.Off.r * 0.3f, Theme.Off.g * 0.3f, Theme.Off.b * 0.3f, 0.75f), Theme.Good, Theme.Accent);
}

/// <summary>
/// The drawing layer for OnGUI repaint events: lines, circles, labels and markers at world positions, filled shapes,
/// edge arrows, world bars and minimap markers. The host takes
/// the camera once at the start of the World GUI layer and drops it at its end, so features keep no drawing state;
/// with no camera nothing is drawn. Resources are made on first use and freed by Release (reload or quit).
/// </summary>
internal static class Draw
{
    private static Texture2D? s_white;
    // The lower-left half of a square: any triangle is an affine image of it.
    private static Texture2D? s_triangle;
    private const int TriangleSize = 64;
    private static Camera? s_camera;

    /// <summary>Style for world labels, set by the theme on build and cleared on release; no labels without it.</summary>
    public static GUIStyle? LabelStyle;

    /// <summary>Textures the layer holds now, for the dev bridge.</summary>
    public static int ResourceCount => (s_white != null ? 1 : 0) + (s_triangle != null ? 1 : 0);

    public static void Begin() => s_camera = Camera.main;

    public static void End() => s_camera = null;

    public static void Release()
    {
        if (s_white != null) UnityEngine.Object.Destroy(s_white);
        s_white = null;
        if (s_triangle != null) UnityEngine.Object.Destroy(s_triangle);
        s_triangle = null;
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

        float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
        Matrix4x4 previousMatrix = GUI.matrix;
        Color previousColor = GUI.color;
        GUI.matrix = previousMatrix * Matrix4x4.TRS(new Vector3(a.x, a.y, 0f), Quaternion.Euler(0f, 0f, angle), Vector3.one);
        GUI.color = color;
        GUI.DrawTexture(new Rect(0f, -width / 2f, length, width), White);
        GUI.color = previousColor;
        GUI.matrix = previousMatrix;
    }

    /// <summary>A filled rectangle in screen pixels (top-left origin).</summary>
    public static void FillRect(Rect rect, Color colour)
    {
        if (Event.current.type != EventType.Repaint || rect.width <= 0f || rect.height <= 0f) return;
        Color previous = GUI.color;
        GUI.color = colour;
        GUI.DrawTexture(rect, White);
        GUI.color = previous;
    }

    /// <summary>A filled triangle in screen pixels: the triangle texture drawn through the affine map that takes its
    /// corners to a, b and c.</summary>
    public static void FillTriangle(Vector2 a, Vector2 b, Vector2 c, Color colour)
    {
        if (Event.current.type != EventType.Repaint) return;
        Vector2 u = b - a, v = c - a;
        if (Mathf.Abs(u.x * v.y - u.y * v.x) < 0.5f) return;
        var map = Matrix4x4.identity;
        map.SetColumn(0, new Vector4(u.x, u.y, 0f, 0f));
        map.SetColumn(1, new Vector4(v.x, v.y, 0f, 0f));
        map.SetColumn(3, new Vector4(a.x, a.y, 0f, 1f));
        Matrix4x4 previousMatrix = GUI.matrix;
        Color previousColor = GUI.color;
        GUI.matrix = previousMatrix * map;
        GUI.color = colour;
        GUI.DrawTexture(new Rect(0f, 0f, 1f, 1f), Triangle);
        GUI.color = previousColor;
        GUI.matrix = previousMatrix;
    }

    /// <summary>
    /// An arrow at the screen edge pointing toward a world point that is off screen (behind the camera too: the
    /// direction is flipped); nothing while the point is on screen. Placed on the screen rect inset by margin.
    /// </summary>
    public static void EdgeArrow(Vector3 world, Color colour, float size = 18f, float margin = 24f)
    {
        if (Event.current.type != EventType.Repaint || Camera is not { } camera) return;
        Vector3 s = camera.WorldToScreenPoint(world);
        var screen = new Vector2(s.x, Screen.height - s.y);
        bool behind = s.z <= 0f;
        if (!behind && screen.x >= 0f && screen.x <= Screen.width && screen.y >= 0f && screen.y <= Screen.height) return;
        var centre = new Vector2(Screen.width / 2f, Screen.height / 2f);
        Vector2 dir = screen - centre;
        if (behind) dir = -dir;
        if (dir.sqrMagnitude < 1e-4f) dir = Vector2.down;
        dir.Normalize();
        // Where the ray from the centre leaves the inset rect.
        float halfW = Mathf.Max(1f, centre.x - margin), halfH = Mathf.Max(1f, centre.y - margin);
        float t = Mathf.Min(Mathf.Abs(dir.x) > 1e-4f ? halfW / Mathf.Abs(dir.x) : float.MaxValue, Mathf.Abs(dir.y) > 1e-4f ? halfH / Mathf.Abs(dir.y) : float.MaxValue);
        Vector2 tip = centre + dir * t;
        Vector2 side = new Vector2(-dir.y, dir.x) * (size * 0.5f);
        Vector2 baseCentre = tip - dir * size;
        FillTriangle(tip, baseCentre + side, baseCentre - side, colour);
    }

    /// <summary>
    /// A bar centred above a world point (lifted by heightOffset metres): health as a fraction of its maximum (0–1),
    /// and ward, which has no maximum, as a fraction of max health drawn after the health part, capped to the bar.
    /// </summary>
    public static void WorldBar(Vector3 world, float heightOffset, float health, float? ward, BarColours colours, float width = 60f, float height = 6f)
    {
        if (Event.current.type != EventType.Repaint || ToScreen(world + new Vector3(0f, heightOffset, 0f)) is not Vector2 at) return;
        var rect = new Rect(at.x - width / 2f, at.y - height, width, height);
        FillRect(rect, colours.Background);
        float h = Mathf.Clamp01(health);
        FillRect(new Rect(rect.x, rect.y, width * h, height), colours.Health);
        if (ward is float w && w > 0f)
        {
            float part = Mathf.Min(Mathf.Clamp01(w), 1f - h);
            FillRect(new Rect(rect.x + width * h, rect.y, width * part, height), colours.Ward);
        }
    }

    /// <summary>A filled diamond on the game's minimap at a world point; nothing without a minimap or outside its area.</summary>
    public static void MinimapMarker(Vector3 world, Color colour, float size = 8f)
    {
        if (Event.current.type != EventType.Repaint || MinimapMap.ToScreen(world) is not Vector2 at) return;
        float h = size / 2f;
        Vector2 top = at + new Vector2(0f, -h), right = at + new Vector2(h, 0f), bottom = at + new Vector2(0f, h), left = at + new Vector2(-h, 0f);
        FillTriangle(top, right, bottom, colour);
        FillTriangle(top, bottom, left, colour);
    }

    private static Texture2D White
    {
        get
        {
            if (s_white == null)
            {
                s_white = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
                s_white.SetPixel(0, 0, Color.white);
                s_white.Apply();
            }
            return s_white;
        }
    }

    // GUI y runs down and texture rows up: a GUI point (u, v) samples row (1 - v), so the filled half is u + v <= 1.
    private static Texture2D Triangle
    {
        get
        {
            if (s_triangle != null) return s_triangle;
            s_triangle = new Texture2D(TriangleSize, TriangleSize, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[TriangleSize * TriangleSize];
            for (int y = 0; y < TriangleSize; y++)
            {
                for (int x = 0; x < TriangleSize; x++)
                {
                    float u = (x + 0.5f) / TriangleSize, v = 1f - (y + 0.5f) / TriangleSize;
                    pixels[y * TriangleSize + x] = u + v <= 1f ? new Color32(255, 255, 255, 255) : new Color32(255, 255, 255, 0);
                }
            }
            s_triangle.SetPixels32(pixels);
            s_triangle.Apply();
            return s_triangle;
        }
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
