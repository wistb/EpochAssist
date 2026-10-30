using EpochAssist.Core;
using EpochAssist.Ui;
using UnityEngine;

namespace EpochAssist.Dev;

internal static partial class DevCommands
{
    // ---- Screenshots ----

    private sealed class Capture
    {
        public string Name = string.Empty;
        public string FullPath = string.Empty;
        public Rect? Crop;
        // Pixels kept around the crop on each side.
        public float Pad = CropPad;
        public float Deadline;
        public long LastSize = -1;
        public bool Matte;
        // Matte shots: the first capture (over black) waits here while the second (over white) is taken.
        public string? BlackPath;
        // Null saves a PNG; otherwise a JPG at this quality (1-100).
        public int? Jpg;
        // Shrinks the image so its long edge is at most this; null keeps its size.
        public int? Fit;
        // Enlarges the image this many times, pixels kept square; 1 keeps its size.
        public int Zoom = 1;
        // The see list drawn on the image as numbered boxes; null draws none.
        public List<SeenItem>? Marks;
        // Matte shots: puts the backdrop colour under what is captured; null takes it away.
        public Action<Color?> Backdrop = SetHudBackdrop;
        // Added to the reply, e.g. that our HUD covers part of the shot.
        public string Note = string.Empty;
        // Lines after the reply, e.g. the marks' list.
        public List<string> Legend = new();

        public Capture With(ShotOptions options)
        {
            Matte = options.Matte;
            Jpg = options.Jpg;
            Fit = options.Fit;
            Zoom = options.Zoom;
            if (options.Marks) Marks = new();
            return this;
        }
    }

    // What follows the target in a shot command, the flags in any order: [name] [matte] [jpg|jpg<quality>] [marks]
    // [fit<long edge>] [x2|x3|x4].
    private sealed record ShotOptions(string? Name, bool Matte, int? Jpg, bool Marks, int? Fit, int Zoom);

    private const int JpgQuality = 90, ViewFit = 1600, ViewJpg = 85, MaxZoom = 8;
    private const float CropPad = 4f, ItemPad = 8f;

    private static ShotOptions ParseShotOptions(string[] args)
    {
        string? name = null;
        bool matte = false, marks = false;
        int? jpg = null, fit = null;
        int zoom = 1;
        foreach (string arg in args)
        {
            string lower = arg.ToLowerInvariant();
            if (lower == "matte") matte = true;
            else if (lower == "marks") marks = true;
            else if (lower.StartsWith("jpg", StringComparison.Ordinal))
                jpg = lower.Length == 3 ? JpgQuality : int.TryParse(lower[3..], out int q) && q is >= 1 and <= 100 ? q : throw new ArgumentException("jpg quality is 1 to 100, e.g. jpg85");
            else if (lower.StartsWith("fit", StringComparison.Ordinal))
                fit = int.TryParse(lower[3..], out int f) && f >= 16 ? f : throw new ArgumentException("fit takes the long edge in pixels, e.g. fit1600");
            else if (lower.Length > 1 && lower[0] == 'x' && int.TryParse(lower[1..], out int z))
                zoom = z is >= 1 and <= MaxZoom ? z : throw new ArgumentException($"zoom is x1 to x{MaxZoom}");
            else name ??= arg;
        }
        if (matte && jpg != null) throw new ArgumentException("matte needs a PNG; a JPG has no transparency");
        if (matte && marks) throw new ArgumentException("marks go on a plain shot, not a matte one");
        return new ShotOptions(name, matte, jpg, marks, fit, zoom);
    }

    // Matte shots fill the screen under our HUD and menu with this colour; null draws nothing.
    private static Color? s_backdrop;

    private static void SetHudBackdrop(Color? color) => s_backdrop = color;

    /// <summary>The dev bridge's HUD layer hook: drawn before the HUD layer's features.</summary>
    public static void DrawBackdrop()
    {
        if (s_backdrop is not Color color || Event.current.type != EventType.Repaint) return;
        Matrix4x4 matrix = GUI.matrix;
        Color old = GUI.color;
        GUI.matrix = Matrix4x4.identity;
        GUI.color = color;
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = old;
        GUI.matrix = matrix;
    }

    // shot full|menu|hud [options]; shot view [name]: the whole screen with marks, fitted, as a JPG; shot rect <x> <y>
    // <w> <h> [options]: an exact area; shot #n [options]: item n of the last see list; shot ui: DevCommands.UiShots.
    private static CommandResult ShotCommand(CommandCall call)
    {
        string target = call.Lower(1);
        if (target == "ui") return UiShot(call);
        if (target == "view")
        {
            var view = ParseShotOptions(call.Args.Skip(1).ToArray());
            return StartCapture(new Capture { Name = SafeName(view.Name ?? "view"), Jpg = view.Jpg ?? ViewJpg, Fit = view.Fit ?? ViewFit, Zoom = view.Zoom, Marks = new() });
        }
        if (target == "rect")
        {
            if (call.Args.Length < 5 || !float.TryParse(call.Args[1], out float x) || !float.TryParse(call.Args[2], out float y)
                || !float.TryParse(call.Args[3], out float w) || !float.TryParse(call.Args[4], out float h) || w < 1f || h < 1f)
                throw new ArgumentException("usage: shot rect <x> <y> <w> <h> [options], game-window pixels from the top left");
            // Matte works here when the area holds only the menu or the HUD layer (a HUD hover view beside the HUD's own
            // area): the backdrop goes under both, and anything of the game's in the area comes out solid.
            var area = ParseShotOptions(call.Args.Skip(5).ToArray());
            return StartCapture(new Capture { Name = SafeName(area.Name ?? "rect"), Crop = new Rect(x, y, w, h), Pad = 0f }.With(area));
        }
        if (target.StartsWith('#'))
        {
            if (!int.TryParse(target[1..], out int n) || n < 1 || n > s_seen.Count)
                throw new ArgumentException(s_seen.Count == 0 ? "shot #n needs a see list first" : $"{target} is not in the last list ({s_seenWhat}: #1 to #{s_seen.Count})");
            var item = ParseShotOptions(call.Args.Skip(1).ToArray());
            if (item.Matte) throw new ArgumentException("matte needs the menu, the HUD or game UI; use shot ui with the item's name");
            return StartCapture(new Capture { Name = SafeName(item.Name ?? $"item{n}"), Crop = s_seen[n - 1].Rect, Pad = ItemPad }.With(item));
        }

        var options = ParseShotOptions(call.Args.Skip(1).ToArray());
        if (options.Matte && target == "full") throw new ArgumentException("matte needs the menu, the HUD or game UI");
        Rect? crop = target switch
        {
            "menu" => Menu.Visible ? Menu.ScreenRect : throw new InvalidOperationException("the menu is closed"),
            "hud" => Hud.ScreenRect is { width: > 0 } r ? r : throw new InvalidOperationException("the HUD is not shown"),
            "full" => null,
            _ => throw new ArgumentException("shot target is full, view, menu, hud, ui, rect or #n"),
        };
        return StartCapture(new Capture { Name = SafeName(options.Name ?? target), Crop = crop }.With(options));
    }

    private static CommandResult StartCapture(Capture capture)
    {
        // The marks are what is on screen as the capture is taken; they become the see list, so #n matches the image.
        if (capture.Marks != null)
        {
            capture.Marks = Look(string.Empty);
            capture.Legend = Remember(capture.Marks, "shot marks");
        }
        Directory.CreateDirectory(ShotDir);
        capture.FullPath = Path.Combine(ShotDir, "_screen.png");
        capture.Deadline = Time.unscaledTime + ShotTimeoutSeconds;
        if (File.Exists(capture.FullPath)) File.Delete(capture.FullPath);
        if (capture.Matte) capture.Backdrop(Color.black);
        ScreenCapture.CaptureScreenshot(capture.FullPath, 1);
        return new CommandResult(string.Empty) { Pending = () => ContinueCapture(capture) };
    }

    // Unity writes the screenshot at the end of a later frame; wait until the file exists and stops growing.
    private static string? ContinueCapture(Capture capture)
    {
        if (Time.unscaledTime > capture.Deadline)
        {
            capture.Backdrop(null);
            return $"error shot {capture.Name}: the screenshot was not written";
        }
        if (!File.Exists(capture.FullPath)) return null;
        long size = new FileInfo(capture.FullPath).Length;
        if (size == 0 || size != capture.LastSize)
        {
            capture.LastSize = size;
            return null;
        }

        if (capture.Matte && capture.BlackPath == null)
        {
            capture.BlackPath = Path.Combine(ShotDir, "_black.png");
            File.Copy(capture.FullPath, capture.BlackPath, overwrite: true);
            File.Delete(capture.FullPath);
            capture.LastSize = -1;
            capture.Backdrop(Color.white);
            ScreenCapture.CaptureScreenshot(capture.FullPath, 1);
            return null;
        }

        try
        {
            string file = capture.Name + (capture.Jpg != null ? ".jpg" : ".png");
            string output = Path.Combine(ShotDir, file);
            Vector2Int dims = Develop(capture, output);
            var lines = new List<string> { $"shot {file} {dims.x}x{dims.y}{capture.Note}" };
            lines.AddRange(capture.Legend);
            return DevOutput.Reply("shot", lines);
        }
        catch (Exception e)
        {
            return $"error shot {capture.Name}: {e.Message}";
        }
        finally
        {
            capture.Backdrop(null);
            if (capture.BlackPath != null) File.Delete(capture.BlackPath);
        }
    }

    // The captured screen to the saved image: cut (or matte), marks, size, encode.
    private static Vector2Int Develop(Capture capture, string output)
    {
        bool plain = capture.Crop == null && capture.Marks == null && capture.Fit == null && capture.Zoom == 1 && capture.Jpg == null;
        Texture2D image;
        if (capture.BlackPath != null && capture.Crop is Rect matteCrop)
        {
            image = Matte(LoadTexture(capture.BlackPath), LoadTexture(capture.FullPath), matteCrop, capture.Pad);
        }
        else
        {
            image = Cut(LoadTexture(capture.FullPath), capture.Crop, capture.Pad, out Rect area);
            if (capture.Marks != null) DrawMarks(image, area, capture.Marks);
        }
        try
        {
            if (capture.Fit is int fit && Mathf.Max(image.width, image.height) > fit)
            {
                float k = fit / (float)Mathf.Max(image.width, image.height);
                image = Resized(image, Mathf.RoundToInt(image.width * k), Mathf.RoundToInt(image.height * k), FilterMode.Bilinear);
            }
            if (capture.Zoom > 1) image = Resized(image, image.width * capture.Zoom, image.height * capture.Zoom, FilterMode.Point);
            if (plain) File.Copy(capture.FullPath, output, overwrite: true);
            else Save(image, output, capture.Jpg);
            return new Vector2Int(image.width, image.height);
        }
        finally
        {
            UnityEngine.Object.Destroy(image);
        }
    }

    private static void Save(Texture2D texture, string output, int? jpg) =>
        File.WriteAllBytes(output, (jpg is int quality ? ImageConversion.EncodeToJPG(texture, quality) : ImageConversion.EncodeToPNG(texture)).ToArray());

    // The crop's pixels, padded, as a new texture (the screen texture is let go); no crop gives the screen itself. area is
    // the part of the screen the result covers, in screen pixels from the top left.
    private static Texture2D Cut(Texture2D screen, Rect? crop, float pad, out Rect area)
    {
        float sx = screen.width / (float)Screen.width, sy = screen.height / (float)Screen.height;
        if (crop is not Rect c)
        {
            area = new Rect(0f, 0f, Screen.width, Screen.height);
            return screen;
        }
        var (x, top, w, h) = PixelArea(screen, c, pad);
        int y = screen.height - top - h;
        var cut = new Texture2D(w, h, TextureFormat.RGBA32, false);
        cut.SetPixels(screen.GetPixels(x, y, w, h));
        cut.Apply();
        UnityEngine.Object.Destroy(screen);
        area = new Rect(x / sx, top / sy, w / sx, h / sy);
        return cut;
    }

    // crop is in screen pixels with a top-left origin; the result is in the texture's pixels, still from the top.
    private static (int X, int Top, int W, int H) PixelArea(Texture2D texture, Rect crop, float pad)
    {
        float sx = texture.width / (float)Screen.width, sy = texture.height / (float)Screen.height;
        int x = Mathf.Clamp(Mathf.FloorToInt((crop.x - pad) * sx), 0, texture.width - 1);
        int w = Mathf.Clamp(Mathf.CeilToInt((crop.xMax + pad) * sx) - x, 1, texture.width - x);
        int top = Mathf.Clamp(Mathf.FloorToInt((crop.y - pad) * sy), 0, texture.height - 1);
        int h = Mathf.Clamp(Mathf.CeilToInt((crop.yMax + pad) * sy) - top, 1, texture.height - top);
        return (x, top, w, h);
    }

    // Over black a pixel reads a·c, over white a·c + (1 − a): the difference gives alpha, the black capture the colour.
    // The result is trimmed to the pixels that were drawn; both captures are let go.
    private static Texture2D Matte(Texture2D black, Texture2D white, Rect crop, float pad)
    {
        try
        {
            if (black.width != white.width || black.height != white.height) throw new IOException("the two captures differ in size");
            var (x, top, w, h) = PixelArea(black, crop, pad);
            int y = black.height - top - h;

            var overBlack = black.GetPixels(x, y, w, h);
            var overWhite = white.GetPixels(x, y, w, h);
            var pixels = new Color[w * h];
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color b = overBlack[i], c = overWhite[i];
                float a = Mathf.Clamp01(1f - ((c.r - b.r) + (c.g - b.g) + (c.b - b.b)) / 3f);
                if (a < 0.5f / 255f) continue;
                pixels[i] = new Color(Mathf.Clamp01(b.r / a), Mathf.Clamp01(b.g / a), Mathf.Clamp01(b.b / a), a);
                int col = i % w, row = i / w;
                minX = Math.Min(minX, col); maxX = Math.Max(maxX, col);
                minY = Math.Min(minY, row); maxY = Math.Max(maxY, row);
            }
            if (maxX < 0) throw new IOException("nothing was drawn there");

            int tw = maxX - minX + 1, th = maxY - minY + 1;
            var trimmed = new Color[tw * th];
            for (int row = 0; row < th; row++) Array.Copy(pixels, (minY + row) * w + minX, trimmed, row * tw, tw);

            var cut = new Texture2D(tw, th, TextureFormat.RGBA32, false);
            cut.SetPixels(trimmed);
            cut.Apply();
            return cut;
        }
        finally
        {
            UnityEngine.Object.Destroy(black);
            UnityEngine.Object.Destroy(white);
        }
    }

    // A resized copy drawn on the GPU (the source is let go): Bilinear to shrink, Point to enlarge with square pixels.
    private static Texture2D Resized(Texture2D source, int w, int h, FilterMode filter)
    {
        w = Mathf.Max(1, w);
        h = Mathf.Max(1, h);
        var before = RenderTexture.active;
        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        try
        {
            source.filterMode = filter;
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;
            var result = new Texture2D(w, h, TextureFormat.RGBA32, false);
            result.ReadPixels(new Rect(0f, 0f, w, h), 0, 0, false);
            result.Apply();
            return result;
        }
        finally
        {
            RenderTexture.active = before;
            RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.Destroy(source);
        }
    }

    private static Texture2D LoadTexture(string path)
    {
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path))) throw new IOException("could not decode the screenshot");
        return texture;
    }

    private static string SafeName(string name)
    {
        var chars = name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray();
        return chars.Length > 0 ? new string(chars) : "shot";
    }
}
