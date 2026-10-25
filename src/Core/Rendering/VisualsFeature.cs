using Il2Cpp;
using UnityEngine;
using Feature = EpochAssist.Core.Feature;
using Outline = Il2CppLE.Effects.Outline;

namespace EpochAssist.Core;

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
