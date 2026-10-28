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
        private static Texture2D? s_texture;
        private static Sprite? s_ring;
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
                ring.type = Image.Type.Simple;
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

        private static Sprite? WhiteFrame(Sprite frame)
        {
            if (s_white.TryGetValue(frame.name, out var made)) return Game.Alive(made) ? made : null;
            Sprite? white = null;
            RenderTexture? rt = null;
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
                    // A floor, so the darkest parts of the frame still carry some colour.
                    byte v = (byte)(FrameFloor + grey * (255 - FrameFloor) / 255);
                    pixels[i] = new Color32(v, v, v, p.a);
                }
                texture.SetPixels32(pixels);
                texture.Apply(false, true);
                white = Sprite.Create(texture, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), frame.pixelsPerUnit, 0u, SpriteMeshType.FullRect, frame.border);
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
            s_white[frame.name] = white;
            return white;
        }

        private static readonly List<Texture2D> s_whiteTextures = new();
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
    // or as the counters write it, after it out of the plan ("3/4"). Every other mark is the outline alone; its colour
    // gives the state.
    private static string? Label(NodeGuide guide, bool show, bool slash)
    {
        if (!show || guide.NextStep != 1) return null;
        int after = guide.Allocated + 1;
        return slash ? $"{after}/{guide.Planned}" : $"{guide.Allocated} < {after}";
    }


    // Below the skill's level line, its bar and "Minimum Specialized Level", in the panel's own units.
    private const float SkillNoteDrop = 36f;

    /// <summary>Destroys the passive tree's marks and forgets what they were made from.</summary>
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
            run.SkillMarkedTree = tree.Pointer;
            run.SkillMarkedNodes = count;
            run.SkillMarkedGuidance = guidance;
            run.SkillMarkedLook = look;

            int pass = ++s_pass;
            Marks.FrameMissing = false;
            s_kept.Clear();
            var skill = SkillOf(guidance, run.SkillTreeId);
            bool notInBuild = skill != null && (skill.Tree.Flags & TreeFlags.SkillNotInBuild) != 0;
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
            MarkSlots(guidance, look, pass);
            // Under the skill's name at the top left, clear of the specialisation slots; the tree's top edge when the name is gone.
            if (notInBuild)
            {
                // The tree's centre node in the not-in-build colour; its slot above is already marked so.
                var root = tree.getNode(0);
                if (Game.Alive(root)) MarkNode(root!.transform, new NodeGuide(GuidanceState.NotInBuild, 0, 0, 0, 0), look, pass);
                // Under the skill level block ("Level n", its bar and "Minimum Specialized Level"), below the title.
                Component? name = Game.Alive(page) ? page!.SkillLevel : null;
                Keep(Game.Alive(name)
                    ? Marks.Note(name!.transform, look.OffPlan, SkillWords(run, skill!), left: true, drop: SkillNoteDrop)
                    : Marks.Note(tree.transform, look.OffPlan, SkillWords(run, skill!), top: true), pass);
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
        foreach (var group in new[] { page.baseClassPanelIcons, page.masteryClass1PanelIcons, page.masteryClass2PanelIcons, page.masteryClass3PanelIcons })
            for (int i = 0; group != null && i < group.Count; i++) MarkPageIcon(group[i], guidance, stage, allocation, look, pass);
        MarkPageIcon(page.masteryAbilityPanelIcon1, guidance, stage, allocation, look, pass);
        MarkPageIcon(page.masteryAbilityPanelIcon2, guidance, stage, allocation, look, pass);
        MarkPageIcon(page.masteryAbilityPanelIcon3, guidance, stage, allocation, look, pass);
        MarkSlots(guidance, look, pass);
    }

    // The five specialisation slots, each by the skill in it: in its planned slot, in another, or not in the build; an
    // empty slot is to place while a planned skill is still to specialise. Searched only when the panel is (re)marked.
    private static void MarkSlots(Guidance guidance, in MarkLook look, int pass)
    {
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SpecialisedAbilityIcon>(), false))
        {
            var slot = obj.TryCast<SpecialisedAbilityIcon>();
            if (!Game.Alive(slot) || !slot!.gameObject.activeInHierarchy) continue;
            string? id = Game.Alive(slot.ability) ? slot.ability.playerAbilityID : null;
            GuidanceState? state = null;
            if (!string.IsNullOrEmpty(id))
            {
                if (SkillOf(guidance, id) is { } skill)
                    state = skill.State == GuidanceState.NotInBuild ? GuidanceState.NotInBuild
                        : skill.PlannedSlot == skill.Slot ? GuidanceState.Matches : GuidanceState.Partial;
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
            if (skill.State == GuidanceState.NotInBuild) return GuidanceState.NotInBuild;
            return skill.Slot < 0 ? GuidanceState.ToPlace : skill.Slot == skill.PlannedSlot ? GuidanceState.Matches : GuidanceState.Partial;
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
    private static string SkillWords(Run run, SkillGuide skill) =>
        run.SkillWords ??= Words.SkillNotInBuild(SkillName(run.Feature, skill));

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
