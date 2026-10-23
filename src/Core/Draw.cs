using UnityEngine;

namespace EpochAssist.Core;

// Screen-space lines for OnGUI repaint events.
internal static class Draw
{
    private static Texture2D? s_white;
    private static Camera? s_camera;

    public static void Begin() => s_camera = Camera.main;

    public static void End() => s_camera = null;

    public static void Release()
    {
        if (s_white != null) UnityEngine.Object.Destroy(s_white);
        s_white = null;
    }

    public static void WorldLine(Vector3 a, Vector3 b, Color color, float width)
    {
        var camera = s_camera ?? Camera.main;
        if (camera == null) return;

        Vector3 sa = camera.WorldToScreenPoint(a);
        Vector3 sb = camera.WorldToScreenPoint(b);
        if (sa.z <= 0f || sb.z <= 0f) return;

        ScreenLine(new Vector2(sa.x, Screen.height - sa.y), new Vector2(sb.x, Screen.height - sb.y), color, width);
    }

    public static void ScreenLine(Vector2 a, Vector2 b, Color color, float width)
    {
        Vector2 delta = b - a;
        float length = delta.magnitude;
        if (length < 0.5f) return;

        if (s_white == null)
        {
            s_white = new Texture2D(1, 1);
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
}
