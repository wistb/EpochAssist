using EpochAssist.Core;
using EpochAssist.Ui;
using Il2Cpp;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EpochAssist.Features;

internal static partial class StashSearch
{
    // The results panel on the game's stash window: made of Unity UI pieces under the window's own object, so it moves
    // and scales with it and the game's tooltip draws above it. Rows are virtual: only the visible lines have objects.
    public const string PanelName = "EpochAssist.Search";

    private const float Pad = 10f, Cell = 50f, CellGap = 4f, HeadHeight = 30f, FolderLine = 26f, TabLine = 22f, PanelGap = 6f;
    private const float AnchorSeconds = 0.5f;

    private sealed class PanelCell
    {
        public GameObject Object = null!;
        public RectTransform Rect = null!;
        public Image Edge = null!;
        public Image Icon = null!;
        public TextMeshProUGUI Badge = null!;
        public TextMeshProUGUI Name = null!;
        public Result? Result;
        public bool Settled, Badges, Flashing;
    }

    private sealed class PanelText
    {
        public GameObject Object = null!;
        public RectTransform Rect = null!;
        public TextMeshProUGUI Text = null!;
    }

    // One laid-out line: a folder heading, a tab heading, or a row of results.
    private readonly record struct PanelLine(float Y, float Height, string? Text, bool Folder, List<Result>? Cells, int TabId);

    private sealed class HoverArt
    {
        public HoverArt(Image image, Sprite hover) => (Image, Hover) = (image, hover);
        public readonly Image Image;
        public readonly Sprite Hover;
        public bool On;
    }

    public const string FilterName = PanelName + ".Filter";
    private static GameObject? s_panel, s_body;
    private static RectTransform? s_panelRect, s_art, s_viewport, s_close, s_filter;
    private static HoverArt? s_closeArt, s_filterArt;
    private static readonly List<(Image Copy, Image Source, string Key)> s_artImages = new();
    // Texts copied from Quick View: its font may not be ready when the window opens without Quick View ever shown.
    private static readonly List<TextMeshProUGUI> s_artTexts = new();
    private static Transform? s_closeSource, s_filterSource;
    // The body's insets on Quick View's art (in its units): where its search box and list sit.
    private const float BodySide = 23f, BodyTop = 58f, BodyBottom = 14f, FilterGap = 8f;
    private static TextMeshProUGUI? s_header;
    private static TMP_FontAsset? s_font;
    private static Material? s_fontMaterial;
    private static readonly List<PanelCell> s_cells = new();
    private static readonly List<PanelText> s_texts = new();
    private static List<PanelLine>? s_lines;
    private static (int Generation, int Facts, int Count, int Columns) s_linesFor;
    private static float s_panelScroll, s_nextAnchor;
    private static string s_panelSide = "none";
    private static PanelCell? s_hoverCell;
    // The game centres its tooltip on the point it's given; with the comparison beside it, it is this wide (in the
    // canvas's units). Opened half that beyond the panel's edge, neither form covers the panel or the hovered cell.
    private const float TooltipWithCompare = 938f;
    private static readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3> s_corners = new(4);

    /// <summary>The panel is on the stash window and shows results.</summary>
    public static bool PanelShown => Game.Alive(s_panel) && s_panel!.activeSelf;

    /// <summary>Every frame: makes, places and fills the panel while the stash window is open; removes it otherwise.</summary>
    private static void PanelFrame()
    {
        bool want = Has(PanelGroup) && StashSettings.SearchEnabled.Value && Stash.StashWindowOpen;
        if (!want)
        {
            // The game empties its boxes when the window closes; a ~ search typed again shows again.
            SetBoxQuery(null, null);
            DestroyPanel();
            return;
        }
        var ui = SceneObjects.Find<StashTabbedUIControls>();
        if (!Game.Alive(ui) || !ui!.gameObject.activeInHierarchy)
        {
            DestroyPanel();
            return;
        }
        try
        {
            if (!StashSettings.SearchFilterButton.Value)
            {
                if (Game.Alive(s_filter)) UnityEngine.Object.Destroy(s_filter!.gameObject);
                s_filter = null;
                s_filterArt = null;
            }
            else if (!Game.Alive(s_filter))
            {
                s_filterBuiltAt = Time.unscaledTime;
                s_filter = MakeFilterButton(ui);
                s_artMissing = true;
                s_nextAnchor = 0f;
            }
            if (!Game.Alive(s_panel) && !BuildPanel(ui)) return;
            // The results panel shows a ~ search typed in the game's boxes; the Filter button always shows.
            bool results = BoxQueryShown && StashSettings.SearchPanel.Value;
            if (s_panel!.activeSelf != results)
            {
                s_panel.SetActive(results);
                s_nextAnchor = 0f;
            }
            bool tick = Time.unscaledTime >= s_nextAnchor;
            if (tick)
            {
                s_nextAnchor = Time.unscaledTime + AnchorSeconds;
                if (results) Anchor(ui);
                CheckBoxText(ui);
            }
            // Art the game hasn't loaded yet is looked for every frame until it is there, and the Filter button waits
            // for its frame, so it appears whole with the game's own buttons.
            // Right after the window opens the game swaps some of its sprites within a frame or two: the copies follow
            // every frame for a moment, then twice a second.
            if (tick || s_artMissing || Time.unscaledTime - s_filterBuiltAt < 1.5f) SyncArt();
            if (tick || s_filter != null && !s_filter.gameObject.activeSelf) AnchorFilter(ui);
            ButtonInput();
            if (!results)
            {
                LeaveCell();
                return;
            }
            FillPanel();
            PanelInput(ui);
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("search:panel", $"Stash search: the results panel failed: {e.Message}", 30);
            DestroyPanel();
        }
    }

    // The panel wears Quick View's own art at Quick View's size: its frame, its header with the title, its close button.
    private static bool BuildPanel(StashTabbedUIControls ui)
    {
        var host = PanelHost(ui);
        var qv = QuickView(ui)?.TryCast<RectTransform>();
        if (host == null || qv == null) return false;
        SweepPanels();
        var sample = ui.verticalSearchBar?.textComponent?.TryCast<TextMeshProUGUI>() ?? ui.GetComponentInChildren<TextMeshProUGUI>(true);
        s_font = Game.Alive(sample) ? sample!.font : null;
        s_fontMaterial = Game.Alive(sample) ? sample!.fontSharedMaterial : null;

        s_panel = new GameObject(PanelName);
        s_panelRect = s_panel.AddComponent<RectTransform>();
        s_panelRect.SetParent(host, false);
        s_panel.AddComponent<LayoutElement>().ignoreLayout = true;
        s_panelRect.anchorMin = s_panelRect.anchorMax = new Vector2(0.5f, 0.5f);
        s_panelRect.pivot = new Vector2(0f, 1f);
        // The panel takes the pointer, as any game panel does, so clicks on it don't reach the world below.
        var catcher = s_panel.AddComponent<Image>();
        catcher.color = Color.clear;
        catcher.raycastTarget = true;

        var artGo = new GameObject(PanelName + ".Art");
        s_art = artGo.AddComponent<RectTransform>();
        s_art.SetParent(s_panelRect, false);
        s_art.anchorMin = s_art.anchorMax = new Vector2(0f, 1f);
        s_art.pivot = qv.pivot;
        s_art.sizeDelta = qv.rect.size;
        foreach (string name in new[] { "panel-metallic-template-simplified", "EterraFrameHeader", "TextMeshPro Text (1)" })
            if (FindChild(qv, name) is { } source) PlaceCopy(source, qv, CopyArt(source, s_art));
        var title = s_art.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault();
        if (title != null) title.text = "Filter";
        if (FindChild(qv, "closeVerticalStash") is { } close)
        {
            s_close = CopyArt(close, s_art);
            PlaceCopy(close, qv, s_close);
            s_closeSource = close;
            s_closeArt = HoverOf(close, s_close);
        }

        var bodyGo = new GameObject(PanelName + ".Body");
        var body = bodyGo.AddComponent<RectTransform>();
        body.SetParent(s_art, false);
        body.anchorMin = Vector2.zero;
        body.anchorMax = Vector2.one;
        // Where Quick View's search box and list sit.
        body.offsetMin = new Vector2(BodySide, BodyBottom);
        body.offsetMax = new Vector2(-BodySide, -BodyTop);
        s_body = bodyGo;

        s_header = Text(body, "Header", 17f, TextAlignmentOptions.Left).Text;
        var header = s_header.rectTransform;
        header.anchorMin = new Vector2(0f, 1f);
        header.anchorMax = new Vector2(1f, 1f);
        header.pivot = new Vector2(0f, 1f);
        header.offsetMin = new Vector2(0f, -HeadHeight);
        header.offsetMax = Vector2.zero;

        var view = new GameObject(PanelName + ".View");
        s_viewport = view.AddComponent<RectTransform>();
        s_viewport.SetParent(body, false);
        s_viewport.anchorMin = Vector2.zero;
        s_viewport.anchorMax = Vector2.one;
        s_viewport.offsetMin = Vector2.zero;
        s_viewport.offsetMax = new Vector2(0f, -HeadHeight);
        view.AddComponent<RectMask2D>();
        if (s_close != null) s_close.SetAsLastSibling();
        s_panelRect.SetSiblingIndex(qv.GetSiblingIndex() + 1);
        s_nextAnchor = 0f;
        s_lines = null;
        s_panelScroll = 0f;
        Anchor(ui);
        return true;
    }

    // The Filter button: the game's Configure Tab button copied, with its word, beside the stash's own search box.
    private static RectTransform? MakeFilterButton(StashTabbedUIControls ui)
    {
        var box = SearchBox(ui);
        if (box?.parent is not { } parent) return null;
        // The bottom bar's buttons share the search box's holder.
        var source = (FindChild(parent, "ConfigureTabButton") ?? FindChild(ui.transform, "ConfigureTabButton"))?.TryCast<RectTransform>();
        RectTransform rect;
        if (source != null) rect = CopyArt(source, parent);
        else
        {
            rect = new GameObject().AddComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.gameObject.AddComponent<Image>().color = new Color(0.22f, 0.19f, 0.15f, 1f);
            var plain = Text(rect, "Label", 15f, TextAlignmentOptions.Center);
            plain.Rect.anchorMin = Vector2.zero;
            plain.Rect.anchorMax = Vector2.one;
            plain.Rect.offsetMin = plain.Rect.offsetMax = Vector2.zero;
        }
        rect.name = FilterName;
        rect.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        var image = rect.GetComponent<Image>() ?? rect.gameObject.AddComponent<Image>();
        image.raycastTarget = true;
        var label = rect.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault();
        float width = 96f, height = source != null ? source.rect.height : 40f;
        if (label != null)
        {
            label.text = "Filter";
            var labelRect = label.rectTransform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.pivot = new Vector2(0.5f, 0.5f);
            labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
            // As wide as the word needs, with the room the source button leaves around its own word.
            var sourceLabel = source?.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault();
            float room = source != null && sourceLabel != null ? source.rect.width - sourceLabel.GetPreferredValues(sourceLabel.text).x : 40f;
            width = Mathf.Ceil(label.GetPreferredValues("Filter").x + Mathf.Max(24f, room));
        }
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(1f, 0.5f);
        rect.sizeDelta = new Vector2(width, height);
        s_filterSource = source;
        if (source != null) s_filterArt = HoverOf(source, rect);
        rect.gameObject.SetActive(false);
        return rect;
    }

    // The stash's own search box (below the grid): its holder, which the Filter button sits beside.
    private static RectTransform? SearchBox(StashTabbedUIControls ui) =>
        Game.Alive(ui.primarySearchBar) ? ui.primarySearchBar.transform.parent?.TryCast<RectTransform>() : null;

    // The Filter button has its frame (or waited a second for it, then shows as it is).
    private static bool FilterReady => s_filter == null || Time.unscaledTime - s_filterBuiltAt > 1f
        || s_filter.GetComponent<Image>() is { } image && Game.Alive(image) && Game.Alive(image.sprite);

    // Just left of the search box, level with it; hidden when the box is, and until its frame is there.
    private static void AnchorFilter(StashTabbedUIControls ui, RectTransform? rect = null)
    {
        rect ??= s_filter;
        if (rect == null || rect.parent is not { } parent) return;
        var box = SearchBox(ui);
        bool shown = box != null && box.gameObject.activeInHierarchy && (rect != s_filter || FilterReady);
        if (rect.gameObject.activeSelf != shown) rect.gameObject.SetActive(shown);
        if (!shown) return;
        box!.GetWorldCorners(s_corners);
        float scale = Mathf.Max(0.01f, rect.lossyScale.x);
        var at = new Vector3(s_corners[0].x - FilterGap * scale, (s_corners[0].y + s_corners[1].y) / 2f, 0f);
        rect.localPosition = parent.InverseTransformPoint(at);
    }

    // A copy of a game object's look: its rects, images and texts as plain new objects, without the game's scripts or
    // actions. Hidden parts and empty white fills (the game's layout holders) are left out.
    private static RectTransform CopyArt(Transform source, Transform parent)
    {
        var go = new GameObject(PanelName + "." + source.name);
        var rect = go.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        if (source.TryCast<RectTransform>() is { } from)
        {
            rect.anchorMin = from.anchorMin;
            rect.anchorMax = from.anchorMax;
            rect.pivot = from.pivot;
            rect.sizeDelta = from.sizeDelta;
            rect.anchoredPosition = from.anchoredPosition;
        }
        rect.localScale = source.localScale;
        rect.localRotation = source.localRotation;
        var image = source.GetComponent<Image>();
        if (Game.Alive(image) && image!.enabled)
        {
            var copy = go.AddComponent<Image>();
            copy.type = image.type;
            copy.color = image.color;
            copy.fillCenter = image.fillCenter;
            copy.preserveAspect = image.preserveAspect;
            copy.pixelsPerUnitMultiplier = image.pixelsPerUnitMultiplier;
            copy.raycastTarget = false;
            s_artImages.Add((copy, image, source.name));
            SyncSprite(copy, image, source.name);
        }
        var text = source.GetComponent<TextMeshProUGUI>();
        if (Game.Alive(text) && text!.enabled)
        {
            var copy = go.AddComponent<TextMeshProUGUI>();
            copy.font = text.font;
            copy.fontSharedMaterial = text.fontSharedMaterial;
            copy.fontSize = text.fontSize;
            copy.enableAutoSizing = text.enableAutoSizing;
            copy.fontSizeMin = text.fontSizeMin;
            copy.fontSizeMax = text.fontSizeMax;
            copy.fontStyle = text.fontStyle;
            copy.color = text.color;
            copy.characterSpacing = text.characterSpacing;
            copy.alignment = text.alignment;
            copy.enableWordWrapping = text.enableWordWrapping;
            copy.margin = text.margin;
            copy.text = text.text;
            copy.raycastTarget = false;
            s_artTexts.Add(copy);
        }
        for (int i = 0; i < source.childCount; i++)
            if (source.GetChild(i) is { } child && child.gameObject.activeSelf) CopyArt(child, rect);
        return rect;
    }

    // The game loads Quick View's sprites only while it is open: a copy takes its source's sprite whenever there is one,
    // else the sprite of that name if the game has it loaded for another window (the stash window's frame and close
    // button use the same ones), and hides rather than draw a blank block. A plain coloured fill needs no sprite.
    private static readonly Dictionary<string, string> s_spriteNames = new()
    {
        ["background"] = "panel_background_tiled",
        ["border-base"] = "Panel Border With Corners",
        ["ornament-side"] = "idol_ornament_Side",
        ["ornament-side (1)"] = "idol_ornament_Side",
        ["headerbg"] = "title_background_Eterra",
        ["closeVerticalStash"] = "close-default-new",
    };
    private static readonly Dictionary<string, Sprite> s_loadedSprites = new();
    private static float s_nextSpriteScan;

    private static void SyncSprite(Image copy, Image source, string key)
    {
        if (!Game.Alive(copy)) return;
        var sprite = Game.Alive(source) ? source.sprite : null;
        if (Game.Alive(sprite)) s_spriteNames[key] = sprite!.name;
        else if (!Game.Alive(copy.sprite) && s_spriteNames.TryGetValue(key, out var name) && s_loadedSprites.TryGetValue(name, out var loaded) && Game.Alive(loaded)) sprite = loaded;
        if (Game.Alive(sprite) && (!Game.Alive(copy.sprite) || copy.sprite.Pointer != sprite!.Pointer)) copy.sprite = sprite;
        bool show = Game.Alive(copy.sprite) || copy.color != Color.white;
        if (copy.enabled != show) copy.enabled = show;
    }

    private static bool s_artMissing;
    private static float s_filterBuiltAt;

    private static void SyncArt()
    {
        bool missing = false;
        foreach (var text in s_artTexts)
        {
            if (!Game.Alive(text) || !text.gameObject.activeInHierarchy || string.IsNullOrEmpty(text.text)) continue;
            if (!Game.Alive(text.fontSharedMaterial) && Game.Alive(text.font) && Game.Alive(text.font.material)) text.fontSharedMaterial = text.font.material;
            if (text.textInfo == null || text.textInfo.characterCount == 0)
            {
                text.ForceMeshUpdate();
                missing |= text.textInfo == null || text.textInfo.characterCount == 0;
            }
        }
        foreach (var (copy, source, key) in s_artImages)
        {
            SyncSprite(copy, source, key);
            missing |= Game.Alive(copy) && !Game.Alive(copy.sprite) && s_spriteNames.ContainsKey(key);
        }
        s_artMissing = missing;
        if (missing && (PanelShown || !FilterReady) && Time.unscaledTime >= s_nextSpriteScan)
        {
            // A look through the loaded sprites for the names still wanted: seldom, and only while one is missing.
            s_nextSpriteScan = Time.unscaledTime + 2f;
            var wanted = new HashSet<string>(s_artImages.Where(a => Game.Alive(a.Copy) && !Game.Alive(a.Copy.sprite) && s_spriteNames.ContainsKey(a.Key)).Select(a => s_spriteNames[a.Key]));
            foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()))
                if (obj.TryCast<Sprite>() is { } found && wanted.Contains(found.name)) s_loadedSprites[found.name] = found;
            foreach (var (copy, source, key) in s_artImages) SyncSprite(copy, source, key);
            s_artMissing = s_artImages.Any(a => Game.Alive(a.Copy) && !Game.Alive(a.Copy.sprite) && s_spriteNames.ContainsKey(a.Key));
        }
        if (s_closeArt == null && s_close != null && s_closeSource != null) s_closeArt = HoverOf(s_closeSource, s_close);
        if (s_filterArt == null && s_filter != null && Game.Alive(s_filterSource)) s_filterArt = HoverOf(s_filterSource!, s_filter);
    }

    // Puts a copy where its source sits on Quick View (the copies' holder stands in for Quick View, at its size).
    private static void PlaceCopy(Transform source, RectTransform qv, RectTransform copy)
    {
        if (source.TryCast<RectTransform>() is { } from) copy.sizeDelta = from.rect.size;
        copy.anchorMin = copy.anchorMax = new Vector2(0.5f, 0.5f);
        var q = qv.lossyScale;
        var s = source.lossyScale;
        copy.localScale = new Vector3(s.x / Mathf.Max(0.0001f, q.x), s.y / Mathf.Max(0.0001f, q.y), 1f);
        copy.localPosition = qv.InverseTransformPoint(source.position);
    }

    private static Transform? FindChild(Transform root, string name) =>
        root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == name);

    // The game button's hover look, when it swaps sprites.
    private static HoverArt? HoverOf(Transform source, RectTransform copy)
    {
        var button = source.GetComponent<Button>();
        var image = copy.GetComponent<Image>();
        if (!Game.Alive(button) || !Game.Alive(image) || button!.transition != Selectable.Transition.SpriteSwap) return null;
        var hover = button.spriteState.highlightedSprite;
        return Game.Alive(hover) ? new HoverArt(image!, hover) : null;
    }

    private static void SetHover(HoverArt? art, bool on)
    {
        if (art == null || art.On == on || !Game.Alive(art.Image)) return;
        art.On = on;
        // As the game's buttons do it: the hover sprite over the normal one, which stays.
        art.Image.overrideSprite = on && Game.Alive(art.Hover) ? art.Hover : null;
    }

    // Filter opens the Search page (or closes it when it's the page shown); the panel's X ends the ~ search.
    private static void ButtonInput()
    {
        var pointer = InputState.Pointer;
        bool overFilter = s_filter != null && s_filter.gameObject.activeInHierarchy && Contains(s_filter, pointer);
        bool overClose = s_close != null && s_close.gameObject.activeInHierarchy && Contains(s_close, pointer);
        SetHover(s_filterArt, overFilter);
        SetHover(s_closeArt, overClose);
        if (!InputState.ButtonDown(0)) return;
        if (overFilter) TogglePage();
        else if (overClose) EndBoxSearch();
    }

    /// <summary>Opens the Search page, or closes the menu when the Search page is what it shows.</summary>
    internal static void TogglePage()
    {
        if (Ui.Menu.Visible && Ui.Menu.Current.StartsWith("stash/filter", StringComparison.Ordinal)) Ui.Menu.Close();
        else Ui.Menu.Open("stashsearch");
    }

    // Quick View's own holder: the panel sits beside it there, so the stash window's frame draws over both alike.
    private static RectTransform? PanelHost(StashTabbedUIControls ui) => (QuickView(ui)?.parent ?? ui.transform).TryCast<RectTransform>();

    private static Transform? QuickView(StashTabbedUIControls ui) =>
        Game.Alive(ui.verticalTabWrapper) ? ui.verticalTabWrapper.transform : null;

    // Quick View's size; beside it when it is open, in its place when it is closed, left of the window when the right
    // lacks room.
    private static void Anchor(StashTabbedUIControls ui)
    {
        if (s_panelRect == null || s_art == null || s_panelRect.parent is not { } parent || QuickView(ui)?.TryCast<RectTransform>() is not { } qv) return;
        var ps = s_panelRect.lossyScale;
        float sx = qv.lossyScale.x / Mathf.Max(0.0001f, ps.x), sy = qv.lossyScale.y / Mathf.Max(0.0001f, ps.y);
        var size = qv.rect.size;
        s_art.localScale = new Vector3(sx, sy, 1f);
        s_art.anchoredPosition = new Vector2(size.x * qv.pivot.x * sx, -size.y * (1f - qv.pivot.y) * sy);
        s_panelRect.sizeDelta = new Vector2(size.x * sx, size.y * sy);
        qv.GetWorldCorners(s_corners);
        Vector3 qvTopLeft = s_corners[1], qvTopRight = s_corners[2];
        float worldWidth = qvTopRight.x - qvTopLeft.x, gap = PanelGap * Mathf.Max(0.01f, ps.x);
        Vector3 top;
        if (!qv.gameObject.activeInHierarchy)
        {
            top = qvTopLeft;
            s_panelSide = "in place";
        }
        else if (RightLimit() - qvTopRight.x >= worldWidth + gap)
        {
            top = new Vector3(qvTopRight.x + gap, qvTopRight.y, 0f);
            s_panelSide = "right";
        }
        else
        {
            // No room before the inventory (a larger UI scale): over Quick View, which shows again when the search ends.
            top = qvTopLeft;
            s_panelSide = "over quick view";
        }
        s_panelRect.localPosition = parent.InverseTransformPoint(top);
    }

    // The screen x the panel may reach: the left edge of the game's open right-hand window (the inventory), else the screen's.
    private static float RightLimit()
    {
        float limit = Screen.width;
        try
        {
            var stack = GameObject.Find("Right Panel Stack");
            if (Game.Alive(stack))
                for (int i = 0; i < stack!.transform.childCount; i++)
                    if (stack.transform.GetChild(i) is { } child && child.gameObject.activeInHierarchy && child.TryCast<RectTransform>() is { } r)
                    {
                        r.GetWorldCorners(s_corners);
                        if (s_corners[2].x - s_corners[0].x > 1f) limit = Mathf.Min(limit, s_corners[0].x);
                    }
        }
        catch (Exception)
        {
        }
        return limit;
    }

    // Lays out the lines again when results change, then shows the visible ones with pooled objects.
    private static void FillPanel()
    {
        var groups = BoxGrouped();
        int count = groups.Sum(f => f.Count);
        string head = BoxHeader(groups);
        if (s_header != null && s_header.text != head) s_header.text = head;
        float width = s_viewport!.rect.width;
        int columns = Mathf.Max(1, Mathf.FloorToInt((width + CellGap) / (Cell + CellGap)));
        var stamp = (s_boxGeneration, s_factsGeneration, count, columns);
        if (s_lines == null || s_linesFor != stamp)
        {
            s_lines = Lines(groups, columns);
            s_linesFor = stamp;
        }
        float viewHeight = s_viewport.rect.height;
        float content = s_lines.Count > 0 ? s_lines[^1].Y + s_lines[^1].Height : 0f;
        s_panelScroll = Mathf.Clamp(s_panelScroll, 0f, Mathf.Max(0f, content - viewHeight));
        int cellIndex = 0, textIndex = 0;
        foreach (var line in s_lines)
        {
            if (line.Y + line.Height < s_panelScroll || line.Y > s_panelScroll + viewHeight) continue;
            float y = -(line.Y - s_panelScroll);
            if (line.Cells == null)
            {
                var t = TextAt(textIndex++);
                if (t.Text.text != line.Text)
                {
                    t.Text.text = line.Text;
                    t.Text.fontSize = line.Folder ? 16f : 14f;
                    t.Text.color = line.Folder ? new Color(0.93f, 0.85f, 0.68f) : new Color(0.75f, 0.72f, 0.66f);
                }
                Place(t.Rect, line.Folder ? 0f : 8f, y, width, line.Height);
                continue;
            }
            for (int c = 0; c < line.Cells.Count; c++)
            {
                var cell = CellAt(cellIndex++);
                Show(cell, line.Cells[c], line.TabId);
                Place(cell.Rect, c * (Cell + CellGap), y, Cell, Cell);
            }
        }
        for (int i = textIndex; i < s_texts.Count; i++) if (s_texts[i].Object.activeSelf) s_texts[i].Object.SetActive(false);
        for (int i = cellIndex; i < s_cells.Count; i++)
            if (s_cells[i].Object.activeSelf)
            {
                s_cells[i].Object.SetActive(false);
                s_cells[i].Result = null;
            }
    }

    private static List<PanelLine> Lines(IReadOnlyList<ResultFolder> groups, int columns)
    {
        var lines = new List<PanelLine>();
        float y = 0f;
        foreach (var folder in groups)
        {
            lines.Add(new PanelLine(y, FolderLine, $"{folder.Folder.Name}  ·  {folder.Count}", true, null, -1));
            y += FolderLine;
            foreach (var tab in folder.Tabs)
            {
                lines.Add(new PanelLine(y, TabLine, $"{tab.Tab.Name}  ·  {tab.Items.Count}", false, null, tab.Tab.Id));
                y += TabLine;
                for (int i = 0; i < tab.Items.Count; i += columns)
                {
                    lines.Add(new PanelLine(y, Cell + CellGap, null, false, tab.Items.Skip(i).Take(columns).ToList(), tab.Tab.Id));
                    y += Cell + CellGap;
                }
                y += 4f;
            }
        }
        return lines;
    }

    // Only what changed is set: each change makes the game's canvas rebuild.
    private static void Place(RectTransform r, float x, float y, float w, float h)
    {
        if (!r.gameObject.activeSelf) r.gameObject.SetActive(true);
        var corner = new Vector2(0f, 1f);
        if (r.pivot != corner) r.anchorMin = r.anchorMax = r.pivot = corner;
        var at = new Vector2(x, y);
        if (r.anchoredPosition != at) r.anchoredPosition = at;
        var size = new Vector2(w, h);
        if (r.sizeDelta != size) r.sizeDelta = size;
    }

    private static void Show(PanelCell cell, Result result, int tabId)
    {
        // The item gone to flashes its outline amber, as it does in the stash; otherwise the rarity's colour.
        var flash = PulseColour(result.Place.Entry);
        if (flash is { } amber) cell.Edge.color = amber;
        else if (cell.Flashing) cell.Edge.color = RarityColour(FactsOf(result.Place.Key)?.Rarity);
        cell.Flashing = flash != null;
        // A cell keeps its contents while it shows the same result and its icon has settled.
        if (ReferenceEquals(cell.Result, result) && cell.Settled && cell.Badges == StashSettings.SearchBadges.Value) return;
        cell.Result = result;
        cell.Badges = StashSettings.SearchBadges.Value;
        var facts = FactsOf(result.Place.Key);
        if (!cell.Flashing) cell.Edge.color = RarityColour(facts?.Rarity);
        bool unique = facts != null && facts.Rarity is "unique" or "set" or "legendary";
        var icon = facts != null && Has(IconsGroup) ? GameIcons.Get(facts.Type, facts.Sub, unique ? facts.UniqueId : 0) : null;
        bool hasIcon = icon != null && icon.Ready && Game.Alive(icon.Sprite);
        if (hasIcon && cell.Icon.sprite?.Pointer != icon!.Sprite!.Pointer) cell.Icon.sprite = icon.Sprite;
        cell.Icon.enabled = hasIcon;
        // Without an icon, the item's name stands in.
        cell.Name.gameObject.SetActive(!hasIcon);
        if (!hasIcon) cell.Name.text = facts?.Name ?? "?";
        bool badge = result.Badge != null && StashSettings.SearchBadges.Value;
        cell.Badge.gameObject.SetActive(badge);
        if (badge) cell.Badge.text = result.Badge;
        cell.Settled = hasIcon || icon == null || icon.Missing;
    }

    /// <summary>Rarity edge colours, from the game's item name colours.</summary>
    private static Color RarityColour(string? rarity) => rarity switch
    {
        "magic" => new Color(0.36f, 0.56f, 1f),
        "rare" => new Color(1f, 0.87f, 0.33f),
        "exalted" => new Color(0.74f, 0.45f, 1f),
        "unique" => new Color(1f, 0.55f, 0.18f),
        "set" => new Color(0.35f, 0.88f, 0.42f),
        "legendary" => new Color(1f, 0.34f, 0.29f),
        _ => new Color(0.8f, 0.8f, 0.8f),
    };

    private static PanelCell CellAt(int i)
    {
        while (s_cells.Count <= i) s_cells.Add(MakeCell());
        return s_cells[i];
    }

    private static PanelText TextAt(int i)
    {
        while (s_texts.Count <= i)
        {
            var t = Text(s_viewport!, "Line", 14f, TextAlignmentOptions.Left);
            s_texts.Add(t);
        }
        return s_texts[i];
    }

    private static PanelCell MakeCell()
    {
        var go = new GameObject(PanelName + ".Cell");
        var rect = go.AddComponent<RectTransform>();
        rect.SetParent(s_viewport, false);
        var edge = go.AddComponent<Image>();
        edge.raycastTarget = false;
        var inner = new GameObject(PanelName + ".Back");
        var innerRect = inner.AddComponent<RectTransform>();
        innerRect.SetParent(rect, false);
        innerRect.anchorMin = Vector2.zero;
        innerRect.anchorMax = Vector2.one;
        innerRect.offsetMin = new Vector2(2f, 2f);
        innerRect.offsetMax = new Vector2(-2f, -2f);
        var back = inner.AddComponent<Image>();
        back.raycastTarget = false;
        back.color = new Color(0.05f, 0.045f, 0.04f, 0.95f);
        var iconGo = new GameObject(PanelName + ".Icon");
        var iconRect = iconGo.AddComponent<RectTransform>();
        iconRect.SetParent(rect, false);
        iconRect.anchorMin = Vector2.zero;
        iconRect.anchorMax = Vector2.one;
        iconRect.offsetMin = new Vector2(4f, 4f);
        iconRect.offsetMax = new Vector2(-4f, -4f);
        var icon = iconGo.AddComponent<Image>();
        icon.raycastTarget = false;
        icon.preserveAspect = true;
        var name = Text(rect, "Name", 10f, TextAlignmentOptions.Center);
        name.Text.enableWordWrapping = true;
        name.Text.overflowMode = TextOverflowModes.Ellipsis;
        name.Rect.anchorMin = Vector2.zero;
        name.Rect.anchorMax = Vector2.one;
        name.Rect.offsetMin = new Vector2(3f, 3f);
        name.Rect.offsetMax = new Vector2(-3f, -3f);
        var badge = Text(rect, "Badge", 12f, TextAlignmentOptions.BottomRight);
        badge.Text.color = new Color(1f, 0.92f, 0.7f);
        badge.Text.outlineWidth = 0.25f;
        badge.Rect.anchorMin = Vector2.zero;
        badge.Rect.anchorMax = Vector2.one;
        badge.Rect.offsetMin = new Vector2(2f, 1f);
        badge.Rect.offsetMax = new Vector2(-3f, -2f);
        go.SetActive(false);
        return new PanelCell { Object = go, Rect = rect, Edge = edge, Icon = icon, Badge = badge.Text, Name = name.Text };
    }

    private static PanelText Text(RectTransform parent, string name, float size, TextAlignmentOptions align)
    {
        var go = new GameObject(PanelName + "." + name);
        var rect = go.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        var text = go.AddComponent<TextMeshProUGUI>();
        if (s_font != null) text.font = s_font;
        if (s_fontMaterial != null) text.fontSharedMaterial = s_fontMaterial;
        text.fontSize = size;
        text.alignment = align;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.raycastTarget = false;
        text.color = new Color(0.93f, 0.89f, 0.8f);
        return new PanelText { Object = go, Rect = rect, Text = text };
    }

    // ---- input: hover, click, wheel; polled against the cells' rects ----

    private static void PanelInput(StashTabbedUIControls ui)
    {
        Vector2 mouse = InputState.Pointer;
        if (s_panelRect == null || !Contains(s_panelRect, mouse))
        {
            LeaveCell();
            return;
        }
        float wheel = Input.mouseScrollDelta.y;
        if (wheel != 0f) s_panelScroll -= wheel * (Cell + CellGap);
        PanelCell? over = null;
        foreach (var cell in s_cells)
            if (cell.Object.activeSelf && cell.Result != null && Contains(cell.Rect, mouse) && Contains(s_viewport!, mouse))
            {
                over = cell;
                break;
            }
        if (over != s_hoverCell || over != null && !Game.Alive(over.Object))
        {
            LeaveCell();
            if (over != null && Has(TooltipGroup) && ItemAt(over.Result!.Place) is { } item)
            {
                // Beside the panel, level with the cell, on the side away from the stash window.
                over.Rect.GetWorldCorners(s_corners);
                float cellTop = s_corners[1].y;
                s_panelRect!.GetWorldCorners(s_corners);
                float half = (TooltipWithCompare / 2f + PanelGap) * s_panelRect.lossyScale.x;
                var at = s_panelSide == "left" ? new Vector3(s_corners[0].x - half, cellTop, 0f) : new Vector3(s_corners[2].x + half, cellTop, 0f);
                bool toRight = s_panelSide != "left";
                float gap = PanelGap * s_panelRect.lossyScale.x;
                ItemTooltip.Open(item, over.Object, at, besideEdge: toRight ? s_corners[2].x + gap : s_corners[0].x - gap, toRight: toRight);
                s_hoverCell = over;
            }
            else s_hoverCell = over;
        }
        if (over != null && InputState.ButtonDown(0)) GoTo(over.Result!.Place, fromPage: false);
    }

    private static void LeaveCell()
    {
        if (s_hoverCell == null) return;
        s_hoverCell = null;
        ItemTooltip.Close();
    }

    private static bool Contains(RectTransform r, Vector2 screen)
    {
        r.GetWorldCorners(s_corners);
        // The stash window's canvas is an overlay: world space is screen pixels from the bottom left.
        return screen.x >= s_corners[0].x && screen.x <= s_corners[2].x && screen.y >= s_corners[0].y && screen.y <= s_corners[2].y;
    }

    /// <summary>The game's item at a place, looked up now (nothing is kept across frames).</summary>
    internal static ItemDataUnpacked? ItemAt(ItemPlace place)
    {
        var containers = Containers();
        if (containers == null || place.TabId < 0 || place.TabId >= containers.Count) return null;
        var content = containers[place.TabId]?.content;
        for (int k = 0; content != null && k < content.Count; k++)
            if (content[k] is { } e && e.Pointer == place.Entry) return e.data?.getAsUnpacked();
        return null;
    }

    private static void DestroyPanel()
    {
        LeaveCell();
        if (Game.Alive(s_panel)) UnityEngine.Object.Destroy(s_panel);
        if (Game.Alive(s_filter)) UnityEngine.Object.Destroy(s_filter!.gameObject);
        DestroyPageAnchor();
        s_panel = s_body = null;
        s_panelRect = s_art = s_viewport = s_close = s_filter = null;
        s_closeArt = s_filterArt = null;
        s_artImages.Clear();
        s_artTexts.Clear();
        s_closeSource = s_filterSource = null;
        s_artMissing = false;
        s_header = null;
        s_cells.Clear();
        s_texts.Clear();
        s_lines = null;
        s_panelSide = "none";
    }

    /// <summary>Removes panels an earlier load left (found by name).</summary>
    private static void SweepPanels()
    {
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>(), true))
        {
            var r = obj.TryCast<RectTransform>();
            if (Game.Alive(r) && (r!.name == PanelName && r.gameObject != s_panel || r.name == FilterName && r != s_filter || r.name == PageAnchorName && r.gameObject != s_pageAnchor))
                UnityEngine.Object.Destroy(r.gameObject);
        }
    }

    private static string FilterState()
    {
        s_filter!.GetWorldCorners(s_corners);
        return $"{(s_filter.gameObject.activeInHierarchy ? "shown" : "hidden")} ({s_corners[0].x:0},{Screen.height - s_corners[2].y:0}) {s_corners[2].x - s_corners[0].x:0}x{s_corners[2].y - s_corners[0].y:0}";
    }

    /// <summary>For the dev verb: the panel's state, and how many panel roots the scene holds (a reload leaves none).</summary>
    internal static string PanelState()
    {
        int roots = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>(), true)
            .Count(o => o.TryCast<RectTransform>() is { } r && Game.Alive(r) && (r.name == PanelName || r.name == FilterName));
        if (!Game.Alive(s_panel)) return $"no panel (window {Stash.StashWindowOpen}, group {Has(PanelGroup)}), roots in the scene {roots}";
        s_panelRect!.GetWorldCorners(s_corners);
        return $"panel {(s_panel!.activeSelf ? "shown" : "hidden (no search)")}, side {s_panelSide}, screen rect ({s_corners[0].x:0},{Screen.height - s_corners[2].y:0}) {s_corners[2].x - s_corners[0].x:0}x{s_corners[2].y - s_corners[0].y:0}, "
            + $"scale {s_panelRect.lossyScale.x:0.###}, lines {s_lines?.Count ?? 0}, cells shown {s_cells.Count(c => c.Object.activeSelf)} of pool {s_cells.Count}, "
            + $"item elements {s_itemUis.Count} (stash entries {s_stashEntries.Count}, match entries {s_matchEntries.Count}), "
            + $"roots in the scene {roots}, scroll {s_panelScroll:0}, hovered {(s_hoverCell?.Result is { } r ? $"tab {r.Place.TabId} ({r.Place.X},{r.Place.Y})" : "none")}, icons waiting {GameIcons.Pending}, filter button {(Game.Alive(s_filter) ? FilterState() : "none")}";
    }
}
