using UnityEngine;

namespace EpochAssist.Dev;

internal static partial class DevCommands
{
    // ---- Marks: the see list drawn on a shot ----
    // Each item gets an outline and a tag with its number at its top-left corner, in its kind's colour: game windows
    // magenta, game controls yellow, ours cyan, text green. Drawn into the image after capture, so the game's frame and
    // the item positions stay as they were.

    // 3 × 5 digits, a row of three bits per line from the top.
    private static readonly int[][] s_digits =
    {
        new[] { 7, 5, 5, 5, 7 }, new[] { 2, 6, 2, 2, 7 }, new[] { 7, 1, 7, 4, 7 }, new[] { 7, 1, 7, 1, 7 }, new[] { 5, 5, 7, 1, 1 },
        new[] { 7, 4, 7, 1, 7 }, new[] { 7, 4, 7, 5, 7 }, new[] { 7, 1, 1, 1, 1 }, new[] { 7, 5, 7, 5, 7 }, new[] { 7, 5, 7, 1, 7 },
    };

    private static readonly Color32 TagBack = new(0, 0, 0, 255), TagText = new(255, 255, 255, 255);

    private static Color32 MarkColour(string kind) => kind switch
    {
        "window" => new Color32(255, 60, 220, 255),
        "ours" or "control" => new Color32(0, 220, 255, 255),
        "text" => new Color32(80, 255, 120, 255),
        _ => new Color32(255, 210, 0, 255),
    };

    // area: the part of the screen the image covers, in screen pixels from the top left. Only the touched blocks go to
    // the texture: copying a whole frame's pixels through IL2CPP takes seconds.
    private static void DrawMarks(Texture2D image, Rect area, List<SeenItem> items)
    {
        int w = image.width, h = image.height;
        float kx = w / area.width, ky = h / area.height;
        // About 3 px per digit pixel on a 1440-high screen, so a fitted view still reads.
        int scale = Mathf.Max(2, Mathf.RoundToInt(Screen.height / 480f * Mathf.Min(kx, ky)));
        int line = Mathf.Max(1, scale / 2);
        for (int i = 0; i < items.Count; i++)
        {
            Rect r = items[i].Rect;
            if (!r.Overlaps(area)) continue;
            int x0 = Mathf.RoundToInt((r.xMin - area.x) * kx), x1 = Mathf.RoundToInt((r.xMax - area.x) * kx);
            int y0 = Mathf.RoundToInt((r.yMin - area.y) * ky), y1 = Mathf.RoundToInt((r.yMax - area.y) * ky);
            var colour = MarkColour(items[i].Kind);
            Fill(image, x0, y0, x1 - x0, line, colour);
            Fill(image, x0, y1 - line, x1 - x0, line, colour);
            Fill(image, x0, y0, line, y1 - y0, colour);
            Fill(image, x1 - line, y0, line, y1 - y0, colour);
            Tag(image, Mathf.Max(x0, 0), Mathf.Max(y0, 0), (i + 1).ToString(), scale, colour);
        }
        image.Apply();
    }

    // A solid block, (x, y) from the top left, clipped to the image.
    private static void Fill(Texture2D image, int x, int y, int bw, int bh, Color32 colour)
    {
        int x0 = Mathf.Max(x, 0), y0 = Mathf.Max(y, 0), x1 = Mathf.Min(x + bw, image.width), y1 = Mathf.Min(y + bh, image.height);
        if (x1 <= x0 || y1 <= y0) return;
        var block = new Color32[(x1 - x0) * (y1 - y0)];
        Array.Fill(block, colour);
        Put(image, x0, y0, x1 - x0, y1 - y0, block);
    }

    // The number on a dark tag with a coloured edge, its top-left corner at (x, y), kept inside the image.
    private static void Tag(Texture2D image, int x, int y, string number, int scale, Color32 colour)
    {
        int pad = scale;
        int tw = number.Length * 4 * scale - scale + 2 * pad, th = 5 * scale + 2 * pad;
        if (tw > image.width || th > image.height) return;
        x = Mathf.Clamp(x, 0, image.width - tw);
        y = Mathf.Clamp(y, 0, image.height - th);
        // Rows from the top here; Put turns the block over.
        var block = new Color32[tw * th];
        for (int ty = 0; ty < th; ty++)
        {
            for (int tx = 0; tx < tw; tx++) block[ty * tw + tx] = tx == 0 || ty == 0 || tx == tw - 1 || ty == th - 1 ? colour : TagBack;
        }
        for (int d = 0; d < number.Length; d++)
        {
            int[] glyph = s_digits[number[d] - '0'];
            for (int row = 0; row < 5; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    if ((glyph[row] & (4 >> col)) == 0) continue;
                    for (int sy = 0; sy < scale; sy++)
                    {
                        int ty = pad + row * scale + sy;
                        for (int sx = 0; sx < scale; sx++) block[ty * tw + pad + d * 4 * scale + col * scale + sx] = TagText;
                    }
                }
            }
        }
        Put(image, x, y, tw, th, block);
    }

    // A block whose rows run from the top, at (x, y) from the top left; texture rows count from the bottom.
    private static void Put(Texture2D image, int x, int y, int bw, int bh, Color32[] block)
    {
        var flipped = new Color32[block.Length];
        for (int row = 0; row < bh; row++) Array.Copy(block, row * bw, flipped, (bh - 1 - row) * bw, bw);
        image.SetPixels32(x, image.height - y - bh, bw, bh, flipped);
    }
}
