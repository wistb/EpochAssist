using EpochAssist.Core;

namespace EpochAssist.Ui;

// The menu's own entries, declared like a feature's: the Settings tab (Profiles, Interface with the General group's look,
// HUD and key sections, and Diagnostics) and the Session tab (current session, past sessions, history and export).
// Section ids, order and stored values are the General group's, unchanged.
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
            Title = "Profiles",
            Pages = { new MenuPage("Profiles") { Block = "profiles", Resettable = false } },
        },
        new MenuEntry(MenuCategory.Settings, order: 1)
        {
            Title = "Interface",
            Pages = { new MenuPage(HudSettings.Hud), Appearance(), new MenuPage(Section("keys")) },
        },
        new MenuEntry(MenuCategory.Sessions, order: 0)
        {
            Title = "Current session",
            Pages = { CurrentSession() },
        },
        new MenuEntry(MenuCategory.Sessions, order: 1)
        {
            Title = "Past sessions",
            Pages = { new MenuPage("Past sessions") { Rows = { new BlockRow(SessionsPage.RecentBlock) }, Resettable = false } },
        },
        new MenuEntry(MenuCategory.Sessions, order: 2)
        {
            Title = "History",
            Pages = { SessionHistory() },
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

    // The current session's stats, then the drops the loot filter showed.
    private static MenuPage CurrentSession()
    {
        var page = new MenuPage("Current session") { Resettable = false };
        page.Rows.AddRange(SessionsPage.CurrentRows());
        page.Rows.Add(new HeadingRow("Filtered drops"));
        page.Rows.Add(new BlockRow(SessionsPage.FilteredBlock));
        return page;
    }

    // What is recording and kept, the sessions section's switch and limits, then Export with its range.
    private static MenuPage SessionHistory()
    {
        var page = new MenuPage("History")
        {
            Rows =
            {
                new LiveText("Now", () => (Sessions.NowText(), Sessions.Recording ? StatusState.Active : StatusState.Off)),
                new LiveText("Kept", () => (Sessions.HistoryText(), null)),
            },
        };
        page.Rows.AddRange(SessionSettings.Section.Settings.Select(s => new SettingRow(s)));
        page.Rows.Add(new HeadingRow("Export"));
        page.Rows.Add(new HintRow(() => "Two files in UserData/EpochAssist/exports. Excel with a comma decimal mark: open them with Data › From Text/CSV."));
        page.Rows.Add(new ValueChoice("Range", () => SessionExport.PageRange, i => SessionExport.PageRange = i, () => SessionExport.PageRanges));
        page.Rows.Add(new ActionButton("Export CSV", "sessions.export"));
        page.Rows.Add(new LiveText("Last export", () => SessionExport.LastText.Length == 0 ? ("none yet", null) : (SessionExport.LastText, SessionExport.LastOk ? StatusState.Active : StatusState.Paused)));
        return page;
    }

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
