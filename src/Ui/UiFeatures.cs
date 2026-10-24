using EpochAssist.Core;

namespace EpochAssist.Ui;

internal sealed class MenuFeature : Feature
{
    public override string Id => "menu";
    public override string Name => "Menu";
    public override GuiLayer Layer => GuiLayer.Menu;

    public override void Start()
    {
        Actions.Register(Id, "menu.toggle", "Menu", Menu.Toggle, () => Menu.Visible);
        Actions.Register(Id, "menu.close", "Close the menu", Menu.Close);
        Actions.Register(Id, "menu.page.autocast", "Open Auto-Cast", () => Menu.Open(Page.AutoCast));
        Actions.Register(Id, "menu.page.potion", "Open Potion", () => Menu.Open(Page.Potion));
        Actions.Register(Id, "menu.page.settings", "Open Settings", () => Menu.Open(Page.Settings));
    }

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

    public override void Start()
    {
        InputGate.HudHitTest = Hud.HitTest;
        Actions.Register(Id, "hud.togglePanel", "Quick toggles panel", Hud.TogglePanel, () => Hud.PanelOpen);
    }
    public override void Update() => Hud.OnUpdate();
    public override void OnGUI() => Hud.OnGUI();

    public override void Shutdown()
    {
        InputGate.HudHitTest = null;
        InputGate.Hud = false;
    }
}
