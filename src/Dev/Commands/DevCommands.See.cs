using EpochAssist.Core;
using EpochAssist.Ui;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.EventSystems;

namespace EpochAssist.Dev;

internal static partial class DevCommands
{
    // ---- see: what is on screen, as text ----
    // see [game|ours|text|all] [filter]: numbers what is on screen, in game-window pixels from the top left: the game's
    // open windows (canvases that sort on their own), its clickable controls (with whether something covers them),
    // our menu and HUD and the menu's controls (by focus key); text adds the game's visible text. The list is kept, so
    // click #n, press #n, cursor #n and shot #n use its numbers; a long list goes whole to see.txt.

    private sealed record SeenItem(string Kind, string Name, string Text, Rect Rect, string Note)
    {
        public Vector2 Centre => Rect.center;
    }

    private sealed record SeenControl(Rect Rect, int Frame);

    private const float MinWindowWidth = 160f, MinWindowHeight = 120f;
    private const int MaxTextChars = 48, OursMaxFrames = 10;

    private static readonly Dictionary<string, SeenControl> s_ours = new();
    private static List<SeenItem> s_seen = new();
    private static string s_seenWhat = string.Empty;

    /// <summary>The menu's Focus hook: each control as it repaints, kept in screen pixels while it shows.</summary>
    public static void SeeControl(string key, Rect rect)
    {
        // A control scrolled out of its view still reports; only its visible part counts.
        Rect clip = GUIClip.visibleRect;
        Rect shown = Rect.MinMaxRect(Mathf.Max(rect.xMin, clip.xMin), Mathf.Max(rect.yMin, clip.yMin), Mathf.Min(rect.xMax, clip.xMax), Mathf.Min(rect.yMax, clip.yMax));
        if (shown.width < 2f || shown.height < 2f) return;
        // In the menu's window GUIToScreenPoint scales the point within the window but not the window's own position
        // (measured at UI scale 1.25: off by a quarter of it), and GUIToScreenRect keeps the size as drawn; both
        // corners, and the window's position scaled, give the box on screen.
        Vector2 min = GUIUtility.GUIToScreenPoint(shown.min), max = GUIUtility.GUIToScreenPoint(shown.max);
        float scale = GUI.matrix.m00;
        if (Menu.Visible && scale > 0f) min += Menu.ScreenRect.position * (1f - 1f / scale);
        if (Menu.Visible && scale > 0f) max += Menu.ScreenRect.position * (1f - 1f / scale);
        s_ours[key] = new SeenControl(Rect.MinMaxRect(min.x, min.y, max.x, max.y), Time.frameCount);
    }

    private static CommandResult SeeCommand(CommandCall call)
    {
        string mode = call.Lower(1) is "game" or "ours" or "text" or "all" ? call.Lower(1) : string.Empty;
        string filter = call.Rest(mode.Length > 0 ? 2 : 1);
        var items = Look(mode);
        if (filter.Length > 0)
        {
            items = items.Where(i => i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || i.Text.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || i.Kind.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        string what = $"see{(mode.Length > 0 ? " " + mode : string.Empty)}{(filter.Length > 0 ? " " + filter : string.Empty)}";
        var counts = items.GroupBy(i => i.Kind).Select(g => $"{g.Count()} {g.Key}");
        var reply = new List<string> { $"ok {what}: {(items.Count > 0 ? string.Join(", ", counts) : "nothing")} on {Screen.width}x{Screen.height}" };
        reply.AddRange(Remember(items, what));
        return new CommandResult(DevOutput.Reply("see", reply));
    }

    // Keeps the list for #n and gives its lines, indented.
    private static List<string> Remember(List<SeenItem> items, string what)
    {
        s_seen = items;
        s_seenWhat = what;
        return items.Select((item, i) => "  " + Describe(i + 1, item)).ToList();
    }

    private static string Describe(int n, SeenItem item)
    {
        Rect r = item.Rect;
        string text = item.Text.Length > 0 ? $" \"{item.Text}\"" : string.Empty;
        string note = item.Note.Length > 0 ? $" [{item.Note}]" : string.Empty;
        return $"#{n} {item.Kind}{text} at {item.Centre.x:F0},{item.Centre.y:F0} box {r.x:F0},{r.y:F0} {r.width:F0}x{r.height:F0} {item.Name}{note}";
    }

    // Default: windows, controls, ours. game: windows and controls. ours: ours. text: game text. all: everything.
    private static List<SeenItem> Look(string mode)
    {
        var items = new List<SeenItem>();
        if (mode is "" or "game" or "all") items.AddRange(GameWindows());
        if (mode is "" or "game" or "all") items.AddRange(GameControls());
        if (mode is "" or "ours" or "all") items.AddRange(OurUi());
        if (mode is "text" or "all") items.AddRange(GameText());
        return items;
    }

    private static IEnumerable<SeenItem> GameWindows()
    {
        var found = new List<SeenItem>();
        var screen = new Rect(0f, 0f, Screen.width, Screen.height);
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Canvas>(), false))
        {
            var canvas = obj.TryCast<Canvas>();
            if (!Game.Alive(canvas) || !canvas!.isActiveAndEnabled || !(canvas.isRootCanvas || canvas.overrideSorting)) continue;
            if (canvas.rootCanvas.renderMode == RenderMode.WorldSpace || canvas.transform.TryCast<RectTransform>() is not { } rt) continue;
            if (DrawnArea(rt, strict: true) is not Rect drawn || Clip(drawn, screen) is var area && (area.width < MinWindowWidth || area.height < MinWindowHeight)) continue;
            found.Add(new SeenItem("window", ShortPath(canvas.transform), string.Empty, area, string.Empty));
        }
        return found.OrderByDescending(w => w.Rect.width * w.Rect.height);
    }

    private static IEnumerable<SeenItem> GameControls()
    {
        var found = new List<SeenItem>();
        var screen = new Rect(0f, 0f, Screen.width, Screen.height);
        var all = UnityEngine.UI.Selectable.allSelectablesArray;
        for (int i = 0; i < all.Length; i++)
        {
            var s = all[i];
            if (!Game.Alive(s) || !s.isActiveAndEnabled || !s.IsInteractable() || s.transform.TryCast<RectTransform>() is not { } rt) continue;
            var canvas = s.GetComponentInParent<Canvas>();
            if (!Game.Alive(canvas) || canvas!.rootCanvas.renderMode == RenderMode.WorldSpace) continue;
            var graphic = s.targetGraphic;
            if (Game.Alive(graphic) && graphic!.canvasRenderer.GetInheritedAlpha() <= 0.01f) continue;
            Rect r = Clip(ScreenRect(rt), screen);
            if (r.width < 2f || r.height < 2f) continue;
            found.Add(new SeenItem(ControlKind(s), ShortPath(s.transform), TextUnder(s.transform), r, Cover(s.transform, r.center)));
        }
        return found.OrderBy(c => c.Rect.y).ThenBy(c => c.Rect.x);
    }

    private static IEnumerable<SeenItem> OurUi()
    {
        var found = new List<SeenItem>();
        if (Menu.Visible && Menu.ScreenRect is { width: > 0f } menu) found.Add(new SeenItem("ours", "menu", string.Empty, menu, string.Empty));
        if (Hud.ScreenRect is { width: > 0f } hud) found.Add(new SeenItem("ours", "mini HUD", string.Empty, hud, string.Empty));
        int now = Time.frameCount;
        foreach (string key in s_ours.Where(kv => now - kv.Value.Frame > OursMaxFrames).Select(kv => kv.Key).ToList()) s_ours.Remove(key);
        if (!Menu.Visible) return found;
        found.AddRange(s_ours.OrderBy(kv => kv.Value.Rect.y).ThenBy(kv => kv.Value.Rect.x)
            .Select(kv => new SeenItem("control", kv.Key, string.Empty, kv.Value.Rect, string.Empty)));
        return found;
    }

    private static IEnumerable<SeenItem> GameText()
    {
        var found = new List<SeenItem>();
        var screen = new Rect(0f, 0f, Screen.width, Screen.height);
        foreach (var type in new[] { Il2CppType.Of<Il2CppTMPro.TMP_Text>(), Il2CppType.Of<UnityEngine.UI.Text>() })
        {
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(type, false))
            {
                var graphic = obj.TryCast<UnityEngine.UI.Graphic>();
                if (!Game.Alive(graphic) || !graphic!.isActiveAndEnabled || graphic.color.a <= 0f || graphic.canvasRenderer.GetInheritedAlpha() <= 0.01f) continue;
                var canvas = graphic.canvas;
                if (!Game.Alive(canvas) || canvas!.rootCanvas.renderMode == RenderMode.WorldSpace) continue;
                string text = Flatten(graphic.TryCast<Il2CppTMPro.TMP_Text>()?.text ?? graphic.TryCast<UnityEngine.UI.Text>()?.text);
                if (text.Length == 0) continue;
                Rect r = Clip(ScreenRect(graphic.rectTransform), screen);
                if (r.width < 2f || r.height < 2f) continue;
                found.Add(new SeenItem("text", ShortPath(graphic.transform), text, r, string.Empty));
            }
        }
        return found.OrderBy(t => t.Rect.y).ThenBy(t => t.Rect.x);
    }

    private static string ControlKind(UnityEngine.UI.Selectable s) =>
        s.TryCast<UnityEngine.UI.Toggle>() != null ? "toggle"
        : s.TryCast<UnityEngine.UI.Slider>() != null ? "slider"
        : s.TryCast<UnityEngine.UI.Scrollbar>() != null ? "scrollbar"
        : s.TryCast<Il2CppTMPro.TMP_InputField>() != null || s.TryCast<UnityEngine.UI.InputField>() != null ? "field"
        : s.TryCast<Il2CppTMPro.TMP_Dropdown>() != null || s.TryCast<UnityEngine.UI.Dropdown>() != null ? "dropdown"
        : "button";

    // What a click at the control's centre would reach first: nothing in the way, our menu or HUD, or another object.
    private static string Cover(Transform control, Vector2 centre)
    {
        if (Menu.Visible && Menu.ScreenRect.Contains(centre)) return "under our menu";
        if (InputGate.HudHitTest?.Invoke(centre) == true) return "under our HUD";
        var events = EventSystem.current;
        if (events == null) return string.Empty;
        var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
        events.RaycastAll(new PointerEventData(events) { position = new Vector2(centre.x, Screen.height - centre.y) }, results);
        for (int i = 0; i < results.Count; i++)
        {
            var hit = results[i].gameObject;
            if (hit == null || hit.transform.TryCast<RectTransform>() == null) continue;
            return hit.transform.IsChildOf(control) ? string.Empty : $"covered by {ShortPath(hit.transform)}";
        }
        return "no raycast hit";
    }

    private static string TextUnder(Transform t)
    {
        foreach (var tmp in t.GetComponentsInChildren<Il2CppTMPro.TMP_Text>(false))
        {
            if (Game.Alive(tmp) && Flatten(tmp.text) is { Length: > 0 } s) return s;
        }
        foreach (var text in t.GetComponentsInChildren<UnityEngine.UI.Text>(false))
        {
            if (Game.Alive(text) && Flatten(text.text) is { Length: > 0 } s) return s;
        }
        return string.Empty;
    }

    // One line, rich-text tags out, cut short.
    private static string Flatten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string plain = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]*>", string.Empty);
        plain = string.Join(' ', plain.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return plain.Length > MaxTextChars ? plain[..(MaxTextChars - 1)] + "…" : plain;
    }

    // The object's name with up to two parents, enough to tell it apart and to use as a shot ui target.
    private static string ShortPath(Transform t)
    {
        string path = t.name;
        var p = t.parent;
        for (int i = 0; i < 2 && p != null; i++, p = p.parent) path = p.name + "/" + path;
        return path;
    }

    private static Rect Clip(Rect r, Rect to) =>
        Rect.MinMaxRect(Mathf.Max(r.xMin, to.xMin), Mathf.Max(r.yMin, to.yMin), Mathf.Min(r.xMax, to.xMax), Mathf.Min(r.yMax, to.yMax));

    /// <summary>
    /// For the pointer commands: each "#n" argument becomes the centre of item n of the last see list ("x y"). Error
    /// when a number has no item.
    /// </summary>
    public static CommandCall Marked(CommandCall call, out string? error)
    {
        error = null;
        if (!call.Args.Any(a => a.StartsWith('#'))) return call;
        var parts = new List<string> { call.Name };
        foreach (string arg in call.Args)
        {
            if (!arg.StartsWith('#'))
            {
                parts.Add(arg);
                continue;
            }
            if (!int.TryParse(arg[1..], out int n) || n < 1 || n > s_seen.Count)
            {
                error = s_seen.Count == 0 ? $"{call.Name}: {arg} needs a see list first" : $"{call.Name}: {arg} is not in the last list ({s_seenWhat}: #1 to #{s_seen.Count})";
                return call;
            }
            Vector2 c = s_seen[n - 1].Centre;
            parts.Add($"{Mathf.RoundToInt(c.x)} {Mathf.RoundToInt(c.y)}");
        }
        return new CommandCall(string.Join(' ', parts), call.Source);
    }
}
