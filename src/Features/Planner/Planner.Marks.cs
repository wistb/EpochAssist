using EpochAssist.Core;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UI;
using Feature = EpochAssist.Core.Feature;
using S =EpochAssist.Features.PlannerSettings;

namespace EpochAssist.Features;

/// <summary>One mark: a ring under a game element, and its label when it has one.</summary>
internal sealed class PlannerMark
{
    public GameObject Object = null!;
    public Image Ring = null!;
    public Text? Label;
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

        /// <summary>The element's mark, made on first use and reused after; null when it couldn't be made.
        /// <paramref name="scale"/> sizes the ring about its centre; a <paramref name="fill"/> with no alpha draws no fill.</summary>
        public static PlannerMark? Mark(Transform parent, Color colour, string? label, float scale, Color fill, int labelSize) =>
            Mark(parent, colour, label, true, scale, fill, labelSize);

        private static PlannerMark? Mark(Transform parent, Color colour, string? label, bool frame, float scale, Color fill, int labelSize)
        {
            if (!Game.Alive(parent)) return null;
            if (s_marks.TryGetValue(parent.Pointer, out var mark))
            {
                if (Game.Alive(mark.Object))
                {
                    Recolour(mark, colour);
                    Restyle(parent, mark, scale, fill);
                    SetLabel(mark, label, labelSize);
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
                SetLabel(mark, label, labelSize);
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
        }

        /// <summary>Destroys every mark and the ring texture (the feature stopping).</summary>
        public static void Release()
        {
            ClearAll();
            if (s_ring != null) UnityEngine.Object.Destroy(s_ring);
            if (s_texture != null) UnityEngine.Object.Destroy(s_texture);
            s_ring = null;
            s_texture = null;
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
                foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Image>(), true))
                {
                    var image = obj.TryCast<Image>();
                    if (!Game.Alive(image) || image!.gameObject.name != MarkName) continue;
                    UnityEngine.Object.Destroy(image.gameObject);
                    swept++;
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
                return new PlannerMark { Object = go, Ring = ring, Colour = colour, BaseScale = baseScale, Parent = parent.Pointer };
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
            return new PlannerMark { Object = go, Ring = ring, Colour = colour, Parent = parent.Pointer };
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
        // keeps an invisible "Border"), the specialisation slots "border". Hidden, see-through or empty ones are skipped.
        private static Image? Frame(Transform parent)
        {
            if (Child(parent, BorderName) is { } named && Visible(named)) return named;
            foreach (var image in parent.GetComponentsInChildren<Image>(true))
            {
                if (!Game.Alive(image) || image.name == MarkName) continue;
                string name = image.name;
                if (!name.StartsWith(BorderName, StringComparison.OrdinalIgnoreCase) || name.Equals(BrightName, StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Flash", StringComparison.OrdinalIgnoreCase)) continue;
                if (Game.Alive(image.sprite) && Visible(image)) return image;
            }
            return null;
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

        private const string BorderName = "Border", BrightName = "BorderBright", IconMaskName = "IconMask";

        private static void SetLabel(PlannerMark mark, string? label, int size)
        {
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
                // Above the ring's top edge, centred.
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(1f, 1f);
                rect.pivot = new Vector2(0.5f, 0f);
                rect.sizeDelta = new Vector2(40f, size + 6f);
                rect.anchoredPosition = Vector2.zero;
                rect.localScale = Vector3.one / mark.Scale;
                var text = go.AddComponent<Text>();
                text.font = font;
                text.fontSize = size;
                text.fontStyle = FontStyle.Bold;
                text.alignment = TextAnchor.LowerCenter;
                text.horizontalOverflow = HorizontalWrapMode.Overflow;
                text.verticalOverflow = VerticalWrapMode.Overflow;
                text.raycastTarget = false;
                text.color = mark.Ring.color;
                mark.Label = text;
            }
            if (!mark.Label!.gameObject.activeSelf) mark.Label.gameObject.SetActive(true);
            if (mark.Label.fontSize != size)
            {
                mark.Label.fontSize = size;
                var area = mark.Label.rectTransform;
                area.sizeDelta = new Vector2(area.sizeDelta.x, size + 6f);
            }
            if (mark.Label.text != label) mark.Label.text = label;
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

        private static Sprite Ring()
        {
            if (s_ring != null) return s_ring;
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
                    float alpha = Mathf.Clamp01(0.5f - outside) * Mathf.Clamp01(outside + RingWidth + 0.5f);
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            texture.Apply(false, true);
            s_texture = texture;
            s_ring = Sprite.Create(texture, new Rect(0f, 0f, RingSize, RingSize), new Vector2(0.5f, 0.5f), 100f, 0u, SpriteMeshType.FullRect,
                new Vector4(RingBorder, RingBorder, RingBorder, RingBorder));
            s_ring.hideFlags = HideFlags.HideAndDontSave;
            return s_ring;
        }
    }
}

internal static partial class Planner
{
    // The marks kept by the pass running now; swapped into the run's list, so no pass allocates a list.
    private static readonly List<PlannerMark> s_kept = new();
    private static int s_pass;

    // A highlighted next step's ring is this much larger than the others.
    private const float HighlightGrow = 1.1f;

    /// <summary>Every setting the marks are drawn from, for one panel; a pass runs again when any of them changes.</summary>
    private struct MarkLook
    {
        public Color Match, Partial, ToPlace, OffPlan, NextColour;
        public bool Next, NextLabel, Highlight, NextOwn, FillTint, SkillOffPlan;
        public bool ShowMatch, ShowPartial, ShowToPlace, ShowOffPlan;
        public float Opacity, Size, Fill, Panel;
        public int LabelSize, NextCount;

        public static MarkLook Now(GuidedPanel panel) => new()
        {
            Match = S.Colour(S.MatchColour),
            Partial = S.Colour(S.PartialColour),
            ToPlace = S.Colour(S.ToPlaceColour),
            OffPlan = S.Colour(S.OffPlanColour),
            NextColour = S.Colour(S.NextColour),
            Next = S.NextSteps.Value,
            NextLabel = S.NextLabel.Value,
            Highlight = S.HighlightNext.Value,
            NextOwn = S.NextOwnColour.Value,
            FillTint = S.FillTint.Value,
            SkillOffPlan = S.SkillOffPlanNodes.Value,
            ShowMatch = S.ShowMatches.Value,
            ShowPartial = S.ShowPartial.Value,
            ShowToPlace = S.ShowToPlace.Value,
            ShowOffPlan = S.ShowOffPlan.Value,
            Opacity = S.OutlineOpacity.Value,
            Size = S.OutlineSize.Value,
            Fill = S.FillOpacity.Value,
            Panel = PanelOpacity(panel).Value,
            LabelSize = Mathf.RoundToInt(S.NextLabelSize.Value),
            NextCount = Mathf.RoundToInt(S.NextCount.Value),
        };

        public readonly bool Same(MarkLook other) => Match == other.Match && Partial == other.Partial && ToPlace == other.ToPlace
            && OffPlan == other.OffPlan && NextColour == other.NextColour && Next == other.Next && NextLabel == other.NextLabel
            && Highlight == other.Highlight && NextOwn == other.NextOwn && FillTint == other.FillTint && SkillOffPlan == other.SkillOffPlan
            && ShowMatch == other.ShowMatch && ShowPartial == other.ShowPartial && ShowToPlace == other.ShowToPlace
            && ShowOffPlan == other.ShowOffPlan && Opacity == other.Opacity && Size == other.Size && Fill == other.Fill
            && Panel == other.Panel && LabelSize == other.LabelSize && NextCount == other.NextCount;

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
        /// to click, so it keeps its mark whatever its state's switch, and it and the highlighted steps ignore the outline
        /// opacity.
        /// </summary>
        public readonly bool Style(NodeGuide guide, out Color ring, out float scale, out Color fill)
        {
            bool next = guide.NextStep == 1;
            bool highlighted = Highlight && guide.NextStep > 1 && guide.NextStep <= NextCount;
            var colour = next && NextOwn ? NextColour : Of(guide.State);
            if (!next && !Shown(guide.State))
            {
                ring = fill = default;
                scale = 1f;
                return false;
            }
            ring = new Color(colour.r, colour.g, colour.b, colour.a * Panel * (next || highlighted ? 1f : Opacity));
            scale = highlighted ? Size * HighlightGrow : Size;
            fill = FillTint ? new Color(colour.r, colour.g, colour.b, colour.a * Panel * Fill) : Color.clear;
            return true;
        }
    }

    // Each panel's own opacity; skill choice marks, when drawn, take theirs from here too.
    private static Number PanelOpacity(GuidedPanel panel) => panel switch
    {
        GuidedPanel.Passive => S.PassiveOpacity,
        GuidedPanel.SkillTree => S.SkillTreeOpacity,
        _ => S.SkillChoiceOpacity,
    };

    /// <summary>
    /// Marks the page of the passive tree the panel shows. A page's nodes exist and are active only once it's shown, so
    /// the marks are made again when the page, its tree or its node count changes, and when the guidance or the look
    /// changes; otherwise a call only compares. Run each tick and after each new comparison while the panel is open.
    /// </summary>
    private static void MarkPassive(Run run)
    {
        var guidance = run.Guidance;
        if (run.Open != GuidedPanel.Passive || guidance == null || !S.Enabled.Value || !S.PassiveMarks.Value)
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
            bool otherMastery = guidance.DifferentMastery;
            PlannerMark? next = null;
            for (int i = 0; i < count; i++)
            {
                var node = nodes![i];
                if (!Game.Alive(node) || !node!.gameObject.activeInHierarchy) continue;
                // The character's mastery isn't the build's: none of its tree is marked, off-plan points included.
                if (otherMastery && node.mastery != 0) continue;
                if (!guides.TryGetValue(node.id, out var guide)) continue;
                var mark = MarkNode(node.transform, guide, look, pass);
                if (look.Next && guide.NextStep == 1 && mark != null) next = mark;
            }
            Marks.PulseOn(next);
            if (otherMastery)
            {
                // Under the mastery banner, over the tree; the panel itself when the banner is gone.
                var banner = manager.openedBanner;
                Keep(Marks.Note(Game.Alive(banner) ? banner!.transform : manager.transform, look.Partial, MasteryWords(run)), pass);
            }
            foreach (var mark in run.PassiveMarks)
                if (mark.Pass != pass) Marks.Clear(mark);
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

    private static void Keep(PlannerMark? mark, int pass)
    {
        if (mark == null) return;
        mark.Pass = pass;
        s_kept.Add(mark);
    }

    // One node's mark as the look draws it, kept by this pass; null for a node whose state is switched off.
    private static PlannerMark? MarkNode(Transform node, NodeGuide guide, in MarkLook look, int pass)
    {
        if (!look.Style(guide, out var ring, out float scale, out var fill)) return null;
        var mark = Marks.Mark(node, ring, Label(guide, look.NextLabel), scale, fill, look.LabelSize);
        Keep(mark, pass);
        return mark;
    }

    // Only the node the next point goes into is labelled: its points now against its points after this step of the
    // order. Every other mark is the outline alone; its colour gives the state.
    private static string? Label(NodeGuide guide, bool show)
    {
        if (!show || guide.NextStep != 1) return null;
        return $"{guide.Allocated} < {guide.Allocated + guide.NextAdd}";
    }

    // The specialisation slot showing this skill above its open tree; searched only when the tree is (re)marked.
    private static Transform? SlotIcon(string? abilityId)
    {
        if (string.IsNullOrEmpty(abilityId)) return null;
        try
        {
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SpecialisedAbilityIcon>(), false))
            {
                var slot = obj.TryCast<SpecialisedAbilityIcon>();
                if (Game.Alive(slot) && Game.Alive(slot!.ability) && slot.ability.playerAbilityID == abilityId) return slot.transform;
            }
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("planner:slot", $"Planner: the skill's slot couldn't be found: {e.Message}", 30);
        }
        return null;
    }

    // Below the skill's level line, its bar and "Minimum Specialized Level", in the panel's own units.
    private const float SkillNoteDrop = 36f;

    /// <summary>Destroys the passive tree's marks and forgets what they were made from.</summary>
    private static void ClearPassiveMarks(Run run)
    {
        if (run.MarkedTree == IntPtr.Zero && run.PassiveMarks.Count == 0) return;
        foreach (var mark in run.PassiveMarks) Marks.Clear(mark);
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
        if (run.Open != GuidedPanel.SkillTree || guidance == null || !S.Enabled.Value || !S.SkillTreeMarks.Value)
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
            // Under the skill's name at the top left, clear of the specialisation slots; the tree's top edge when the name is gone.
            if (notInBuild)
            {
                // The tree's centre node and the skill's slot in the row of five above it, in the not-in-build colour.
                var root = tree.getNode(0);
                var offPlan = new NodeGuide(GuidanceState.NotInBuild, 0, 0, 0, 0);
                if (Game.Alive(root)) MarkNode(root!.transform, offPlan, look, pass);
                if (SlotIcon(run.SkillTreeId) is { } slot) MarkNode(slot, offPlan, look, pass);
                // Under the skill level block ("Level n", its bar and "Minimum Specialized Level"), below the title.
                Component? name = Game.Alive(page) ? page!.SkillLevel : null;
                Keep(Game.Alive(name)
                    ? Marks.Note(name!.transform, look.OffPlan, SkillWords(run, skill!), left: true, drop: SkillNoteDrop)
                    : Marks.Note(tree.transform, look.OffPlan, SkillWords(run, skill!), top: true), pass);
            }
            foreach (var mark in run.SkillMarks)
                if (mark.Pass != pass) Marks.Clear(mark);
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
        foreach (var mark in run.SkillMarks) Marks.Clear(mark);
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
    private static string SkillName(Feature feature, SkillGuide skill)
    {
        string id = skill.AbilityId;
        string fallback = skill.Name.Length > 0 ? skill.Name : id;
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
