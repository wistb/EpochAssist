using Il2Cpp;
using UnityEngine;

namespace EpochAssist.Core;

/// <summary>
/// The game's own item tooltip for an item the mod shows (a search result): opened at an element of the mod's with the
/// slot type that gives the game's comparison with the equipped item (a stash item's), and closed again. The game's
/// prompts under the tooltip act on the real item under the pointer, so they are hidden while it shows.
/// </summary>
internal static class ItemTooltip
{
    private static IntPtr s_item;
    // What the game shows for it: the game keeps its own object for the item and makes a new one when it draws the
    // tooltip again (holding Alt or Ctrl), so the helper knows its tooltip by the item's own fields.
    private static string? s_sign;
    private static GameObject? s_hiddenPrompts;
    // How the open tooltip was asked for, and the screen x its left edge should sit at: the game centres its tooltip on
    // the point given, and draws one or two (the comparison, which Ctrl hides), so the point is moved until the edge fits.
    private static (ItemDataUnpacked Item, GameObject? Opener, Vector3 At, TooltipItemManager.SlotType Type, Vector2 Size)? s_open;
    private static float? s_alignEdge;
    private static bool s_alignRight;
    private static readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3> s_corners = new(4);

    /// <summary>The item this helper has open, if the game still shows it.</summary>
    public static bool IsOpen(ItemDataUnpacked item) => s_item != IntPtr.Zero && s_item == item.Pointer && Ours();

    // The game's tooltip shows this helper's item (the same base, unique, affixes and rolls).
    private static bool Ours()
    {
        try
        {
            var active = Tooltips()?.ActiveItem;
            return active != null && s_sign != null && (active.Pointer == s_item || Sign(active) == s_sign);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Sign(ItemData item)
    {
        var parts = new System.Text.StringBuilder();
        parts.Append(item.itemType).Append('/').Append(item.subType).Append('/').Append(item.uniqueID).Append('/').Append(item.rarity);
        var affixes = item.TryCast<ItemDataUnpacked>()?.affixes;
        for (int i = 0; affixes != null && i < affixes.Count; i++)
            if (affixes[i] is { } a) parts.Append('|').Append(a.affixId).Append(':').Append(a.affixTier).Append(':').Append(a.affixRoll);
        return parts.ToString();
    }

    /// <summary>
    /// Opens an item's tooltip beside an opener (the game places it clear of the opener's rect). As a stash item the
    /// game adds its own comparison with the equipped item of the same slot; alone (no game element behind it) it shows
    /// as equipped, which the game draws without comparison.
    /// </summary>
    /// <param name="besideEdge">The screen x the tooltips' near edge keeps to (a menu's or panel's side), on the right of
    /// it when <paramref name="toRight"/>, else on its left; null leaves the game's placement.</param>
    public static bool Open(ItemDataUnpacked item, GameObject? opener, Vector3 at, bool alone = false, float? besideEdge = null, bool toRight = true)
    {
        if (Tooltips() is not { } tooltips) return false;
        try
        {
            var size = opener != null ? opener.GetComponent<RectTransform>()?.rect.size ?? Vector2.zero : Vector2.zero;
            var type = alone ? TooltipItemManager.SlotType.EQUIPPED : TooltipItemManager.SlotType.STASH;
            tooltips.OpenItemTooltip(item, type, Vector2.zero, at, opener, size);
            s_item = item.Pointer;
            s_sign = Sign(item);
            s_open = (item, opener, at, type, size);
            s_alignEdge = besideEdge;
            s_alignRight = toRight;
            HidePrompts();
            return true;
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("tooltip:open", $"Item tooltip couldn't be opened: {e.Message}", 30);
            return false;
        }
    }

    // Just before the screen is drawn, after the game has placed its tooltip for the frame: moved there, the tooltip never
    // shows a frame where the game put it.
    private static Canvas.WillRenderCanvases? s_beforeRender;

    /// <summary>Starts keeping an open tooltip beside its edge at the last moment of each frame (the owner starting).</summary>
    public static void Hook()
    {
        if (s_beforeRender != null) return;
        try
        {
            s_beforeRender = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Canvas.WillRenderCanvases>(new Action(BeforeRender));
            Canvas.add_willRenderCanvases(s_beforeRender);
        }
        catch (Exception e)
        {
            s_beforeRender = null;
            Log.Warn($"Item tooltip: no hook before drawing ({e.Message}); tooltips are placed in the frame instead");
        }
    }

    /// <summary>Stops it (the owner stopping).</summary>
    public static void Unhook()
    {
        if (s_beforeRender == null) return;
        try
        {
            Canvas.remove_willRenderCanvases(s_beforeRender);
        }
        catch (Exception e)
        {
            Log.Warn($"Item tooltip: the hook before drawing couldn't be removed: {e.Message}");
        }
        s_beforeRender = null;
    }

    private static void BeforeRender()
    {
        if (s_item == IntPtr.Zero) return;
        try
        {
            Align();
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("tooltip:render", $"Item tooltip couldn't be placed: {e.Message}", 30);
        }
    }

    /// <summary>Each frame while open: the game turns its prompts on again as it builds the tooltip.</summary>
    public static void Frame()
    {
        if (s_item == IntPtr.Zero) return;
        if (!Ours())
        {
            Forget();
            return;
        }
        HidePrompts();
        Align();
    }

    /// <summary>Closes the tooltip if it's still the one this helper opened, and gives the game back its prompts.</summary>
    public static void Close()
    {
        if (s_item == IntPtr.Zero) return;
        try
        {
            var tooltips = Tooltips();
            if (tooltips != null && Ours()) tooltips.CloseTooltip();
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("tooltip:close", $"Item tooltip couldn't be closed: {e.Message}", 30);
        }
        Forget();
    }

    /// <summary>For the dev verb: the helper's state and every item tooltip the game shows, with screen rects.</summary>
    internal static string State()
    {
        var lines = new List<string> { $"helper: item {(s_item == IntPtr.Zero ? "none" : "open")}, ours {Ours()}, align {s_alignEdge?.ToString("0") ?? "-"} {(s_alignRight ? "right" : "left")}, at {s_open?.At.x.ToString("0") ?? "-"}" };
        try
        {
            var active = Tooltips()?.ActiveItem;
            lines.Add($"game active item: {(active == null ? "none" : Sign(active))}; mine {(s_open is { } o ? Sign(o.Item) : "-")}");
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<UITooltipItem>(), true))
            {
                var t = obj.TryCast<UITooltipItem>();
                if (t == null || t.transform.TryCast<RectTransform>() is not { } r) continue;
                r.GetWorldCorners(s_corners);
                lines.Add($"  {t.name} active {t.gameObject.activeInHierarchy} instance {(t.Pointer == UITooltipItem.instance?.Pointer)} x {s_corners[0].x:0}..{s_corners[2].x:0} y {Screen.height - s_corners[1].y:0}");
            }
        }
        catch (Exception e)
        {
            lines.Add("  read failed: " + e.Message);
        }
        return string.Join("\n", lines);
    }

    // Keeps the tooltips' near edge (the hovered item's and the comparison, whichever the game draws) at the edge asked
    // for. The game centres them on the point given, but while Ctrl drops the comparison it places the single tooltip
    // itself and ignores the point, so the tooltips' own objects are moved, every frame while they show.
    private static readonly List<RectTransform> s_tips = new();
    private static float s_tipsListedAt = -10f;

    private static void Align()
    {
        if (s_alignEdge is not { } edge || s_open == null) return;
        if (Time.unscaledTime - s_tipsListedAt > 1f)
        {
            s_tipsListedAt = Time.unscaledTime;
            s_tips.Clear();
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<UITooltipItem>(), true))
                if (obj.TryCast<UITooltipItem>()?.transform.TryCast<RectTransform>() is { } r) s_tips.Add(r);
        }
        float? near = null;
        foreach (var rect in s_tips)
        {
            if (!Game.Alive(rect) || !rect.gameObject.activeInHierarchy) continue;
            rect.GetWorldCorners(s_corners);
            if (s_corners[2].x < 0f || s_corners[0].x > Screen.width) continue;
            float x = s_alignRight ? s_corners[0].x : s_corners[2].x;
            near = near is { } n ? (s_alignRight ? Mathf.Min(n, x) : Mathf.Max(n, x)) : x;
        }
        if (near is not { } at) return;
        float off = edge - at;
        if (Mathf.Abs(off) < 2f) return;
        foreach (var rect in s_tips)
        {
            if (!Game.Alive(rect) || !rect.gameObject.activeInHierarchy) continue;
            rect.GetWorldCorners(s_corners);
            if (s_corners[2].x < 0f || s_corners[0].x > Screen.width) continue;
            // A tooltip inside another moves with it.
            if (s_tips.Any(other => other.Pointer != rect.Pointer && Game.Alive(other) && other.gameObject.activeInHierarchy && rect.IsChildOf(other))) continue;
            rect.position += new Vector3(off, 0f, 0f);
        }
    }

    private static void Forget()
    {
        s_open = null;
        s_alignEdge = null;
        s_item = IntPtr.Zero;
        s_sign = null;
        if (s_hiddenPrompts is { } prompts && Game.Alive(prompts)) prompts.SetActive(true);
        s_hiddenPrompts = null;
    }

    private static void HidePrompts()
    {
        var prompts = UITooltipItem.instance?._itemTooltipPrompts;
        if (!Game.Alive(prompts)) return;
        var go = prompts!.gameObject;
        if (go.activeSelf) go.SetActive(false);
        s_hiddenPrompts = go;
    }

    private static TooltipItemManager? Tooltips()
    {
        var tooltips = SceneObjects.Find<TooltipItemManager>();
        return Game.Alive(tooltips) ? tooltips : null;
    }
}
