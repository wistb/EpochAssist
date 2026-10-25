using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using Feature = EpochAssist.Core.Feature;
using Outline = Il2CppLE.Effects.Outline;

namespace EpochAssist.Core;

internal enum HighlightKind { Outline, Tint, Effect }

/// <summary>An outline's look: one of the game's three outline groups (one colour each), and whether it shows through walls.</summary>
internal readonly record struct OutlineLook(int Group, bool ThroughWalls);

/// <summary>A tint: the rim colour and how strong it is (1 = the game's own rare glow).</summary>
internal readonly record struct TintLook(Color Colour, float Strength);

/// <summary>
/// One owner's highlight on one actor or effect, carried by an edit. It ends with its subject (dead, gone,
/// pooled), when the owner removes it, or with the owner.
/// </summary>
internal sealed class Highlight
{
    internal Highlight(Feature owner, HighlightKind kind, IntPtr subject, string name)
    {
        Owner = owner;
        Kind = kind;
        Subject = subject;
        Name = name;
    }

    public Feature Owner { get; }
    public HighlightKind Kind { get; }
    /// <summary>What it is on, for display (the actor's name or the effect's root).</summary>
    public string Name { get; }
    public bool Active => Edit is { Removed: false };
    internal IntPtr Subject { get; }
    internal Edit? Edit { get; set; }
    internal WorldEntry? Actor { get; init; }
    internal GameObject? Root { get; init; }
    /// <summary>The look as text (group, colour), for the dev command.</summary>
    internal string Look { get; set; } = string.Empty;

    public void Remove()
    {
        Edit?.Remove();
        Highlights.Forget(this);
    }
}

/// <summary>
/// Outlines, tints and effect recolours on single actors and effects, each an edit owned by the caller:
/// outlines through the game's own outline post effect (our own Outline component per renderer, in one of three group
/// colours), tints through the actor's own rarity glow block, effects through their particle renderers' blocks. Other
/// users of a shared material don't change. The visuals feature ends each highlight with its subject.
/// </summary>
internal static class Highlights
{
    private static readonly List<Highlight> s_all = new();
    private static readonly string[] s_effectColours = { "_TintColor", "_Color", "_BaseColor" };

    public static IReadOnlyList<Highlight> All => s_all;
    public static int Count => s_all.Count;

    private static bool Has(string group) => FeatureHost.Find("visuals") is { State: FeatureState.Running } f && f.Has(group);

    private static Highlight? Existing(Feature owner, HighlightKind kind, IntPtr subject) =>
        s_all.Find(h => h.Owner == owner && h.Kind == kind && h.Subject == subject && h.Active);

    internal static void Forget(Highlight highlight) => s_all.Remove(highlight);

    /// <summary>
    /// Outlines an actor in one of the game's outline groups (0 red: the game's enemy hover, 1 green, 2 lavender); with
    /// throughWalls it shows behind walls too. Asking again changes it. Null when outlines are missing or the actor has
    /// no visuals. A dormant actor's renderers are outlined once it wakes.
    /// </summary>
    public static Highlight? Outline(Feature owner, WorldEntry actor, int group, bool throughWalls)
    {
        if (!Has(VisualsFeature.Outlines) || group is < 0 or > 2 || actor.Gone || !Game.Alive(actor.Visuals)) return null;
        var look = new OutlineLook(group, throughWalls);
        var visuals = actor.Visuals!;
        IntPtr subject = visuals.Pointer;
        if (Existing(owner, HighlightKind.Outline, subject) is { Edit: Edit<OutlineLook> edit } existing)
        {
            edit.Set(look);
            existing.Look = Describe(look);
            return existing;
        }
        // Our components per renderer, so a restore destroys exactly those.
        var added = new Dictionary<IntPtr, Outline>();
        var target = new EditTarget<OutlineLook>
        {
            Name = $"outline: {actor.Name ?? "actor"} {subject:x}",
            Find = () => ActiveRenderers(visuals),
            Apply = (obj, value) =>
            {
                var renderer = obj.Cast<Renderer>();
                var outline = renderer.gameObject.AddComponent<Outline>();
                outline.Group = (Il2CppLE.Effects.OutlineGroup)value.Group;
                outline.DepthTest = !value.ThroughWalls;
                // Awake fills Renderer on an active object; set it and register again if it didn't.
                if (!Game.Alive(outline.Renderer))
                {
                    outline._Renderer_k__BackingField = renderer;
                    outline.enabled = false;
                    outline.enabled = true;
                }
                added[renderer.Pointer] = outline;
            },
            Restore = (obj, _) =>
            {
                if (!added.Remove(obj.Pointer, out var outline) || !Game.Alive(outline)) return;
                // Out of the game's outline list first: the destroyed component must not stay listed.
                Il2CppLE.Effects.OutlineEffectState.Instance?.Remove(outline);
                outline.ForceMarkAsRemovedFromOutlineEffectState();
                UnityEngine.Object.Destroy(outline);
                SweepOutlineState();
            },
            Show = Describe,
        };
        return Add(new Highlight(owner, HighlightKind.Outline, subject, actor.Name ?? "actor") { Actor = actor, Look = Describe(look) }, owner, target, look);
    }

    /// <summary>
    /// Tints an actor's rim in any colour, in place of its rarity glow while active (the game's settings for a rare
    /// glow, the colour scaled by strength); its own rarity glow comes back after. Null when tints are missing.
    /// </summary>
    public static Highlight? Tint(Feature owner, WorldEntry actor, Color colour, float strength = 1f)
    {
        if (!Has(VisualsFeature.Tints) || actor.Gone || !Game.Alive(actor.Visuals) || !Game.Alive(actor.Visuals!.rendererManager)) return null;
        var look = new TintLook(colour, strength);
        var visuals = actor.Visuals!;
        IntPtr subject = visuals.Pointer;
        if (Existing(owner, HighlightKind.Tint, subject) is { Edit: Edit<TintLook> edit } existing)
        {
            edit.Set(look);
            existing.Look = Describe(look);
            return existing;
        }
        var rarity = actor.Rarity switch
        {
            Core.Rarity.Magic => Il2Cpp.Actor.Rarity.Magic,
            Core.Rarity.Rare or Core.Rarity.Boss => Il2Cpp.Actor.Rarity.Rare,
            _ => Il2Cpp.Actor.Rarity.Normal,
        };
        var target = new EditTarget<TintLook>
        {
            Name = $"tint: {actor.Name ?? "actor"} {subject:x}",
            Find = () => Game.Alive(visuals) && Game.Alive(visuals.rendererManager) ? new UnityEngine.Object[] { visuals.rendererManager } : Array.Empty<UnityEngine.Object>(),
            Apply = (obj, value) =>
            {
                var game = MaterialList.getRareMonsterRaritySettings();
                var c = value.Colour * Mathf.Max(0f, value.Strength);
                obj.Cast<RendererManager>().ApplyMonsterRarityPropertyBlock(new MonsterRarityColorSettings
                {
                    color = new Color(c.r, c.g, c.b, 1f),
                    bias = game?.bias ?? 1f,
                    power = game?.power ?? 3f,
                    normalInfluence = game?.normalInfluence ?? 1f,
                });
            },
            Restore = (obj, _) => obj.Cast<RendererManager>().ApplyMonsterRarityGlow(rarity),
            Show = Describe,
        };
        return Add(new Highlight(owner, HighlightKind.Tint, subject, actor.Name ?? "actor") { Actor = actor, Look = Describe(look) }, owner, target, look);
    }

    /// <summary>
    /// Recolours an effect (a hazard or telegraph root the caller found): each active particle renderer under it gets
    /// the colour in its block; the renderer's own block comes back after. Null when effects are missing.
    /// </summary>
    public static Highlight? Effect(Feature owner, GameObject root, Color colour)
    {
        if (!Has(VisualsFeature.Effects) || !Game.Alive(root)) return null;
        IntPtr subject = root.Pointer;
        if (Existing(owner, HighlightKind.Effect, subject) is { Edit: Edit<Color> edit } existing)
        {
            edit.Set(colour);
            existing.Look = Describe(colour);
            return existing;
        }
        var kept = new Dictionary<IntPtr, MaterialPropertyBlock>();
        var target = new EditTarget<Color>
        {
            Name = $"effect: {root.name} {subject:x}",
            Find = () => EffectRenderers(root),
            Apply = (obj, value) =>
            {
                var renderer = obj.Cast<Renderer>();
                var material = renderer.sharedMaterial;
                string? property = Game.Alive(material) ? Array.Find(s_effectColours, material.HasProperty) : null;
                if (property == null) return;
                var own = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(own);
                kept[renderer.Pointer] = own;
                var block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block);
                block.SetColor(property, value);
                renderer.SetPropertyBlock(block);
            },
            Restore = (obj, _) =>
            {
                if (kept.Remove(obj.Pointer, out var own)) obj.Cast<Renderer>().SetPropertyBlock(own);
            },
            Show = Describe,
        };
        return Add(new Highlight(owner, HighlightKind.Effect, subject, root.name) { Root = root, Look = Describe(colour) }, owner, target, colour);
    }

    /// <summary>
    /// Changes an outline group's colour. It changes every outline in that group, the game's own hover outlines included,
    /// and comes back when the edit is removed. Null when the outline colours are missing.
    /// </summary>
    public static Edit<Color>? OutlineColour(Feature owner, int group, Color colour)
    {
        if (!Has(VisualsFeature.OutlineColours) || group is < 0 or > 2) return null;
        string field = $"outlineColour{group}";
        var target = new EditTarget<Color>
        {
            Name = $"outline colour {group}",
            Find = OutlineEffect,
            Read = obj => ParameterValue<Color>(Parameter(obj, field)),
            Write = (obj, value) => SetParameterValue(Parameter(obj, field), value),
            Same = (a, b) => Mathf.Abs(a.r - b.r) < 1e-3f && Mathf.Abs(a.g - b.g) < 1e-3f && Mathf.Abs(a.b - b.b) < 1e-3f && Mathf.Abs(a.a - b.a) < 1e-3f,
            Show = Describe,
            Scene = false,
        };
        return Edits.Apply(owner, target, colour);
    }

    // Destroyed outlines left in the game's list (an earlier build left ours there) are taken out.
    private static void SweepOutlineState()
    {
        var items = Il2CppLE.Effects.OutlineEffectState.Instance?.items;
        if (items == null) return;
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (!Game.Alive(items[i])) items.RemoveAt(i);
        }
    }

    private static Highlight Add<T>(Highlight highlight, Feature owner, EditTarget<T> target, T value)
    {
        s_all.Add(highlight);
        highlight.Edit = Edits.Apply(owner, target, value);
        return highlight;
    }

    /// <summary>Ends highlights whose subject is dead, gone or pooled, and forgets removed ones (the visuals feature, each frame).</summary>
    internal static void Check()
    {
        for (int i = s_all.Count - 1; i >= 0; i--)
        {
            var highlight = s_all[i];
            if (!highlight.Active)
            {
                s_all.RemoveAt(i);
                continue;
            }
            if (Ended(highlight)) highlight.Remove();
        }
    }

    // An actor whose visual is dead, gone or back in the game's pool; an effect that is inactive, destroyed or pooled.
    // The restore runs here, before the pooled object can be used again.
    private static bool Ended(Highlight highlight)
    {
        if (highlight.Actor is { } actor)
        {
            if (actor.Gone || actor.Dead || !Game.Alive(actor.Visuals)) return true;
            var go = actor.Visuals!.gameObject;
            return !go.activeInHierarchy && Pooled(go.transform);
        }
        if (highlight.Root is { } root) return !Game.Alive(root) || !root.activeInHierarchy || Pooled(root.transform);
        return true;
    }

    private static bool Pooled(Transform transform)
    {
        var root = transform.root;
        return root != null && root.name == "Auto Pool Parent";
    }

    /// <summary>The visuals feature stopped: nothing would end the highlights any more, so every one is removed now.</summary>
    internal static void RemoveAll()
    {
        foreach (var highlight in s_all.ToList()) highlight.Edit?.Remove();
        s_all.Clear();
    }

    // Only active renderers: an Outline added to an inactive object never runs its Awake and isn't drawn.
    private static IEnumerable<UnityEngine.Object> ActiveRenderers(ActorVisuals visuals)
    {
        if (!Game.Alive(visuals)) yield break;
        var renderers = visuals.rendererManager?.renderers;
        if (renderers == null) yield break;
        foreach (var renderer in renderers)
        {
            if (Game.Alive(renderer) && renderer.gameObject.activeInHierarchy) yield return renderer;
        }
    }

    private static IEnumerable<UnityEngine.Object> EffectRenderers(GameObject root)
    {
        if (!Game.Alive(root) || !root.activeInHierarchy) yield break;
        foreach (var renderer in root.GetComponentsInChildren<ParticleSystemRenderer>(false))
        {
            if (Game.Alive(renderer)) yield return renderer;
        }
    }

    // The game's outline post effect: the setting on its global post-process volume's shared profile (never `profile`,
    // which copies it). The volume sits next to the virtual camera (CameraTarget/Outline Post-Process Effect).
    private static IEnumerable<UnityEngine.Object> OutlineEffect()
    {
        var target = Game.Camera?.transform.parent;
        if (target == null) yield break;
        foreach (var volume in target.GetComponentsInChildren<UnityEngine.Rendering.PostProcessing.PostProcessVolume>(false))
        {
            var settings = volume?.sharedProfile?.settings;
            if (settings == null) continue;
            foreach (var setting in settings)
            {
                if (setting?.TryCast<Il2CppLE.Effects.OutlinePostEffect>() is { } outline)
                {
                    yield return outline;
                    yield break;
                }
            }
        }
    }

    private static Il2CppObjectBase Parameter(UnityEngine.Object effect, string field)
    {
        var outline = effect.Cast<Il2CppLE.Effects.OutlinePostEffect>();
        return field switch
        {
            "outlineColour0" => outline.outlineColour0,
            "outlineColour1" => outline.outlineColour1,
            _ => outline.outlineColour2,
        };
    }

    // The interop property of a generic class's field (ParameterOverride<T>.value) reads a wrong offset; the class's
    // own field info has the right one.
    private static IntPtr ValueAddress(Il2CppObjectBase parameter)
    {
        IntPtr field = IntPtr.Zero;
        for (IntPtr k = IL2CPP.il2cpp_object_get_class(parameter.Pointer); k != IntPtr.Zero && field == IntPtr.Zero; k = IL2CPP.il2cpp_class_get_parent(k))
            field = IL2CPP.il2cpp_class_get_field_from_name(k, "value");
        if (field == IntPtr.Zero) throw new MissingFieldException(parameter.GetType().Name, "value");
        return parameter.Pointer + (int)IL2CPP.il2cpp_field_get_offset(field);
    }

    private static T ParameterValue<T>(Il2CppObjectBase parameter) where T : struct =>
        System.Runtime.InteropServices.Marshal.PtrToStructure<T>(ValueAddress(parameter));

    private static void SetParameterValue<T>(Il2CppObjectBase parameter, T value) where T : struct =>
        System.Runtime.InteropServices.Marshal.StructureToPtr(value, ValueAddress(parameter), false);

    private static string Describe(OutlineLook look) => $"group {look.Group}{(look.ThroughWalls ? " walls" : string.Empty)}";
    private static string Describe(TintLook look) => $"{Describe(look.Colour)} x{look.Strength:0.##}";
    private static string Describe(Color c) => $"({c.r:0.##}, {c.g:0.##}, {c.b:0.##})";
}
