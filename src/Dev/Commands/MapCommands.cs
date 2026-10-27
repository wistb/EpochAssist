using System.Diagnostics;
using System.Globalization;
using EpochAssist.Core;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppLE.UI.Minimap;
using UnityEngine;

namespace EpochAssist.Dev;

// map, map icons, map volumes and map step: the minimap's fog, the game's own map icons and the explored-volume record,
// read straight from the game, and each reveal step on its own so it can be screenshotted. Nothing here writes volumes,
// calls their enter notices or saves the character.
internal static class MapCommands
{
    public const string Usage = "map | map icons [n] | map volumes | map step radius|maxdist|patch on|off | map step zoom <overlay> [corner]|off | map overlay open|close | map zoom [set|param] <value> | map show <name part> on|off | map worldicons [n] | map roots | map material [reset] | map prop <name> <r,g,b,a>|<value>|off | map pan <x> <y> [frames] | map maxdist <n>|off | map frames [n] | map sprites [text,text] | map playericon sprite <name>|tint <r,g,b,a>|scale <x> | map outline <value> | map background <r,g,b,a>";
    private const int DefaultIcons = 40;
    // The stamp's radius must reach every corner of the level from any point in it.
    private const float MinRadius = 1000f;
    private const float RadiusMargin = 256f;

    /// <summary>The dev patch group on the fog stamp, applied only while `map step patch on` holds it.</summary>
    public const string StampGroup = "map: stamp radius";
    /// <summary>The dev patch group on the minimap's update, applied only while `map step zoom` holds it.</summary>
    public const string ZoomGroup = "map: overlay zoom";

    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;

    // Object names of the kinds the reveal may offer, matched on the icon's object and its parents. A guess only: the
    // facts pass records the real names.
    private static readonly (string Kind, string[] Names)[] s_kindNames =
    {
        ("shrines", new[] { "Shrine" }),
        ("chests", new[] { "Chest" }),
        ("runeprisons", new[] { "Rune Prison" }),
        ("forges", new[] { "Forge" }),
        ("rifts", new[] { "UnstableRift", "Unstable Rift" }),
        ("waypoints", new[] { "Waypoint" }),
        ("exits", new[] { "Exit", "SceneChange", "Transition", "Door" }),
    };

    internal static readonly EditTarget<int> MaxDistance = new()
    {
        Name = "minimap: max distance",
        Find = () => One(Game.Minimap),
        Read = o => o.Cast<Minimap>().maxDistance,
        Write = (o, v) => o.Cast<Minimap>().maxDistance = v,
        Scene = false,
    };

    // map show: the name part the dev icon edit covers, and the edit.
    private static string s_showName = string.Empty;
    private static Edit<bool>? s_show;

    // The icon objects the game keeps switched off until the player comes near, switched on; Restore switches off only
    // those Apply switched on.
    private static readonly HashSet<IntPtr> s_shown = new();
    private static readonly EditTarget<bool> IconShown = new()
    {
        Name = "map: icon shown (dev)",
        Find = ShowTargets,
        Apply = (o, _) =>
        {
            var go = o.Cast<GameObject>();
            if (go.activeSelf) return;
            go.SetActive(true);
            s_shown.Add(go.Pointer);
        },
        Restore = (o, _) =>
        {
            var go = o.Cast<GameObject>();
            if (s_shown.Remove(go.Pointer) && go.activeSelf) go.SetActive(false);
        },
    };

    private static IEnumerable<UnityEngine.Object> ShowTargets()
    {
        if (s_showName.Length == 0) yield break;
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Il2CppDMM.DMMapIcon>(), true))
        {
            var icon = obj.TryCast<Il2CppDMM.DMMapIcon>();
            if (!Game.Alive(icon)) continue;
            // Only an icon whose own object is the one switched off, inside an active parent.
            var parent = icon!.transform.parent;
            if (!Game.Alive(parent) || !parent!.gameObject.activeInHierarchy) continue;
            if (Path(icon.transform).Contains(s_showName, StringComparison.OrdinalIgnoreCase)) yield return icon.gameObject;
        }
    }

    // The configs' range past which an icon is drawn clamped with an arrow; it is in world units, so it grows with the
    // zoom (the game's own scale is its saved setting, 120 to 300).
    internal static readonly EditTarget<float> IconRange = new()
    {
        Name = "map: icon range",
        Find = () => One(Il2Cpp.DMMapGlobalConfigs.instance),
        Read = o => o.Cast<Il2Cpp.DMMapGlobalConfigs>().iconDistanceThreshold,
        Write = (o, v) => o.Cast<Il2Cpp.DMMapGlobalConfigs>().iconDistanceThreshold = v,
        Scene = false,
    };
    private static Edit<float>? s_iconRange;

    private static Edit<float>? s_radius;
    private static Edit<int>? s_maxDistance;
    // The radius the stamp prefix puts in place of the game's argument; 0 while off.
    private static float s_patchRadius;
    // The map scales the update postfix writes, for the overlay (open) and the corner minimap (closed); 0 leaves the
    // game's own. The game's overlay range is 120 to 300 (larger is wider); the corner's comes from its own zoom.
    private static float s_zoomScale;
    private static float s_cornerScale;

    private static IEnumerable<UnityEngine.Object> One(UnityEngine.Object? obj)
    {
        if (Game.Alive(obj)) yield return obj!;
    }

    public static CommandResult Run(CommandCall call) => call.Lower(1) switch
    {
        "" => Status(),
        "icons" => Icons(call),
        "volumes" => Volumes(),
        "step" => Step(call),
        "overlay" => Overlay(call),
        "zoom" => Zoom(call),
        "show" => Show(call),
        "worldicons" => WorldIcons(call),
        "roots" => Roots(),
        "material" when call.Lower(2) == "reset" => MaterialReset(),
        "material" => MaterialProperties(),
        "prop" => Prop(call),
        "pan" => Pan(call),
        "maxdist" => MaxDist(call),
        "graphic" => CommandResult.Ok("map graphic: " + GraphicFacts()),
        "frames" => Frames(call),
        "sprites" => Sprites(call.Rest(2)),
        "playericon" => PlayerIcon(call),
        "outline" when float.TryParse(call.Lower(2), NumberStyles.Float, s_inv, out float thickness) => Outline(thickness),
        "background" => Background(call.Lower(2)),
        _ => CommandResult.Error($"map: usage {Usage}"),
    };

    public static void Reset()
    {
        s_radius = null;
        s_maxDistance = null;
        s_patchRadius = 0f;
        s_zoomScale = 0f;
        s_cornerScale = 0f;
        s_show = null;
        s_showName = string.Empty;
        s_iconRange = null;
        s_props.Clear();
    }

    // ---- map ----

    private static CommandResult Status()
    {
        var map = Game.Minimap;
        if (!Game.Alive(map)) return CommandResult.Error("map: no minimap");
        var lines = new List<string>
        {
            $"ok map: switch {(FeatureHost.Find("mapreveal") is { } f ? f.State.ToString().ToLowerInvariant() : "-")}, scene {GameState.ActiveSceneName}, online={GameState.Online?.ToString() ?? "?"}",
            "zone map: " + ZoneMap(map!),
            $"reveal radius {F(map!.RevealRadius)} (original {Original(ClientCommands.RevealRadius.Name)}), max distance {map.maxDistance} (original {Original(MaxDistance.Name)})",
            $"overlay open {(Game.Alive(Il2Cpp.UIBase.instance) ? Il2Cpp.UIBase.instance.overlayMapOpen.ToString() : "?")}, {OverlayZoom()}, minimap ptr {map.Pointer:X}, material ptr {(Game.Alive(map.material) ? map.material.Pointer.ToString("X") : "none")}, canvas {CanvasPlace(map)}",
            $"dev steps: radius {(s_radius is { Removed: false } ? s_radius.ValueText : "off")}, maxdist {(s_maxDistance is { Removed: false } ? s_maxDistance.ValueText : "off")}, "
                + $"patch {(s_patchRadius > 0f ? F(s_patchRadius) : "off")}",
        };
        var mine = Edits.Active.Where(e => e.Owner.Id == "mapreveal").ToList();
        lines.Add($"map reveal edits: {(mine.Count == 0 ? "none" : string.Empty)}");
        lines.AddRange(mine.Select(e => "  " + e.Describe()));
        lines.AddRange(Features.MapReveal.Describe().Select(l => "feature: " + l));
        return new CommandResult(DevOutput.Reply("map", lines));
    }

    private static string ZoneMap(Minimap map)
    {
        var info = map.minimapInformation;
        if (!Game.Alive(info)) return "none";
        var sdf = info.LiveSdf;
        var b = info.LevelBounds;
        string size = Game.Alive(sdf) ? $"{sdf.width}x{sdf.height} ptr {sdf.Pointer:X}" : "none";
        return $"{info.name} ptr {info.Pointer:X}, LiveSdf {size}, LevelBounds centre {V(b.center)} size {V(b.size)}, radius for it {F(RadiusFor(info))}";
    }

    // Where the map's canvas sits and how big it is: the corner holder with Tab closed, the full-screen one while open.
    private static string CanvasPlace(Minimap map)
    {
        var canvas = map.Canvas;
        if (!Game.Alive(canvas)) return "none";
        var parent = canvas.parent;
        var rect = canvas.rect;
        return $"{(Game.Alive(parent) ? Path(parent!) + " > " : string.Empty)}{canvas.gameObject.name} {F(rect.width)}x{F(rect.height)} active={canvas.gameObject.activeInHierarchy}";
    }

    private static string Original(string target) =>
        Edits.Active.FirstOrDefault(e => e.TargetName == target) is { } edit ? edit.OriginalText : "no edit";

    /// <summary>The level's diagonal across x and z plus a margin, at least 1000.</summary>
    private static float RadiusFor(Il2Cpp.MinimapInformation info)
    {
        var size = info.LevelBounds.size;
        return Mathf.Max(MinRadius, Mathf.Sqrt(size.x * size.x + size.z * size.z) + RadiusMargin);
    }

    // ---- map icons ----

    private sealed class IconRow
    {
        public string Name = string.Empty;
        public string Kind = "-";
        public float Distance;
        public string Text = string.Empty;
    }

    private static CommandResult Icons(CommandCall call)
    {
        int count = DefaultIcons;
        if (call.Args.Length > 1 && (!int.TryParse(call.Lower(2), out count) || count < 1)) return CommandResult.Error($"map: usage {Usage}");
        var watch = Stopwatch.StartNew();
        var found = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Il2CppDMM.DMMapIcon>(), true);
        double searchMs = watch.Elapsed.TotalMilliseconds;

        // The manager's icon list, by icon pointer, with its UI.
        var manager = MinimapIconManager.Instance;
        var listed = new Dictionary<IntPtr, MinimapIconUI?>();
        if (Game.Alive(manager) && manager!.icons is { } icons)
        {
            for (int i = 0; i < icons.Count; i++)
            {
                var info = icons[i];
                if (info != null && Game.Alive(info.icon)) listed[info.icon.Pointer] = info.ui;
            }
        }
        var player = Player.Transform is { } t && Game.Alive(t) ? t.position : (Vector3?)null;
        var rows = new List<IconRow>();
        foreach (var obj in found)
        {
            var icon = obj.TryCast<Il2CppDMM.DMMapIcon>();
            if (!Game.Alive(icon)) continue;
            var go = icon!.gameObject;
            var row = new IconRow { Name = Path(icon.transform), Kind = KindOf(icon.transform) };
            row.Distance = player is { } p ? Vector3.Distance(p, icon.transform.position) : -1f;
            string ui = "ui=none";
            if (listed.TryGetValue(icon.Pointer, out var iconUi) && Game.Alive(iconUi))
            {
                var image = iconUi!.MainImage;
                ui = $"ui={(iconUi.gameObject.activeInHierarchy ? "on" : "off")}"
                    + (Game.Alive(image) ? $" alpha={F(image!.color.a)}/{F(image.canvasRenderer.GetAlpha())}" : " image=none");
            }
            var sprite = icon.icon;
            row.Text = $"active={go.activeInHierarchy}{(go.activeInHierarchy ? string.Empty : $" off at {OffAt(icon.transform)}")} enabled={icon.enabled} listed={listed.ContainsKey(icon.Pointer)} {ui} "
                + $"sprite={(Game.Alive(sprite) ? sprite!.name : "-")} iconGO={(Game.Alive(icon.iconGO) ? icon.iconGO.activeSelf.ToString() : "none")} "
                + $"type={icon.GetIl2CppType().Name} parent [{Components(icon.transform.parent)}]";
            rows.Add(row);
        }
        watch.Stop();
        rows.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var lines = new List<string>
        {
            $"ok map icons: {Math.Min(count, rows.Count)} of {rows.Count}, {listed.Count} in the manager, search {searchMs.ToString("F2", s_inv)} ms, all {watch.Elapsed.TotalMilliseconds.ToString("F2", s_inv)} ms",
            "per kind: " + string.Join(", ", rows.GroupBy(r => r.Kind).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")),
        };
        foreach (var row in rows.Take(count)) lines.Add($"{row.Kind,-11} {F(row.Distance),8} m  {row.Name}  {row.Text}");
        return new CommandResult(DevOutput.Reply("map", lines));
    }

    // The icon's object and its parents up to the spawned root, as "Root > ... > Leaf".
    private static string Path(Transform t)
    {
        var names = new List<string>();
        for (var at = t; Game.Alive(at) && names.Count < 4; at = at!.parent)
        {
            names.Add(at!.gameObject.name);
            if (at.gameObject.name.Contains("(Clone)")) break;
        }
        names.Reverse();
        return string.Join(" > ", names);
    }

    // The component types on an object, for finding what the game uses to switch an icon on.
    private static string Components(Transform? t)
    {
        if (!Game.Alive(t)) return "-";
        var all = t!.GetComponents(Il2CppType.Of<Component>());
        return string.Join(", ", all.Where(c => c != null).Select(c => c.GetIl2CppType().Name).Where(n => n != "Transform"));
    }

    // The highest object in the chain switched off itself, and how many levels above the icon it is (0: the icon).
    private static string OffAt(Transform t)
    {
        string off = "?";
        int level = 0;
        for (var at = t; Game.Alive(at); at = at!.parent, level++)
        {
            if (!at!.gameObject.activeSelf) off = $"'{at.gameObject.name}' (+{level})";
        }
        return off;
    }

    private static string KindOf(Transform t)
    {
        for (var at = t; Game.Alive(at); at = at!.parent)
        {
            string name = at!.gameObject.name;
            foreach (var (kind, names) in s_kindNames)
            {
                if (names.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase))) return kind;
            }
        }
        return "-";
    }

    // ---- map worldicons ----

    // The game's other icon class: a world object's icon type, and the map icon it made (if any).
    private static CommandResult WorldIcons(CommandCall call)
    {
        int count = DefaultIcons;
        if (call.Args.Length > 1 && (!int.TryParse(call.Lower(2), out count) || count < 1)) return CommandResult.Error($"map: usage {Usage}");
        var watch = Stopwatch.StartNew();
        var found = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Il2Cpp.DMMapWorldIcon>(), true);
        double searchMs = watch.Elapsed.TotalMilliseconds;
        var player = Player.Transform is { } t && Game.Alive(t) ? t.position : (Vector3?)null;
        var rows = new List<(float Distance, string Text, string Type)>();
        foreach (var obj in found)
        {
            var icon = obj.TryCast<Il2Cpp.DMMapWorldIcon>();
            if (!Game.Alive(icon)) continue;
            float distance = player is { } p ? Vector3.Distance(p, icon!.transform.position) : -1f;
            var mm = icon!.MmIcon;
            string mmText = Game.Alive(mm) ? $"mm={mm!.gameObject.name} active={mm.gameObject.activeInHierarchy}" : "mm=none";
            string type = icon.icon.ToString();
            rows.Add((distance, $"{type,-16} {F(distance),8} m  {Path(icon.transform)}  active={icon.gameObject.activeInHierarchy}{(icon.gameObject.activeInHierarchy ? string.Empty : $" off at {OffAt(icon.transform)}")} current={icon.currentIcon} {mmText}", type));
        }
        rows.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var lines = new List<string>
        {
            $"ok map worldicons: {Math.Min(count, rows.Count)} of {rows.Count}, search {searchMs.ToString("F2", s_inv)} ms",
            "per type: " + string.Join(", ", rows.GroupBy(r => r.Type).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")),
        };
        lines.AddRange(rows.Take(count).Select(r => r.Text));
        return new CommandResult(DevOutput.Reply("map", lines));
    }

    // ---- map material ----

    // The map material's shader properties and their values now, and the map configs' look values: what the map's look
    // can be changed by.
    private static CommandResult MaterialProperties()
    {
        var map = Game.Minimap;
        if (!Game.Alive(map)) return CommandResult.Error("map material: no minimap");
        var lines = new List<string>();
        foreach (var (label, material) in new[] { ("Material", map!.Material), ("material", map.material) })
        {
            if (material == null) continue;
            var shader = material.shader;
            int count = shader.GetPropertyCount();
            lines.Add($"{label} '{material.name}' shader '{shader.name}', {count} properties, keywords [{string.Join(", ", material.shaderKeywords)}]");
            for (int i = 0; i < count; i++)
            {
                string name = shader.GetPropertyName(i);
                var type = shader.GetPropertyType(i);
                string value = type switch
                {
                    UnityEngine.Rendering.ShaderPropertyType.Color => material.GetColor(name).ToString(),
                    UnityEngine.Rendering.ShaderPropertyType.Vector => material.GetVector(name).ToString(),
                    UnityEngine.Rendering.ShaderPropertyType.Float or UnityEngine.Rendering.ShaderPropertyType.Range => material.GetFloat(name).ToString("G4", s_inv),
                    UnityEngine.Rendering.ShaderPropertyType.Texture => material.GetTexture(name) is { } t ? $"{t.name} {t.width}x{t.height}" : "none",
                    _ => "?",
                };
                lines.Add($"  {name} ({type}) = {value}");
            }
        }
        var configs = Il2Cpp.DMMapGlobalConfigs.instance;
        if (Game.Alive(configs))
            lines.Add($"configs: opacity {F(configs!.OpacityValue)}, outline {F(configs.OutlineThickness)}, background {configs.mapBackgroundColor}, icon scale {F(configs.globalIconScale)}");
        lines.Add($"minimap: opacity {F(map.opacity)}, outline {F(map.outlineThickness)}, units per pixel {F(map.UnitsPerPixel)}, parameters {map.mapParameters.x},{map.mapParameters.y},{map.mapParameters.z},{map.mapParameters.w}");
        lines.Insert(0, "ok map material:");
        return new CommandResult(DevOutput.Reply("map", lines));
    }

    // Dev repair, written straight: the map's copy gets its base material's look properties back (not the map
    // parameters, which the game pushes only on a zoom change).
    private static readonly string[] s_lookColours = { "_FillColor", "_OutlineColor", "_FillShadowColor", "_OuterNoiseColor" };
    private static readonly string[] s_lookFloats = { "_NoiseStrength", "_NoiseSize", "_FillShadowThickness", "_OuterNoiseStrength", "_OuterNoiseSize", "_OuterNoiseSharpness" };

    private static CommandResult MaterialReset()
    {
        var map = Game.Minimap;
        if (!Game.Alive(map) || map!.Material == null || map.material == null) return CommandResult.Error("map material reset: no map material");
        foreach (string p in s_lookColours) map.Material.SetColor(p, map.material.GetColor(p));
        foreach (string p in s_lookFloats) map.Material.SetFloat(p, map.material.GetFloat(p));
        return CommandResult.Ok($"map material reset: {s_lookColours.Length + s_lookFloats.Length} look properties from '{map.material.name}'") with { Settle = 5 };
    }

    // ---- map prop ----

    // One dev edit per material property tried, on the map's own material instance, removed with "off" or a reload.
    private static readonly Dictionary<string, Edit> s_props = new();

    private static CommandResult Prop(CommandCall call)
    {
        if (FeatureHost.Find("dev") is not { State: FeatureState.Running } dev) return CommandResult.Error("map prop: the dev bridge feature isn't running");
        string name = call.Args.Length > 1 ? call.Args[1] : string.Empty;
        string text = call.Lower(3);
        if (name.Length == 0 || text.Length == 0) return CommandResult.Error($"map: usage {Usage}");
        if (name == "all" && text == "off")
        {
            foreach (var e in s_props.Values) e.Remove();
            s_props.Clear();
            return CommandResult.Ok("map prop all off") with { Settle = 5 };
        }
        if (s_props.Remove(name, out var old)) old.Remove();
        if (text == "off") return CommandResult.Ok($"map prop {name} off") with { Settle = 5 };
        IEnumerable<UnityEngine.Object> Find() => One(Game.Alive(Game.Minimap) ? Game.Minimap!.Material : null);
        string[] parts = text.Split(',');
        if (parts.Length == 4 && parts.All(p => float.TryParse(p, NumberStyles.Float, s_inv, out _)))
        {
            var v = parts.Select(p => float.Parse(p, NumberStyles.Float, s_inv)).ToArray();
            var target = new EditTarget<Color>
            {
                Name = $"map: material {name} (dev)", Find = Find, Scene = false,
                Read = o => o.Cast<Material>().GetColor(name), Write = (o, c) => o.Cast<Material>().SetColor(name, c),
            };
            s_props[name] = Edits.Apply(dev, target, new Color(v[0], v[1], v[2], v[3]));
        }
        else if (float.TryParse(text, NumberStyles.Float, s_inv, out float f))
        {
            var target = new EditTarget<float>
            {
                Name = $"map: material {name} (dev)", Find = Find, Scene = false,
                Read = o => o.Cast<Material>().GetFloat(name), Write = (o, x) => o.Cast<Material>().SetFloat(name, x),
            };
            s_props[name] = Edits.Apply(dev, target, f);
        }
        else return CommandResult.Error($"map: usage {Usage}");
        return CommandResult.Ok($"map prop {s_props[name].Describe()}") with { Settle = 5 };
    }

    // Dev probes, written straight (not edits), to see what each value does: the minimap's own outline call, and the
    // configs' background colour. The game's own values come back on the next zone or a restart.
    private static CommandResult Outline(float thickness)
    {
        var map = Game.Minimap;
        if (!Game.Alive(map)) return CommandResult.Error("map outline: no minimap");
        float before = map!.outlineThickness;
        map.OverlayOutlineThickness(thickness);
        return CommandResult.Ok($"map outline: {F(before)} -> {F(map.outlineThickness)}") with { Settle = 5 };
    }

    private static CommandResult Background(string text)
    {
        var configs = Il2Cpp.DMMapGlobalConfigs.instance;
        var parts = text.Split(',');
        if (!Game.Alive(configs) || parts.Length != 4) return CommandResult.Error($"map: usage {Usage}");
        var v = parts.Select(p => float.Parse(p, NumberStyles.Float, s_inv)).ToArray();
        var before = configs!.mapBackgroundColor;
        configs.mapBackgroundColor = new Color(v[0], v[1], v[2], v[3]);
        return CommandResult.Ok($"map background: {before} -> {configs.mapBackgroundColor}") with { Settle = 5 };
    }

    // Dev probe, written straight: the local player's map icon gets a loaded sprite by name, a tint and a size.
    private static CommandResult PlayerIcon(CommandCall call)
    {
        var player = Player.Object;
        var icons = Game.Alive(player) ? player!.GetComponentsInChildren<Il2CppDMM.DMMapIcon>(true) : null;
        var icon = icons?.FirstOrDefault(i => Game.Alive(i));
        if (icon == null) return CommandResult.Error("map playericon: no player map icon");
        string what = call.Lower(2);
        string before = $"rotate {icon.rotate} rotateWithMap {icon.rotateWithMap} offset {F(icon.rotationOffset)} dirIndicator {icon.useDirectionIndicator}; sprite {(Game.Alive(icon.icon) ? icon.icon.name : "-")} tint {icon.tint.r},{icon.tint.g},{icon.tint.b},{icon.tint.a} scale {F(icon.scaleMultiplier)}";
        switch (what)
        {
            case "sprite":
            {
                string name = call.Rest(3);
                var sprite = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Sprite>()).Select(o => o.TryCast<Sprite>()).FirstOrDefault(sp => sp != null && sp.name == name);
                if (sprite == null) return CommandResult.Error($"map playericon: no sprite '{name}'");
                icon.ChangeIcon(sprite);
                break;
            }
            case "tint":
            {
                var v = call.Lower(3).Split(',').Select(p => byte.Parse(p)).ToArray();
                icon.tint = new Color32(v[0], v[1], v[2], v[3]);
                break;
            }
            case "scale":
                icon.scaleMultiplier = float.Parse(call.Lower(3), s_inv);
                break;
            case "repair":
            {
                // The game's arrow comes in two sizes; its icon uses the 64 px one (a 33 px icon), the 200 px one
                // makes a 400 px icon. Put the small one back through the game's own call.
                var small = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Sprite>()).Select(o => o.TryCast<Sprite>())
                    .Where(sp => sp != null && sp.name == "player-minimap-arrow-v6").OrderBy(sp => sp!.rect.width).FirstOrDefault();
                if (small == null) return CommandResult.Error("map playericon repair: no arrow sprite");
                icon.ChangeIcon(small);
                break;
            }
            case "readd":
                // The game's own path: the manager drops the icon's UI and makes it again from the icon's sprite.
                MinimapIconManager.Instance.Remove(icon);
                MinimapIconManager.Instance.Add(icon, icon.icon);
                break;
        }
        string ui = "ui none";
        var manager = MinimapIconManager.Instance;
        if (Game.Alive(manager) && manager!.icons is { } list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i]?.icon == null || list[i].icon.Pointer != icon.Pointer || !Game.Alive(list[i].ui)) continue;
                var u = list[i].ui;
                var img = u.MainImage;
                ui = $"pivot {img.rectTransform.pivot} sprite pivot {(img.sprite != null ? img.sprite.pivot.ToString() : "-")} at {((RectTransform)u.transform).anchoredPosition} rot ui {u.transform.localEulerAngles.z:F0} image {img.rectTransform.localEulerAngles.z:F0} same {u.transform.Pointer == img.rectTransform.Pointer}; ui scale {u.transform.localScale.x:F2} image scale {img.rectTransform.localScale.x:F2} ui rect {((RectTransform)u.transform).rect.width:F0} size {img.rectTransform.sizeDelta} rect {img.rectTransform.rect.width:F0}x{img.rectTransform.rect.height:F0} sprite {(img.sprite != null ? $"{img.sprite.name} {img.sprite.rect.width:F0}x{img.sprite.rect.height:F0}" : "-")} preserve {img.preserveAspect}";
            }
        }
        return CommandResult.Ok($"map playericon {what}: {ui}; {before} -> sprite {(Game.Alive(icon.icon) ? icon.icon.name : "-")} tint {icon.tint.r},{icon.tint.g},{icon.tint.b} scale {F(icon.scaleMultiplier)} ({icons!.Length} icons on the player, '{icon.gameObject.name}')") with { Settle = 5 };
    }

    // Loaded sprites whose name has the text (any of a comma list), with their size: what a map icon could show.
    private static CommandResult Sprites(string filter)
    {
        var parts = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var found = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Sprite>())
            .Select(o => o.TryCast<Sprite>()).Where(sp => sp != null)
            .Where(sp => parts.Length == 0 || parts.Any(p => sp!.name.Contains(p, StringComparison.OrdinalIgnoreCase)))
            .Select(sp => $"{sp!.name} {sp.rect.width:F0}x{sp.rect.height:F0}").Distinct().OrderBy(n => n).ToList();
        return new CommandResult(DevOutput.Reply("map", new List<string> { $"ok map sprites '{filter}': {found.Count}" }.Concat(found).ToList()));
    }

    // map frames [n]: real frame times over n frames (GPU included), so a look setting's draw cost shows.
    private static CommandResult Frames(CommandCall call)
    {
        int n = int.TryParse(call.Lower(2), out int v) ? Math.Clamp(v, 30, 3000) : 300;
        var times = new List<float>(n);
        var gpu = new List<float>(n);
        var cpu = new List<float>(n);
        int skip = 10;
        return new CommandResult(string.Empty)
        {
            Pending = () =>
            {
                FrameTimingManager.CaptureFrameTimings();
                if (skip-- > 0) return null;
                times.Add(Time.unscaledDeltaTime * 1000f);
                var timing = new FrameTiming[1];
                if (FrameTimingManager.GetLatestTimings(1, timing) > 0 && timing[0].gpuFrameTime > 0) gpu.Add((float)timing[0].gpuFrameTime);
                if (FrameTimingManager.GetLatestTimings(1, timing) > 0 && timing[0].cpuFrameTime > 0) cpu.Add((float)timing[0].cpuFrameTime);
                if (times.Count < n) return null;
                times.Sort();
                float mean = times.Average(), p95 = times[(int)(times.Count * 0.95f)];
                return $"ok map frames {n}: mean {mean:F2} ms ({1000f / mean:F0} fps), median {times[times.Count / 2]:F2} ms, p95 {p95:F2} ms; "
                    + (gpu.Count > 0 ? $"gpu mean {gpu.Average():F2} ms ({gpu.Count} frames)" : "gpu timing unavailable") + (cpu.Count > 0 ? $", cpu main mean {cpu.Average():F2} ms" : string.Empty);
            },
        };
    }

    // What draws the map: the minimap object's components, its graphic's type, texture, material and rect.
    private static string GraphicFacts()
    {
        var map = Game.Minimap;
        if (!Game.Alive(map)) return "no minimap";
        var go = map!.gameObject;
        string parts = Components(go.transform);
        var graphic = go.GetComponent<UnityEngine.UI.Graphic>();
        if (graphic == null) return $"{go.name}: [{parts}], no graphic";
        var rt = graphic.rectTransform;
        string tex = graphic.mainTexture != null ? $"{graphic.mainTexture.name} {graphic.mainTexture.width}x{graphic.mainTexture.height}" : "none";
        return $"{go.name}: [{parts}], graphic {graphic.GetIl2CppType().Name}, texture {tex}, material '{graphic.material?.name}' ptr {graphic.material?.Pointer:X} "
            + $"(Minimap.Material ptr {map.Material?.Pointer:X}), rect {rt.rect.width:F0}x{rt.rect.height:F0}, anchored {rt.anchoredPosition}, parent {Path(rt.parent)}, siblings {rt.parent.childCount}, raycast {graphic.raycastTarget}";
    }

    // map maxdist <n>|off: the dev max distance edit at any value, to see what the distance range does to the lines.
    private static CommandResult MaxDist(CommandCall call)
    {
        if (FeatureHost.Find("dev") is not { State: FeatureState.Running } dev) return CommandResult.Error("map maxdist: the dev bridge feature isn't running");
        s_maxDistance?.Remove();
        s_maxDistance = null;
        if (call.Lower(2) == "off") return CommandResult.Ok("map maxdist off") with { Settle = 5 };
        if (!int.TryParse(call.Lower(2), out int n) || n < 1) return CommandResult.Error($"map: usage {Usage}");
        s_maxDistance = Edits.Apply(dev, MaxDistance, n);
        return CommandResult.Ok($"map maxdist {s_maxDistance.Describe()}") with { Settle = 5 };
    }

    // The game's own overlay pan, called each frame for a number of frames, as a held arrow key would.
    private static CommandResult Pan(CommandCall call)
    {
        if (!float.TryParse(call.Lower(2), NumberStyles.Float, s_inv, out float x) || !float.TryParse(call.Lower(3), NumberStyles.Float, s_inv, out float y))
            return CommandResult.Error($"map: usage {Usage}");
        int frames = int.TryParse(call.Lower(4), out int n) ? n : 60;
        var ui = Il2Cpp.UIBase.instance;
        if (!Game.Alive(ui)) return CommandResult.Error("map pan: no UIBase");
        var map = Game.Minimap;
        string before = Game.Alive(map) ? $"{map!.mapOffset.x:F1},{map.mapOffset.y:F1}" : "?";
        int done = 0;
        return new CommandResult(string.Empty)
        {
            Pending = () =>
            {
                if (done++ < frames)
                {
                    ui!.MoveOverlayMap(new Vector2(x, y));
                    return null;
                }
                return $"ok map pan {F(x)} {F(y)} x{frames}: map offset {before} -> {(Game.Alive(map) ? $"{map!.mapOffset.x:F1},{map.mapOffset.y:F1}" : "?")}";
            },
        };
    }

    // ---- map roots ----

    // Each click listener's spawned root (its highest "(Clone)" ancestor, else its scene root), and the map icons in
    // that root's subtree: whether a listener leads to its object's icon. Timed.
    private static CommandResult Roots()
    {
        var all = Il2Cpp.WorldObjectClickListener.allListener;
        if (all == null) return CommandResult.Error("map roots: no listener list");
        var watch = Stopwatch.StartNew();
        var seen = new HashSet<IntPtr>();
        var lines = new List<string>();
        int withIcons = 0;
        for (int i = 0; i < all.Count; i++)
        {
            var listener = all[i];
            if (!Game.Alive(listener)) continue;
            Transform root = listener.transform;
            for (var t = listener.transform; Game.Alive(t); t = t!.parent)
            {
                if (t!.gameObject.name.Contains("(Clone)")) root = t;
            }
            if (!seen.Add(root.Pointer)) continue;
            var icons = root.GetComponentsInChildren(Il2CppType.Of<Il2CppDMM.BaseDMMapIcon>(), true);
            var world = root.GetComponentsInChildren(Il2CppType.Of<Il2Cpp.DMMapWorldIcon>(), true);
            int off = 0;
            foreach (var c in icons) if (c != null && !c.Cast<Component>().gameObject.activeSelf) off++;
            foreach (var c in world) if (c != null && !c.Cast<Component>().gameObject.activeSelf) off++;
            if (icons.Length + world.Length > 0) withIcons++;
            lines.Add($"{root.gameObject.name}  listener {listener.gameObject.name}  icons {icons.Length} world {world.Length} off {off}");
        }
        watch.Stop();
        lines.Insert(0, $"ok map roots: {seen.Count} roots from {all.Count} listeners, {withIcons} with icons, {watch.Elapsed.TotalMilliseconds.ToString("F2", s_inv)} ms");
        return new CommandResult(DevOutput.Reply("map", lines));
    }

    // ---- map volumes ----

    private static CommandResult Volumes()
    {
        var manager = UnityEngine.Object.FindObjectOfType(Il2CppType.Of<Il2Cpp.MinimapVolumesManager>())?.TryCast<Il2Cpp.MinimapVolumesManager>();
        if (!Game.Alive(manager)) return CommandResult.Error("map volumes: no volumes manager");
        var volumes = manager!.volumes;
        var statuses = manager.statuses;
        int entered = 0;
        // FNV-1a over the entered flags, so two reads can be compared at a glance.
        uint hash = 2166136261;
        if (statuses != null)
        {
            for (int i = 0; i < statuses.Length; i++)
            {
                if (statuses[i]) entered++;
                hash = (hash ^ (statuses[i] ? 1u : 0u)) * 16777619;
            }
        }
        return CommandResult.Ok($"map volumes: {volumes?.Length ?? 0} volumes, {statuses?.Length ?? 0} statuses, {entered} entered, hash {hash:X8}, "
            + $"initialized {manager.initialized}, version {manager.version}, scene {GameState.ActiveSceneName}");
    }

    // ---- map step ----

    private static CommandResult Step(CommandCall call)
    {
        string step = call.Lower(2);
        if (FeatureHost.Find("dev") is not { State: FeatureState.Running } dev) return CommandResult.Error("map step: the dev bridge feature isn't running");
        if (step == "zoom")
        {
            bool zoomOn = float.TryParse(call.Lower(3), NumberStyles.Float, s_inv, out float scale) && scale >= 0f;
            if (!zoomOn && call.Lower(3) != "off") return CommandResult.Error($"map: usage {Usage}");
            if (!dev.Has(ZoomGroup)) return CommandResult.Error($"map step zoom: '{ZoomGroup}' is missing from the game");
            float corner = zoomOn && float.TryParse(call.Lower(4), NumberStyles.Float, s_inv, out float c) && c > 0f ? c : 0f;
            s_zoomScale = zoomOn ? scale : 0f;
            s_cornerScale = corner;
            zoomOn = s_zoomScale > 0f || s_cornerScale > 0f;
            FeatureHost.SetPatchGroup(dev, ZoomGroup, zoomOn);
            s_iconRange?.Remove();
            s_iconRange = null;
            float gameScale = Il2CppLE.Settings.SettingsManager.Gameplay?.OverlayMapZoom?.GetValue() is float saved && saved > 0f ? saved : 120f;
            if (s_zoomScale > 0f && Game.Alive(Il2Cpp.DMMapGlobalConfigs.instance))
                s_iconRange = Edits.Apply(dev, IconRange, Il2Cpp.DMMapGlobalConfigs.instance.iconDistanceThreshold * scale / gameScale);
            return CommandResult.Ok($"map step zoom {(zoomOn ? $"overlay {(s_zoomScale > 0f ? F(s_zoomScale) : "game's")}, corner {(s_cornerScale > 0f ? F(s_cornerScale) : "game's")}" : "off")}, patched {dev.Record.DemandGroupsOn.Contains(ZoomGroup)}, icon range {(s_iconRange is { } r ? r.Describe() : "game's")}") with { Settle = 10 };
        }
        if (step is not ("radius" or "maxdist" or "patch") || call.Lower(3) is not ("on" or "off")) return CommandResult.Error($"map: usage {Usage}");
        bool on = call.Lower(3) == "on";
        var map = Game.Minimap;
        var info = Game.Alive(map) ? map!.minimapInformation : null;
        if (on && !Game.Alive(info)) return CommandResult.Error("map step: no zone map yet");
        switch (step)
        {
            case "radius":
                s_radius?.Remove();
                s_radius = on ? Edits.Apply(dev, ClientCommands.RevealRadius, RadiusFor(info!)) : null;
                return CommandResult.Ok($"map step radius {(on ? s_radius!.Describe() : "off")}") with { Settle = 2 };
            case "maxdist":
            {
                s_maxDistance?.Remove();
                var sdf = on ? info!.LiveSdf : null;
                if (on && !Game.Alive(sdf)) return CommandResult.Error("map step maxdist: no LiveSdf yet");
                s_maxDistance = on ? Edits.Apply(dev, MaxDistance, Math.Max(sdf!.width, sdf.height)) : null;
                return CommandResult.Ok($"map step maxdist {(on ? s_maxDistance!.Describe() : "off")}") with { Settle = 2 };
            }
            default:
                if (!dev.Has(StampGroup)) return CommandResult.Error($"map step patch: '{StampGroup}' is missing from the game");
                s_patchRadius = on ? RadiusFor(info!) : 0f;
                FeatureHost.SetPatchGroup(dev, StampGroup, on);
                return CommandResult.Ok($"map step patch {(on ? $"on, radius {F(s_patchRadius)}" : "off")}, patched {dev.Record.DemandGroupsOn.Contains(StampGroup)}") with { Settle = 2 };
        }
    }

    // ---- map show ----

    // Dev only and slow (a scene-wide search each pass): proves an icon hidden on itself shows once switched on.
    private static CommandResult Show(CommandCall call)
    {
        string state = call.Lower(call.Args.Length);
        string name = string.Join(' ', call.Args.Skip(1).Take(call.Args.Length - 2));
        if (state is not ("on" or "off") || (state == "on" && name.Length == 0)) return CommandResult.Error($"map: usage {Usage}");
        if (FeatureHost.Find("dev") is not { State: FeatureState.Running } dev) return CommandResult.Error("map show: the dev bridge feature isn't running");
        s_show?.Remove();
        s_show = null;
        s_showName = state == "on" ? name : string.Empty;
        if (state == "on") s_show = Edits.Apply(dev, IconShown, true);
        return CommandResult.Ok($"map show {(state == "on" ? $"'{name}': {s_show!.Describe()}" : "off")}") with { Settle = 10 };
    }

    // ---- map overlay, map zoom ----

    // The game's own open and close of the overlay map, as its Tab key does.
    private static CommandResult Overlay(CommandCall call)
    {
        var ui = Il2Cpp.UIBase.instance;
        if (!Game.Alive(ui)) return CommandResult.Error("map overlay: no UIBase");
        switch (call.Lower(2))
        {
            case "open": ui!.OpenOverlayMap(); break;
            case "close": ui!.CloseOverlayMapOrRecenter(false); break;
            default: return CommandResult.Error($"map: usage {Usage}");
        }
        return CommandResult.Ok($"map overlay {call.Lower(2)}: overlayMapOpen {ui.overlayMapOpen}, {OverlayZoom()}") with { Settle = 10 };
    }

    // The map configs' zoom field only, not their setter, so nothing reaches the game's saved settings.
    private static CommandResult Zoom(CommandCall call)
    {
        // "param": the map's scale in its shader parameters, written straight to the minimap.
        if (call.Lower(2) == "param" && float.TryParse(call.Lower(3), NumberStyles.Float, s_inv, out float scale))
        {
            var minimap = Game.Minimap;
            if (!Game.Alive(minimap)) return CommandResult.Error("map zoom: no minimap");
            var parameters = minimap!.mapParameters;
            parameters.z = scale;
            minimap.mapParameters = parameters;
            return CommandResult.Ok($"map zoom param {F(scale)}: {OverlayZoom()}") with { Settle = 10 };
        }
        // "set": the configs' own setter; it saves the game's setting a moment later.
        bool setter = call.Lower(2) == "set";
        if (!float.TryParse(call.Lower(setter ? 3 : 2), NumberStyles.Float, s_inv, out float value)) return CommandResult.Error($"map: usage {Usage}");
        var configs = Il2Cpp.DMMapGlobalConfigs.instance;
        if (!Game.Alive(configs)) return CommandResult.Error("map zoom: no map configs");
        if (setter) configs!.SetOverlayZoom(value);
        else configs!.ZoomValue = value;
        return CommandResult.Ok($"map zoom {F(value)}: {OverlayZoom()}") with { Settle = 10 };
    }

    private static string OverlayZoom()
    {
        var configs = Il2Cpp.DMMapGlobalConfigs.instance;
        string parameters = Game.Alive(Game.Minimap) ? $", map parameters {Game.Minimap!.mapParameters.z.ToString("F2", s_inv)}" : string.Empty;
        string saved = Il2CppLE.Settings.SettingsManager.Gameplay?.OverlayMapZoom is { } setting ? F(setting.GetValue()) : "?";
        return Game.Alive(configs) ? $"configs zoom {F(configs!.ZoomValue)} (overlay {F(configs.minOverlayZoom)}-{F(configs.maxOverlayZoom)}){parameters}, saved setting {saved}" : "no map configs";
    }

    // The game's fog stamps take their radius as an argument; while the dev step is on, it is replaced.
    [HarmonyPatch(typeof(Minimap), nameof(Minimap.UpdateFoW))]
    internal static class StampPatch
    {
        private static void Prefix(ref float radius)
        {
            if (s_patchRadius > 0f) radius = s_patchRadius;
        }
    }

    [HarmonyPatch(typeof(Minimap), nameof(Minimap.UpdateFoW_Singular))]
    internal static class StampSingularPatch
    {
        private static void Prefix(ref float radius)
        {
            if (s_patchRadius > 0f) radius = s_patchRadius;
        }
    }

    // The game sets the map's scale each update, from its saved overlay zoom while the overlay is open and from the
    // corner minimap's zoom while it is closed; while the dev step is on, ours replaces the one for the map shown, in
    // the field and in the material the map draws with.
    [HarmonyPatch(typeof(Minimap), nameof(Minimap.Update))]
    internal static class ZoomPatch
    {
        private static void Postfix(Minimap __instance)
        {
            var ui = Il2Cpp.UIBase.instance;
            float scale = ui != null && ui.overlayMapOpen ? s_zoomScale : s_cornerScale;
            if (scale <= 0f) return;
            var parameters = __instance.mapParameters;
            parameters.z = scale;
            __instance.mapParameters = parameters;
            var material = __instance.Material;
            if (material != null) material.SetVector(Minimap.shaderMapParameters, new Vector4(parameters.x, parameters.y, parameters.z, parameters.w));
        }
    }

    private static string F(float value) => value.ToString("F2", s_inv);

    private static string V(Vector3 v) => $"{F(v.x)},{F(v.y)},{F(v.z)}";
}
