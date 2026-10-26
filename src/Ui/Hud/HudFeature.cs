using EpochAssist.Core;

namespace EpochAssist.Ui;

internal sealed class HudFeature : Feature
{
    public override string Id => "hud";
    public override string Name => "Mini HUD";
    // Reads settings each frame, after the character's values are in place.
    public override string[] RunsAfter => new[] { "profiles" };
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
