using EpochAssist.Core;
using EpochAssist.Ui;
using System.Text.RegularExpressions;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace EpochAssist.Dev;

internal static partial class DevCommands
{
    // ---- Game UI shots ----
    // shot ui <target> [name] [matte] [jpg]: crops to what one piece of the game's UI draws. The target is tooltip (the
    // shown tooltips), hovered (the UI under the cursor) or an object's name or path end, e.g. Content or
    // ItemTooltip/Content, with #n for the nth match (from 1); quote it when it has spaces, and " (Clone)" may be left off. The crop is the union of the object's drawn graphics,
    // so empty layout space is left out and children that hang outside it are kept. Matte puts a black, then a white
    // canvas just under the target's canvas, which leaves out the world and lower UI.

    private const string TooltipCanvasName = "Tooltip", TooltipContentName = "Content", CloneSuffix = " (Clone)";

    private static readonly Il2CppStructArray<Vector3> s_uiCorners = new(4);

    private static CommandResult UiShot(CommandCall call)
    {
        string[] args = QuotedArgs(call.Rest(2));
        string spec = args.Length > 0 ? args[0] : throw new ArgumentException("shot ui needs a target: tooltip, hovered or an object's name or path");
        var options = ParseShotOptions(args.Skip(1).ToArray());
        var roots = UiTargets(spec);
        if (roots.Count == 0) throw new InvalidOperationException($"no game UI on screen matches {spec}");

        Rect? area = null;
        foreach (var root in roots) area = Union(area, DrawnArea(root));
        var screen = new Rect(0f, 0f, Screen.width, Screen.height);
        if (area is not Rect r || !r.Overlaps(screen)) throw new InvalidOperationException($"{spec} draws nothing on screen");
        Rect crop = Rect.MinMaxRect(Mathf.Max(r.xMin, 0f), Mathf.Max(r.yMin, 0f), Mathf.Min(r.xMax, Screen.width), Mathf.Min(r.yMax, Screen.height));

        string name = options.Name ?? (spec is "tooltip" or "hovered" ? spec : roots[0].name);
        var capture = new Capture { Name = SafeName(name), Crop = crop, Matte = options.Matte, Jpg = options.Jpg, Note = OverlapNote(crop) };
        if (options.Matte) capture.Backdrop = UiBackdrop(roots);
        return StartCapture(capture);
    }

    private static List<RectTransform> UiTargets(string spec)
    {
        if (spec.Equals("tooltip", StringComparison.OrdinalIgnoreCase)) return ShownTooltips();
        if (spec.Equals("hovered", StringComparison.OrdinalIgnoreCase))
        {
            Hovered.Run();
            var hit = Hovered.LastHit?.TryCast<GameObject>();
            return Game.Alive(hit) && hit!.transform.TryCast<RectTransform>() is { } rt ? new() { rt } : new();
        }

        int nth = 1;
        int hash = spec.LastIndexOf('#');
        if (hash > 0 && int.TryParse(spec[(hash + 1)..], out int n) && n >= 1)
        {
            nth = n;
            spec = spec[..hash];
        }
        var screen = new Rect(0f, 0f, Screen.width, Screen.height);
        var matches = new List<RectTransform>();
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<RectTransform>(), false))
        {
            var rt = obj.TryCast<RectTransform>();
            if (!Game.Alive(rt) || !PathMatches(rt!, spec)) continue;
            // World-space canvases (name plates, labels) have no fixed screen rect.
            var canvas = rt!.GetComponentInParent<Canvas>();
            if (!Game.Alive(canvas) || canvas!.rootCanvas.renderMode == RenderMode.WorldSpace) continue;
            if (DrawnArea(rt) is Rect r && r.Overlaps(screen)) matches.Add(rt);
        }
        matches.Sort((a, b) => string.CompareOrdinal(GameTypes.PathOf(a.gameObject), GameTypes.PathOf(b.gameObject)));
        return matches.Count >= nth ? new() { matches[nth - 1] } : new();
    }

    // Words split at spaces, except inside double quotes.
    private static string[] QuotedArgs(string text) =>
        Regex.Matches(text, "\"([^\"]*)\"|(\\S+)").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToArray();

    // The spec is the end of the object's path: its name, or parent/name and so on.
    private static bool PathMatches(Transform t, string spec)
    {
        string[] parts = spec.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = parts.Length - 1; i >= 0; i--, t = t.parent)
        {
            if (t == null || !NameMatches(t.name, parts[i])) return false;
        }
        return true;
    }

    private static bool NameMatches(string name, string part) =>
        name.Equals(part, StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(CloneSuffix, StringComparison.Ordinal) && name[..^CloneSuffix.Length].Equals(part, StringComparison.OrdinalIgnoreCase);

    // The game parks a hidden tooltip's Content off screen instead of switching it off (see GameTooltips).
    private static List<RectTransform> ShownTooltips()
    {
        var shown = new List<RectTransform>();
        var screen = new Rect(0f, 0f, Screen.width, Screen.height);
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Canvas>(), false))
        {
            var canvas = obj.TryCast<Canvas>();
            if (!Game.Alive(canvas) || !canvas!.name.Contains(TooltipCanvasName, StringComparison.Ordinal)) continue;
            if (canvas.transform.Find(TooltipContentName)?.TryCast<RectTransform>() is { } content && content.gameObject.activeInHierarchy
                && ScreenRect(content) is var r && r.width >= 1f && r.Overlaps(screen)) shown.Add(content);
        }
        return shown;
    }

    // The union of the screen rects of the visible graphics at or under root; root's own rect when it draws none.
    private static Rect? DrawnArea(RectTransform root)
    {
        Rect? area = null;
        foreach (var graphic in root.GetComponentsInChildren<UnityEngine.UI.Graphic>(false))
        {
            if (!Game.Alive(graphic) || !graphic.enabled || graphic.color.a <= 0f || graphic.canvasRenderer.GetAlpha() <= 0f) continue;
            var r = ScreenRect(graphic.rectTransform);
            if (r.width >= 1f && r.height >= 1f) area = Union(area, r);
        }
        return area ?? (ScreenRect(root) is { width: >= 1f, height: >= 1f } own ? own : null);
    }

    // Screen pixels, top-left origin, through the canvas's camera when it has one.
    private static Rect ScreenRect(RectTransform rt)
    {
        rt.GetWorldCorners(s_uiCorners);
        var canvas = rt.GetComponentInParent<Canvas>();
        var root = Game.Alive(canvas) ? canvas!.rootCanvas : null;
        Camera? camera = Game.Alive(root) && root!.renderMode != RenderMode.ScreenSpaceOverlay ? root.worldCamera : null;
        Vector2 low = RectTransformUtility.WorldToScreenPoint(camera, s_uiCorners[0]), high = RectTransformUtility.WorldToScreenPoint(camera, s_uiCorners[2]);
        return Rect.MinMaxRect(low.x, Screen.height - high.y, high.x, Screen.height - low.y);
    }

    private static Rect Union(Rect? a, Rect? b) => (a, b) switch
    {
        (Rect x, Rect y) => Rect.MinMaxRect(Mathf.Min(x.xMin, y.xMin), Mathf.Min(x.yMin, y.yMin), Mathf.Max(x.xMax, y.xMax), Mathf.Max(x.yMax, y.yMax)),
        (Rect x, null) => x,
        (null, Rect y) => y,
        _ => Rect.zero,
    };

    // Our IMGUI draws over every canvas, so a crop it covers would include it.
    private static string OverlapNote(Rect crop)
    {
        var over = new List<string>();
        if (Menu.Visible && Menu.ScreenRect.Overlaps(crop)) over.Add("our menu");
        if (Hud.ScreenRect is { width: > 0f } hud && hud.Overlaps(crop)) over.Add("our HUD");
        return over.Count > 0 ? $", {string.Join(" and ", over)} covers part of it" : string.Empty;
    }

    // A full-screen canvas sorted just under the lowest target canvas: it covers the world and lower UI, not the target.
    private static Action<Color?> UiBackdrop(List<RectTransform> roots)
    {
        Canvas? lowest = null;
        foreach (var root in roots)
        {
            var sorting = SortingCanvas(root);
            if (Game.Alive(sorting) && (lowest == null || sorting!.sortingOrder < lowest.sortingOrder)) lowest = sorting;
        }
        if (lowest == null) throw new InvalidOperationException("the target is on no canvas");

        GameObject? backdrop = null;
        UnityEngine.UI.Image? image = null;
        return color =>
        {
            if (color is not Color c)
            {
                if (Game.Alive(backdrop)) UnityEngine.Object.Destroy(backdrop);
                backdrop = null;
                return;
            }
            if (!Game.Alive(backdrop))
            {
                backdrop = new GameObject("EpochAssist dev shot backdrop");
                var canvas = backdrop.AddComponent<Canvas>();
                var top = lowest.rootCanvas;
                canvas.renderMode = top.renderMode;
                if (top.renderMode == RenderMode.ScreenSpaceCamera)
                {
                    canvas.worldCamera = top.worldCamera;
                    canvas.planeDistance = top.planeDistance;
                }
                canvas.overrideSorting = true;
                canvas.sortingLayerID = lowest.sortingLayerID;
                canvas.sortingOrder = lowest.sortingOrder - 1;

                var fill = new GameObject("fill");
                fill.transform.SetParent(backdrop.transform, false);
                image = fill.AddComponent<UnityEngine.UI.Image>();
                image.raycastTarget = false;
                var rt = image.rectTransform;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
            }
            image!.color = c;
        };
    }

    // The canvas whose sorting order places the target: the nearest one that overrides sorting, else the root.
    private static Canvas? SortingCanvas(Transform t)
    {
        for (var canvas = t.GetComponentInParent<Canvas>(); Game.Alive(canvas); canvas = canvas!.transform.parent?.GetComponentInParent<Canvas>())
        {
            if (canvas!.isRootCanvas || canvas.overrideSorting) return canvas;
        }
        return null;
    }
}
