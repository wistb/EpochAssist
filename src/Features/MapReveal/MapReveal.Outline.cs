using EpochAssist.Core;
using UnityEngine;
using UnityEngine.UI;
using S = EpochAssist.Features.MapRevealSettings;

namespace EpochAssist.Features;

// A thicker outline. The game's map shader draws a one-pixel line whatever its width value, so copies of the map image
// sit in front of it, one at every whole screen-pixel offset within the thickness (a filled disk: eight directions alone leave gaps
// on the diagonals), each drawn with a copy of the map's material that shows only the outline. Copies farther out fade,
// so the band blends into the game's own line. The copies are ours, made and removed through an edit on the map image.
internal static partial class MapReveal
{
    // The copies and one material per ring, kept outside the run: the host removes the edit after Shutdown.
    private static readonly List<GameObject> s_copies = new();
    private static readonly List<(int Ring, Vector2 Offset)> s_copyOffsets = new();
    private static readonly List<Material> s_ringMaterials = new();

    internal static readonly EditTarget<int> OutlineCopiesTarget = new()
    {
        Name = "map: outline copies",
        Find = () => One(Game.Alive(Game.Minimap) ? Game.Minimap!.GetComponent<RawImage>() : null),
        Apply = (o, rings) => MakeCopies(o.Cast<RawImage>(), rings),
        Restore = (_, _) => DestroyCopies(),
        Scene = false,
    };

    private static void MakeCopies(RawImage image, int rings)
    {
        DestroyCopies();
        var map = Game.Minimap;
        if (rings <= 0 || !Game.Alive(map) || map!.Material == null) return;
        for (int ring = 1; ring <= rings; ring++) s_ringMaterials.Add(new Material(map.Material) { name = $"EpochAssist outline ring {ring}" });
        // Every offset in the disk, its ring the distance rounded: ring 1 draws at full strength, the outer ones fade.
        for (int dx = -rings; dx <= rings; dx++)
        {
            for (int dy = -rings; dy <= rings; dy++)
            {
                float distance = Mathf.Sqrt(dx * dx + dy * dy);
                // Half a pixel past the thickness keeps the disk round (the diagonals at 1 px are in).
                if (distance == 0f || distance > rings + 0.5f) continue;
                int ring = Mathf.Clamp(Mathf.RoundToInt(distance), 1, rings);
                s_copyOffsets.Add((ring, new Vector2(dx, dy)));
                var go = new GameObject("EpochAssist outline");
                var raw = go.AddComponent<RawImage>();
                raw.raycastTarget = false;
                raw.material = s_ringMaterials[ring - 1];
                s_copies.Add(go);
            }
        }
        FollowImage(image);
    }

    private static void DestroyCopies()
    {
        foreach (var go in s_copies)
        {
            if (Game.Alive(go)) UnityEngine.Object.Destroy(go);
        }
        foreach (var material in s_ringMaterials)
        {
            if (material != null) UnityEngine.Object.Destroy(material);
        }
        s_copies.Clear();
        s_copyOffsets.Clear();
        s_ringMaterials.Clear();
    }

    private static void HoldOutlineCopies(Run run, bool on)
    {
        var look = run.Look;
        int rings = on ? Mathf.RoundToInt(S.OutlineThickness.Value) : 0;
        if (rings <= 0)
        {
            look.OutlineCopies?.Remove();
            look.OutlineCopies = null;
            return;
        }
        if (look.OutlineCopies is not { Removed: false } edit) look.OutlineCopies = Edits.Apply(run.Feature, OutlineCopiesTarget, rings);
        else if (edit.Value != rings) edit.Set(rings);
    }

    /// <summary>Each frame while there are copies: they follow the map image (the game moves it between the corner and
    /// the overlay) and its material (zoom, pan, the player), showing only the outline, fading ring by ring.</summary>
    public static void OutlineFrame()
    {
        if (s_copies.Count == 0) return;
        var map = Game.Minimap;
        var image = Game.Alive(map) ? map!.GetComponent<RawImage>() : null;
        if (image == null || map!.Material == null) return;
        int rings = s_ringMaterials.Count;
        float softness = S.OutlineSoftness.Value;
        for (int ring = 1; ring <= rings; ring++)
        {
            var material = s_ringMaterials[ring - 1];
            material.CopyPropertiesFromMaterial(map.Material);
            HideColour(material, FillProperty);
            HideColour(material, ShadowProperty);
            HideColour(material, GlowProperty);
            material.SetFloat(FillTextureProperty, 0f);
            // Outer rings fade by the softness: with 2 rings at 50%, the outer ring draws at half the outline's strength.
            float fade = 1f - softness * (ring - 1f) / Mathf.Max(1f, rings - 1f);
            var outline = material.GetColor(OutlineProperty);
            outline.a *= Mathf.Clamp01(fade);
            material.SetColor(OutlineProperty, outline);
        }
        FollowImage(image);
    }

    private static void HideColour(Material material, string property)
    {
        var c = material.GetColor(property);
        c.a = 0f;
        material.SetColor(property, c);
    }

    // Between the map image and its icons, the same size, place and texture, each shifted by its offset.
    private static void FollowImage(RawImage image)
    {
        var src = image.rectTransform;
        bool shown = image.gameObject.activeInHierarchy;
        // Offsets are screen pixels: the canvas is scaled (and differently in the corner and the overlay), and a step
        // of more than a pixel leaves gaps between the shifted lines.
        float scale = Mathf.Abs(src.lossyScale.x);
        float unit = scale > 1e-4f ? 1f / scale : 1f;
        for (int i = 0; i < s_copies.Count; i++)
        {
            var go = s_copies[i];
            if (!Game.Alive(go)) continue;
            var rt = go.GetComponent<RectTransform>();
            // Right after the map image and before the icons beside it: behind the image, its own fill would cover the
            // band's inner half; after the icons, the band would cover them.
            if (rt.parent != src.parent) rt.SetParent(src.parent, false);
            int wanted = src.GetSiblingIndex() + 1 + i;
            if (rt.GetSiblingIndex() != wanted) rt.SetSiblingIndex(wanted);
            rt.anchorMin = src.anchorMin;
            rt.anchorMax = src.anchorMax;
            rt.pivot = src.pivot;
            rt.sizeDelta = src.sizeDelta;
            rt.localScale = src.localScale;
            rt.localRotation = src.localRotation;
            rt.anchoredPosition = src.anchoredPosition + s_copyOffsets[i].Offset * unit;
            var raw = go.GetComponent<RawImage>();
            raw.texture = image.texture;
            raw.uvRect = image.uvRect;
            raw.color = image.color;
            if (go.activeSelf != shown) go.SetActive(shown);
        }
    }
}
