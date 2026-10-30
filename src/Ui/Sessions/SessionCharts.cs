using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

/// <summary>
/// The Session tab's drawing. Stacked bars are drawn directly; the area chart, the column chart and the timeline are
/// drawn once per change into a texture at the screen's own pixel size (the menu is scaled), with anti-aliased lines,
/// smoothed curves and soft fills, and blitted each frame with bilinear filtering, so a chart costs one primitive a frame
/// however many points it has. A texture is drawn again only when its size, colours or key (the figures' version) change.
/// Repaint only.
/// </summary>
internal static class SessionCharts
{
    /// <summary>A chart's texture and what it was drawn for.</summary>
    internal sealed class Canvas
    {
        public Texture2D? Texture;
        public Color32[]? Pixels;
        public int Width, Height;
        public int Key;
    }

    /// <summary>Texture draws since load, and primitives drawn (dev `sessions cost`).</summary>
    public static int Redraws, Primitives;

    private static readonly List<Canvas> s_canvases = new();
    private const int MaxSide = 2048;

    /// <summary>Parts side by side, each as wide as its share of the total; nothing when the total is 0.</summary>
    public static void StackedBar(Rect r, float[] values, Color[] colours, int count)
    {
        if (Event.current.type != EventType.Repaint) return;
        float total = 0f;
        for (int i = 0; i < count; i++) total += Mathf.Max(0f, values[i]);
        Fill(r, Theme.Values.Control);
        if (total <= 0f) return;
        float x = r.x;
        for (int i = 0; i < count; i++)
        {
            if (values[i] <= 0f) continue;
            float w = i == count - 1 ? r.xMax - x : Mathf.Round(r.width * values[i] / total);
            if (w >= 1f) Fill(new Rect(x, r.y, w, r.height), colours[i]);
            x += w;
        }
    }

    public static void Fill(Rect r, Color colour)
    {
        Theme.Fill(r, colour);
        Primitives++;
    }

    public static void Paint(Rect r, string text, GUIStyle style)
    {
        Widgets.Paint(r, text, style);
        Primitives++;
    }

    /// <summary>
    /// One series over 0..xMax and 0..yMax as a smoothed, anti-aliased line over a soft fill, with faint quarter lines
    /// (none for a sparkline); spans in gaps are drawn thinner and fainter. Line scales the line's width and end dot.
    /// </summary>
    public static void Area(Rect r, Canvas canvas, int key, IReadOnlyList<(double T, double V)> points, double xMax, double yMax, Color colour,
        IReadOnlyList<(double From, double To)> gaps, bool grid = true, float line = 1f)
    {
        if (Event.current.type != EventType.Repaint) return;
        var (w, h, scale) = Size(r);
        if (Prepare(canvas, w, h, HashCode.Combine(key, w, h, colour, Theme.DividerColor, grid, line)) is { } pixels)
        {
            if (grid) Grid(pixels, w, h, scale);
            if (points.Count >= 2 && xMax > 0d && yMax > 0d)
            {
                var curve = Smooth(points, xMax, yMax, w, h, 3f * scale, scale);
                // The fill: from the curve down, fading towards the axis.
                for (int i = 0; i + 1 < curve.Count; i++)
                {
                    int x0 = Mathf.Clamp(Mathf.RoundToInt(curve[i].x), 0, w - 1), x1 = Mathf.Clamp(Mathf.RoundToInt(curve[i + 1].x), 0, w - 1);
                    for (int x = x0; x < x1; x++)
                    {
                        float top = Mathf.Lerp(curve[i].y, curve[i + 1].y, (x - x0) / (float)(x1 - x0));
                        for (int y = 0; y < Mathf.Min(h, (int)top); y++) Blend(pixels, y * w + x, colour, 0.03f + 0.17f * y / Mathf.Max(1f, top));
                    }
                }
                var cover = Coverage(w, h);
                for (int i = 0; i + 1 < curve.Count; i++)
                {
                    double tMid = (curve[i].x + curve[i + 1].x) / 2d / (w - 1) * xMax;
                    bool inGap = false;
                    foreach (var gap in gaps)
                        if (tMid >= gap.From && tMid <= gap.To) inGap = true;
                    Stroke(cover, w, h, curve[i], curve[i + 1], (inGap ? 0.5f : 1f) * line * scale, inGap ? 0.4f : 1f);
                }
                // A dot where the line ends: now, or the session's end.
                Dot(cover, w, h, curve[^1], 2.5f * Mathf.Clamp(line, 1f, 1.3f) * scale);
                Composite(pixels, cover, colour);
            }
            Apply(canvas);
        }
        Blit(r, canvas);
    }

    /// <summary>
    /// One column per value (oldest left), a value over cap clipped with a notch at its top, and a smoothed line through
    /// average when given.
    /// </summary>
    public static void Columns(Rect r, Canvas canvas, int key, IReadOnlyList<double> values, double cap, Color colour, IReadOnlyList<double>? average, Color averageColour)
    {
        if (Event.current.type != EventType.Repaint) return;
        var (w, h, scale) = Size(r);
        if (Prepare(canvas, w, h, HashCode.Combine(key, w, h, colour, averageColour, Theme.DividerColor)) is { } pixels)
        {
            Grid(pixels, w, h, scale);
            int n = values.Count;
            if (n > 0 && cap > 0d)
            {
                float pitch = w / (float)n, gap = Mathf.Min(pitch * 0.3f, 4f * scale), top = h - 4f * scale;
                for (int i = 0; i < n; i++)
                {
                    float x0 = i * pitch + gap / 2f, x1 = (i + 1) * pitch - gap / 2f;
                    float height = (float)(Math.Min(values[i], cap) / cap) * top;
                    FillRect(pixels, w, h, x0, 0f, x1, height, colour, 0.7f);
                    FillRect(pixels, w, h, x0, height - Mathf.Max(1f, scale), x1, height, colour, 1f);
                    // Clipped: a gap across the column's top.
                    if (values[i] > cap) FillRect(pixels, w, h, x0, height - 4f * scale, x1, height - 2.5f * scale, Color.black, 0.55f);
                }
                if (average is { Count: >= 2 })
                {
                    var points = new List<(double, double)>(average.Count);
                    for (int i = 0; i < average.Count; i++) points.Add((i + 0.5d, Math.Min(average[i], cap)));
                    var curve = Smooth(points, n, cap * h / top, w, h, 0f, scale);
                    var cover = Coverage(w, h);
                    for (int i = 0; i + 1 < curve.Count; i++) Stroke(cover, w, h, curve[i], curve[i + 1], 1f * scale, 1f);
                    Composite(pixels, cover, averageColour);
                }
            }
            Apply(canvas);
        }
        Blit(r, canvas);
    }

    /// <summary>
    /// Zone segments in their kinds' colours along the bottom, marks above them (drops of rare and above in their rarity's
    /// colour, deaths in the high danger colour), gaps hatched; at most 80 segments and 40 marks.
    /// </summary>
    public static void Timeline(Rect r, Canvas canvas, SessionStats stats)
    {
        if (Event.current.type != EventType.Repaint) return;
        var (w, h, scale) = Size(r);
        if (Prepare(canvas, w, h, HashCode.Combine(stats.Id, stats.Version, w, h, ColourRoles.Version, Theme.Values.Control)) is { } pixels)
        {
            double duration = Math.Max(1d, stats.Duration);
            float band = Mathf.Round(h * 0.5f), radius = 3f * scale;
            FillRect(pixels, w, h, 0f, 0f, w, band, Theme.Values.Control, 1f);
            foreach (var (start, end, kind) in stats.Timeline.Drawn(duration, r.width, SessionStats.DrawnSegments))
            {
                float x0 = (float)(start / duration * w);
                FillRect(pixels, w, h, x0, 0f, Mathf.Max(x0 + 1f, (float)(end / duration * w)), band, ColourRoles.Zone(kind), 1f);
            }
            foreach (var (from, to) in stats.Gaps)
            {
                float x0 = (float)(from / duration * w), x1 = (float)(to / duration * w);
                for (float x = x0; x < x1; x += 4f * scale) FillRect(pixels, w, h, x, 0f, Mathf.Min(x1, x + 2f * scale), band, Color.black, 0.3f);
            }
            foreach (var (t, kind) in stats.Timeline.DrawnMarks(duration, r.width, SessionStats.DrawnMarks))
            {
                var cover = Coverage(w, h);
                Dot(cover, w, h, new Vector2(Mathf.Clamp((float)(t / duration * w), radius, w - radius), band + (h - band) / 2f), radius);
                Composite(pixels, cover, kind == "death" ? ColourRoles.Danger(2) : ColourRoles.ItemNamed(kind));
            }
            Apply(canvas);
        }
        Blit(r, canvas);
    }

    /// <summary>Frees one canvas's texture.</summary>
    public static void Release(Canvas canvas)
    {
        if (canvas.Texture != null) UnityEngine.Object.Destroy(canvas.Texture);
        canvas.Texture = null;
        canvas.Pixels = null;
        canvas.Key = 0;
        s_canvases.Remove(canvas);
    }

    /// <summary>Frees every chart texture (the menu stops, a view closes).</summary>
    public static void Release()
    {
        foreach (var canvas in s_canvases.ToList()) Release(canvas);
        s_canvases.Clear();
    }

    /// <summary>Live chart textures, for the dev reload check.</summary>
    public static int TextureCount => s_canvases.Count(c => c.Texture != null);

    // ---- Pixels (y up, as the texture's rows) ----

    // The texture's size in screen pixels for a rect in menu units, and the menu's scale.
    private static (int W, int H, float Scale) Size(Rect r)
    {
        float scale = Mathf.Max(0.5f, Mathf.Abs(GUI.matrix.m00));
        return (Mathf.Clamp(Mathf.RoundToInt(r.width * scale), 8, MaxSide), Mathf.Clamp(Mathf.RoundToInt(r.height * scale), 8, MaxSide), scale);
    }

    // The pixel buffer to draw into, cleared, when the canvas must be drawn again; null when its texture is current.
    private static Color32[]? Prepare(Canvas canvas, int w, int h, int key)
    {
        if (canvas.Texture != null && canvas.Key == key) return null;
        if (canvas.Texture == null || canvas.Width != w || canvas.Height != h)
        {
            if (canvas.Texture != null) UnityEngine.Object.Destroy(canvas.Texture);
            canvas.Texture = new Texture2D(w, h, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            canvas.Pixels = new Color32[w * h];
            canvas.Width = w;
            canvas.Height = h;
            if (!s_canvases.Contains(canvas)) s_canvases.Add(canvas);
        }
        canvas.Key = key;
        Array.Clear(canvas.Pixels!, 0, canvas.Pixels!.Length);
        return canvas.Pixels;
    }

    private static void Apply(Canvas canvas)
    {
        canvas.Texture!.SetPixels32(canvas.Pixels!);
        canvas.Texture.Apply(false);
        Redraws++;
    }

    // The texture at its own pixel size on whole screen pixels: the menu and HUD are scaled, and a texture landing between
    // pixels is softened by the bilinear filter.
    private static void Blit(Rect r, Canvas canvas)
    {
        if (canvas.Texture == null) return;
        float scale = Mathf.Max(0.5f, Mathf.Abs(GUI.matrix.m00));
        GUI.DrawTexture(new Rect(Mathf.Round(r.x * scale) / scale, Mathf.Round(r.y * scale) / scale, canvas.Width / scale, canvas.Height / scale), canvas.Texture);
        Primitives++;
    }

    // The axis, and faint lines at a quarter, half and three quarters.
    private static void Grid(Color32[] pixels, int w, int h, float scale)
    {
        Color line = Theme.DividerColor;
        FillRect(pixels, w, h, 0f, 0f, w, Mathf.Max(1f, scale), line, 1f);
        for (int q = 1; q <= 3; q++) FillRect(pixels, w, h, 0f, h * q / 4f, w, h * q / 4f + Mathf.Max(1f, scale * 0.75f), line, 0.4f);
    }

    // Points to pixels through a monotone cubic (Fritsch–Carlson), sampled every 2 px: a rising series never dips or
    // overshoots between its points.
    private static List<Vector2> Smooth(IReadOnlyList<(double T, double V)> points, double xMax, double yMax, int w, int h, float pad, float scale)
    {
        int n = points.Count;
        var xs = new float[n];
        var ys = new float[n];
        for (int i = 0; i < n; i++)
        {
            xs[i] = (float)(points[i].T / xMax) * (w - 1);
            ys[i] = pad + (float)Math.Clamp(points[i].V / yMax, 0d, 1d) * (h - 1 - 2f * pad);
        }
        var deltas = new float[n - 1];
        for (int i = 0; i + 1 < n; i++) deltas[i] = xs[i + 1] > xs[i] ? (ys[i + 1] - ys[i]) / (xs[i + 1] - xs[i]) : 0f;
        var slopes = new float[n];
        slopes[0] = deltas[0];
        slopes[n - 1] = deltas[n - 2];
        for (int i = 1; i + 1 < n; i++) slopes[i] = deltas[i - 1] * deltas[i] <= 0f ? 0f : (deltas[i - 1] + deltas[i]) / 2f;
        for (int i = 0; i + 1 < n; i++)
        {
            if (deltas[i] == 0f)
            {
                slopes[i] = slopes[i + 1] = 0f;
                continue;
            }
            float a = slopes[i] / deltas[i], b = slopes[i + 1] / deltas[i], s = a * a + b * b;
            if (s > 9f)
            {
                float t = 3f / Mathf.Sqrt(s);
                slopes[i] = t * a * deltas[i];
                slopes[i + 1] = t * b * deltas[i];
            }
        }
        var curve = new List<Vector2>(w / 2 + n + 2) { new(xs[0], ys[0]) };
        float step = 2f * scale;
        for (int i = 0; i + 1 < n; i++)
        {
            float dx = xs[i + 1] - xs[i];
            int samples = Mathf.Max(1, Mathf.CeilToInt(dx / step));
            for (int k = 1; k <= samples; k++)
            {
                float t = k / (float)samples, t2 = t * t, t3 = t2 * t;
                float y = (2 * t3 - 3 * t2 + 1) * ys[i] + (t3 - 2 * t2 + t) * dx * slopes[i] + (-2 * t3 + 3 * t2) * ys[i + 1] + (t3 - t2) * dx * slopes[i + 1];
                curve.Add(new Vector2(xs[i] + dx * t, y));
            }
        }
        return curve;
    }

    // One line's coverage, kept apart from the pixels so its overlapping segments take the stronger coverage at their
    // joins instead of adding up; composited once in its colour.
    private static float[] s_cover = Array.Empty<float>();

    private static float[] Coverage(int w, int h)
    {
        if (s_cover.Length < w * h) s_cover = new float[w * h];
        else Array.Clear(s_cover, 0, w * h);
        return s_cover;
    }

    private static void Composite(Color32[] pixels, float[] cover, Color colour)
    {
        for (int i = 0; i < pixels.Length; i++)
            if (cover[i] > 0f) Blend(pixels, i, colour, cover[i]);
    }

    // A segment as a line of the given half width, its edge anti-aliased by distance.
    private static void Stroke(float[] cover, int w, int h, Vector2 a, Vector2 b, float halfWidth, float alpha)
    {
        float reach = halfWidth + 1f;
        int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, b.x) - reach)), x1 = Mathf.Min(w - 1, Mathf.CeilToInt(Mathf.Max(a.x, b.x) + reach));
        int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, b.y) - reach)), y1 = Mathf.Min(h - 1, Mathf.CeilToInt(Mathf.Max(a.y, b.y) + reach));
        Vector2 ab = b - a;
        float length2 = Mathf.Max(1e-6f, ab.sqrMagnitude);
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / length2);
                float c = Mathf.Clamp01(halfWidth + 0.5f - (p - (a + ab * t)).magnitude) * alpha;
                if (c > cover[y * w + x]) cover[y * w + x] = c;
            }
        }
    }

    private static void Dot(float[] cover, int w, int h, Vector2 centre, float radius)
    {
        for (int y = Mathf.Max(0, (int)(centre.y - radius - 1f)); y <= Mathf.Min(h - 1, (int)(centre.y + radius + 1f)); y++)
        {
            for (int x = Mathf.Max(0, (int)(centre.x - radius - 1f)); x <= Mathf.Min(w - 1, (int)(centre.x + radius + 1f)); x++)
            {
                float c = Mathf.Clamp01(radius + 0.5f - (new Vector2(x + 0.5f, y + 0.5f) - centre).magnitude);
                if (c > cover[y * w + x]) cover[y * w + x] = c;
            }
        }
    }

    // A rect, its partly covered edge columns and rows blended by their coverage.
    private static void FillRect(Color32[] pixels, int w, int h, float x0, float y0, float x1, float y1, Color colour, float alpha)
    {
        for (int y = Mathf.Max(0, Mathf.FloorToInt(y0)); y < Mathf.Min(h, Mathf.CeilToInt(y1)); y++)
        {
            float cy = Mathf.Clamp01(Mathf.Min(y + 1f, y1) - Mathf.Max(y, y0));
            for (int x = Mathf.Max(0, Mathf.FloorToInt(x0)); x < Mathf.Min(w, Mathf.CeilToInt(x1)); x++)
            {
                float cx = Mathf.Clamp01(Mathf.Min(x + 1f, x1) - Mathf.Max(x, x0));
                Blend(pixels, y * w + x, colour, alpha * cx * cy);
            }
        }
    }

    // Colour over the pixel with alpha a ("over" on straight alpha).
    private static void Blend(Color32[] pixels, int index, Color colour, float a)
    {
        if (a <= 0f) return;
        ref Color32 p = ref pixels[index];
        float da = p.a / 255f, oa = a + da * (1f - a);
        if (oa <= 0f) return;
        p = new Color32(
            (byte)Mathf.Clamp((colour.r * 255f * a + p.r * da * (1f - a)) / oa, 0f, 255f),
            (byte)Mathf.Clamp((colour.g * 255f * a + p.g * da * (1f - a)) / oa, 0f, 255f),
            (byte)Mathf.Clamp((colour.b * 255f * a + p.b * da * (1f - a)) / oa, 0f, 255f),
            (byte)Mathf.Clamp(oa * 255f, 0f, 255f));
    }
}
