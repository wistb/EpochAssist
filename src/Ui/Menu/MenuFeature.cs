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
        // One per page, from the registered features; a page that isn't running still opens, to show why.
        foreach (var feature in Menu.PageFeatures)
        {
            string page = feature.Id;
            Actions.Register(Id, $"menu.page.{page}", $"Open {feature.Page!.Title}", () => Menu.Open(page));
        }
        Actions.Register(Id, $"menu.page.{Menu.SettingsPage}", "Open Settings", Menu.OpenSettings);
        Actions.Register(Id, "menu.profiles", "Open Profiles", Menu.OpenProfiles);
        // The theme choice lists presets and player files, which the core setting can't know about.
        Themes.Rescan();
        Config.Theme.Options = () => Themes.Names;
        Config.Theme.Notes = () => Themes.NotesFor(Config.Theme.Value);
        Diagnostics.ThemeProblems = () => Themes.Problems;
    }

    public override void Update() => Menu.OnUpdate();
    public override void OnGUI() => Menu.OnGUI();

    public override void Shutdown()
    {
        Menu.Close();
        Config.Theme.Options = null;
        Config.Theme.Notes = null;
        Diagnostics.ThemeProblems = null;
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
