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
        public float Deadline;
        public long LastSize = -1;
        public bool Matte;
        // Matte shots: the first capture (over black) waits here while the second (over white) is taken.
        public string? BlackPath;
        // Null saves a PNG; otherwise a JPG at this quality (1-100).
        public int? Jpg;
        // Matte shots: puts the backdrop colour under what is captured; null takes it away.
        public Action<Color?> Backdrop = SetHudBackdrop;
        // Added to the reply, e.g. that our HUD covers part of the shot.
        public string Note = string.Empty;
    }

    // What follows the target in a shot command: [name] [matte] [jpg|jpg<quality>], the flags in any order.
    private sealed record ShotOptions(string? Name, bool Matte, int? Jpg);

    private const int JpgQuality = 90;

    private static ShotOptions ParseShotOptions(string[] args)
    {
        string? name = null;
        bool matte = false;
        int? jpg = null;
        foreach (string arg in args)
        {
            string lower = arg.ToLowerInvariant();
            if (lower == "matte") matte = true;
            else if (lower.StartsWith("jpg", StringComparison.Ordinal))
                jpg = lower.Length == 3 ? JpgQuality : int.TryParse(lower[3..], out int q) && q is >= 1 and <= 100 ? q : throw new ArgumentException("jpg quality is 1 to 100, e.g. jpg85");
            else name ??= arg;
        }
        if (matte && jpg != null) throw new ArgumentException("matte needs a PNG; a JPG has no transparency");
        return new ShotOptions(name, matte, jpg);
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

    // shot full|menu|hud [name] [matte] [jpg]
    private static CommandResult ShotCommand(CommandCall call)
    {
        string target = call.Lower(1);
        if (target == "ui") return UiShot(call);
        var options = ParseShotOptions(call.Args.Skip(1).ToArray());
        if (options.Matte && target == "full") throw new ArgumentException("matte needs the menu, the HUD or game UI");
        Rect? crop = target switch
        {
            "menu" => Menu.Visible ? Menu.ScreenRect : throw new InvalidOperationException("the menu is closed"),
            "hud" => Hud.ScreenRect is { width: > 0 } r ? r : throw new InvalidOperationException("the HUD is not shown"),
            "full" => null,
            _ => throw new ArgumentException("shot target is full, menu, hud or ui"),
        };
        return StartCapture(new Capture { Name = SafeName(options.Name ?? target), Crop = crop, Matte = options.Matte, Jpg = options.Jpg });
    }

    private static CommandResult StartCapture(Capture capture)
    {
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
            Vector2Int dims = capture.BlackPath != null && capture.Crop is Rect matteCrop
                ? MatteTo(capture.BlackPath, capture.FullPath, matteCrop, output)
                : capture.Crop is Rect crop ? CropTo(capture.FullPath, crop, output, capture.Jpg) : CopyFull(capture.FullPath, output, capture.Jpg);
            return $"shot {file} {dims.x}x{dims.y}{capture.Note}";
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

    private static void Save(Texture2D texture, string output, int? jpg) =>
        File.WriteAllBytes(output, (jpg is int quality ? ImageConversion.EncodeToJPG(texture, quality) : ImageConversion.EncodeToPNG(texture)).ToArray());

    private static Vector2Int CopyFull(string full, string output, int? jpg)
    {
        var probe = LoadTexture(full);
        var dims = new Vector2Int(probe.width, probe.height);
        if (jpg != null) Save(probe, output, jpg);
        else File.Copy(full, output, overwrite: true);
        UnityEngine.Object.Destroy(probe);
        return dims;
    }

    // crop is in screen pixels with a top-left origin; textures count rows from the bottom.
    private static Vector2Int CropTo(string full, Rect crop, string output, int? jpg)
    {
        Texture2D screen = LoadTexture(full);
        try
        {
            float sx = screen.width / (float)Screen.width, sy = screen.height / (float)Screen.height;
            int x = Mathf.Clamp(Mathf.FloorToInt((crop.x - 4f) * sx), 0, screen.width - 1);
            int w = Mathf.Clamp(Mathf.CeilToInt((crop.width + 8f) * sx), 1, screen.width - x);
            int top = Mathf.Clamp(Mathf.FloorToInt((crop.y - 4f) * sy), 0, screen.height - 1);
            int h = Mathf.Clamp(Mathf.CeilToInt((crop.height + 8f) * sy), 1, screen.height - top);
            int y = screen.height - top - h;

            var cut = new Texture2D(w, h, TextureFormat.RGBA32, false);
            cut.SetPixels(screen.GetPixels(x, y, w, h));
            cut.Apply();
            Save(cut, output, jpg);
            UnityEngine.Object.Destroy(cut);
            return new Vector2Int(w, h);
        }
        finally
        {
            UnityEngine.Object.Destroy(screen);
        }
    }

    // Over black a pixel reads a·c, over white a·c + (1 − a): the difference gives alpha, the black capture the colour.
    // The result is trimmed to the pixels that were drawn.
    private static Vector2Int MatteTo(string blackPath, string whitePath, Rect crop, string output)
    {
        Texture2D black = LoadTexture(blackPath), white = LoadTexture(whitePath);
        try
        {
            if (black.width != white.width || black.height != white.height) throw new IOException("the two captures differ in size");
            float sx = black.width / (float)Screen.width, sy = black.height / (float)Screen.height;
            int x = Mathf.Clamp(Mathf.FloorToInt((crop.x - 4f) * sx), 0, black.width - 1);
            int w = Mathf.Clamp(Mathf.CeilToInt((crop.width + 8f) * sx), 1, black.width - x);
            int top = Mathf.Clamp(Mathf.FloorToInt((crop.y - 4f) * sy), 0, black.height - 1);
            int h = Mathf.Clamp(Mathf.CeilToInt((crop.height + 8f) * sy), 1, black.height - top);
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
            File.WriteAllBytes(output, ImageConversion.EncodeToPNG(cut).ToArray());
            UnityEngine.Object.Destroy(cut);
            return new Vector2Int(tw, th);
        }
        finally
        {
            UnityEngine.Object.Destroy(black);
            UnityEngine.Object.Destroy(white);
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
