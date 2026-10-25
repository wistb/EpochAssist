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

/// <summary>
/// World positions on the game's minimap and back, worked out once a frame while asked for: from the minimap's own
/// fields (its map rect is centred on the player and shows 2 × zoom metres across, turned with the camera's yaw), or,
/// when that is more than 2 pixels off the game's own icons or the fields are missing, fitted to those icons once a
/// second. Nothing while the minimap is missing, inactive or hidden. A read only; never an edit.
/// </summary>
internal static class MinimapMap
{
    private const float FitSeconds = 1f;
    private const float MaxErrorPixels = 2f;

    private static int s_frame = -1;
    private static bool s_ready;
    private static Rect s_rect;
    private static Vector2 s_centre;
    private static Vector3 s_player;
    // screen offset (bottom-left origin) = (A00·dx + A01·dz, A10·dx + A11·dz) for d = world − player.
    private static float s_a00, s_a01, s_a10, s_a11;
    private static bool s_fitted;
    private static float s_fitA00, s_fitA01, s_fitA10, s_fitA11;
    private static Cooldown s_nextFit;

    /// <summary>The minimap is there and visible, and the mapping is known this frame.</summary>
    public static bool Ready
    {
        get
        {
            Compute();
            return s_ready;
        }
    }

    /// <summary>The minimap's map area on screen, top-left origin.</summary>
    public static Rect ScreenRect
    {
        get
        {
            Compute();
            return s_rect;
        }
    }

    /// <summary>"fields" or "fitted".</summary>
    public static string Source => s_fitted ? "fitted" : "fields";

    /// <summary>Pixels per metre along the map's two axes and the map's rotation in degrees, for the dev command.</summary>
    public static (float X, float Y, float Rotation) Scale
    {
        get
        {
            Compute();
            return (new Vector2(s_a00, s_a10).magnitude, new Vector2(s_a01, s_a11).magnitude, Mathf.Atan2(s_a10, s_a00) * Mathf.Rad2Deg);
        }
    }

    /// <summary>Where a world point is on the minimap (top-left origin), or null outside its area or without one.</summary>
    public static Vector2? ToScreen(Vector3 world)
    {
        if (!Ready) return null;
        float dx = world.x - s_player.x, dz = world.z - s_player.z;
        var p = new Vector2(s_centre.x + s_a00 * dx + s_a01 * dz, Screen.height - (s_centre.y + s_a10 * dx + s_a11 * dz));
        return s_rect.Contains(p) ? p : null;
    }

    /// <summary>The world point under a minimap point (top-left origin); y is the player's.</summary>
    public static Vector3? ToWorld(Vector2 screen)
    {
        if (!Ready) return null;
        float det = s_a00 * s_a11 - s_a01 * s_a10;
        if (Mathf.Abs(det) < 1e-6f) return null;
        float ux = screen.x - s_centre.x, uy = Screen.height - screen.y - s_centre.y;
        return new Vector3(s_player.x + (s_a11 * ux - s_a01 * uy) / det, s_player.y, s_player.z + (-s_a10 * ux + s_a00 * uy) / det);
    }

    /// <summary>The game's own icons with their screen point and ours (top-left origin), for the dev check.</summary>
    public static IEnumerable<(string Name, Vector2 Game, Vector2? Ours)> Icons()
    {
        if (!Ready) yield break;
        foreach (var (world, ui, name) in GameIcons())
        {
            float dx = world.x - s_player.x, dz = world.z - s_player.z;
            var ours = new Vector2(s_centre.x + s_a00 * dx + s_a01 * dz, Screen.height - (s_centre.y + s_a10 * dx + s_a11 * dz));
            yield return (name, new Vector2(ui.x, Screen.height - ui.y), ours);
        }
    }

    internal static void Reset()
    {
        s_frame = -1;
        s_ready = false;
        s_fitted = false;
        s_nextFit.Clear();
    }

    private static void Compute()
    {
        int frame = Time.frameCount;
        if (frame == s_frame) return;
        s_frame = frame;
        s_ready = false;
        if (FeatureHost.Find("visuals") is not { State: FeatureState.Running } feature || !feature.Has(VisualsFeature.Minimap)) return;
        try
        {
            ComputeNow();
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("visuals:minimap", $"Minimap mapping failed: {e.Message}", 30);
        }
    }

    private static void ComputeNow()
    {
        var map = Game.Minimap;
        if (!Game.Alive(map) || !map!.gameObject.activeInHierarchy || Player.Transform is not Transform player) return;
        var canvas = map.GetComponentInParent<Canvas>();
        if (canvas == null || !canvas.isActiveAndEnabled || canvas.rootCanvas == null || !canvas.rootCanvas.enabled) return;
        var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
        map.transform.Cast<RectTransform>().GetWorldCorners(corners);
        // An overlay canvas's world space is the screen in pixels (bottom-left origin).
        float left = corners[0].x, bottom = corners[0].y, right = corners[2].x, top = corners[2].y;
        if (right - left < 4f || top - bottom < 4f) return;
        s_rect = new Rect(left, Screen.height - top, right - left, top - bottom);
        s_centre = new Vector2((left + right) / 2f, (bottom + top) / 2f);
        s_player = player.position;
        float zoom = map.mapParameters.z;
        var camera = Camera.main;
        if (zoom > 0f && camera != null)
        {
            float yaw = camera.transform.eulerAngles.y * Mathf.Deg2Rad;
            float sx = s_rect.width / (2f * zoom), sy = s_rect.height / (2f * zoom);
            // Screen right is the camera's right, screen up its forward, both on the ground plane.
            s_a00 = Mathf.Cos(yaw) * sx;
            s_a01 = -Mathf.Sin(yaw) * sx;
            s_a10 = Mathf.Sin(yaw) * sy;
            s_a11 = Mathf.Cos(yaw) * sy;
        }
        if (s_nextFit.Ready)
        {
            s_nextFit.Start(FitSeconds);
            Fit(zoom > 0f && camera != null);
        }
        if (s_fitted)
        {
            s_a00 = s_fitA00;
            s_a01 = s_fitA01;
            s_a10 = s_fitA10;
            s_a11 = s_fitA11;
        }
        else if (zoom <= 0f || camera == null)
        {
            return;
        }
        s_ready = true;
    }

    // Once a second: how far the fields' mapping is off the game's icons (the median of those inside the map; icons for
    // things beyond it, like an echo's objective pulses, are clamped to its edge); past 2 pixels, or without the
    // fields, the mapping is fitted to 3 or more icons by least squares and used until the fields agree again.
    private static void Fit(bool haveFields)
    {
        double sxx = 0, sxz = 0, szz = 0, sux = 0, suz = 0, svx = 0, svz = 0;
        var errors = new List<double>();
        foreach (var (world, ui, _) in GameIcons())
        {
            double dx = world.x - s_player.x, dz = world.z - s_player.z, u = ui.x - s_centre.x, v = ui.y - s_centre.y;
            if (Math.Abs(u) > s_rect.width || Math.Abs(v) > s_rect.height) continue;
            // An icon the fields put outside the map is one the game clamps to the edge: it says nothing of the mapping.
            if (haveFields && (Math.Abs(s_a00 * dx + s_a01 * dz) > s_rect.width / 2 || Math.Abs(s_a10 * dx + s_a11 * dz) > s_rect.height / 2)) continue;
            sxx += dx * dx; sxz += dx * dz; szz += dz * dz;
            sux += u * dx; suz += u * dz; svx += v * dx; svz += v * dz;
            if (haveFields) errors.Add(Math.Sqrt(Math.Pow(s_a00 * dx + s_a01 * dz - u, 2) + Math.Pow(s_a10 * dx + s_a11 * dz - v, 2)));
        }
        if (haveFields && errors.Count > 0)
        {
            errors.Sort();
            if (errors[errors.Count / 2] <= MaxErrorPixels)
            {
                s_fitted = false;
                return;
            }
        }
        if (errors.Count < 3 && haveFields)
        {
            s_fitted = false;
            return;
        }
        double det = sxx * szz - sxz * sxz;
        if (Math.Abs(det) < 1e-6) return;
        s_fitA00 = (float)((sux * szz - suz * sxz) / det);
        s_fitA01 = (float)((suz * sxx - sux * sxz) / det);
        s_fitA10 = (float)((svx * szz - svz * sxz) / det);
        s_fitA11 = (float)((svz * sxx - svx * sxz) / det);
        s_fitted = true;
    }

    // The game's minimap icons: their world point and their UI element's screen point (bottom-left origin).
    private static IEnumerable<(Vector3 World, Vector3 Ui, string Name)> GameIcons()
    {
        var manager = Il2CppLE.UI.Minimap.MinimapIconManager.Instance;
        if (!Game.Alive(manager) || manager.icons == null) yield break;
        foreach (var info in manager.icons)
        {
            if (info == null || !Game.Alive(info.icon) || !Game.Alive(info.ui) || !info.ui.gameObject.activeInHierarchy) continue;
            yield return (info.icon.transform.position, info.ui.transform.position, info.icon.name);
        }
    }
}

/// <summary>
/// The core visuals layer: highlights on actors and effects, ended with their subject, and the minimap
/// mapping for drawing on the game's minimap. Each part is an optional group, so a game update removes only what broke.
/// </summary>
internal sealed class VisualsFeature : Feature
{
    public const string Outlines = "visuals: outlines";
    public const string OutlineColours = "visuals: outline colours";
    public const string Tints = "visuals: tints";
    public const string Effects = "visuals: effects";
    public const string Minimap = "visuals: minimap";

    public override string Id => "visuals";
    public override string Name => "Visuals";
    public override string[] Needs => new[] { "game", "world" };

    public override Requirement[] Requirements => new[]
    {
        Need<Outline>(nameof(Outline.Group), Outlines),
        Need<Outline>(nameof(Outline.DepthTest), Outlines),
        Need<Outline>(nameof(Outline._Renderer_k__BackingField), Outlines),
        Need<Outline>(nameof(Outline.ForceMarkAsRemovedFromOutlineEffectState), Outlines),
        Need<Il2CppLE.Effects.OutlineEffectState>(nameof(Il2CppLE.Effects.OutlineEffectState.Instance), Outlines),
        Need<Il2CppLE.Effects.OutlineEffectState>(nameof(Il2CppLE.Effects.OutlineEffectState.Remove), Outlines),
        Need<ActorVisuals>(nameof(ActorVisuals.rendererManager), Outlines),
        Need<RendererManager>(nameof(RendererManager.renderers), Outlines),
        Need<Il2CppLE.Effects.OutlinePostEffect>(nameof(Il2CppLE.Effects.OutlinePostEffect.outlineColour0), OutlineColours),
        Need<Il2CppLE.Effects.OutlinePostEffect>(nameof(Il2CppLE.Effects.OutlinePostEffect.outlineColour1), OutlineColours),
        Need<Il2CppLE.Effects.OutlinePostEffect>(nameof(Il2CppLE.Effects.OutlinePostEffect.outlineColour2), OutlineColours),
        Need<UnityEngine.Rendering.PostProcessing.PostProcessVolume>(nameof(UnityEngine.Rendering.PostProcessing.PostProcessVolume.sharedProfile), OutlineColours),
        Need<ActorVisuals>(nameof(ActorVisuals.rendererManager), Tints),
        Need<RendererManager>(nameof(RendererManager.ApplyMonsterRarityPropertyBlock), Tints),
        Need<RendererManager>(nameof(RendererManager.ApplyMonsterRarityGlow), Tints),
        Need<MaterialList>(nameof(MaterialList.getRareMonsterRaritySettings), Tints),
        Need<MonsterRarityColorSettings>(nameof(MonsterRarityColorSettings.normalInfluence), Tints),
        Need<Renderer>(nameof(Renderer.SetPropertyBlock), Effects),
        Need<Material>(nameof(Material.HasProperty), Effects),
        Need<Il2CppLE.UI.Minimap.Minimap>(nameof(Il2CppLE.UI.Minimap.Minimap.mapParameters), Minimap),
        Need<Il2CppLE.UI.Minimap.MinimapIconManager>(nameof(Il2CppLE.UI.Minimap.MinimapIconManager.Instance), Minimap),
        Need<Il2CppLE.UI.Minimap.MinimapIconManager>(nameof(Il2CppLE.UI.Minimap.MinimapIconManager.minimap), Minimap),
        Need<CameraManager>(nameof(CameraManager.instance), OutlineColours),
        Need<Il2CppLE.UI.Minimap.MinimapIconManager>(nameof(Il2CppLE.UI.Minimap.MinimapIconManager.icons), Minimap),
    };

    public override void Update() => Highlights.Check();

    public override void OnSceneChanged() => MinimapMap.Reset();

    // Nothing ends a highlight once this stops, so the edits behind them go now, whoever owns them.
    public override void Shutdown()
    {
        Highlights.RemoveAll();
        MinimapMap.Reset();
    }
}
