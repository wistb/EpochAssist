using EpochAssist.Core;

namespace EpochAssist.Ui;

internal sealed class MenuFeature : Feature
{
    public override string Id => "menu";
    public override string Name => "Menu";
    // Reads settings each frame; menu.toggle runs in the hotkey layer's update, before the menu sets the input gate.
    public override string[] RunsAfter => new[] { "profiles", "hotkeys" };
    public override GuiLayer Layer => GuiLayer.Menu;

    public override void Start()
    {
        RowKinds.RegisterBuiltIn();
        ProfilesPage.Register();
        InputGate.MenuHitTest = point => Ui.Menu.ScreenRect.Contains(point);
        DiagnosticsPage.Register();
        SessionsPage.Register();
        RolePreview.Register();
        ConsolePage.Register();
        MenuTree.Invalidate();

        Actions.Register(Id, "menu.toggle", "Menu", Ui.Menu.Toggle, () => Ui.Menu.Visible);
        Actions.Register(Id, "menu.close", "Close the menu", Ui.Menu.Close);
        Actions.Register(Id, "menu.escape", "Close a list, cancel, clear the search, or close the menu", Ui.Menu.Escape);
        // One per feature with a menu entry; a feature that isn't running still opens, to show why.
        foreach (var feature in FeatureHost.All.Where(f => f.Menu != null))
        {
            string id = feature.Id;
            Actions.Register(Id, $"menu.page.{id}", $"Open {feature.Menu!.Title ?? feature.Name}", () => Ui.Menu.Open(id));
        }
        Actions.Register(Id, "menu.page.settings", "Open Settings", Ui.Menu.OpenSettings);
        Actions.Register(Id, "menu.profiles", "Open Profiles", Ui.Menu.OpenProfiles);
        // The theme choice lists presets and player files, which the core setting can't know about.
        Themes.Rescan();
        LookSettings.Theme.Options = () => Themes.Names;
        LookSettings.Theme.Notes = () => Themes.NotesFor(LookSettings.Theme.Value);
        Diagnostics.ThemeProblems = () => Themes.Problems;
    }

    public override void Update() => Ui.Menu.OnUpdate();
    public override void OnGUI() => Ui.Menu.OnGUI();

    public override void Shutdown()
    {
        InputGate.MenuHitTest = null;
        Ui.Menu.Close();
        Popups.Reset();
        MenuTree.Invalidate();
        MenuSearch.Reset();
        MenuRoutes.Reset();
        PageView.Reset();
        TreeView.Reset();
        ProfilesPage.Reset();
        SessionsPage.Reset();
        TextCache.Clear();
        MenuBlocks.Clear();
        SettingsContent.Reset();
        LookSettings.Theme.Options = null;
        LookSettings.Theme.Notes = null;
        Diagnostics.ThemeProblems = null;
        InputGate.Menu = false;
        GameState.UiTyping = false;
    }
}
