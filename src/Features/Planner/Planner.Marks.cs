using EpochAssist.Core;
using EpochAssist.Features.PlannerFormats;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;
using Feature = EpochAssist.Core.Feature;
using S =EpochAssist.Features.PlannerSettings;

namespace EpochAssist.Features;

/// <summary>A node label's side of the node and its offset from there, in the node's units.</summary>
internal readonly record struct LabelPlace(LabelSide Side, float X, float Y, LabelBack Back);

/// <summary>One mark: a ring under a game element, and its label when it has one.</summary>
internal sealed class PlannerMark
{
    public GameObject Object = null!;
    public Image Ring = null!;
    public Text? Label;
    /// <summary>A node's label: the game's own text, in the node counter's font.</summary>
    public TextMeshProUGUI? NodeText;
    /// <summary>The dark plate under a node's label, so it reads over the nodes and counters around it.</summary>
    public Image? Plate;
    /// <summary>The element the mark is for.</summary>
    public Transform Node = null!;
    /// <summary>What the element is to the plan, for the hover hint.</summary>
    public MarkHint Hint;
    /// <summary>The faint tint over the node's inside, a sibling drawn under the ring; null when off or not possible.</summary>
    public Image? Fill;
    public bool FillSought;
    /// <summary>The ring's steady colour; the pulse changes the ring's own colour around it.</summary>
    public Color Colour;
    public Vector3 BaseScale = Vector3.one;
    public float Scale = 1f;
    public IntPtr Parent;
    /// <summary>The marking pass that last kept it; a mark the latest pass skipped is cleared.</summary>
    public int Pass;
    /// <summary>The game art a frame corner is drawn with; null for the plain outline.</summary>
    public Sprite? Art;
}

internal static partial class Planner
{
    /// <summary>
    /// Marks are the mod's own objects, each a child of the game element it marks, so they follow the panel's pan, zoom
    /// and scale with no conversion. The game's own images are never changed, and a mark never takes a click.
    /// </summary>
    internal static class Marks
    {
        public const string MarkName = "EA.PlannerMark";

        private const int RingSize = 32;
        private const int RingRadius = 9;
        private const float RingWidth = 3f;
        // The ring's 9-slice border: the rounded corners keep their size whatever the node's.
        private const float RingBorder = 12f;
        // How far the ring sits outside the element's edges.
        private const float Pad = 4f;
        private const int LabelSize = 14;

        private static readonly Dictionary<IntPtr, PlannerMark> s_marks = new();
        private static readonly List<PlannerMark> s_dead = new();
        private static Texture2D? s_texture, s_circleTexture;
        private static Sprite? s_ring, s_circle;
        private static Font? s_font;

        public static int Count => s_marks.Count;

        private static readonly Il2CppSystem.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult> s_hits = new();
        private static UnityEngine.EventSystems.PointerEventData? s_pointer;
        private const int HitsLooked = 4;

        /// <summary>The marked element under a screen point (bottom-left origin): the first of the game's UI hits there that
        /// is, or sits in, an element with a mark. Marks take no hits, so the game's own element answers.</summary>
        public static PlannerMark? Under(Vector3 point)
        {
            if (s_marks.Count == 0) return null;
            var events = UnityEngine.EventSystems.EventSystem.current;
            if (!Game.Alive(events)) return null;
            if (s_pointer == null || !Game.Alive(s_pointer)) s_pointer = new UnityEngine.EventSystems.PointerEventData(events);
            s_pointer.position = point;
            s_hits.Clear();
            events!.RaycastAll(s_pointer, s_hits);
            for (int i = 0; i < s_hits.Count && i < HitsLooked; i++)
            {
                var hit = s_hits[i].gameObject;
                if (!Game.Alive(hit)) continue;
                for (var t = hit.transform; t != null; t = t.parent)
                    if (s_marks.TryGetValue(t.Pointer, out var mark) && Game.Alive(mark.Object)) return mark;
            }
            return null;
        }

        /// <summary>The element's mark, made on first use and reused after; null when it couldn't be made.
        /// <paramref name="scale"/> sizes the ring about its centre; a <paramref name="fill"/> with no alpha draws no fill.</summary>
        public static PlannerMark? Mark(Transform parent, Color colour, string? label, float scale, Color fill, int labelSize, LabelPlace place) =>
            Mark(parent, colour, label, true, scale, fill, labelSize, place);

        private static PlannerMark? Mark(Transform parent, Color colour, string? label, bool frame, float scale, Color fill, int labelSize,
            LabelPlace place = default)
        {
            if (!Game.Alive(parent)) return null;
            if (s_marks.TryGetValue(parent.Pointer, out var mark))
            {
                if (Game.Alive(mark.Object))
                {
                    Recolour(mark, colour);
                    Restyle(parent, mark, scale, fill);
                    SetLabel(mark, label, labelSize, place, frame);
                    return mark;
                }
                s_marks.Remove(parent.Pointer);
                Destroy(mark);
            }
            // A node whose frame isn't drawn yet (a panel still opening) gets no mark now rather than a plain square; the
            // marking pass sees FrameMissing and tries again.
            if (frame && Frame(parent) == null)
            {
                FrameMissing = true;
                return null;
            }
            try
            {
                mark = Make(parent, colour, frame);
                s_marks[parent.Pointer] = mark;
                Restyle(parent, mark, scale, fill);
                SetLabel(mark, label, labelSize, place, frame);
                return mark;
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("planner:mark", $"Planner: a mark couldn't be made: {e.Message}", 30);
                return null;
            }
        }

        /// <summary>
        /// A small icon at one corner of an element: a rounded square in the state colour with a dark edge, inset by
        /// <paramref name="inset"/> and <paramref name="size"/> wide in the element's units. Made on first use, moved and
        /// resized after; null when it couldn't be made. <paramref name="corner"/> is the corner as anchor (0 or 1 each).
        /// </summary>
        public static PlannerMark? Icon(Transform parent, Color colour, Vector2 corner, float size, float inset = 2f)
        {
            if (!Game.Alive(parent)) return null;
            if (!s_marks.TryGetValue(parent.Pointer, out var mark) || !Game.Alive(mark.Object) || mark.Object.GetComponent<RectMask2D>() != null)
            {
                if (mark != null)
                {
                    s_marks.Remove(parent.Pointer);
                    Destroy(mark);
                }
                try
                {
                    var go = new GameObject(MarkName);
                    var rect = go.AddComponent<RectTransform>();
                    rect.SetParent(parent, false);
                    go.AddComponent<LayoutElement>().ignoreLayout = true;
                    var body = go.AddComponent<Image>();
                    body.raycastTarget = false;
                    body.sprite = PlateSprite();
                    body.type = Image.Type.Sliced;
                    var edge = new GameObject(MarkName + ".Edge");
                    var edgeRect = edge.AddComponent<RectTransform>();
                    edgeRect.SetParent(rect, false);
                    edgeRect.anchorMin = Vector2.zero;
                    edgeRect.anchorMax = Vector2.one;
                    edgeRect.offsetMin = edgeRect.offsetMax = Vector2.zero;
                    var ring = edge.AddComponent<Image>();
                    ring.raycastTarget = false;
                    ring.sprite = Ring();
                    ring.type = Image.Type.Sliced;
                    ring.color = IconEdge;
                    rect.SetAsLastSibling();
                    mark = new PlannerMark { Object = go, Ring = body, Colour = Color.clear, Parent = parent.Pointer, Node = parent };
                    s_marks[parent.Pointer] = mark;
                }
                catch (Exception e)
                {
                    Log.ErrorThrottled("planner:mark", $"Planner: an icon couldn't be made: {e.Message}", 30);
                    return null;
                }
            }
            var r = mark.Object.transform.TryCast<RectTransform>()!;
            if (r.GetSiblingIndex() != parent.childCount - 1) r.SetAsLastSibling();
            r.anchorMin = r.anchorMax = r.pivot = corner;
            r.sizeDelta = new Vector2(size, size);
            r.anchoredPosition = new Vector2(corner.x > 0.5f ? -inset : inset, corner.y > 0.5f ? -inset : inset);
            Recolour(mark, colour);
            return mark;
        }

        private static readonly Color IconEdge = new(0.04f, 0.04f, 0.05f, 0.9f);


        /// <summary>
        /// A ring of a game image's own art in the state colour: the part of <paramref name="image"/>'s art between
        /// <paramref name="inner"/> and <paramref name="outer"/> (radii in the image's local units) round
        /// <paramref name="centre"/>, recoloured and laid exactly over it. A child of the image, so it draws just after it
        /// and under whatever the panel draws later.
        /// </summary>
        /// <summary>
        /// A plain round ring of <paramref name="outer"/> radius on an element's centre, in the element's own units, with
        /// its band <see cref="CircleBand"/> of the radius wide: for a slot whose own art leaves no room for a ring on it
        /// (a blessing's roll gauge fills its edge).
        /// </summary>
        public static PlannerMark? CircleRing(RectTransform host, float outer, Color colour)
        {
            if (!Game.Alive(host)) return null;
            if (!s_marks.TryGetValue(host.Pointer, out var mark) || !Game.Alive(mark.Object))
            {
                if (mark != null)
                {
                    s_marks.Remove(host.Pointer);
                    Destroy(mark);
                }
                try
                {
                    var go = new GameObject(MarkName);
                    go.AddComponent<RectTransform>().SetParent(host, false);
                    go.AddComponent<LayoutElement>().ignoreLayout = true;
                    var face = go.AddComponent<Image>();
                    face.raycastTarget = false;
                    face.sprite = s_circle ??= CircleSprite(out s_circleTexture);
                    mark = new PlannerMark { Object = go, Ring = face, Colour = Color.clear, Parent = host.Pointer, Node = host };
                    s_marks[host.Pointer] = mark;
                }
                catch (Exception e)
                {
                    Log.ErrorThrottled("planner:mark", $"Planner: a ring couldn't be made: {e.Message}", 30);
                    return null;
                }
            }
            var r = mark.Object.transform.TryCast<RectTransform>()!;
            r.anchorMin = r.anchorMax = r.pivot = new Vector2(0.5f, 0.5f);
            r.sizeDelta = new Vector2(2f * outer, 2f * outer);
            r.anchoredPosition = Vector2.zero;
            // Over what the slot draws after its host (a Grand blessing's gold frame), under the game's tooltips.
            Above(mark.Object, host);
            mark.Ring.color = mark.Colour = colour;
            return mark;
        }

        /// <summary>
        /// A copy of an element's own art over it, in <paramref name="colour"/> with the art's light and shade: for a game
        /// border that should itself show the state (a selected blessing slot's border).
        /// </summary>
        public static PlannerMark? ArtCopy(Image image, Color colour)
        {
            if (!Game.Alive(image) || !Game.Alive(image.sprite)) return null;
            var host = image.rectTransform;
            if (!s_marks.TryGetValue(host.Pointer, out var mark) || !Game.Alive(mark.Object) || mark.Art != image.sprite)
            {
                if (mark != null)
                {
                    s_marks.Remove(host.Pointer);
                    Destroy(mark);
                }
                try
                {
                    var go = new GameObject(MarkName);
                    var r = go.AddComponent<RectTransform>();
                    r.SetParent(host, false);
                    r.anchorMin = Vector2.zero;
                    r.anchorMax = Vector2.one;
                    r.offsetMin = r.offsetMax = Vector2.zero;
                    go.AddComponent<LayoutElement>().ignoreLayout = true;
                    var face = go.AddComponent<Image>();
                    face.raycastTarget = false;
                    face.sprite = WhiteFrame(image.sprite) ?? image.sprite;
                    face.preserveAspect = image.preserveAspect;
                    mark = new PlannerMark { Object = go, Ring = face, Colour = Color.clear, Parent = host.Pointer, Node = host, Art = image.sprite };
                    s_marks[host.Pointer] = mark;
                }
                catch (Exception e)
                {
                    Log.ErrorThrottled("planner:mark", $"Planner: a border copy couldn't be made: {e.Message}", 30);
                    return null;
                }
            }
            Above(mark.Object, host);
            mark.Ring.color = mark.Colour = colour;
            return mark;
        }

        /// <summary>A round ring's band, as a share of its radius.</summary>
        public const float CircleBand = 0.09f;
        private const int CircleSize = 128;

        private static Sprite CircleSprite(out Texture2D made)
        {
            var texture = new Texture2D(CircleSize, CircleSize, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            float half = CircleSize / 2f, band = half * CircleBand;
            for (int y = 0; y < CircleSize; y++)
                for (int x = 0; x < CircleSize; x++)
                {
                    float d = new Vector2(x + 0.5f - half, y + 0.5f - half).magnitude;
                    // Soft by a pixel at both edges.
                    float alpha = Mathf.Clamp01(half - 0.5f - d) * Mathf.Clamp01(d - (half - band) + 0.5f);
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            texture.Apply(false, true);
            made = texture;
            var sprite = Sprite.Create(texture, new Rect(0f, 0f, CircleSize, CircleSize), new Vector2(0.5f, 0.5f), 100f, 0u, SpriteMeshType.FullRect);
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        public static PlannerMark? ArtRing(Image image, Vector2 centre, float inner, float outer, Color colour, bool onTop = false)
        {
            var art = image.sprite;
            if (!Game.Alive(image) || !Game.Alive(art)) return null;
            var host = image.rectTransform;
            if (!s_marks.TryGetValue(host.Pointer, out var mark) || !Game.Alive(mark.Object))
            {
                if (mark != null)
                {
                    s_marks.Remove(host.Pointer);
                    Destroy(mark);
                }
                try
                {
                    var go = new GameObject(MarkName);
                    go.AddComponent<RectTransform>().SetParent(host, false);
                    go.AddComponent<LayoutElement>().ignoreLayout = true;
                    var face = go.AddComponent<Image>();
                    face.raycastTarget = false;
                    mark = new PlannerMark { Object = go, Ring = face, Colour = Color.clear, Parent = host.Pointer, Node = host, Art = art };
                    s_marks[host.Pointer] = mark;
                }
                catch (Exception e)
                {
                    Log.ErrorThrottled("planner:mark", $"Planner: a ring couldn't be made: {e.Message}", 30);
                    return null;
                }
            }
            var r = mark.Object.transform.TryCast<RectTransform>()!;
            r.anchorMin = r.anchorMax = r.pivot = new Vector2(0.5f, 0.5f);
            r.sizeDelta = new Vector2(2f * outer, 2f * outer);
            r.anchoredPosition = centre - host.rect.center;
            // Over what the panel draws after the image (a locked slot's padlock), under the game's tooltips.
            if (onTop) Above(mark.Object, host);
            if (mark.Colour != colour)
            {
                // The art as the image draws it, to map the ring into the sprite's pixels.
                var drawn = Drawn(image, art);
                var scale = new Vector2(art.rect.width / drawn.width, art.rect.height / drawn.height);
                var pixelCentre = (centre - drawn.min) * scale;
                if (RingCopy(art, pixelCentre, inner * scale.x, outer * scale.x, colour) is { } copy)
                {
                    mark.Ring.sprite = copy;
                    mark.Colour = colour;
                }
            }
            return mark;
        }

        // Where an image draws its sprite inside its rect: the whole rect, or kept to the sprite's shape and placed by
        // the image's pivot when it keeps its aspect, as the game draws it.
        private static Rect Drawn(Image image, Sprite art)
        {
            var drawn = image.rectTransform.rect;
            if (!image.preserveAspect || art.rect.height <= 0f || drawn.height <= 0f) return drawn;
            float aspect = art.rect.width / art.rect.height;
            var size = drawn.width / drawn.height > aspect ? new Vector2(drawn.height * aspect, drawn.height) : new Vector2(drawn.width, drawn.width / aspect);
            var pivot = image.rectTransform.pivot;
            return new Rect(drawn.x + (drawn.width - size.x) * pivot.x, drawn.y + (drawn.height - size.y) * pivot.y, size.x, size.y);
        }

        // A sprite's pixels, read through a render texture since game atlases aren't readable; null when they can't be.
        private static Color32[]? ReadArt(Sprite art, out int w, out int h)
        {
            var area = art.textureRect;
            w = Mathf.Max(1, Mathf.RoundToInt(area.width));
            h = Mathf.Max(1, Mathf.RoundToInt(area.height));
            var before = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Texture2D? texture = null;
            try
            {
                var source = art.texture;
                Graphics.Blit(source, rt, new Vector2(area.width / source.width, area.height / source.height), new Vector2(area.x / source.width, area.y / source.height));
                RenderTexture.active = rt;
                texture = new Texture2D(w, h, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0f, 0f, w, h), 0, 0, false);
                return texture.GetPixels32();
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("planner:art", $"Planner: game art couldn't be read: {e.Message}", 30);
                return null;
            }
            finally
            {
                RenderTexture.active = before;
                RenderTexture.ReleaseTemporary(rt);
                if (texture != null) UnityEngine.Object.Destroy(texture);
            }
        }

        // The art's pixels between two radii round a point (sprite pixels, bottom-left origin), recoloured, as a square
        // sprite the outer radius wide; outside the ring is clear, its edges softened over a pixel. Made once per colour.
        private static Sprite? RingCopy(Sprite art, Vector2 centre, float inner, float outer, Color colour)
        {
            string key = $"{art.name}#ring#{centre.x:0}#{centre.y:0}#{inner:0}#{outer:0}#{ColorUtility.ToHtmlStringRGB(colour)}";
            if (s_white.TryGetValue(key, out var made)) return Game.Alive(made) ? made : null;
            Sprite? ring = null;
            if (ReadArt(art, out int w, out int h) is { } pixels)
            {
                int brightest = 1;
                foreach (var p in pixels)
                    if (p.a >= 32) brightest = Math.Max(brightest, (p.r * 77 + p.g * 150 + p.b * 29) >> 8);
                int side = Mathf.Max(1, Mathf.CeilToInt(2f * outer));
                var cut = new Color32[side * side];
                for (int y = 0; y < side; y++)
                {
                    for (int x = 0; x < side; x++)
                    {
                        float sx = centre.x - outer + x + 0.5f, sy = centre.y - outer + y + 0.5f;
                        int ix = Mathf.FloorToInt(sx), iy = Mathf.FloorToInt(sy);
                        if (ix < 0 || iy < 0 || ix >= w || iy >= h) continue;
                        float d = Vector2.Distance(new Vector2(sx, sy), centre);
                        float keep = Mathf.Clamp01(outer - d + 0.5f) * Mathf.Clamp01(d - inner + 0.5f);
                        if (keep <= 0f) continue;
                        var p = pixels[iy * w + ix];
                        int grey = Math.Min(255, ((p.r * 77 + p.g * 150 + p.b * 29) >> 8) * 255 / brightest);
                        var c = Recoloured(colour, p, grey / 255f);
                        c.a = (byte)(c.a * keep);
                        cut[y * side + x] = c;
                    }
                }
                var texture = new Texture2D(side, side, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                texture.SetPixels32(cut);
                texture.Apply(false, true);
                ring = Sprite.Create(texture, new Rect(0f, 0f, side, side), new Vector2(0.5f, 0.5f), art.pixelsPerUnit);
                ring.hideFlags = HideFlags.HideAndDontSave;
                s_whiteTextures.Add(texture);
            }
            s_white[key] = ring;
            return ring;
        }

        /// <summary>
        /// A small badge at one corner inside an element: an optional round <paramref name="back"/> drawn as it is, and
        /// <paramref name="glyph"/> recoloured to the state colour keeping its own shading. <paramref name="size"/> wide,
        /// inset by <paramref name="inset"/> and moved by <paramref name="offset"/> (x right, y down), at
        /// <paramref name="opacity"/>.
        /// </summary>
        public static PlannerMark? Badge(Transform parent, Sprite? back, Sprite glyph, float glyphScale, Color colour, Vector2 corner, float size,
            Vector2 offset, float opacity, float inset = 4f)
        {
            if (!Game.Alive(parent) || !Game.Alive(glyph)) return null;
            if (!s_marks.TryGetValue(parent.Pointer, out var mark) || !Game.Alive(mark.Object) || mark.Art?.Pointer != glyph.Pointer)
            {
                if (mark != null)
                {
                    s_marks.Remove(parent.Pointer);
                    Destroy(mark);
                }
                try
                {
                    var go = new GameObject(MarkName);
                    go.AddComponent<RectTransform>().SetParent(parent, false);
                    go.AddComponent<LayoutElement>().ignoreLayout = true;
                    var body = go.AddComponent<Image>();
                    body.raycastTarget = false;
                    body.preserveAspect = true;
                    var face = new GameObject(MarkName + ".Glyph");
                    face.AddComponent<RectTransform>().SetParent(go.transform, false);
                    var glyphImage = face.AddComponent<Image>();
                    glyphImage.raycastTarget = false;
                    glyphImage.preserveAspect = true;
                    glyphImage.sprite = glyph;
                    mark = new PlannerMark { Object = go, Ring = glyphImage, Colour = Color.clear, Parent = parent.Pointer, Node = parent, Art = glyph };
                    s_marks[parent.Pointer] = mark;
                }
                catch (Exception e)
                {
                    Log.ErrorThrottled("planner:mark", $"Planner: a badge couldn't be made: {e.Message}", 30);
                    return null;
                }
            }
            var r = mark.Object.transform.TryCast<RectTransform>()!;
            if (r.GetSiblingIndex() != parent.childCount - 1) r.SetAsLastSibling();
            r.anchorMin = r.anchorMax = r.pivot = corner;
            r.sizeDelta = new Vector2(size, size);
            r.anchoredPosition = new Vector2((corner.x > 0.5f ? -inset : inset) + offset.x, (corner.y > 0.5f ? -inset : inset) - offset.y);
            Above(mark.Object, parent);
            // The stone background, drawn as the game draws it; none leaves the glyph the whole badge.
            var background = mark.Object.GetComponent<Image>();
            bool stone = back != null && Game.Alive(back);
            if (stone) background.sprite = back;
            background.color = stone ? new Color(1f, 1f, 1f, opacity) : Color.clear;
            var glyphRect = mark.Ring.rectTransform;
            glyphRect.anchorMin = new Vector2(0.5f - glyphScale / 2f, 0.5f - glyphScale / 2f);
            glyphRect.anchorMax = new Vector2(0.5f + glyphScale / 2f, 0.5f + glyphScale / 2f);
            glyphRect.offsetMin = glyphRect.offsetMax = Vector2.zero;
            if (mark.Colour != colour && WhiteFrame(glyph, 0f, colour) is { } recoloured)
            {
                mark.Ring.sprite = recoloured;
                mark.Colour = colour;
            }
            mark.Ring.color = new Color(1f, 1f, 1f, opacity);
            return mark;
        }

        // Drawn on a canvas of its own one step above the panel's, so the panel's later cells, frames and items never
        // cover it; the game's tooltips sit higher still (the inventory draws at 101, the item tooltip at 105).
        private static void Above(GameObject mark, Transform parent)
        {
            var panel = parent.GetComponentInParent<Canvas>();
            if (!Game.Alive(panel)) return;
            var own = mark.GetComponent<Canvas>();
            if (!Game.Alive(own)) own = mark.AddComponent<Canvas>();
            own!.overrideSorting = true;
            own.sortingLayerID = panel!.sortingLayerID;
            int order = panel.sortingOrder + 1;
            if (own.sortingOrder != order) own.sortingOrder = order;
        }

        /// <summary>Whether a screen point (bottom-left origin) is on a mark's own image; <paramref name="camera"/> is its
        /// canvas's camera, null for an overlay canvas.</summary>
        public static bool Contains(PlannerMark mark, Vector2 point, Camera? camera)
        {
            if (!Game.Alive(mark.Object)) return false;
            var rect = mark.Object.transform.TryCast<RectTransform>();
            return Game.Alive(rect) && RectTransformUtility.RectangleContainsScreenPoint(rect, point, camera);
        }

        /// <summary>A line of words on a panel element, with no ring: a note about the whole panel rather than one node.
        /// Under the element's bottom edge, or with <paramref name="top"/> just inside its top edge.</summary>
        public static PlannerMark? Note(Transform parent, Color colour, string text, bool top = false, bool left = false, float drop = 0f)
        {
            // No node frame: a panel holding a tree has its nodes' frames among its children.
            var mark = Mark(parent, colour, text, false, 1f, Color.clear, LabelSize);
            if (mark == null || !Game.Alive(mark.Label)) return mark;
            if (mark.Ring.enabled)
            {
                mark.Ring.enabled = false;
                if (Game.Alive(mark.Plate)) mark.Plate!.gameObject.SetActive(false);
                // As wide as the element.
                var rect = mark.Label!.rectTransform;
                float edge = top ? 1f : 0f;
                rect.anchorMin = new Vector2(0f, edge);
                rect.anchorMax = new Vector2(1f, edge);
                rect.pivot = new Vector2(0.5f, 1f);
                rect.sizeDelta = new Vector2(0f, LabelSize + 6f);
                rect.anchoredPosition = top ? new Vector2(0f, -Pad) : new Vector2(0f, -drop);
                mark.Label.alignment = left ? TextAnchor.UpperLeft : TextAnchor.UpperCenter;
            }
            return mark;
        }

        // The very next step's frame pulses in its own colour so the next point stands out from the other numbered steps.
        /// <summary>Set by a marking pass when a node frame wasn't drawn yet, so the pass runs again.</summary>
        public static bool FrameMissing;

        private static PlannerMark? s_pulse;
        private const float PulseSeconds = 1.2f, PulseLow = 0.45f;

        /// <summary>Makes this mark the pulsing one (null for none), putting the previous one back to its colour.</summary>
        public static void PulseOn(PlannerMark? mark)
        {
            if (s_pulse != null && s_pulse != mark && Game.Alive(s_pulse.Ring)) s_pulse.Ring.color = s_pulse.Colour;
            s_pulse = mark;
        }

        /// <summary>One frame of the pulse; nothing when no mark pulses.</summary>
        public static void Pulse()
        {
            if (s_pulse == null) return;
            if (!Game.Alive(s_pulse.Ring))
            {
                s_pulse = null;
                return;
            }
            float t = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * (2f * Mathf.PI / PulseSeconds));
            float k = Mathf.Lerp(PulseLow, 1f, t);
            var c = s_pulse.Colour;
            s_pulse.Ring.color = new Color(c.r * k, c.g * k, c.b * k, c.a);
        }

        public static void Recolour(PlannerMark mark, Color colour)
        {
            if (!Game.Alive(mark.Ring) || mark.Colour == colour) return;
            mark.Colour = colour;
            mark.Ring.color = colour;
            if (Game.Alive(mark.Label)) mark.Label!.color = colour;
            if (Game.Alive(mark.NodeText)) mark.NodeText!.color = colour;
        }

        // Size about the ring's centre, with the label kept at its own size, and the fill made on first need.
        private static void Restyle(Transform parent, PlannerMark mark, float scale, Color fill)
        {
            if (mark.Scale != scale)
            {
                mark.Scale = scale;
                mark.Object.transform.localScale = mark.BaseScale * scale;
                if (Game.Alive(mark.Label)) mark.Label!.transform.localScale = Vector3.one / scale;
            }
            bool on = fill.a > 0f;
            if (on && !mark.FillSought)
            {
                mark.FillSought = true;
                mark.Fill = MakeFill(parent, mark);
            }
            if (!Game.Alive(mark.Fill)) return;
            if (mark.Fill!.enabled != on) mark.Fill.enabled = on;
            if (on && mark.Fill.color != fill) mark.Fill.color = fill;
        }

        /// <summary>Destroys one mark.</summary>
        public static void Clear(PlannerMark mark)
        {
            // A cleared mark's element may carry a newer mark by now.
            if (s_marks.TryGetValue(mark.Parent, out var current) && current == mark) s_marks.Remove(mark.Parent);
            Destroy(mark);
        }

        /// <summary>Destroys every mark.</summary>
        public static void ClearAll()
        {
            if (s_marks.Count == 0) return;
            s_dead.AddRange(s_marks.Values);
            s_marks.Clear();
            foreach (var mark in s_dead) Destroy(mark);
            s_dead.Clear();
        }

        private static void Destroy(PlannerMark mark)
        {
            if (Game.Alive(mark.Object)) UnityEngine.Object.Destroy(mark.Object);
            if (Game.Alive(mark.Fill)) UnityEngine.Object.Destroy(mark.Fill!.gameObject);
            if (Game.Alive(mark.Label)) UnityEngine.Object.Destroy(mark.Label!.gameObject);
            if (Game.Alive(mark.NodeText)) UnityEngine.Object.Destroy(mark.NodeText!.gameObject);
            if (Game.Alive(mark.Plate)) UnityEngine.Object.Destroy(mark.Plate!.gameObject);
        }

        /// <summary>Destroys every mark and the ring texture (the feature stopping).</summary>
        public static void Release()
        {
            ClearAll();
            if (s_ring != null) UnityEngine.Object.Destroy(s_ring);
            if (s_texture != null) UnityEngine.Object.Destroy(s_texture);
            if (s_circle != null) UnityEngine.Object.Destroy(s_circle);
            if (s_circleTexture != null) UnityEngine.Object.Destroy(s_circleTexture);
            s_circle = null;
            s_circleTexture = null;
            if (s_plate != null) UnityEngine.Object.Destroy(s_plate);
            if (s_plateTexture != null) UnityEngine.Object.Destroy(s_plateTexture);
            s_ring = null;
            s_texture = null;
            s_plate = null;
            s_plateTexture = null;
            s_font = null;
            foreach (var sprite in s_white.Values)
                if (Game.Alive(sprite)) UnityEngine.Object.Destroy(sprite);
            foreach (var texture in s_whiteTextures)
                if (Game.Alive(texture)) UnityEngine.Object.Destroy(texture);
            s_white.Clear();
            s_whiteTextures.Clear();
        }

        /// <summary>Destroys marks a previous instance left under the game's panels (a dev reload), found by name.</summary>
        public static void Sweep()
        {
            int swept = 0;
            try
            {
                // Rings and fills are named for the mark; node labels and plates, in the node's container, by a suffix.
                foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Graphic>(), true))
                {
                    var part = obj.TryCast<Graphic>();
                    if (!Game.Alive(part) || !part!.gameObject.name.StartsWith(MarkName, StringComparison.Ordinal)) continue;
                    UnityEngine.Object.Destroy(part.gameObject);
                    if (part.gameObject.name == MarkName) swept++;
                }
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("planner:sweep", $"Planner: old marks couldn't be swept: {e.Message}", 30);
            }
            if (swept > 0) Log.Info($"Planner: removed {swept} mark{(swept == 1 ? string.Empty : "s")} left by an earlier load");
        }

        private static PlannerMark Make(Transform parent, Color colour, bool frame)
        {
            var go = new GameObject(MarkName);
            var rect = go.AddComponent<RectTransform>();
            var ring = go.AddComponent<Image>();
            ring.raycastTarget = false;
            ring.color = colour;
            var border = frame ? Frame(parent) : null;
            if (border != null)
            {
                // The node's own hexagon frame, tinted: our copy sits exactly over the game's border, which stays as it is.
                // Pivoted at its centre, so the outline size setting grows it evenly around the node.
                var source = border.rectTransform;
                // A taken weaver node shows its frame as "BorderActive" inside "Border", under the bright frame drawn after
                // it: the copy lines up with "Border" so it goes above that.
                if (source.parent is { } nested && nested.name == BorderName && nested.TryCast<RectTransform>() is { } outer) source = outer;
                // A specialisation slot's level diamond and its number are children of its "border": the copy goes inside
                // the border, before them, so it tints the frame without covering the level.
                if (source.name == SlotBorderName && source.childCount > 0 && source.gameObject.activeInHierarchy)
                {
                    rect.SetParent(source, false);
                    rect.anchorMin = Vector2.zero;
                    rect.anchorMax = Vector2.one;
                    rect.pivot = new Vector2(0.5f, 0.5f);
                    rect.offsetMin = rect.offsetMax = Vector2.zero;
                    var innerScale = Vector3.one * FrameFit(parent, source);
                    rect.localScale = innerScale;
                    ring.sprite = WhiteFrame(border.sprite!) ?? border.sprite;
                    ring.type = border.type == Image.Type.Sliced ? Image.Type.Sliced : Image.Type.Simple;
                    ring.pixelsPerUnitMultiplier = border.pixelsPerUnitMultiplier;
                    ring.preserveAspect = border.preserveAspect;
                    rect.SetAsFirstSibling();
                    return new PlannerMark { Object = go, Ring = ring, Colour = colour, BaseScale = innerScale, Parent = parent.Pointer, Node = parent };
                }
                rect.SetParent(source.parent, false);
                rect.anchorMin = source.anchorMin;
                rect.anchorMax = source.anchorMax;
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.offsetMin = source.offsetMin;
                rect.offsetMax = source.offsetMax;
                // A skill tree's centre node draws its gold hexagon inside a much larger image; the copy is brought down
                // so its hexagon sits on the node's, not around the whole image.
                var baseScale = source.localScale * FrameFit(parent, source);
                rect.localScale = baseScale;
                rect.localRotation = source.localRotation;
                // The lighter highlighted frame tints brighter than the grey one; either has the same hexagon.
                var bright = source.parent != null ? source.parent.Find(BrightName)?.GetComponent<Image>() : null;
                var frameSprite = Game.Alive(bright) && Game.Alive(bright!.sprite) ? bright.sprite : border.sprite;
                ring.sprite = WhiteFrame(frameSprite!) ?? frameSprite;
                // Drawn as the game draws it: a weaver socket stretches its card frame nine-sliced (square on a small one).
                ring.type = border.type == Image.Type.Sliced ? Image.Type.Sliced : Image.Type.Simple;
                ring.pixelsPerUnitMultiplier = border.pixelsPerUnitMultiplier;
                ring.preserveAspect = border.preserveAspect;
                // Over the game's frames and over its overlays too (the dark square on a node not taken yet would cut
                // through the outline), and under the point counter, which stays readable.
                int after = source.GetSiblingIndex();
                var holder = source.parent;
                for (int i = 0; holder != null && i < holder.childCount; i++)
                {
                    var child = holder.GetChild(i);
                    if (child == rect) continue;
                    string name = child.name;
                    if (name.StartsWith("Border") || name.StartsWith("FlashingBorder") || name.StartsWith("FadeOverlay") || name == "Shine" || name == "HighlightGlow")
                        after = Math.Max(after, child.GetSiblingIndex());
                }
                rect.SetSiblingIndex(Math.Min(after + 1, holder != null ? holder.childCount - 1 : after + 1));
                return new PlannerMark { Object = go, Ring = ring, Colour = colour, BaseScale = baseScale, Parent = parent.Pointer, Node = parent };
            }
            else
            {
                rect.SetParent(parent, false);
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = new Vector2(-Pad, -Pad);
                rect.offsetMax = new Vector2(Pad, Pad);
                ring.sprite = Ring();
                ring.type = Image.Type.Sliced;
            }
            // Drawn after the element's own images.
            rect.SetAsLastSibling();
            return new PlannerMark { Object = go, Ring = ring, Colour = colour, Parent = parent.Pointer, Node = parent };
        }

        // A white copy of the node's icon mask, tinted faintly, drawn over the icon and under the ring. A node without
        // an icon mask gets no fill.
        private static Image? MakeFill(Transform parent, PlannerMark mark)
        {
            var mask = Child(parent, IconMaskName);
            if (mask == null) return null;
            GameObject? go = null;
            try
            {
                go = new GameObject(MarkName);
                var rect = go.AddComponent<RectTransform>();
                var source = mask.rectTransform;
                rect.SetParent(source.parent, false);
                rect.anchorMin = source.anchorMin;
                rect.anchorMax = source.anchorMax;
                rect.pivot = source.pivot;
                rect.anchoredPosition = source.anchoredPosition;
                rect.sizeDelta = source.sizeDelta;
                rect.localScale = source.localScale;
                rect.localRotation = source.localRotation;
                var fill = go.AddComponent<Image>();
                fill.raycastTarget = false;
                fill.sprite = WhiteFrame(mask.sprite) ?? mask.sprite;
                fill.type = Image.Type.Simple;
                fill.preserveAspect = mask.preserveAspect;
                fill.enabled = false;
                rect.SetSiblingIndex(source.GetSiblingIndex() + 1);
                var ring = mark.Object.transform;
                if (ring.parent == rect.parent && rect.GetSiblingIndex() > ring.GetSiblingIndex()) rect.SetSiblingIndex(ring.GetSiblingIndex());
                return fill;
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("planner:fill", $"Planner: a node fill couldn't be made: {e.Message}", 30);
                if (go != null) UnityEngine.Object.Destroy(go);
                return null;
            }
        }

        // The game draws a tree node's hexagon frame as a child image named "Border" and its icon's shape as one named
        // "IconMask"; elements without a border get the ring.
        private static Image? Child(Transform parent, string name)
        {
            var found = parent.Find(name);
            if (found == null)
            {
                foreach (var image in parent.GetComponentsInChildren<Image>(true))
                    if (Game.Alive(image) && image.name == name) { found = image.transform; break; }
            }
            var child = found != null ? found.GetComponent<Image>() : null;
            return Game.Alive(child) && Game.Alive(child!.sprite) ? child : null;
        }

        // The element's visible frame: tree nodes name it "Border" (a skill tree's centre node draws "Border-escape" and
        // keeps an invisible "Border"), the specialisation slots "border", the skill icons "Frame" ("Locked Frame" while
        // locked), and a bar slot draws an unnamed image with the "Skill Frame" sprite. Hidden, see-through or empty ones
        // are skipped.
        private static Image? Frame(Transform parent)
        {
            if (Child(parent, BorderName) is { } named && Visible(named)) return named;
            Image? skillFrame = null;
            foreach (var image in parent.GetComponentsInChildren<Image>(true))
            {
                if (!Game.Alive(image) || image.name == MarkName || !Game.Alive(image.sprite) || !Visible(image)) continue;
                string name = image.name;
                if (name.StartsWith(BorderName, StringComparison.OrdinalIgnoreCase))
                {
                    if (name.Equals(BrightName, StringComparison.OrdinalIgnoreCase) || name.Contains("Flash", StringComparison.OrdinalIgnoreCase)) continue;
                    return image;
                }
                if (skillFrame == null && (name == FrameName || name.EndsWith(" " + FrameName, StringComparison.Ordinal) || image.sprite.name == SlotFrameSprite))
                    skillFrame = image;
            }
            // An empty specialisation slot hides its "border" and shows the add button; the hidden border has the same
            // hexagon in the same place.
            if (skillFrame == null && parent.Find(SlotBorderName)?.GetComponent<Image>() is { } hidden && Game.Alive(hidden) && Game.Alive(hidden.sprite))
                return hidden;
            return skillFrame;
        }

        // 1 for a frame about the node's size; for one far bigger than its node, the factor that brings the hexagon drawn
        // in it to the node's frame size (the gold hexagon is about 1.32 times the node's own rect).
        private static float FrameFit(Transform parent, RectTransform frame)
        {
            var node = parent.TryCast<RectTransform>();
            if (!Game.Alive(node)) return 1f;
            float nodeSide = Mathf.Max(1f, Mathf.Min(node!.rect.width, node.rect.height));
            float frameSide = Mathf.Min(frame.rect.width, frame.rect.height);
            return frameSide > nodeSide * 1.6f ? nodeSide * CentreFit / frameSide : 1f;
        }

        private const float CentreFit = 1.32f;

        private static bool Visible(Image image)
        {
            var size = image.rectTransform.rect.size;
            return image.gameObject.activeInHierarchy && image.color.a > 0.01f && size.x > 1f && size.y > 1f;
        }

        private const string BorderName = "Border", BrightName = "BorderBright", IconMaskName = "IconMask", FrameName = "Frame", SlotFrameSprite = "Skill Frame", SlotBorderName = "border";

        // A node's label goes in the node's container, after every node, so the nodes drawn after this one (and their
        // point counters) don't cover it; it stays under the game's tooltips, which are other canvases. It is the game's
        // own text in the node counter's font. A note's label goes in its mark and keeps the note's layout.
        private static void SetLabel(PlannerMark mark, string? label, int size, LabelPlace place, bool onNode)
        {
            if (onNode)
            {
                SetNodeLabel(mark, label, size, place);
                return;
            }
            if (string.IsNullOrEmpty(label))
            {
                if (Game.Alive(mark.Label) && mark.Label!.gameObject.activeSelf) mark.Label.gameObject.SetActive(false);
                return;
            }
            if (!Game.Alive(mark.Label))
            {
                var font = GameFont(mark.Object.transform);
                if (font == null) return;
                var go = new GameObject(MarkName + ".Label");
                var rect = go.AddComponent<RectTransform>();
                rect.SetParent(mark.Object.transform, false);
                rect.localScale = Vector3.one / mark.Scale;
                var text = go.AddComponent<Text>();
                text.font = font;
                text.fontSize = size;
                text.fontStyle = FontStyle.Bold;
                text.alignment = TextAnchor.MiddleCenter;
                text.horizontalOverflow = HorizontalWrapMode.Overflow;
                text.verticalOverflow = VerticalWrapMode.Overflow;
                text.raycastTarget = false;
                text.color = mark.Ring.color;
                mark.Label = text;
            }
            if (!mark.Label!.gameObject.activeSelf) mark.Label.gameObject.SetActive(true);
            mark.Label.fontSize = size;
            mark.Label.text = label;
        }

        private static void SetNodeLabel(PlannerMark mark, string? label, int size, LabelPlace place)
        {
            if (string.IsNullOrEmpty(label))
            {
                if (Game.Alive(mark.NodeText) && mark.NodeText!.gameObject.activeSelf) mark.NodeText.gameObject.SetActive(false);
                if (Game.Alive(mark.Plate) && mark.Plate!.gameObject.activeSelf) mark.Plate.gameObject.SetActive(false);
                return;
            }
            var host = mark.Node.parent;
            if (host == null) return;
            if (!Game.Alive(mark.NodeText))
            {
                var counter = CounterText(mark.Node);
                var font = Game.Alive(counter) ? counter!.font : TMP_Settings.defaultFontAsset;
                if (font == null) return;
                mark.Plate = MakePlate(host);
                var go = new GameObject(MarkName + ".Label");
                var rect = go.AddComponent<RectTransform>();
                rect.SetParent(host, false);
                go.AddComponent<LayoutElement>().ignoreLayout = true;
                var made = go.AddComponent<TextMeshProUGUI>();
                made.font = font;
                if (Game.Alive(counter) && counter!.fontSharedMaterial != null) made.fontSharedMaterial = counter.fontSharedMaterial;
                made.alignment = TextAlignmentOptions.Center;
                made.enableWordWrapping = false;
                made.overflowMode = TextOverflowModes.Overflow;
                made.raycastTarget = false;
                made.color = mark.Ring.color;
                mark.NodeText = made;
            }
            var text = mark.NodeText!;
            if (!text.gameObject.activeSelf) text.gameObject.SetActive(true);
            if (text.fontSize != size) text.fontSize = size;
            if (text.text != label) text.text = label;
            var box = new Vector2(text.GetPreferredValues(label).x + PlatePad * 2f, size + PlatePad * 2f);
            Backing(mark, place.Back, ref box);
            // Nodes a page shows later are added after the label; it moves back to the end on each pass.
            if (Game.Alive(mark.Plate)) mark.Plate!.transform.SetAsLastSibling();
            text.transform.SetAsLastSibling();
            Place(mark, text.rectTransform, place, box);
            if (Game.Alive(mark.Plate)) Place(mark, mark.Plate!.rectTransform, place, box);
        }

        // The node's own point counter text ("0/8"), whose font and material the label takes.
        private static TMP_Text? CounterText(Transform node)
        {
            foreach (var text in node.GetComponentsInChildren<TMP_Text>(true))
                if (Game.Alive(text) && text.name == CounterTextName && text.font != null) return text;
            return null;
        }

        private const string CounterTextName = "Text";

        // What the label sits on. The game box is a copy of the node's own point counter (sprite, tint, and at least its
        // size); the plate is the dark rounded box; with none, a dark outline round the text keeps it readable.
        private static void Backing(PlannerMark mark, LabelBack back, ref Vector2 box)
        {
            var text = mark.NodeText!;
            float outline = back == LabelBack.None ? OutlineWidth : 0f;
            if (text.outlineWidth != outline)
            {
                text.outlineWidth = outline;
                text.outlineColor = new Color32(0, 0, 0, 230);
            }
            if (!Game.Alive(mark.Plate)) return;
            var plate = mark.Plate!;
            bool shown = back != LabelBack.None;
            if (plate.gameObject.activeSelf != shown) plate.gameObject.SetActive(shown);
            if (!shown) return;
            var counter = back == LabelBack.Game ? Counter(mark.Node) : null;
            if (counter != null)
            {
                if (plate.sprite != counter.sprite) plate.sprite = counter.sprite;
                plate.type = counter.type;
                plate.color = counter.color;
                var size = counter.rectTransform.rect.size;
                box = new Vector2(Mathf.Max(box.x, size.x), Mathf.Max(box.y, size.y));
            }
            else
            {
                if (plate.sprite != PlateSprite()) plate.sprite = PlateSprite();
                plate.type = Image.Type.Sliced;
                plate.color = PlateColour;
            }
        }

        private const float OutlineWidth = 0.25f;

        // The box behind a passive or skill node's "0/8" counter: an image named "Image" with a sprite. A node without one
        // (a skill tree's centre) falls back to the plate.
        private static Image? Counter(Transform node) => Child(node, CounterName);

        private const string CounterName = "Image", CounterSpriteName = "InputField_Dropdown_Pressed";
        // The counter box's tint as the trees draw it.
        public static readonly Color CounterTint = new(0.816f, 0.862f, 0.896f, 1f);
        private static Sprite? s_counterSprite;
        private static bool s_counterSought;

        /// <summary>The game's counter box sprite, for the label background's example; looked up once, null when the game
        /// has none loaded.</summary>
        public static Sprite? CounterSprite()
        {
            if (Game.Alive(s_counterSprite) || s_counterSought) return Game.Alive(s_counterSprite) ? s_counterSprite : null;
            s_counterSought = true;
            foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Sprite>()))
            {
                var sprite = obj.TryCast<Sprite>();
                if (Game.Alive(sprite) && sprite!.name == CounterSpriteName) return s_counterSprite = sprite;
            }
            return null;
        }

        /// <summary>The dark plate's texture, for the label background's example.</summary>
        public static Texture2D? PlateTexture
        {
            get
            {
                PlateSprite();
                return s_plateTexture;
            }
        }

        public static Color Plate => PlateColour;

        private static readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3> s_corners = new(4);

        // On the chosen side of the mark's outline, just outside it, then moved by the offset; in the container's units.
        private static void Place(PlannerMark mark, RectTransform rect, LabelPlace place, Vector2 size)
        {
            var outline = mark.Object.transform.TryCast<RectTransform>();
            var host = rect.parent;
            if (!Game.Alive(outline) || host == null) return;
            outline!.GetWorldCorners(s_corners);
            Vector2 low = host.InverseTransformPoint(s_corners[0]), high = host.InverseTransformPoint(s_corners[2]);
            Vector2 centre = (low + high) / 2f;
            (Vector2 at, Vector2 pivot) = place.Side switch
            {
                LabelSide.Bottom => (new Vector2(centre.x, low.y), new Vector2(0.5f, 1f)),
                LabelSide.Left => (new Vector2(low.x, centre.y), new Vector2(1f, 0.5f)),
                LabelSide.Right => (new Vector2(high.x, centre.y), new Vector2(0f, 0.5f)),
                _ => (new Vector2(centre.x, high.y), new Vector2(0.5f, 0f)),
            };
            rect.pivot = pivot;
            rect.sizeDelta = size;
            rect.localPosition = new Vector3(at.x + place.X, at.y + place.Y, 0f);
        }

        private const float PlatePad = 5f;
        private static readonly Color PlateColour = new(0.04f, 0.04f, 0.05f, 1f);

        // A plain dark box under a node's label, placed and sized with it. The node container lays out its own children,
        // so both stay out of its layout.
        private static Image MakePlate(Transform host)
        {
            var go = new GameObject(MarkName + ".Plate");
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(host, false);
            go.AddComponent<LayoutElement>().ignoreLayout = true;
            var plate = go.AddComponent<Image>();
            plate.raycastTarget = false;
            plate.sprite = PlateSprite();
            plate.type = Image.Type.Sliced;
            plate.color = PlateColour;
            return plate;
        }

        // The font of the game's own text in the same panel; any loaded font when the panel has none.
        private static Font? GameFont(Transform near)
        {
            if (s_font != null) return s_font;
            var text = near.root.GetComponentInChildren<Text>(true);
            if (Game.Alive(text) && text!.font != null) s_font = text.font;
            else
            {
                foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Font>()))
                {
                    s_font = obj.TryCast<Font>();
                    if (s_font != null) break;
                }
            }
            return s_font;
        }

        // One white rounded ring, tinted per mark and 9-sliced to the element's size.
        // The game's frame sprites are grey, so a tint comes out dark. A white copy of each, with the frame's own
        // transparency, tints to the exact state colour along the frame's outline. Made once per sprite.
        private static readonly Dictionary<string, Sprite?> s_white = new();

        private static Sprite? WhiteFrame(Sprite frame, float border = 0f, Color? recolour = null)
        {
            string key = border > 0f || recolour != null
                ? $"{frame.name}#{border}#{(recolour is { } c ? ColorUtility.ToHtmlStringRGB(c) : string.Empty)}"
                : frame.name;
            if (s_white.TryGetValue(key, out var made)) return Game.Alive(made) ? made : null;
            Sprite? white = null;
            RenderTexture? rt = null;
            bool hollow = recolour == null && frame.name.StartsWith(WeaverFramePrefix, StringComparison.Ordinal);
            bool round = frame.name.StartsWith(WeaverRoundPrefix, StringComparison.Ordinal);
            // A weaver socket's card and a key node's hexagon keep their own drawn border (the line art near the outer edge),
            // not a cut shape, so the tint lies on the frame as the game draws it.
            bool card = hollow && (frame.name.StartsWith(WeaverSlotPrefix, StringComparison.Ordinal) || frame.name.StartsWith(WeaverKeyPrefix, StringComparison.Ordinal));
            var before = RenderTexture.active;
            try
            {
                var area = frame.textureRect;
                int w = Mathf.Max(1, Mathf.RoundToInt(area.width)), h = Mathf.Max(1, Mathf.RoundToInt(area.height));
                var source = frame.texture;
                // Atlas textures aren't readable; drawing the frame's area into a render texture reads it on the GPU.
                rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                var scale = new Vector2(area.width / source.width, area.height / source.height);
                var offset = new Vector2(area.x / source.width, area.y / source.height);
                Graphics.Blit(source, rt, scale, offset);
                RenderTexture.active = rt;
                var texture = new Texture2D(w, h, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                texture.ReadPixels(new Rect(0f, 0f, w, h), 0, 0, false);
                var pixels = texture.GetPixels32();
                // The frame's own light and shade, in grey, stretched so its brightest part is white: tinted, it keeps
                // the game's bevels and edges in the state's colour instead of a flat band.
                int brightest = 1;
                var edge = card ? EdgeDistance(pixels, w, h) : null;
                for (int i = 0; i < pixels.Length; i++)
                {
                    var p = pixels[i];
                    if (p.a < 32) continue;
                    brightest = Math.Max(brightest, (p.r * 77 + p.g * 150 + p.b * 29) >> 8);
                }
                for (int i = 0; i < pixels.Length; i++)
                {
                    var p = pixels[i];
                    int grey = Math.Min(255, ((p.r * 77 + p.g * 150 + p.b * 29) >> 8) * 255 / brightest);
                    if (recolour is { } to)
                    {
                        pixels[i] = Recoloured(to, p, grey / 255f);
                        continue;
                    }
                    // The weaver tree's frames are drawn with their inside filled: only the band outside the inner hexagon
                    // is the frame.
                    byte alpha = p.a;
                    // A card keeps its drawn border only: the line art near its edge, not the dark fill inside it.
                    if (edge != null) alpha = (byte)(alpha * Mathf.Clamp01(w * WeaverCardBand - edge[i] + 0.5f) * Mathf.Clamp01((grey - CardDark) / CardSoft));
                    else if (hollow) alpha = (byte)(alpha * (1f - (round ? InsideCircle(i % w, i / w, w, h) : InsideHex(i % w, i / w, w, h))));
                    // A floor, so the darkest parts of the frame still carry some colour.
                    byte v = (byte)(FrameFloor + grey * (255 - FrameFloor) / 255);
                    pixels[i] = new Color32(v, v, v, alpha);
                }
                texture.SetPixels32(pixels);
                texture.Apply(false, true);
                white = Sprite.Create(texture, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), frame.pixelsPerUnit, 0u, SpriteMeshType.FullRect,
                    border > 0f ? new Vector4(border, border, border, border) : frame.border);
                white.hideFlags = HideFlags.HideAndDontSave;
                s_whiteTextures.Add(texture);
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("planner:frame", $"Planner: a node frame couldn't be copied, marks use the frame as it is: {e.Message}", 30);
            }
            finally
            {
                RenderTexture.active = before;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }
            s_white[key] = white;
            return white;
        }

        private static readonly List<Texture2D> s_whiteTextures = new();
        private const string WeaverFramePrefix = "Weaver_", WeaverRoundPrefix = "Weaver_Circle", WeaverSlotPrefix = "Weaver_Slot", WeaverKeyPrefix = "Weaver_KeyNode";
        // How far in from a socket card's outer edge its border art reaches (corner curls included), as a share of the
        // sprite's width, and the brightness (0–255, the art's brightest at 255) below which a pixel is the dark fill.
        private const float WeaverCardBand = 0.2f, CardDark = 48f, CardSoft = 32f;

        // Each pixel's distance in pixels to the art's outside (a see-through pixel or the sprite's edge), by a two-pass
        // chamfer (straight steps 1, diagonal 1.4).
        private static float[] EdgeDistance(Color32[] pixels, int w, int h)
        {
            var d = new float[pixels.Length];
            for (int i = 0; i < d.Length; i++) d[i] = pixels[i].a < 32 ? 0f : float.MaxValue;
            float At(int x, int y) => x < 0 || y < 0 || x >= w || y >= h ? 0f : d[y * w + x];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (d[i] == 0f) continue;
                    d[i] = Mathf.Min(d[i], Mathf.Min(Mathf.Min(At(x - 1, y), At(x, y - 1)) + 1f, Mathf.Min(At(x - 1, y - 1), At(x + 1, y - 1)) + 1.4f));
                }
            for (int y = h - 1; y >= 0; y--)
                for (int x = w - 1; x >= 0; x--)
                {
                    int i = y * w + x;
                    if (d[i] == 0f) continue;
                    d[i] = Mathf.Min(d[i], Mathf.Min(Mathf.Min(At(x + 1, y), At(x, y + 1)) + 1f, Mathf.Min(At(x + 1, y + 1), At(x - 1, y + 1)) + 1.4f));
                }
            return d;
        }
        // The weaver frames' insides, as a share of the sprite's half width: a key node's hexagon (pointed top and bottom)
        // and a round node's circle.
        private const float WeaverInner = 0.8f, WeaverRoundInner = 0.82f;

        private static float InsideCircle(int x, int y, int w, int h)
        {
            float half = w / 2f;
            float d = new Vector2(x + 0.5f - half, y + 0.5f - h / 2f).magnitude / (half * WeaverRoundInner);
            return Mathf.Clamp01((1f - d) * half * WeaverRoundInner + 0.5f);
        }

        // How far a pixel is inside the frame's inner hexagon: 1 well inside, 0 outside, a pixel's softening between.
        private static float InsideHex(int x, int y, int w, int h)
        {
            float half = w / 2f;
            float dx = Mathf.Abs(x + 0.5f - half) / half, dy = Mathf.Abs(y + 0.5f - h / 2f) / half;
            float d = Mathf.Max(dx, dx * 0.5f + dy * 0.866f) / WeaverInner;
            return Mathf.Clamp01((1f - d) * half * WeaverInner + 0.5f);
        }
        // The art's brightness the state colour lands on (darker parts darker, brighter towards white), and how much of
        // the art's own colour stays.
        private const float RecolourMid = 0.7f, OwnColour = 0.15f;

        // A pixel of game art in another colour, keeping its own light and shade: its brightness (0 to 1, the art's
        // brightest at 1) scales the colour, running on towards white past the colour's own brightness so bevels and
        // highlights stay; a little of the art's own colour is kept.
        private static Color32 Recoloured(Color to, Color32 own, float brightness)
        {
            float k = brightness / RecolourMid;
            Color c = k <= 1f ? to * k : Color.Lerp(to, Color.white, Mathf.Clamp01((k - 1f) / (1f / RecolourMid - 1f)));
            c = Color.Lerp(c, own, OwnColour);
            return new Color32((byte)(Mathf.Clamp01(c.r) * 255f), (byte)(Mathf.Clamp01(c.g) * 255f), (byte)(Mathf.Clamp01(c.b) * 255f), own.a);
        }
        private const int FrameFloor = 70;
        private static Sprite Ring() => s_ring ??= RoundedSprite(false, out s_texture);


        // The label plate: the ring's rounded square, filled. A sprite-less image draws nothing in this game.
        private static Sprite PlateSprite() => s_plate ??= RoundedSprite(true, out s_plateTexture);

        private static Texture2D? s_plateTexture;
        private static Sprite? s_plate;

        private static Sprite RoundedSprite(bool filled, out Texture2D? made)
        {
            var texture = new Texture2D(RingSize, RingSize, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            float half = RingSize / 2f;
            for (int y = 0; y < RingSize; y++)
            {
                for (int x = 0; x < RingSize; x++)
                {
                    // Signed distance to a rounded square's edge, negative inside.
                    float qx = Mathf.Abs(x + 0.5f - half) - (half - RingRadius), qy = Mathf.Abs(y + 0.5f - half) - (half - RingRadius);
                    float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - RingRadius;
                    float alpha = Mathf.Clamp01(0.5f - outside) * (filled ? 1f : Mathf.Clamp01(outside + RingWidth + 0.5f));
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            texture.Apply(false, true);
            made = texture;
            var sprite = Sprite.Create(texture, new Rect(0f, 0f, RingSize, RingSize), new Vector2(0.5f, 0.5f), 100f, 0u, SpriteMeshType.FullRect,
                new Vector4(RingBorder, RingBorder, RingBorder, RingBorder));
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }
    }
}

internal static partial class Planner
{
    // The marks kept by the pass running now; swapped into the run's list, so no pass allocates a list.
    private static readonly List<PlannerMark> s_kept = new();
    private static int s_pass;

    /// <summary>Every setting the marks are drawn from, for one panel; a pass runs again when any of them changes.</summary>
    private struct MarkLook
    {
        public Color Match, Partial, ToPlace, OffPlan, NextColour;
        public bool Next, NextLabel, LabelSlash, NextOwn, FillTint, SkillOffPlan;
        public bool ShowMatch, ShowPartial, ShowToPlace, ShowOffPlan;
        public float Opacity, Size, Fill, Panel;
        public int LabelSize;
        public LabelPlace LabelPlace;

        public static MarkLook Now(GuidedPanel panel) => Now(S.InUse(LookOf(panel)));

        // Interfaces without a next point (the skill screens) never draw one; Interfaces' settings stand in.
        private static MarkLook Now(S.PanelLook panel) => Now(panel, panel.Next ?? S.Interfaces.Next!);

        private static MarkLook Now(S.PanelLook panel, S.NextPointLook next) => new()
        {
            Match = S.Colour(S.MatchColour),
            Partial = S.Colour(S.PartialColour),
            ToPlace = S.Colour(S.ToPlaceColour),
            OffPlan = S.Colour(S.OffPlanColour),
            NextColour = S.Colour(next.Colour),
            Next = next.Steps.Value,
            NextLabel = next.Label.Value,
            LabelSlash = next.Slash,
            NextOwn = next.OwnColour.Value,
            FillTint = panel.Fill.Value,
            SkillOffPlan = S.SkillOffPlanNodes.Value,
            ShowMatch = S.ShowMatches.Value,
            ShowPartial = S.ShowPartial.Value,
            ShowToPlace = S.ShowToPlace.Value,
            ShowOffPlan = S.ShowOffPlan.Value,
            Opacity = S.OutlineOpacity.Value,
            Size = panel.Size.Value,
            Fill = panel.FillOpacity.Value,
            Panel = panel.Opacity.Value,
            LabelSize = Mathf.RoundToInt(next.LabelSize.Value),
            LabelPlace = new LabelPlace(next.Side, next.LabelX.Value, next.LabelY.Value, next.Back),
        };

        public readonly bool Same(MarkLook other) => Match == other.Match && Partial == other.Partial && ToPlace == other.ToPlace
            && OffPlan == other.OffPlan && NextColour == other.NextColour && Next == other.Next && NextLabel == other.NextLabel && LabelSlash == other.LabelSlash
            && NextOwn == other.NextOwn && FillTint == other.FillTint && SkillOffPlan == other.SkillOffPlan
            && ShowMatch == other.ShowMatch && ShowPartial == other.ShowPartial && ShowToPlace == other.ShowToPlace
            && ShowOffPlan == other.ShowOffPlan && Opacity == other.Opacity && Size == other.Size && Fill == other.Fill
            && Panel == other.Panel && LabelSize == other.LabelSize && LabelPlace == other.LabelPlace;

        public readonly Color Of(GuidanceState state) => state switch
        {
            GuidanceState.Matches => Match,
            GuidanceState.Partial => Partial,
            GuidanceState.ToPlace => ToPlace,
            _ => OffPlan,
        };

        public readonly bool Shown(GuidanceState state) => state switch
        {
            GuidanceState.Matches => ShowMatch,
            GuidanceState.Partial => ShowPartial,
            GuidanceState.ToPlace => ShowToPlace,
            _ => ShowOffPlan,
        };

        /// <summary>
        /// One node's ring colour, size and fill; false for a node whose state is switched off. The next point is where
        /// to click, so it keeps its mark whatever its state's switch and ignores the outline opacity.
        /// </summary>
        public readonly bool Style(NodeGuide guide, out Color ring, out float scale, out Color fill)
        {
            bool next = guide.NextStep == 1;
            var colour = next && NextOwn ? NextColour : Of(guide.State);
            if (!next && !Shown(guide.State))
            {
                ring = fill = default;
                scale = 1f;
                return false;
            }
            ring = new Color(colour.r, colour.g, colour.b, colour.a * Panel * (next ? 1f : Opacity));
            scale = Size;
            fill = FillTint ? new Color(colour.r, colour.g, colour.b, colour.a * Panel * Fill) : Color.clear;
            return true;
        }
    }

    // Each panel's own opacity, outline size and fill.
    private static S.PanelLook LookOf(GuidedPanel panel) => panel switch
    {
        GuidedPanel.Passive => S.PassiveLook,
        GuidedPanel.SkillTree => S.SkillTreeLook,
        GuidedPanel.SkillChoice => S.SkillChoiceLook,
        GuidedPanel.Weaver => S.WeaverLook,
        _ => S.SkillsPageLook,
    };

    /// <summary>
    /// Marks the page of the passive tree the panel shows. A page's nodes exist and are active only once it's shown, so
    /// the marks are made again when the page, its tree or its node count changes, and when the guidance or the look
    /// changes; otherwise a call only compares. Run each tick and after each new comparison while the panel is open.
    /// </summary>
    private static void MarkPassive(Run run)
    {
        var guidance = run.Guidance;
        if (run.Open != GuidedPanel.Passive || guidance == null || !S.Enabled.Value || !S.PassiveLook.Marks!.Value)
        {
            ClearPassiveMarks(run);
            return;
        }
        try
        {
            var manager = Panels.Passive();
            var tree = manager != null ? manager.activeTree : null;
            // A respec takes points out node by node; marks would only flicker through it.
            if (!Game.Alive(tree))
            {
                ClearPassiveMarks(run);
                return;
            }
            var nodes = tree!.nodeList;
            int count = nodes?.Count ?? 0;
            int page = manager!.activeMastery;
            var look = MarkLook.Now(GuidedPanel.Passive);
            if (run.MarkedTree == tree.Pointer && run.MarkedPage == page && run.MarkedNodes == count && run.MarkedGuidance == guidance
                && run.MarkedLook.Same(look)) return;
            run.MarkedTree = tree.Pointer;
            run.MarkedPage = page;
            run.MarkedNodes = count;
            run.MarkedGuidance = guidance;
            run.MarkedLook = look;

            int pass = ++s_pass;
            Marks.FrameMissing = false;
            s_kept.Clear();
            var guides = guidance.Passive.Nodes;
            PlannerMark? next = null;
            for (int i = 0; i < count; i++)
            {
                var node = nodes![i];
                if (!Game.Alive(node) || !node!.gameObject.activeInHierarchy) continue;
                if (!guides.TryGetValue(node.id, out var guide)) continue;
                var mark = MarkNode(node.transform, guide, look, pass);
                if (look.Next && guide.NextStep == 1 && mark != null) next = mark;
            }
            Marks.PulseOn(next);
            foreach (var mark in run.PassiveMarks)
                if (mark.Pass != pass) Drop(run, mark, run.PassiveMarks);
            run.PassiveMarks.Clear();
            run.PassiveMarks.AddRange(s_kept);
            // A frame not drawn yet: mark again on the next pass.
            s_passiveRetries = Marks.FrameMissing ? s_passiveRetries + 1 : 0;
            if (Marks.FrameMissing && s_passiveRetries < FrameRetries) run.MarkedNodes = -1;
            s_kept.Clear();
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("planner:passive marks", $"Planner: the passive tree couldn't be marked: {e.Message}", 30);
        }
    }

    // A panel's frames show within a few passes of it opening; past this many a node without one is left unmarked.
    private const int FrameRetries = 30;
    private static int s_passiveRetries, s_skillRetries;

    // A mark goes only when no other panel's list still holds it: the Skills page and a skill tree mark the same five
    // slots, and a mark is one per element.
    private static void Drop(Run run, PlannerMark mark, List<PlannerMark> own)
    {
        if ((own != run.PassiveMarks && run.PassiveMarks.Contains(mark)) || (own != run.SkillMarks && run.SkillMarks.Contains(mark))
            || (own != run.ChoiceMarks && run.ChoiceMarks.Contains(mark))) return;
        Marks.Clear(mark);
    }

    private static void Keep(PlannerMark? mark, int pass)
    {
        if (mark == null) return;
        mark.Pass = pass;
        s_kept.Add(mark);
    }

    // One node's mark as the look draws it, kept by this pass; null for a node whose state is switched off.
    private static PlannerMark? MarkNode(Transform node, NodeGuide guide, in MarkLook look, int pass, MarkHint hint = default)
    {
        if (!look.Style(guide, out var ring, out float scale, out var fill)) return null;
        var mark = Marks.Mark(node, ring, Label(guide, look.NextLabel, look.LabelSlash), scale, fill, look.LabelSize, look.LabelPlace);
        if (mark != null) mark.Hint = hint.Kind == HintKind.None ? new MarkHint(HintKind.Node, guide) : hint;
        Keep(mark, pass);
        return mark;
    }

    // Only the node the next point goes into is labelled, for that one point: its points now against after it ("2 < 3"),
    // or as the counters write it, now out of the plan ("2/4"). Every other mark is the outline alone; its colour gives
    // the state.
    private static string? Label(NodeGuide guide, bool show, bool slash)
    {
        if (!show || guide.NextStep != 1) return null;
        return slash ? $"{guide.Allocated}/{guide.Planned}" : $"{guide.Allocated} < {guide.Allocated + 1}";
    }


    // Below the skill's level line, its bar and "Minimum Specialized Level", in the panel's own units.
    private const float SkillNoteDrop = 36f;

    /// <summary>Destroys the passive tree's marks and forgets what they were made from.</summary>
    /// <summary>
    /// The weaver tree, while its panel shows: each node the stage plans or the character has points in, by state, with
    /// the next point; made again when the guidance, the panel or the look changes.
    /// </summary>
    private static void MarkWeaver(Run run)
    {
        var guide = run.Gear?.Weaver;
        var items = run.Gear is { WeaverItems.Count: > 0 } gear && run.Feature.Has(WeaverItemsGroup) ? gear.WeaverItems : null;
        if (guide == null && items == null || !S.Enabled.Value || !S.WeaverLook.Marks!.Value || !run.Feature.Has(WeaverUiGroup))
        {
            ClearWeaverMarks(run);
            return;
        }
        try
        {
            // The rank page says whether its tree shows: the tree itself is looked for only then.
            var page = WeaverPage();
            var tree = page != null && page.IsWeaverTreeOpened ? Panels.Find<Il2CppLE.Factions.WeaverTree>() : null;
            if (!Panels.Open(tree))
            {
                ClearWeaverMarks(run);
                return;
            }
            var nodes = tree!.nodeList;
            int count = nodes?.Count ?? 0;
            if (guide != null) guide = PanelWeaverGuide(run, guide, nodes);
            var look = MarkLook.Now(GuidedPanel.Weaver);
            if (run.WeaverMarkedTree == tree.Pointer && run.WeaverMarkedNodes == count && run.WeaverMarkedGuide == guide && run.WeaverMarkedItems == items
                && run.WeaverMarkedLook.Same(look)) return;
            run.WeaverMarkedTree = tree.Pointer;
            run.WeaverMarkedNodes = count;
            run.WeaverMarkedGuide = guide;
            run.WeaverMarkedItems = items;
            run.WeaverMarkedLook = look;

            int pass = ++s_pass;
            Marks.FrameMissing = false;
            s_kept.Clear();
            PlannerMark? next = null;
            for (int i = 0; i < count; i++)
            {
                var node = nodes![i];
                if (!Game.Alive(node)) continue;
                if (!string.IsNullOrWhiteSpace(node!.nodeName)) s_weaverNames[node.id] = node.nodeName;
                // A socket node keeps its point mark beside its card's item mark: the point and the item are separate steps.
                if (guide == null) continue;
                if (!node.gameObject.activeInHierarchy || !guide.Nodes.TryGetValue(node.id, out var nodeGuide)) continue;
                var mark = MarkNode(node.transform, nodeGuide, look, pass);
                if (look.Next && nodeGuide.NextStep == 1 && mark != null) next = mark;
            }
            if (next != null) Marks.PulseOn(next);
            if (items != null) MarkSockets(page!, items, look, pass);
            foreach (var mark in run.WeaverMarks)
                if (mark.Pass != pass) Marks.Clear(mark);
            run.WeaverMarks.Clear();
            run.WeaverMarks.AddRange(s_kept);
            if (Marks.FrameMissing && ++s_weaverRetries < FrameRetries) run.WeaverMarkedNodes = -1;
            else if (!Marks.FrameMissing) s_weaverRetries = 0;
            s_kept.Clear();
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("planner:weaver marks", $"Planner: the weaver tree couldn't be marked: {e.Message}", 30);
        }
    }

    private static int s_weaverRetries;

    // Each planned socket's card frame on the weaver page, in its item's state colour (grey when this game version lacks the
    // item). Looked for only when the marks are made again, not every tick.
    private static void MarkSockets(Il2CppLE.Factions.FactionRankUIWeaver page, SortedDictionary<int, ItemGuide> items, in MarkLook look, int pass)
    {
        foreach (var element in page.GetComponentsInChildren<Il2CppLE.Factions.WeaverTreeItemContainerUI>(false))
        {
            if (!Game.Alive(element) || SocketOn(items, element!.LinkedNodeID) is not (>= 0 and var socket)) continue;
            var mark = Marks.Mark(element.transform, IconColour(items[socket]), null, 1f, Color.clear, look.LabelSize, look.LabelPlace);
            if (mark != null) mark.Hint = new MarkHint(HintKind.WeaverItem, Slot: socket);
            Keep(mark, pass);
        }
    }

    // The planned socket on a weaver node, or -1.
    private static int SocketOn(SortedDictionary<int, ItemGuide> items, byte node)
    {
        foreach (int socket in items.Keys)
            if (WeaverSockets.TryGetValue(socket, out byte on) && on == node) return socket;
        return -1;
    }

    /// <summary>A weaver socket's name: the node it sits on, or the socket's id when this game version lacks it.</summary>
    private static string SocketName(Core.Feature feature, int socket) =>
        WeaverSockets.TryGetValue(socket, out byte node) ? WeaverNodeName(feature, node) : $"Socket {socket}";

    // The weaver tree takes points unconfirmed until its Confirm button: the character's tree data has only confirmed
    // ones. While the tree is open its marks follow the panel's own counts, as the player sees them, and remember which
    // nodes differ from the confirmed points for the hint card.
    private static readonly Dictionary<byte, byte> s_panelPoints = new();

    private static TreeGuide PanelWeaverGuide(Run run, TreeGuide saved, Il2CppSystem.Collections.Generic.List<SkillTreeNode>? nodes)
    {
        ulong h = 14695981039346656037UL;
        s_panelPoints.Clear();
        for (int i = 0; nodes != null && i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (!Game.Alive(node) || node!.pointsAllocated == 0) continue;
            s_panelPoints[node.id] = (byte)node.pointsAllocated;
            h = (h ^ (uint)(node.id << 8 | node.pointsAllocated)) * 1099511628211UL;
        }
        if (run.WeaverPanelGuide != null && run.WeaverPanelSignature == h && run.WeaverPanelBase == saved) return run.WeaverPanelGuide;
        run.WeaverPanelSignature = h;
        run.WeaverPanelBase = saved;
        run.WeaverUnconfirmed.Clear();
        foreach (var (node, points) in s_panelPoints)
            if (!run.Equipped.Weaver.TryGetValue(node, out byte had) || had != points) run.WeaverUnconfirmed.Add(node);
        foreach (var node in run.Equipped.Weaver.Keys)
            if (!s_panelPoints.ContainsKey(node)) run.WeaverUnconfirmed.Add(node);
        if (run.WeaverUnconfirmed.Count == 0 || run.Build is not { } build || GearOf(build, run.Stage).Weaver is not { } planned)
            return run.WeaverPanelGuide = saved;
        var guide = new TreeGuide();
        CompareTree(guide, WeaverTreeId, planned, s_panelPoints, s_noUnknown, s_noCaps, null, NextStepCount);
        return run.WeaverPanelGuide = guide;
    }

    // The weaver rank page is one of the game's full-screen panels, a child of its "Full Screen Panel Stack" while
    // open. The stack is found once (by name) and its few children checked each tick; an object search for the page
    // would scan the whole scene, about 25 ms, whenever it isn't there.
    private const string PanelStackName = "Full Screen Panel Stack", WeaverPagePrefix = "FactionRankPanelWeaver";
    private static Transform? s_panelStack;
    private static float s_panelStackSearch;

    private static Il2CppLE.Factions.FactionRankUIWeaver? WeaverPage()
    {
        if (!Game.Alive(s_panelStack))
        {
            if (Clock.Now < s_panelStackSearch) return null;
            s_panelStackSearch = Clock.Now + 5f;
            s_panelStack = GameObject.Find(PanelStackName)?.transform;
            if (!Game.Alive(s_panelStack)) return null;
        }
        for (int i = 0; i < s_panelStack!.childCount; i++)
        {
            var child = s_panelStack.GetChild(i);
            if (!child.gameObject.activeInHierarchy || !child.name.StartsWith(WeaverPagePrefix, StringComparison.Ordinal)) continue;
            var page = child.GetComponentInChildren<Il2CppLE.Factions.FactionRankUIWeaver>(false);
            if (Game.Alive(page)) return page;
        }
        return null;
    }

    // The weaver nodes' names as the panel shows them; the game data holds only internal ids ("Node_EchoWeb…").
    private static readonly Dictionary<byte, string> s_weaverNames = new();

    /// <summary>A weaver node's name: the panel's once seen, else the internal id made readable.</summary>
    private static string WeaverNodeName(Core.Feature feature, byte node)
    {
        if (s_weaverNames.TryGetValue(node, out var name)) return name;
        if (PlayerName(WeaverTreeId, node) is { } shown) return s_weaverNames[node] = shown;
        string id = NodeName(feature, WeaverTreeId, node);
        if (id.StartsWith("Node_", StringComparison.Ordinal)) id = id[5..];
        return System.Text.RegularExpressions.Regex.Replace(id, "(?<=[a-z])(?=[A-Z])", " ");
    }

    private static void ClearWeaverMarks(Run run)
    {
        if (run.WeaverMarkedTree == IntPtr.Zero && run.WeaverMarks.Count == 0) return;
        foreach (var mark in run.WeaverMarks) Marks.Clear(mark);
        run.WeaverMarks.Clear();
        run.WeaverMarkedTree = IntPtr.Zero;
        run.WeaverMarkedNodes = -1;
        run.WeaverMarkedGuide = null;
        run.WeaverMarkedItems = null;
    }

    private static void ClearPassiveMarks(Run run)
    {
        if (run.MarkedTree == IntPtr.Zero && run.PassiveMarks.Count == 0) return;
        foreach (var mark in run.PassiveMarks) Drop(run, mark, run.PassiveMarks);
        run.PassiveMarks.Clear();
        run.MarkedTree = IntPtr.Zero;
        run.MarkedPage = -1;
        run.MarkedNodes = -1;
        run.MarkedGuidance = null;
    }

    /// <summary>
    /// Marks the skill tree open on the Skills page, as the passive tree is marked: a planned skill's nodes by state, or
    /// a note on the tree when the skill is specialised but not in the build. Made again when the open tree, its node
    /// count, the guidance or the look changes; otherwise a call only compares. Run each tick and after each new
    /// comparison while the tree is open.
    /// </summary>
    private static void MarkSkillTree(Run run)
    {
        var guidance = run.Guidance;
        if (run.Open != GuidedPanel.SkillTree || guidance == null || !S.Enabled.Value || !S.SkillTreeLook.Marks!.Value)
        {
            ClearSkillMarks(run);
            return;
        }
        try
        {
            var page = Panels.SkillsPage();
            var tree = Panels.SkillTree(page);
            if (!Game.Alive(tree))
            {
                ClearSkillMarks(run);
                return;
            }
            var nodes = tree!.nodeList;
            int count = nodes?.Count ?? 0;
            var look = MarkLook.Now(GuidedPanel.SkillTree);
            if (run.SkillMarkedTree == tree.Pointer && run.SkillMarkedNodes == count && run.SkillMarkedGuidance == guidance
                && run.SkillMarkedLook.Same(look)) return;
            // The id is read only when another tree shows, so an unchanged tree reads no string.
            if (run.SkillMarkedTree != tree.Pointer)
            {
                run.SkillTreeId = tree.treeID ?? string.Empty;
                run.SkillWords = null;
            }
            // The note's words follow the plan too (not in the build, or on the bar only).
            if (run.SkillMarkedGuidance != guidance) run.SkillWords = null;
            run.SkillMarkedTree = tree.Pointer;
            run.SkillMarkedNodes = count;
            run.SkillMarkedGuidance = guidance;
            run.SkillMarkedLook = look;

            int pass = ++s_pass;
            Marks.FrameMissing = false;
            s_kept.Clear();
            var skill = SkillOf(guidance, run.SkillTreeId);
            bool notInBuild = skill != null && (skill.Tree.Flags & TreeFlags.SkillNotInBuild) != 0;
            var shownStage = run.Build?.Stages[run.Stage];
            PlannerMark? next = null;
            // A skill the build doesn't use gets only the note unless its nodes are asked for: all of its tree is off the plan.
            if (skill != null && (!notInBuild || look.SkillOffPlan))
            {
                var guides = skill.Tree.Nodes;
                for (int i = 0; i < count; i++)
                {
                    var node = nodes![i];
                    if (!Game.Alive(node) || !node!.gameObject.activeInHierarchy) continue;
                    if (!guides.TryGetValue(node.id, out var guide)) continue;
                    var mark = MarkNode(node.transform, guide, look, pass);
                    if (look.Next && guide.NextStep == 1 && mark != null) next = mark;
                }
            }
            Marks.PulseOn(next);
            // The row of five above the tree, each slot in its state, as on the Skills & Specializations page.
            if (shownStage != null) MarkSlots(guidance, shownStage, look, pass);
            // Under the skill's name at the top left, clear of the specialisation slots; the tree's top edge when the name is gone.
            if (notInBuild)
            {
                // The tree's centre node in its slot's colour (not in the build, or partial for a skill the build has on
                // the bar but doesn't specialise).
                var state = shownStage != null ? SpecialisedState(skill!, shownStage) : GuidanceState.NotInBuild;
                var root = tree.getNode(0);
                if (Game.Alive(root)) MarkNode(root!.transform, new NodeGuide(state, 0, 0, 0, 0), look, pass);
                // Under the skill level block ("Level n", its bar and "Minimum Specialized Level"), below the title.
                Component? name = Game.Alive(page) ? page!.SkillLevel : null;
                var colour = look.Of(state);
                Keep(Game.Alive(name)
                    ? Marks.Note(name!.transform, colour, SkillWords(run, skill!, state), left: true, drop: SkillNoteDrop)
                    : Marks.Note(tree.transform, colour, SkillWords(run, skill!, state), top: true), pass);
            }
            foreach (var mark in run.SkillMarks)
                if (mark.Pass != pass) Drop(run, mark, run.SkillMarks);
            run.SkillMarks.Clear();
            run.SkillMarks.AddRange(s_kept);
            // A frame not drawn yet: mark again on the next pass.
            s_skillRetries = Marks.FrameMissing ? s_skillRetries + 1 : 0;
            if (Marks.FrameMissing && s_skillRetries < FrameRetries) run.SkillMarkedNodes = -1;
            s_kept.Clear();
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("planner:skill marks", $"Planner: the skill tree couldn't be marked: {e.Message}", 30);
        }
    }


    /// <summary>
    /// Marks where skills are chosen: with the selection panel open, the bar's slots by state and the skill planned for
    /// the clicked slot in its grid; on the Skills &amp; Specializations page, each skill and specialisation slot by state.
    /// The bar is marked only while the selection panel is open, and every mark goes when it closes. Made again when the
    /// panel, the clicked slot, the icon count, the guidance or the look changes; otherwise a call only compares.
    /// </summary>
    private static void MarkSkillChoice(Run run)
    {
        var guidance = run.Guidance;
        var open = run.Open;
        if (open is not (GuidedPanel.SkillChoice or GuidedPanel.SkillsPage) || guidance == null || run.Build == null || !S.Enabled.Value
            || !(open == GuidedPanel.SkillChoice ? S.SkillChoiceLook : S.SkillsPageLook).Marks!.Value)
        {
            ClearChoiceMarks(run);
            return;
        }
        try
        {
            var selector = open == GuidedPanel.SkillChoice ? AbilityBarSelectorManager.instance : null;
            var page = open == GuidedPanel.SkillsPage ? Panels.SkillsPage() : null;
            var picks = Game.Alive(selector) ? selector!._abilitySelectionIcons : null;
            int key = Game.Alive(selector) ? selector!.abilityClicked * 1000 + (picks?.Count ?? 0) : Game.Alive(page) ? -1 : -2;
            var look = MarkLook.Now(open);
            if (run.ChoicePanel == open && run.ChoiceKey == key && run.ChoiceMarkedGuidance == guidance && run.ChoiceMarkedLook.Same(look)) return;
            run.ChoicePanel = open;
            run.ChoiceKey = key;
            run.ChoiceMarkedGuidance = guidance;
            run.ChoiceMarkedLook = look;

            int pass = ++s_pass;
            Marks.FrameMissing = false;
            s_kept.Clear();
            var stage = run.Build.Stages[run.Stage];
            if (Game.Alive(selector)) MarkSelection(selector!, picks, guidance, stage, look, pass);
            else if (Game.Alive(page)) MarkSkillsPage(page!, guidance, stage, run.Allocation, look, pass);
            foreach (var mark in run.ChoiceMarks)
                if (mark.Pass != pass) Drop(run, mark, run.ChoiceMarks);
            run.ChoiceMarks.Clear();
            run.ChoiceMarks.AddRange(s_kept);
            // A frame not drawn yet: mark again on the next pass.
            s_choiceRetries = Marks.FrameMissing ? s_choiceRetries + 1 : 0;
            if (Marks.FrameMissing && s_choiceRetries < FrameRetries) run.ChoiceKey = int.MinValue;
            s_kept.Clear();
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("planner:choice marks", $"Planner: the skill choice couldn't be marked: {e.Message}", 30);
        }
    }

    private static int s_choiceRetries;

    // The bar's five slots in their states, on each slot's frame (drawn by the slot that holds the icon), and the clicked
    // slot's planned skill in the grid. The grid's empty entry and Attack are never planned, so never marked.
    private static void MarkSelection(AbilityBarSelectorManager selector, Il2CppSystem.Collections.Generic.List<AbilitySelectionIcon>? picks,
        Guidance guidance, Stage stage, in MarkLook look, int pass)
    {
        var bar = Game.BarIcons;
        for (int i = 0; bar != null && i < bar.Count; i++)
        {
            var icon = bar[i];
            if (!Game.Alive(icon) || !icon!.gameObject.activeInHierarchy) continue;
            int slot = icon.abilityNumber - 1;
            if (slot < 0 || slot >= Stage.BarSlots || guidance.Bar[slot] is not { } state) continue;
            if (icon.transform.parent is { } holder) MarkNode(holder, new NodeGuide(state, 0, 0, 0, 0), look, pass, new MarkHint(HintKind.BarSlot, Slot: slot));
        }
        int clicked = selector.abilityClicked - 1;
        string? planned = clicked >= 0 && clicked < Stage.BarSlots ? stage.Bar[clicked] : null;
        if (planned == null || picks == null) return;
        for (int i = 0; i < picks.Count; i++)
        {
            var pick = picks[i];
            if (!Game.Alive(pick) || !pick!.gameObject.activeInHierarchy || !Game.Alive(pick.ability) || pick.ability.playerAbilityID != planned) continue;
            MarkNode(pick.transform, new NodeGuide(GuidanceState.Matches, 0, 0, 0, 0), look, pass, new MarkHint(HintKind.Pick, Slot: clicked, AbilityId: planned));
            break;
        }
    }

    // Each skill in the class and mastery columns by where it sits against the plan, and each specialisation slot by the
    // skill in it; an empty slot is to place while a planned skill is still to specialise.
    private static void MarkSkillsPage(SkillsPanelManager page, Guidance guidance, Stage stage, Allocation allocation, in MarkLook look, int pass)
    {
        // The class's skills, those its passive tree unlocks (Smoke Bomb, Umbral Blades), and each mastery's.
        foreach (var group in new[] { page.baseClassPanelIcons, page.baseClassTreePanelIcons, page.masteryClass1PanelIcons, page.masteryClass2PanelIcons, page.masteryClass3PanelIcons })
            for (int i = 0; group != null && i < group.Count; i++) MarkPageIcon(group[i], guidance, stage, allocation, look, pass);
        MarkPageIcon(page.masteryAbilityPanelIcon1, guidance, stage, allocation, look, pass);
        MarkPageIcon(page.masteryAbilityPanelIcon2, guidance, stage, allocation, look, pass);
        MarkPageIcon(page.masteryAbilityPanelIcon3, guidance, stage, allocation, look, pass);
        MarkSlots(guidance, stage, look, pass);
    }

    // The five specialisation slots, each by the skill in it: in its planned slot with its tree complete, in another or
    // still being taken, or not in the build; an empty slot is to place while a planned skill is still to specialise. Searched only when the panel is (re)marked.
    private static void MarkSlots(Guidance guidance, Stage stage, in MarkLook look, int pass)
    {
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SpecialisedAbilityIcon>(), false))
        {
            var slot = obj.TryCast<SpecialisedAbilityIcon>();
            if (!Game.Alive(slot) || !slot!.gameObject.activeInHierarchy) continue;
            string? id = Game.Alive(slot.ability) ? slot.ability.playerAbilityID : null;
            GuidanceState? state = null;
            if (!string.IsNullOrEmpty(id))
            {
                if (SkillOf(guidance, id) is { } skill) state = SpecialisedState(skill, stage);
            }
            else
            {
                // Free while a planned skill is still to specialise, whichever slot the plan gives it; a slot the character
                // hasn't unlocked can't take one.
                if (Game.LocalTree is { } tree && slot.abilityNumber >= tree.numberOfUnlockedSlots) continue;
                foreach (var skill in guidance.Skills)
                    if (skill.State != GuidanceState.NotInBuild && skill.Slot < 0) { state = GuidanceState.ToPlace; break; }
            }
            if (state is { } s) MarkNode(slot.transform, new NodeGuide(s, 0, 0, 0, 0), look, pass, new MarkHint(HintKind.SpecSlot, Slot: slot.abilityNumber, AbilityId: id));
        }
    }

    private static void MarkPageIcon(AbilityPanelIcon? icon, Guidance guidance, Stage stage, Allocation allocation, in MarkLook look, int pass)
    {
        if (!Game.Alive(icon) || !icon!.gameObject.activeInHierarchy || !Game.Alive(icon.ability)) return;
        if (PageState(icon.ability.playerAbilityID, guidance, stage, allocation) is { } state)
            MarkNode(icon.transform, new NodeGuide(state, 0, 0, 0, 0), look, pass, new MarkHint(HintKind.PageIcon, AbilityId: icon.ability.playerAbilityID));
    }

    // The page is about specialisations, so a skill the plan specialises goes by its specialisation slot: in it, in
    // another, or not specialised. A skill planned only on the bar goes by its bar slot the same way. A skill the plan
    // doesn't use, specialised or on the bar: not in the build. Any other skill: no mark.
    private static GuidanceState? PageState(string? abilityId, Guidance guidance, Stage stage, Allocation allocation)
    {
        if (string.IsNullOrEmpty(abilityId)) return null;
        if (SkillOf(guidance, abilityId) is { } skill)
        {
            if (skill.State == GuidanceState.NotInBuild) return SpecialisedState(skill, stage);
            return skill.Slot < 0 ? GuidanceState.ToPlace : SpecialisedState(skill, stage);
        }
        int planned = Array.IndexOf(stage.Bar, abilityId), have = Array.IndexOf(allocation.Bar, abilityId);
        if (planned >= 0) return have == planned ? GuidanceState.Matches : have >= 0 ? GuidanceState.Partial : GuidanceState.ToPlace;
        return have >= 0 ? GuidanceState.NotInBuild : null;
    }

    /// <summary>Destroys the skill choice marks and forgets what they were made from.</summary>
    private static void ClearChoiceMarks(Run run)
    {
        if (run.ChoicePanel == GuidedPanel.None && run.ChoiceMarks.Count == 0) return;
        foreach (var mark in run.ChoiceMarks) Drop(run, mark, run.ChoiceMarks);
        run.ChoiceMarks.Clear();
        run.ChoicePanel = GuidedPanel.None;
        run.ChoiceKey = int.MinValue;
        run.ChoiceMarkedGuidance = null;
    }

    // A specialised skill: matching only in its planned slot with its tree complete (every planned node, nothing off
    // plan); partial in another slot, with nodes still to take, or when the build has it on the bar but doesn't
    // specialise it (it takes a slot another planned skill needs); not in the build otherwise.
    private static GuidanceState SpecialisedState(SkillGuide skill, Stage stage) => skill.State == GuidanceState.NotInBuild
        ? (OnPlannedBar(skill, stage) ? GuidanceState.Partial : GuidanceState.NotInBuild)
        : skill.PlannedSlot == skill.Slot && skill.State == GuidanceState.Matches ? GuidanceState.Matches : GuidanceState.Partial;

    // A skill the stage puts on the bar.
    private static bool OnPlannedBar(SkillGuide skill, Stage stage) => Array.IndexOf(stage.Bar, skill.AbilityId) >= 0;

    // The guidance for a skill, planned or specialised; null for one that is neither.
    private static SkillGuide? SkillOf(Guidance guidance, string? abilityId)
    {
        if (string.IsNullOrEmpty(abilityId)) return null;
        foreach (var skill in guidance.Skills)
            if (string.Equals(skill.AbilityId, abilityId, StringComparison.Ordinal)) return skill;
        return null;
    }

    /// <summary>Destroys the skill tree's marks and forgets what they were made from.</summary>
    private static void ClearSkillMarks(Run run)
    {
        if (run.SkillMarkedTree == IntPtr.Zero && run.SkillMarks.Count == 0) return;
        foreach (var mark in run.SkillMarks) Drop(run, mark, run.SkillMarks);
        run.SkillMarks.Clear();
        run.SkillMarkedTree = IntPtr.Zero;
        run.SkillMarkedNodes = -1;
        run.SkillMarkedGuidance = null;
        run.SkillTreeId = null;
        run.SkillWords = null;
    }

    // The "not in this build" words for the open tree, made once per tree.
    private static string SkillWords(Run run, SkillGuide skill, GuidanceState state) =>
        run.SkillWords ??= state == GuidanceState.Partial ? Words.SkillNotSpecialised(SkillName(run.Feature, skill)) : Words.SkillNotInBuild(SkillName(run.Feature, skill));

    // Ability names only change with a game update.
    private static readonly Dictionary<string, string> s_skillNames = new();

    /// <summary>The skill's name: the build's, else the game's, else its id.</summary>
    // The game's own name first: Maxroll's names are its internal ones ("Flay 1", "ReaperForm").
    private static string SkillName(Feature feature, SkillGuide skill) =>
        AbilityName(feature, skill.AbilityId, skill.Name.Length > 0 ? skill.Name : skill.AbilityId);

    /// <summary>An ability's name by its id: the game's, else <paramref name="fallback"/>.</summary>
    private static string AbilityName(Feature feature, string id, string fallback)
    {
        if (id.Length == 0) return fallback;
        if (s_skillNames.TryGetValue(id, out var cached)) return cached;
        if (!feature.Has(GameDataGroup)) return fallback;
        string name = fallback;
        try
        {
            // The lookup answers an id it doesn't have with some other ability, so the answer's own id is compared.
            var ability = AbilityManager.getPlayerAbility(id);
            if (Game.Alive(ability) && ability!.playerAbilityID == id && !string.IsNullOrWhiteSpace(ability.abilityName)) name = ability.abilityName;
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("planner:skill name", $"Planner: the skill's name couldn't be read: {e.Message}", 30);
            return fallback;
        }
        s_skillNames[id] = name;
        return name;
    }

    /// <summary>The "different mastery" words for the run's stage, made once per class and mastery.</summary>
    private static string MasteryWords(Run run)
    {
        if (run.Build is not { } build) return string.Empty;
        var stage = build.Stages[run.Stage];
        if (run.MasteryWordsFor == (stage.ClassId, stage.Mastery) && run.MasteryWords != null) return run.MasteryWords;
        run.MasteryWordsFor = (stage.ClassId, stage.Mastery);
        run.MasteryWords = Words.DifferentMastery(MasteryName(run.Feature, stage.ClassId, stage.Mastery));
        return run.MasteryWords;
    }

    // The game's own name for the class's mastery; its number when the game data can't be read.
    private static string MasteryName(Feature feature, int classId, int mastery)
    {
        if (feature.Has(GameDataGroup))
        {
            try
            {
                string? name = CharacterClassList.getClassName(classId, mastery);
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("planner:mastery name", $"Planner: the mastery's name couldn't be read: {e.Message}", 30);
            }
        }
        return $"mastery {mastery}";
    }
}
