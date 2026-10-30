using EpochAssist.Core;
using EpochAssist.Ui;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace EpochAssist.Features;

internal static partial class Stash
{
    // While the page shows, over the game's stash window: items the game would file into another tab once the preview is
    // made get an amber outline, and the item under the pointer says where it would go. Drawing only; nothing is moved.

    private static readonly List<(InventoryItemUI Ui, IntPtr Entry)> s_itemUis = new();
    private static float s_itemUisAt = -1f;
    private static Dictionary<IntPtr, ItemPlace>? s_placeByEntry;
    private static IReadOnlyList<ItemPlace>? s_placeSource;
    private static readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3> s_itemCorners = new(4);
    private static GUIStyle? s_itemLabel;

    // The window's elements for the checked stash items, found again twice a second: finding them scans the scene. The
    // window draws the open tab from a container of its own, but its elements point at the stash's own entries.
    private static void FindItemUis(Dictionary<IntPtr, ItemPlace> places)
    {
        if (Time.unscaledTime < s_itemUisAt) return;
        s_itemUisAt = Time.unscaledTime + 0.5f;
        s_itemUis.Clear();
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<InventoryItemUI>(), false))
        {
            var ui = obj.TryCast<InventoryItemUI>();
            if (Game.Alive(ui) && ui!.EntryRef is { } entry && places.ContainsKey(entry.Pointer)) s_itemUis.Add((ui, entry.Pointer));
        }
    }

    /// <summary>The HUD pass: the outlines and the pointer's label, only while the page shows and the stash window is open.</summary>
    public static void DrawOverlay()
    {
        bool outlines = StashSettings.ItemOutlines.Value, labels = StashSettings.ItemLabels.Value;
        if (Event.current.type != EventType.Repaint || !PageShown || !StashSettings.Enabled.Value || !(outlines || labels) || !StashWindowOpen) return;
        var places = ItemPlaces;
        if (places == null) return;
        if (!ReferenceEquals(places, s_placeSource))
        {
            s_placeSource = places;
            s_placeByEntry = places.GroupBy(p => p.Entry).ToDictionary(g => g.Key, g => g.First());
            s_itemUisAt = -1f;
        }
        FindItemUis(s_placeByEntry!);
        var matrix = GUI.matrix;
        GUI.matrix = Matrix4x4.identity;
        try
        {
            var mouse = Event.current.mousePosition;
            // The game's tooltip draws under the mod's drawing, so outlines leave out the part it covers.
            s_tooltipArea = GameTooltips.Shown();
            (Rect Rect, ItemPlace Place)? hovered = null;
            foreach (var (ui, entry) in s_itemUis)
            {
                if (!Game.Alive(ui) || !ui.gameObject.activeInHierarchy || !s_placeByEntry!.TryGetValue(entry, out var place)) continue;
                ui.RectTransform.GetWorldCorners(s_itemCorners);
                // Overlay canvas: world space is screen pixels from the bottom left.
                var r = Rect.MinMaxRect(s_itemCorners[0].x, Screen.height - s_itemCorners[2].y, s_itemCorners[2].x, Screen.height - s_itemCorners[0].y);
                if (r.width < 2f) continue;
                if (outlines && place.Elsewhere) Outline(r);
                if (labels && r.Contains(mouse)) hovered = (r, place);
            }
            if (hovered is { } h) ItemLabel(h.Rect, h.Place);
        }
        finally
        {
            GUI.matrix = matrix;
        }
    }

    /// <summary>For the dev bridge: what the overlay has to work with.</summary>
    internal static string OverlayState()
    {
        var places = ItemPlaces;
        return $"page {PageShown}, window {StashWindowOpen}, checking {Belonging}, items checked {places?.Count.ToString() ?? "none"} "
            + $"({places?.Count(p => p.Elsewhere) ?? 0} elsewhere), item elements {s_itemUis.Count} ({s_itemUis.Count(u => Game.Alive(u.Ui) && u.Ui.gameObject.activeInHierarchy)} shown)";
    }

    private static Rect? s_tooltipArea;

    // Attention's amber: a thin frame and a faint wash.
    private static void Outline(Rect r)
    {
        var a = Amber;
        Uncovered(r, new Color(a.r, a.g, a.b, 0.12f));
        Uncovered(new Rect(r.x, r.y, r.width, 2f), a);
        Uncovered(new Rect(r.x, r.yMax - 2f, r.width, 2f), a);
        Uncovered(new Rect(r.x, r.y, 2f, r.height), a);
        Uncovered(new Rect(r.xMax - 2f, r.y, 2f, r.height), a);
    }

    // A fill, less the part under the game's tooltip: up to four pieces, above, below, left and right of it.
    private static void Uncovered(Rect r, Color colour)
    {
        if (s_tooltipArea is not { } t || !r.Overlaps(t))
        {
            Theme.Fill(r, colour);
            return;
        }
        if (r.y < t.y) Theme.Fill(Rect.MinMaxRect(r.xMin, r.yMin, r.xMax, Mathf.Min(r.yMax, t.yMin)), colour);
        if (r.yMax > t.yMax) Theme.Fill(Rect.MinMaxRect(r.xMin, Mathf.Max(r.yMin, t.yMax), r.xMax, r.yMax), colour);
        float top = Mathf.Max(r.yMin, t.yMin), bottom = Mathf.Min(r.yMax, t.yMax);
        if (bottom <= top) return;
        if (r.x < t.x) Theme.Fill(Rect.MinMaxRect(r.xMin, top, Mathf.Min(r.xMax, t.xMin), bottom), colour);
        if (r.xMax > t.xMax) Theme.Fill(Rect.MinMaxRect(Mathf.Max(r.xMin, t.xMax), top, r.xMax, bottom), colour);
    }

    // Above the item: where the game would file it once the preview is made.
    private static void ItemLabel(Rect item, ItemPlace place)
    {
        string text = place.Into.Length == 0 ? "No tab takes this" : place.Elsewhere ? $"Belongs in {place.Into}" : $"Stays here ({place.Into})";
        s_itemLabel ??= new GUIStyle(Theme.Tooltip) { wordWrap = false, alignment = TextAnchor.MiddleCenter };
        var size = s_itemLabel.CalcSize(new GUIContent(text));
        var r = new Rect(Mathf.Round(item.center.x - size.x / 2f), Mathf.Round(item.y - size.y - 4f), Mathf.Ceil(size.x), Mathf.Ceil(size.y));
        r.x = Mathf.Clamp(r.x, 0f, Screen.width - r.width);
        if (r.y < 0f) r.y = item.yMax + 4f;
        Theme.DrawSliced(r, Theme.PopupBg, Theme.ControlRadius + 1);
        if (place.Elsewhere) Theme.Fill(new Rect(r.x, r.y, 3f, r.height), Amber);
        Widgets.Paint(r, text, s_itemLabel);
    }
}
