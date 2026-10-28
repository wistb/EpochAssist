using EpochAssist.Core;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace EpochAssist.Ui;

/// <summary>
/// Where the game's own tooltip is on screen, so a hint beside it doesn't cover it. The game keeps each tooltip (tree
/// node, skill, item, the rest) as a canvas named for it, with its panel in a child "Content" that it parks far off screen
/// when hidden rather than switching it off: a tooltip shows when its Content is on screen. The item tooltip has more
/// panels beside it ("Compare-Content" for the equipped item, the blessing ones), kept the same way.
/// </summary>
internal static class GameTooltips
{
    private const string TooltipName = "Tooltip", ContentName = "Content", FrameName = "body-content/Background";
    private const float FindSeconds = 2f;

    private static readonly List<RectTransform> s_contents = new();
    private static readonly Il2CppStructArray<Vector3> s_corners = new(4);
    private static float s_nextFind;

    /// <summary>The shown tooltips' area in screen pixels (top-left origin), or null when none shows.</summary>
    /// <summary>With more than one panel shown (an item and its comparison), the space the game leaves between them in
    /// screen pixels, and whether the comparison is on the left; refreshed by <see cref="Shown"/>.</summary>
    public static (float Gap, bool CompareLeft)? Panels { get; private set; }

    /// <summary>The top of what the shown tooltip draws (screen pixels, top-left origin): the comparison panel's when one
    /// shows, else the item panel's frame, which sits below its panel's own top; null when unknown.</summary>
    public static float? Top { get; private set; }

    public static Rect? Shown()
    {
        if (Time.unscaledTime >= s_nextFind || s_contents.Exists(c => !Game.Alive(c))) Find();
        Rect? area = null, main = null, compare = null, frame = null;
        var screen = new Rect(0f, 0f, Screen.width, Screen.height);
        foreach (var content in s_contents)
        {
            if (!Game.Alive(content) || !content.gameObject.activeInHierarchy) continue;
            content.GetWorldCorners(s_corners);
            // Overlay canvases: world space is screen pixels, bottom-left origin.
            var r = Rect.MinMaxRect(s_corners[0].x, Screen.height - s_corners[2].y, s_corners[2].x, Screen.height - s_corners[0].y);
            if (r.width < 1f || !r.Overlaps(screen)) continue;
            if (content.name == ContentName)
            {
                main = r;
                if (content.Find(FrameName)?.TryCast<RectTransform>() is { } art && art.gameObject.activeInHierarchy)
                {
                    art.GetWorldCorners(s_corners);
                    frame = Rect.MinMaxRect(s_corners[0].x, Screen.height - s_corners[2].y, s_corners[2].x, Screen.height - s_corners[0].y);
                }
            }
            else compare ??= r;
            area = area is { } a ? Rect.MinMaxRect(Mathf.Min(a.xMin, r.xMin), Mathf.Min(a.yMin, r.yMin), Mathf.Max(a.xMax, r.xMax), Mathf.Max(a.yMax, r.yMax)) : r;
        }
        Top = compare?.yMin ?? frame?.yMin ?? area?.yMin;
        Panels = main is { } m && compare is { } c
            ? (Mathf.Max(0f, c.xMax <= m.xMin ? m.xMin - c.xMax : c.xMin - m.xMax), c.center.x < m.center.x)
            : null;
        return area;
    }

    private static void Find()
    {
        s_nextFind = Time.unscaledTime + FindSeconds;
        s_contents.Clear();
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Canvas>(), false))
        {
            var canvas = obj.TryCast<Canvas>();
            if (!Game.Alive(canvas) || !canvas!.name.Contains(TooltipName, StringComparison.Ordinal)) continue;
            // Its panels: Content, and beside it the item tooltip's comparison ("Compare-Content") and blessing ones.
            for (int i = 0; i < canvas.transform.childCount; i++)
                if (canvas.transform.GetChild(i).TryCast<RectTransform>() is { } part && part.name.EndsWith(ContentName, StringComparison.Ordinal)) s_contents.Add(part);
        }
    }
}
