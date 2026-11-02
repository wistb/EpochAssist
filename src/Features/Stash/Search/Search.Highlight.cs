using EpochAssist.Core;
using EpochAssist.Ui;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.EventSystems;

namespace EpochAssist.Features;

internal static partial class StashSearch
{
    private const float PulseSeconds = 2f, GoTimeout = 2f;

    // Going to an item: its place, what happens next, and since when.
    private enum GoStep { None, Tab, Wait }
    private static GoStep s_goStep;
    private static ItemPlace s_goPlace;
    private static float s_goSince;
    private static string s_goLog = string.Empty;
    // The item pulsing on the stash window, and until when.
    private static IntPtr s_pulseEntry;
    private static float s_pulseUntil;

    // The game's search box around a highlight: whether the highlight is on, the text it had, and whether the player
    // typed over it (the highlight stays off until the search changes).
    private static bool s_highlightOn, s_typedOver;
    private static string s_boxKept = string.Empty;
    private static int s_highlightGeneration;

    private static readonly List<(InventoryItemUI Ui, IntPtr Entry)> s_itemUis = new();
    private static float s_itemUisAt = -1f;
    private static readonly HashSet<IntPtr> s_stashEntries = new(), s_matchEntries = new();
    private static (int Generation, int Facts, int Version) s_entriesFor = (-1, -1, -1);
    private static readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3> s_itemCorners = new(4);

    /// <summary>Matches in the open tab are outlined and the rest dimmed (the wrong-tab outlines step aside).</summary>
    public static bool HighlightActive => s_highlightOn;

    /// <summary>
    /// Opens a result's folder and tab with the game's own buttons, as a click on them does, then pulses the item. Only
    /// what the window shows changes; no item moves.
    /// </summary>
    public static string GoTo(ItemPlace place, bool fromPage = true)
    {
        if (!Has(GoGroup)) return "going to items isn't available in this game version";
        if (!Stash.StashWindowOpen) return "open the stash to go there";
        if (fromPage && s_active != null) AddRecent(s_active);
        s_goPlace = place;
        s_goStep = GoStep.Tab;
        s_goSince = Time.unscaledTime;
        s_goLog = $"go tab {place.TabId} ({place.X},{place.Y})";
        return "going";
    }

    /// <summary>For the dev verb: what the last go did.</summary>
    internal static string GoLog => s_goLog;

    // Each frame: the go steps, the search box, the pulse's end.
    private static void HighlightFrame()
    {
        GoFrame();
        BoxFrame();
        if (s_pulseUntil > 0f && Time.unscaledTime > s_pulseUntil)
        {
            s_pulseUntil = 0f;
            s_pulseEntry = IntPtr.Zero;
        }
    }

    private static void GoFrame()
    {
        if (s_goStep == GoStep.None) return;
        var ui = SceneObjects.Find<StashTabbedUIControls>();
        if (!Game.Alive(ui) || !Stash.StashWindowOpen || Time.unscaledTime - s_goSince > GoTimeout)
        {
            s_goLog += Time.unscaledTime - s_goSince > GoTimeout ? "; timed out" : "; window closed";
            s_goStep = GoStep.None;
            return;
        }
        try
        {
            if (ActiveTab() == s_goPlace.TabId)
            {
                s_goLog += "; tab shown; pulse";
                s_pulseEntry = s_goPlace.Entry;
                s_pulseUntil = Time.unscaledTime + PulseSeconds;
                s_goStep = GoStep.None;
                s_itemUisAt = -1f;
                return;
            }
            if (s_goStep != GoStep.Tab) return;
            // Quick View's tab when it shows (opening its folder first when that is folded), else the tab row's.
            var tab = Pair(ui!.localVertTabs, s_goPlace.TabId);
            if (!Shown(tab))
            {
                int? folder = s_snap?.Tab(s_goPlace.TabId)?.FolderId;
                var folderUi = folder is int f ? FolderPair(ui.localVertCategories, f) : null;
                if (Shown(folderUi) && QuickView(ui)?.gameObject.activeInHierarchy == true)
                {
                    Press(folderUi!);
                    s_goLog += $"; folder {folder} pressed";
                    return;
                }
                tab = Pair(ui.localTabs, s_goPlace.TabId);
                if (!Shown(tab))
                {
                    var rowFolder = folder is int rf ? FolderPair(ui.localCategories, rf) : null;
                    if (Shown(rowFolder))
                    {
                        Press(rowFolder!);
                        s_goLog += $"; row folder {folder} pressed";
                        return;
                    }
                }
            }
            if (!Shown(tab))
            {
                s_goLog += "; no button for the tab";
                s_goStep = GoStep.None;
                return;
            }
            Press(tab!);
            s_goLog += "; tab pressed";
            s_goStep = GoStep.Wait;
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("search:go", $"Stash search: going to the item failed: {e.Message}", 30);
            s_goStep = GoStep.None;
        }
    }

    private static bool Shown(TabbedUITabWithIcon? t) => Game.Alive(t) && t!.gameObject.activeInHierarchy;

    private static TabbedUITabWithIcon? Pair(Il2CppSystem.Collections.Generic.List<StashTabbedUIControls.StashTabPair>? pairs, int tabId)
    {
        for (int i = 0; pairs != null && i < pairs.Count; i++)
            if (pairs[i]?.tab?.TabId == tabId) return pairs[i].tabUI;
        return null;
    }

    private static TabbedUITabWithIcon? FolderPair(Il2CppSystem.Collections.Generic.List<StashTabbedUIControls.CategoryTabPair>? pairs, int folderId)
    {
        for (int i = 0; pairs != null && i < pairs.Count; i++)
            if (pairs[i]?.category?.CategoryId == folderId) return pairs[i].tabUI;
        return null;
    }

    // What a left click on the button runs; its Button.onClick doesn't switch tabs.
    private static void Press(TabbedUITabWithIcon button) =>
        button.OnPointerClick(new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left });

    private static int ActiveTab()
    {
        var holder = ItemContainersManager.Instance?.stash;
        var all = holder?.stashItemContainers;
        if (all == null || all.Length == 0) return -1;
        int index = holder!._CurrentStashIndex_k__BackingField;
        return (index >= 0 && index < all.Length ? all[index] : all[0])?.currentlyActiveTab ?? -1;
    }

    /// <summary>The amber flash a pulsing item's outline shows, in the stash and on its result cell alike; null when it
    /// isn't pulsing.</summary>
    private static Color? PulseColour(IntPtr entry)
    {
        if (entry != s_pulseEntry || s_pulseUntil <= Time.unscaledTime) return null;
        float a = 0.55f + 0.45f * Mathf.Sin((s_pulseUntil - Time.unscaledTime) * Mathf.PI * 4f);
        return new Color(1f, 0.85f, 0.3f, a);
    }

    // ---- the game's search box ----

    private static bool HighlightWanted =>
        Stash.StashWindowOpen && StashSettings.SearchHighlight.Value && s_boxQuery == null && s_active != null && !s_active.IsEmpty && !s_typedOver && Has(GoGroup);

    private static void BoxFrame()
    {
        if (s_highlightGeneration != s_generation)
        {
            s_highlightGeneration = s_generation;
            s_typedOver = false;
        }
        if (!Stash.StashWindowOpen)
        {
            // The game empties its box when the window closes: nothing to put back.
            s_highlightOn = false;
            s_boxKept = string.Empty;
            return;
        }
        var ui = SceneObjects.Find<StashTabbedUIControls>();
        if (!Game.Alive(ui)) return;
        try
        {
            // Without the box's members the outline still shows; the box is just left alone.
            var box = Has(BoxGroup) ? ui!.verticalSearchBar : null;
            string text = Game.Alive(box) ? box!.text ?? string.Empty : string.Empty;
            if (s_highlightOn && text.Length > 0)
            {
                // The player typed: their search takes over; the mod's list stays, its outlines go.
                s_highlightOn = false;
                s_typedOver = true;
                s_boxKept = string.Empty;
                return;
            }
            if (HighlightWanted && !s_highlightOn)
            {
                s_boxKept = text;
                if (text.Length > 0) ui!.ClearSearchField();
                s_highlightOn = true;
                s_itemUisAt = -1f;
            }
            else if (!HighlightWanted && s_highlightOn) EndHighlight(ui);
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("search:box", $"Stash search: the game's search box couldn't be read: {e.Message}", 30);
        }
    }

    // Ends the highlight with the window open: the box gets back what it had.
    private static void EndHighlight(StashTabbedUIControls? ui)
    {
        if (!s_highlightOn) return;
        s_highlightOn = false;
        string kept = s_boxKept;
        s_boxKept = string.Empty;
        if (kept.Length > 0 && Has(BoxGroup) && Game.Alive(ui) && Stash.StashWindowOpen) ui!.SetStashWideSearch(kept, true);
    }

    private static void ResetHighlight(bool restore)
    {
        if (restore)
        {
            try
            {
                EndHighlight(SceneObjects.Find<StashTabbedUIControls>());
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("search:box", $"Stash search: the game's search box couldn't be restored: {e.Message}", 30);
            }
        }
        s_highlightOn = s_typedOver = false;
        s_boxKept = string.Empty;
        s_goStep = GoStep.None;
        s_pulseEntry = IntPtr.Zero;
        s_pulseUntil = 0f;
        s_itemUis.Clear();
    }

    // ---- outline and dim over the open tab (HUD pass) ----

    /// <summary>Outlines matches in the open tab and dims the rest; pulses the item gone to. Drawing only.</summary>
    public static void DrawOverlay()
    {
        if (Event.current.type != EventType.Repaint || !Stash.StashWindowOpen || !StashSettings.SearchEnabled.Value) return;
        bool pulse = s_pulseUntil > Time.unscaledTime;
        if (!s_highlightOn && !pulse) return;
        Entries();
        FindItemUis();
        var matrix = GUI.matrix;
        GUI.matrix = Matrix4x4.identity;
        try
        {
            s_tooltip = GameTooltips.Shown();
            bool stale = false;
            foreach (var (ui, entry) in s_itemUis)
            {
                if (!Game.Alive(ui) || !ui.gameObject.activeInHierarchy) continue;
                // The game reuses its item elements when the tab changes: one listed for an item may show another now.
                if ((ui.EntryRef?.Pointer ?? IntPtr.Zero) != entry)
                {
                    stale = true;
                    continue;
                }
                ui.RectTransform.GetWorldCorners(s_itemCorners);
                var r = Rect.MinMaxRect(s_itemCorners[0].x, Screen.height - s_itemCorners[2].y, s_itemCorners[2].x, Screen.height - s_itemCorners[0].y);
                if (r.width < 2f) continue;
                if (pulse && PulseColour(entry) is { } flash)
                {
                    Frame(r, flash, 3f);
                    continue;
                }
                if (!s_highlightOn) continue;
                if (s_matchEntries.Contains(entry)) Frame(r, new Color(Theme.Accent.r, Theme.Accent.g, Theme.Accent.b, 0.95f), 2f);
                else if (StashSettings.SearchDim.Value > 0.001f) Cover(r, new Color(0f, 0f, 0f, StashSettings.SearchDim.Value));
            }
            // Listed again next frame when an element changed, and every frame while a pulse starts (the tab just opened).
            if (stale || pulse && s_pulseUntil - Time.unscaledTime > PulseSeconds - 0.6f) s_itemUisAt = -1f;
        }
        finally
        {
            GUI.matrix = matrix;
        }
    }

    private static Rect? s_tooltip;

    // The stash's entries and the matches' entries, for the item elements on the window.
    private static void Entries()
    {
        var stamp = (s_generation, s_factsGeneration, s_matchVersion);
        if (s_entriesFor == stamp) return;
        s_entriesFor = stamp;
        s_stashEntries.Clear();
        foreach (var p in s_places) s_stashEntries.Add(p.Entry);
        s_matchEntries.Clear();
        foreach (var f in Grouped())
            foreach (var t in f.Tabs)
                foreach (var r in t.Items) s_matchEntries.Add(r.Place.Entry);
    }

    // The open tab's item elements, found again twice a second under the stash grid's own object.
    private static void FindItemUis()
    {
        if (Time.unscaledTime < s_itemUisAt) return;
        s_itemUisAt = Time.unscaledTime + 0.5f;
        s_itemUis.Clear();
        var grid = SceneObjects.Find<StashTabbedUIControls>()?.searchUI;
        if (!Game.Alive(grid)) return;
        foreach (var ui in grid!.GetComponentsInChildren<InventoryItemUI>(false))
            if (Game.Alive(ui) && ui.EntryRef is { } entry && s_stashEntries.Contains(entry.Pointer)) s_itemUis.Add((ui, entry.Pointer));
    }

    private static void Frame(Rect r, Color c, float w)
    {
        Cover(new Rect(r.x, r.y, r.width, w), c);
        Cover(new Rect(r.x, r.yMax - w, r.width, w), c);
        Cover(new Rect(r.x, r.y, w, r.height), c);
        Cover(new Rect(r.xMax - w, r.y, w, r.height), c);
    }

    // A fill, less the part under the game's tooltip (it draws under the mod's drawing).
    private static void Cover(Rect r, Color colour)
    {
        if (s_tooltip is not { } t || !r.Overlaps(t))
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
}
