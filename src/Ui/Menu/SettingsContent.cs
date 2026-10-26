using EpochAssist.Core;

namespace EpochAssist.Ui;

// The Settings tab, declared like a feature's entry: Interface (the General group's look, HUD and key sections),
// Profiles and Diagnostics. Section ids, order and stored values are the General group's, unchanged.
internal static class SettingsContent
{
    private static Section Section(string id) => Config.Group.Sections.First(s => s.Id == id);

    private static List<MenuEntry>? s_entries;

    public static IReadOnlyList<MenuEntry> Entries => s_entries ??= Build();

    /// <summary>Forgets the declarations (shutdown); they are made again on the next use.</summary>
    public static void Reset() => s_entries = null;

    private static List<MenuEntry> Build() => new()
    {
        new MenuEntry(MenuCategory.Settings, order: 0)
        {
            Title = "Interface",
            Pages = { new MenuPage(HudSettings.Hud), Appearance(), new MenuPage(Section("keys")) },
        },
        new MenuEntry(MenuCategory.Settings, order: 1)
        {
            Title = "Profiles",
            Pages = { new MenuPage("Profiles") { Block = "profiles", Resettable = false } },
        },
        new MenuEntry(MenuCategory.Settings, order: 2)
        {
            Title = "Diagnostics",
            Pages =
            {
                Overview(),
                new MenuPage("Features") { Block = "diagnostics.features", Resettable = false },
                new MenuPage("Mods") { Block = "diagnostics.mods", Resettable = false },
                new MenuPage("Reports") { Block = "diagnostics.reports", Resettable = false },
                new MenuPage("Console") { Block = "console", Resettable = false },
            },
        },
    };

    // The appearance section's settings, with the colour role preview under Colour vision.
    private static MenuPage Appearance()
    {
        var section = Section("appearance");
        var page = new MenuPage(section);
        foreach (var setting in section.Settings)
        {
            page.Rows.Add(new SettingRow(setting));
            if (setting == LookSettings.ColourVision) page.Rows.Add(new BlockRow(RolePreview.Block));
        }
        return page;
    }

    // The mod's summary, then Logging with the Diagnostics section's settings; Copy diagnostics in the title row.
    private static MenuPage Overview()
    {
        var page = new MenuPage("Overview")
        {
            Rows = { new BlockRow("diagnostics.overview"), new HeadingRow("Logging") },
            Actions = { new ActionButton("Copy diagnostics", "diagnostics.copy") },
        };
        page.Rows.AddRange(Config.Diagnostics.Settings.Select(s => new SettingRow(s)));
        return page;
    }
}
