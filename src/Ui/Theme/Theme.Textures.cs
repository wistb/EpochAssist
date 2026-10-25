using UnityEngine;

namespace EpochAssist.Ui;

internal static partial class Theme
{
    private static readonly Dictionary<int, GUIStyle> s_sliced = new();

    /// <summary>Draws a rounded texture 9-sliced, so its corners keep their shape at any size.</summary>
    public static void DrawSliced(Rect rect, Texture2D texture, int border)
    {
        border = Slice(border);
        if (!s_sliced.TryGetValue(border, out var style))
        {
            style = new GUIStyle { border = Offset(border) };
            s_sliced[border] = style;
        }
        style.normal.background = texture;
        style.Draw(rect, false, false, false, false);
    }

    // Square corners still need a slice wide enough for the edge; every rounded texture is cut at this border.
    private static int Slice(int radius) => Math.Max(radius, 2);

    // A 9-sliceable rounded rectangle with left and right corner radii (0 = square).
    private static Texture2D Rounded(int left, int right, Color fill, Color edge, float edgeWidth)
    {
        int r = Slice(Math.Max(left, right));
        int size = r * 2 + 2;
        return Paint(size, size, (x, y) =>
        {
            float px = x + 0.5f, py = y + 0.5f;
            float radius = px < size / 2f ? left : right;
            float d = BoxDistance(px, py, size, size, radius);
            Color c = edgeWidth > 0f ? Color.Lerp(fill, edge, Mathf.Clamp01(d + edgeWidth + 0.5f)) : fill;
            return Shade(c, Coverage(d));
        });
    }

    private static Texture2D Circle(int size, Color color) =>
        Paint(size, size, (x, y) => Shade(color, Coverage(CircleDistance(x + 0.5f, y + 0.5f, size / 2f, size / 2f, size / 2f - 0.5f))));

    private static Texture2D Switch(bool on, Color track, Color knob)
    {
        const int w = 36, h = 20;
        float knobX = on ? w - h / 2f : h / 2f;
        return Paint(w, h, (x, y) =>
        {
            float px = x + 0.5f, py = y + 0.5f;
            Color c = Shade(track, Coverage(BoxDistance(px, py, w, h, h / 2f)));
            float k = Coverage(CircleDistance(px, py, knobX, h / 2f, h / 2f - 3f));
            return Over(Shade(knob, k), c);
        });
    }

    // Signed distance to a rounded box filling the texture (negative inside).
    private static float BoxDistance(float px, float py, float w, float h, float r)
    {
        float qx = Mathf.Abs(px - w / 2f) - (w / 2f - r);
        float qy = Mathf.Abs(py - h / 2f) - (h / 2f - r);
        float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude;
        return outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
    }

    // Signed distance to an axis-aligned rectangle (centre, half sizes); square corners.
    private static float RectDistance(float px, float py, float cx, float cy, float hw, float hh) =>
        Mathf.Max(Mathf.Abs(px - cx) - hw, Mathf.Abs(py - cy) - hh);

    private static float CircleDistance(float px, float py, float cx, float cy, float r) =>
        new Vector2(px - cx, py - cy).magnitude - r;

    private static float Coverage(float distance) => Mathf.Clamp01(0.5f - distance);

    private static Color Shade(Color c, float coverage) => new(c.r, c.g, c.b, c.a * coverage);

    private static Color Over(Color top, Color bottom)
    {
        float a = top.a + bottom.a * (1f - top.a);
        if (a <= 0f) return Color.clear;
        Color rgb = (top * top.a + bottom * bottom.a * (1f - top.a)) / a;
        return new Color(rgb.r, rgb.g, rgb.b, a);
    }

    private static Texture2D Paint(int w, int h, Func<int, int, Color> pixel)
    {
        var texture = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                texture.SetPixel(x, y, pixel(x, y));
            }
        }
        texture.Apply();
        s_textures.Add(texture);
        return texture;
    }
}
