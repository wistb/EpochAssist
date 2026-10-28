using EpochAssist.Core;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace EpochAssist.Ui;

/// <summary>
/// Where the game's own tooltip is on screen, so a hint beside it doesn't cover it. The game keeps each tooltip (tree
/// node, skill, item, the rest) as a canvas named for it, with its panel in a child "Content" that it parks far off screen
/// when hidden rather than switching it off: a tooltip shows when its Content is on screen.
/// </summary>
internal static class GameTooltips
{
    private const string TooltipName = "Tooltip", ContentName = "Content";
    private const float FindSeconds = 2f;

    private static readonly List<RectTransform> s_contents = new();
    private static readonly Il2CppStructArray<Vector3> s_corners = new(4);
    private static float s_nextFind;

    /// <summary>The shown tooltips' area in screen pixels (top-left origin), or null when none shows.</summary>
    public static Rect? Shown()
    {
        if (Time.unscaledTime >= s_nextFind || s_contents.Exists(c => !Game.Alive(c))) Find();
        Rect? area = null;
        var screen = new Rect(0f, 0f, Screen.width, Screen.height);
        foreach (var content in s_contents)
        {
            if (!Game.Alive(content) || !content.gameObject.activeInHierarchy) continue;
            content.GetWorldCorners(s_corners);
            // Overlay canvases: world space is screen pixels, bottom-left origin.
            var r = Rect.MinMaxRect(s_corners[0].x, Screen.height - s_corners[2].y, s_corners[2].x, Screen.height - s_corners[0].y);
            if (r.width < 1f || !r.Overlaps(screen)) continue;
            area = area is { } a ? Rect.MinMaxRect(Mathf.Min(a.xMin, r.xMin), Mathf.Min(a.yMin, r.yMin), Mathf.Max(a.xMax, r.xMax), Mathf.Max(a.yMax, r.yMax)) : r;
        }
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
            if (canvas.transform.Find(ContentName)?.TryCast<RectTransform>() is { } content) s_contents.Add(content);
        }
    }
}
