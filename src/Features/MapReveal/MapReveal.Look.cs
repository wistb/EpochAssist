using EpochAssist.Core;
using Il2CppLE.UI.Minimap;
using UnityEngine;
using S = EpochAssist.Features.MapRevealSettings;

namespace EpochAssist.Features;

// The map's look: the ground fill, the outline, the inner shadow and the ragged edge glow are properties of the map's
// own material, each a value edit, so switching off gives the game's back; a thicker outline is drawn by copies
// (MapReveal.Outline).
internal static partial class MapReveal
{
    public const string LookGroup = "map: look";

    private const string FillProperty = "_FillColor", FillTextureProperty = "_NoiseStrength", FillTextureSizeProperty = "_NoiseSize";
    private const string OutlineProperty = "_OutlineColor", ShadowProperty = "_FillShadowColor", ShadowWidthProperty = "_FillShadowThickness";
    private const string WobbleProperty = "_OuterNoiseStrength", WobbleSizeProperty = "_OuterNoiseSize", SharpnessProperty = "_OuterNoiseSharpness";
    private const string GlowProperty = "_OuterNoiseColor";

    // A style: every look value at once. Colours are #RRGGBB, empty for the game's own.
    private sealed record LookStyle(string Fill, float FillStrength, float FillTexture, float FillTextureSize, string Outline, float OutlineStrength,
        float OutlineThickness, float OutlineSoftness, string Shadow, float ShadowStrength, float ShadowWidth, float Wobble, float WobbleSize, float Sharpness, string Glow, float GlowStrength);

    // Slate: no ground fill, a thick soft pale outline and a soft inner shadow, nothing ragged (the defaults, the
    // maintainer's tuned look). Clear: the game's colours,
    // made solid and calm. Game: the game's own values, from the 1.5.3 map material.
    private static readonly Dictionary<string, LookStyle> s_styles = new()
    {
        [S.StyleSlate] = new("#2A3048", 0f, 0f, 2f, "#BCC6F2", 1f, 2f, 0.75f, "#151A2B", 0.4f, 1f, 0f, 0.25f, 0.05f, "#8E9AD0", 0.05f),
        [S.StyleClear] = new(string.Empty, 0.35f, 0.5f, 2f, string.Empty, 1f, 1f, 0.5f, string.Empty, 0.8f, 0.6f, 3f, 1.5f, 0.08f, string.Empty, 0.3f),
        [S.StyleGame] = new(string.Empty, 0.01f, 0.5f, 2f, string.Empty, 0.5f, 0f, 0.5f, string.Empty, 0.8f, 0.6f, 16f, 1.5f, 0.08f, string.Empty, 0.3f),
    };

    /// <summary>The Game defaults button asked for its confirmation.</summary>
    public static bool ConfirmingGameDefaults;

    private sealed class LookState
    {
        // The style seen at the last tick, so a new pick is told from a knob moved away from it.
        public string? Style;
        public readonly Dictionary<string, Edit<Color>> Colours = new();
        public readonly Dictionary<string, Edit<float>> Floats = new();
        public Edit<int>? OutlineCopies;
        public Edit<PlayerLook>? PlayerIcon;
        // The game's colours, from the map's base material, which nothing changes: an empty colour setting means these.
        public readonly Dictionary<string, Color> Originals = new();
    }

    private static readonly Dictionary<string, EditTarget<Color>> s_colourTargets = new();
    private static readonly Dictionary<string, EditTarget<float>> s_floatTargets = new();

    private static IEnumerable<UnityEngine.Object> MapMaterial() => One(Game.Alive(Game.Minimap) ? Game.Minimap!.Material : null);

    private static EditTarget<Color> ColourTarget(string property)
    {
        if (s_colourTargets.TryGetValue(property, out var target)) return target;
        return s_colourTargets[property] = new EditTarget<Color>
        {
            Name = $"map: look {property}",
            Find = MapMaterial,
            Read = o => o.Cast<Material>().GetColor(property),
            Write = (o, v) => o.Cast<Material>().SetColor(property, v),
            Show = c => ColorSetting.Format(new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b))) + $" {c.a:P0}" + (Mathf.Max(c.r, Mathf.Max(c.g, c.b)) > 1f ? " bright" : string.Empty),
            // A colour read back from the material differs by a hair from the one written; exact equality would take
            // every write for a drift and stop the edit.
            Same = (a, b) => Mathf.Abs(a.r - b.r) < 0.002f && Mathf.Abs(a.g - b.g) < 0.002f && Mathf.Abs(a.b - b.b) < 0.002f && Mathf.Abs(a.a - b.a) < 0.002f,
            Scene = false,
        };
    }

    private static EditTarget<float> FloatTarget(string property)
    {
        if (s_floatTargets.TryGetValue(property, out var target)) return target;
        return s_floatTargets[property] = new EditTarget<float>
        {
            Name = $"map: look {property}",
            Find = MapMaterial,
            Read = o => o.Cast<Material>().GetFloat(property),
            Write = (o, v) => o.Cast<Material>().SetFloat(property, v),
            Scene = false,
        };
    }

    private static void TickLook(Run run, bool on)
    {
        var look = run.Look;
        HoldStyle(look);
        on = on && S.LookEnabled.Value && run.Feature.Has(LookGroup);
        var material = Game.Alive(Game.Minimap) ? Game.Minimap!.Material : null;
        if (!on || material == null)
        {
            RemoveLook(look);
            return;
        }
        ReadOriginals(look);
        HoldColour(run, FillProperty, S.FillColour, S.FillStrength.Value);
        HoldColour(run, OutlineProperty, S.OutlineColour, S.OutlineStrength.Value);
        HoldColour(run, ShadowProperty, S.ShadowColour, S.ShadowStrength.Value);
        HoldColour(run, GlowProperty, S.GlowColour, S.GlowStrength.Value);
        HoldFloat(run, FillTextureProperty, S.FillTexture.Value);
        HoldFloat(run, FillTextureSizeProperty, S.FillTextureSize.Value);
        HoldFloat(run, ShadowWidthProperty, S.ShadowWidth.Value);
        HoldFloat(run, WobbleProperty, S.EdgeWobble.Value);
        HoldFloat(run, WobbleSizeProperty, S.EdgeWobbleSize.Value);
        HoldFloat(run, SharpnessProperty, S.EdgeSharpness.Value);
        HoldOutlineCopies(run, on);
    }

    // A style picked writes its values into every knob; a knob moved away from the picked style makes it Custom.
    private static void HoldStyle(LookState look)
    {
        string style = S.Style.Value;
        // On start the stored style is only noted: the knobs keep their stored values (Custom if they differ).
        look.Style ??= style;
        if (style != look.Style)
        {
            look.Style = style;
            if (s_styles.TryGetValue(style, out var picked)) WriteStyle(picked);
            return;
        }
        if (s_styles.TryGetValue(style, out var current) && !Matches(current))
        {
            S.Style.Value = S.StyleCustom;
            look.Style = S.StyleCustom;
        }
    }

    private static void WriteStyle(LookStyle v)
    {
        S.FillColour.Value = v.Fill;
        S.FillStrength.Value = v.FillStrength;
        S.FillTexture.Value = v.FillTexture;
        S.FillTextureSize.Value = v.FillTextureSize;
        S.OutlineColour.Value = v.Outline;
        S.OutlineStrength.Value = v.OutlineStrength;
        S.OutlineThickness.Value = v.OutlineThickness;
        S.OutlineSoftness.Value = v.OutlineSoftness;
        S.ShadowColour.Value = v.Shadow;
        S.ShadowStrength.Value = v.ShadowStrength;
        S.ShadowWidth.Value = v.ShadowWidth;
        S.EdgeWobble.Value = v.Wobble;
        S.EdgeWobbleSize.Value = v.WobbleSize;
        S.EdgeSharpness.Value = v.Sharpness;
        S.GlowColour.Value = v.Glow;
        S.GlowStrength.Value = v.GlowStrength;
    }

    private static bool Matches(LookStyle v)
    {
        static bool Near(float a, float b) => Mathf.Abs(a - b) < 0.005f;
        return S.FillColour.Value == v.Fill && Near(S.FillStrength.Value, v.FillStrength) && Near(S.FillTexture.Value, v.FillTexture)
            && Near(S.FillTextureSize.Value, v.FillTextureSize) && S.OutlineColour.Value == v.Outline && Near(S.OutlineStrength.Value, v.OutlineStrength)
            && Near(S.OutlineThickness.Value, v.OutlineThickness) && Near(S.OutlineSoftness.Value, v.OutlineSoftness) && S.ShadowColour.Value == v.Shadow && Near(S.ShadowStrength.Value, v.ShadowStrength)
            && Near(S.ShadowWidth.Value, v.ShadowWidth) && Near(S.EdgeWobble.Value, v.Wobble) && Near(S.EdgeWobbleSize.Value, v.WobbleSize)
            && Near(S.EdgeSharpness.Value, v.Sharpness) && S.GlowColour.Value == v.Glow && Near(S.GlowStrength.Value, v.GlowStrength);
    }

    /// <summary>Every look value to the game's own (the Game style), after the player confirmed.</summary>
    public static void SetGameDefaults()
    {
        ConfirmingGameDefaults = false;
        S.Style.Value = S.StyleGame;
        WriteStyle(s_styles[S.StyleGame]);
        if (s_run is { } run) run.Look.Style = S.StyleGame;
    }

    // The map draws with its own copy of a base material; the base keeps the game's values whatever happens to the copy.
    private static void ReadOriginals(LookState look)
    {
        if (look.Originals.Count > 0) return;
        var map = Game.Minimap;
        var source = Game.Alive(map) ? map!.material : null;
        if (source == null) return;
        foreach (string property in new[] { FillProperty, OutlineProperty, ShadowProperty, GlowProperty }) look.Originals[property] = source.GetColor(property);
    }

    private static void HoldColour(Run run, string property, ColorSetting setting, float strength)
    {
        var look = run.Look;
        if (!setting.TryGetColor(out var colour)) colour = look.Originals.GetValueOrDefault(property, Color.white);
        colour.a = strength;
        if (!look.Colours.TryGetValue(property, out var edit) || edit.Removed) look.Colours[property] = Edits.Apply(run.Feature, ColourTarget(property), colour);
        else if (edit.Value != colour) edit.Set(colour);
    }

    private static void HoldFloat(Run run, string property, float value)
    {
        var look = run.Look;
        if (!look.Floats.TryGetValue(property, out var edit) || edit.Removed) look.Floats[property] = Edits.Apply(run.Feature, FloatTarget(property), value);
        else if (Mathf.Abs(edit.Value - value) > 1e-4f) edit.Set(value);
    }

    private static void RemoveLook(LookState look)
    {
        foreach (var edit in look.Colours.Values) edit.Remove();
        foreach (var edit in look.Floats.Values) edit.Remove();
        look.Colours.Clear();
        look.Floats.Clear();
        look.OutlineCopies?.Remove();
        look.OutlineCopies = null;
    }

    private static IEnumerable<string> DescribeLook(Run run)
    {
        var look = run.Look;
        yield return $"look: style {S.Style.Value}, fill {(S.FillColour.Value.Length == 0 ? "game" : S.FillColour.Value)} {S.FillStrength.Value:F2}, outline {(S.OutlineColour.Value.Length == 0 ? "game" : S.OutlineColour.Value)} {S.OutlineStrength.Value:F2}, "
            + $"thickness {S.OutlineThickness.Value:F0} softness {S.OutlineSoftness.Value:F2}, wobble {S.EdgeWobble.Value:F1}; {look.Colours.Count} colours, {look.Floats.Count} values, outline copies {s_copies.Count} ({(look.OutlineCopies is { Removed: false } c ? c.Describe() : "none")})";
    }
}
