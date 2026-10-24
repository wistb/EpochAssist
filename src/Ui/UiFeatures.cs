using EpochAssist.Core;

namespace EpochAssist.Ui;

internal sealed class MenuFeature : Feature
{
    public override string Id => "menu";
    public override string Name => "Menu";
    public override GuiLayer Layer => GuiLayer.Menu;

    public override void Update() => Menu.OnUpdate();
    public override void OnGUI() => Menu.OnGUI();

    public override void Shutdown()
    {
        Menu.Close();
        InputGate.Menu = false;
        GameState.UiTyping = false;
    }
}

internal sealed class HudFeature : Feature
{
    public override string Id => "hud";
    public override string Name => "Mini HUD";
    public override GuiLayer Layer => GuiLayer.Hud;

    public override void Start() => InputGate.HudHitTest = Hud.HitTest;
    public override void Update() => Hud.OnUpdate();
    public override void OnGUI() => Hud.OnGUI();

    public override void Shutdown()
    {
        InputGate.HudHitTest = null;
        InputGate.Hud = false;
    }
}
