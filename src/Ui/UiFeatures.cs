using EpochAssist.Core;

namespace EpochAssist.Ui;

internal sealed class MenuFeature : Feature
{
    public override string Id => "menu";
    public override string Name => "Menu";
    public override GuiLayer Layer => GuiLayer.Menu;
    public override Type[] PatchClasses => NestedPatches(typeof(InputGate.Patches));
    public override Requirement[] Requirements => new[]
    {
        Need<Il2Cpp.EpochInputManager>(nameof(Il2Cpp.EpochInputManager.forceDisableInput)),
        Need<Il2Cpp.CameraManager>(nameof(Il2Cpp.CameraManager.targetZoom)),
        Need<Il2Cpp.CameraManager>(nameof(Il2Cpp.CameraManager.currentZoom)),
    };

    public override void Update() => Menu.OnUpdate();
    public override void OnGUI() => Menu.OnGUI();

    public override void Shutdown()
    {
        Menu.Close();
        InputGate.Menu = false;
    }
}

internal sealed class HudFeature : Feature
{
    public override string Id => "hud";
    public override string Name => "Mini HUD";
    public override GuiLayer Layer => GuiLayer.Hud;

    public override void Update() => Hud.OnUpdate();
    public override void OnGUI() => Hud.OnGUI();
    public override void Shutdown() => InputGate.Hud = false;
}
