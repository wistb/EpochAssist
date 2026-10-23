using EpochAssist.Core;
using MelonLoader.Utils;
using UnityEngine;

namespace EpochAssist.Ui;

/// <summary>
/// Dev builds only: runs commands written to UserData/EpochAssist/dev/commands.txt (one per line, one per frame)
/// and reports to result.txt, ending with "done". tools/shot.sh drives it.
///   menu open autocast|potion|settings [tab]   menu close   menu search [text]
///   hud panel open|close   set [setting id] [value]   probe [type] [filter]   find [text]   fonts   wait [frames]   shot full|menu|hud [name]
/// Screenshots: Unity saves the full screen, then the mod crops it and writes shots/[name].png.
/// </summary>
internal static class DevBridge
{
    private const float PollSeconds = 0.25f;
    private const float ShotTimeoutSeconds = 5f;
    private const int SettleFrames = 2;

    private static readonly Queue<string> s_queue = new();
    private static readonly List<string> s_results = new();
    private static float s_nextPollAt;
    private static int s_waitFrames;
    private static Capture? s_capture;
    private static bool s_batchActive;

    private sealed class Capture
    {
        public string Name = string.Empty;
        public string FullPath = string.Empty;
        public Rect? Crop;
        public float Deadline;
        public long LastSize = -1;
    }

    private static string Dir => Path.Combine(MelonEnvironment.UserDataDirectory, "EpochAssist", "dev");
    private static string ShotDir => Path.Combine(Dir, "shots");

    public static void Update()
    {
        if (s_capture != null)
        {
            ContinueCapture(s_capture);
            return;
        }
        if (s_waitFrames > 0)
        {
            s_waitFrames--;
            return;
        }
        if (s_queue.Count > 0)
        {
            string command = s_queue.Dequeue();
            try
            {
                Execute(command);
            }
            catch (Exception e)
            {
                Report($"error {command}: {e.Message}");
            }
            return;
        }
        // The batch is over once its last command, capture and settle frames are all through.
        if (s_batchActive)
        {
            Finish();
            return;
        }
        if (Time.unscaledTime < s_nextPollAt) return;
        s_nextPollAt = Time.unscaledTime + PollSeconds;
        Poll();
    }

    public static void Reset()
    {
        s_queue.Clear();
        s_results.Clear();
        s_capture = null;
        s_waitFrames = 0;
        s_batchActive = false;
    }

    private static void Poll()
    {
        string file = Path.Combine(Dir, "commands.txt");
        if (!File.Exists(file)) return;
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
            File.Delete(file);
        }
        catch (IOException)
        {
            return;
        }
        s_results.Clear();
        foreach (string line in lines)
        {
            if (line.Trim().Length > 0) s_queue.Enqueue(line.Trim());
        }
        s_batchActive = true;
    }

    private static void Execute(string command)
    {
        string[] p = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = p[0].ToLowerInvariant();
        string Arg(int i) => i < p.Length ? p[i].ToLowerInvariant() : string.Empty;

        switch (verb)
        {
            case "menu" when Arg(1) == "open":
                Menu.Open(Arg(2) switch { "potion" => Page.Potion, "settings" => Page.Settings, _ => Page.AutoCast });
                if (int.TryParse(Arg(3), out int tab)) Menu.SetTab(tab);
                s_waitFrames = SettleFrames;
                Report($"ok {command}");
                break;
            case "menu" when Arg(1) == "close":
                Menu.Close();
                s_waitFrames = SettleFrames;
                Report($"ok {command}");
                break;
            case "menu" when Arg(1) == "search":
                Menu.SetSearch(string.Join(' ', p.Skip(2)));
                s_waitFrames = SettleFrames;
                Report($"ok {command}");
                break;
            case "hud" when Arg(1) == "panel":
                Hud.SetPanel(Arg(2) == "open");
                s_waitFrames = SettleFrames;
                Report($"ok {command}");
                break;
            case "set":
                Report(Set(Arg(1), p.Length > 2 ? p[2] : string.Empty) ? $"ok {command}" : $"error {command}: no such setting or bad value");
                s_waitFrames = SettleFrames;
                break;
            case "probe":
                Report(Probe(p.Length > 1 ? p[1] : string.Empty, p.Length > 2 ? p[2] : null));
                break;
            case "find":
                Report(Find(p.Length > 1 ? p[1] : string.Empty));
                break;
            case "fonts":
                var fonts = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Font>());
                Report($"ok fonts: {string.Join(", ", fonts.Select(f => f.name).Distinct())}");
                break;
            case "wait":
                s_waitFrames = int.TryParse(Arg(1), out int frames) ? frames : 30;
                Report($"ok {command}");
                break;
            case "shot":
                StartCapture(Arg(1), p.Length > 2 ? p[2] : Arg(1));
                break;
            default:
                Report($"error unknown command: {command}");
                break;
        }
    }

    // probe <type> [filter]: the type's declared methods, fields and properties, from the game's interop assemblies.
    private static string Probe(string typeName, string? filter)
    {
        Type? type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(typeName) ?? a.GetType("Il2Cpp." + typeName))
            .FirstOrDefault(t => t != null);
        if (type == null) return $"error probe: no type {typeName}";

        const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;
        var members = type.GetMethods(all).Where(m => !m.IsSpecialName)
            .Select(m => $"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(x => x.ParameterType.Name))})")
            .Concat(type.GetProperties(all).Select(x => $"prop {x.PropertyType.Name} {x.Name}"))
            .Where(m => filter == null || m.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Distinct().OrderBy(m => m).ToList();

        Directory.CreateDirectory(ShotDir);
        File.WriteAllLines(Path.Combine(Dir, "probe.txt"), members.Prepend(type.FullName ?? typeName));
        return $"ok probe {type.FullName}: {members.Count} members in probe.txt";
    }

    // find <text>: Type.Member for every method in the game's Il2Cpp assemblies whose name contains the text.
    private static string Find(string text)
    {
        if (text.Length < 3) return "error find: give at least 3 characters";
        var hits = new List<string>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name?.StartsWith("Il2Cpp") == true))
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
            foreach (var type in types)
            {
                foreach (var m in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (!m.Name.StartsWith("NativeMethodInfoPtr") && m.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
                        hits.Add($"{assembly.GetName().Name}: {type.FullName}.{m.Name}");
                }
            }
        }
        Directory.CreateDirectory(Dir);
        File.WriteAllLines(Path.Combine(Dir, "find.txt"), hits.Distinct().OrderBy(h => h));
        return $"ok find {text}: {hits.Count} methods in find.txt";
    }

    // set <id> <value>, where id is a setting's Id, optionally prefixed by its group id and a dot.
    private static bool Set(string id, string value)
    {
        var setting = SettingsRegistry.Groups.SelectMany(g => g.All)
            .FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                || $"{s.Section.Group.Id}.{s.Id}".Equals(id, StringComparison.OrdinalIgnoreCase));
        switch (setting)
        {
            case Toggle t when bool.TryParse(value, out bool b): t.Value = b; return true;
            case Number n when float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f): n.Value = n.Snap(f); return true;
            case Choice<int> c when int.TryParse(value, out int i): c.Value = i; return true;
            case Key k when Enum.TryParse(value, true, out KeyCode key): k.Value = key; return true;
            case Choice<KeyCode> c when Enum.TryParse(value, true, out KeyCode key): c.Value = key; return true;
            default: return false;
        }
    }

    private static void StartCapture(string target, string name)
    {
        Rect? crop = target switch
        {
            "menu" => Menu.Visible ? Menu.ScreenRect : throw new InvalidOperationException("the menu is closed"),
            "hud" => Hud.ScreenRect is { width: > 0 } r ? r : throw new InvalidOperationException("the HUD is not shown"),
            "full" => null,
            _ => throw new ArgumentException("shot target is full, menu or hud"),
        };

        Directory.CreateDirectory(ShotDir);
        string full = Path.Combine(ShotDir, "_screen.png");
        if (File.Exists(full)) File.Delete(full);
        ScreenCapture.CaptureScreenshot(full, 1);
        s_capture = new Capture { Name = SafeName(name), FullPath = full, Crop = crop, Deadline = Time.unscaledTime + ShotTimeoutSeconds };
    }

    // Unity writes the screenshot at the end of a later frame; wait until the file exists and stops growing.
    private static void ContinueCapture(Capture capture)
    {
        if (Time.unscaledTime > capture.Deadline)
        {
            s_capture = null;
            Report($"error shot {capture.Name}: the screenshot was not written");
            return;
        }
        if (!File.Exists(capture.FullPath)) return;
        long size = new FileInfo(capture.FullPath).Length;
        if (size == 0 || size != capture.LastSize)
        {
            capture.LastSize = size;
            return;
        }

        s_capture = null;
        try
        {
            string output = Path.Combine(ShotDir, capture.Name + ".png");
            Vector2Int dims = capture.Crop is Rect crop ? CropTo(capture.FullPath, crop, output) : CopyFull(capture.FullPath, output);
            Report($"shot {capture.Name}.png {dims.x}x{dims.y}");
        }
        catch (Exception e)
        {
            Report($"error shot {capture.Name}: {e.Message}");
        }
    }

    private static Vector2Int CopyFull(string full, string output)
    {
        File.Copy(full, output, overwrite: true);
        var probe = LoadTexture(full);
        var dims = new Vector2Int(probe.width, probe.height);
        UnityEngine.Object.Destroy(probe);
        return dims;
    }

    // crop is in screen pixels with a top-left origin; textures count rows from the bottom.
    private static Vector2Int CropTo(string full, Rect crop, string output)
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
            File.WriteAllBytes(output, ImageConversion.EncodeToPNG(cut).ToArray());
            UnityEngine.Object.Destroy(cut);
            return new Vector2Int(w, h);
        }
        finally
        {
            UnityEngine.Object.Destroy(screen);
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

    private static void Report(string line)
    {
        s_results.Add(line);
        Log.Info("dev: " + line);
    }

    private static void Finish()
    {
        s_batchActive = false;
        s_results.Add("done");
        try
        {
            File.WriteAllLines(Path.Combine(Dir, "result.txt"), s_results);
        }
        catch (IOException e)
        {
            Log.Warn($"dev: could not write result.txt: {e.Message}");
        }
        s_results.Clear();
    }
}

internal sealed class DevBridgeFeature : Feature
{
    public override string Id => "dev";
    public override string Name => "Dev bridge";

    public override void Update() => DevBridge.Update();
    public override void Shutdown() => DevBridge.Reset();
}
