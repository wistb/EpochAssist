using EpochAssist.Core;

namespace EpochAssist.Features;

internal sealed class AutoCastFeature : Feature
{
    public override string Id => "autocast";
    public override string Name => "Auto-Cast";
    public override GuiLayer Layer => GuiLayer.World;
    public override Type[] PatchClasses => NestedPatches(typeof(AutoCast));

    public override void RegisterSettings() => AutoCastSettings.Register();
    public override void Update() => AutoCast.OnUpdate();
    public override void OnGUI() => AutoCast.OnGUI();
    public override void OnSceneChanged() => AutoCast.OnSceneChanged();
    public override void Shutdown() => AutoCast.Shutdown();
}
