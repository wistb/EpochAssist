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
        ModulesPage.Register();
        if (Modules.Loaded("sessions")) RegisterSessionBlocks();
        RolePreview.Register();
        AboutPage.Register();
        ConsolePage.Register();
        MenuTree.Invalidate();

        Actions.Register(Id, "menu.toggle", "Menu", Ui.Menu.Toggle, () => Ui.Menu.Visible);
        Actions.Register(Id, "menu.close", "Close the menu", Ui.Menu.Close);
        Actions.Register(Id, "menu.collapse", "Fold the menu to its bars, or unfold it", Ui.Menu.ToggleCollapsed, () => Ui.Menu.Collapsed);
        Actions.Register(Id, "menu.escape", "Close a list, cancel, clear the search, or close the menu", Ui.Menu.Escape);
        RegisterPageActions(FeatureHost.All);
        Modules.LoadedLate += OnModulesLoaded;
        Actions.Register(Id, "menu.page.settings", "Open Settings", Ui.Menu.OpenSettings);
        Actions.Register(Id, "menu.profiles", "Open Profiles", Ui.Menu.OpenProfiles);
        // The theme choice lists presets and player files, which the core setting can't know about.
        Themes.Rescan();
        LookSettings.Theme.Options = () => Themes.Names;
        LookSettings.Theme.Notes = () => Themes.NotesFor(LookSettings.Theme.Value);
        Diagnostics.ThemeProblems = () => Themes.Problems;
    }

    // One per feature with a menu entry; a feature that isn't running still opens, to show why.
    private void RegisterPageActions(IEnumerable<Feature> features)
    {
        foreach (var feature in features.Where(f => f.Menu != null))
        {
            string id = feature.Id;
            Actions.Register(Id, $"menu.page.{id}", $"Open {feature.Menu!.Title ?? feature.Name}", () => Ui.Menu.Open(id));
        }
        // A feature with a tab of its own and its settings in the Config tree: a way to its settings from the tab.
        foreach (var feature in features.Where(f => f.ConfigMenu != null))
        {
            var owner = feature;
            Actions.Register(Id, $"menu.config.{owner.Id}", $"Open {owner.ConfigMenu!.Title ?? owner.Name} settings", () =>
            {
                if (MenuTree.ConfigNodeOf(owner) is { } node) Ui.Menu.Open(MenuRoutes.First(node));
            });
        }
    }

    private static void RegisterSessionBlocks()
    {
        SessionsPage.Register();
        SessionsPast.Register();
    }

    // The first-start answer loaded modules: their pages, page actions and the settings rows on core pages (Keybinds,
    // Debug) join the menu.
    private void OnModulesLoaded(IReadOnlyList<string> ids)
    {
        if (ids.Contains("sessions")) RegisterSessionBlocks();
        RegisterPageActions(FeatureHost.All.Where(f => ids.Contains(f.Id)));
        SettingsContent.Reset();
        MenuTree.Invalidate();
    }

    public override void Update()
    {
        ModulesPage.OnUpdate();
        Ui.Menu.OnUpdate();
    }
    public override void OnGUI() => Ui.Menu.OnGUI();

    public override void Shutdown()
    {
        Modules.LoadedLate -= OnModulesLoaded;
        InputGate.MenuHitTest = null;
        Ui.Menu.Close();
        Popups.Reset();
        MenuTree.Invalidate();
        MenuSearch.Reset();
        MenuRoutes.Reset();
        PageView.Reset();
        TreeView.Reset();
        ProfilesPage.Reset();
        ModulesPage.Reset();
        SessionsPage.Reset();
        SessionsPast.Reset();
        TextCache.Clear();
        MenuBlocks.Clear();
        SettingsContent.Reset();
        LookSettings.Theme.Options = null;
        LookSettings.Theme.Notes = null;
        Diagnostics.ThemeProblems = null;
        InputGate.Menu = false;
        InputGate.MenuFolded = false;
        InputGate.MenuShared = false;
        GameState.UiTyping = false;
    }
}
