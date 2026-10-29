using EpochAssist.Core;

namespace EpochAssist.Ui;

// The menu's own entries, declared like a feature's: Config › General (Profiles, Keybinds, Mini HUD and Appearance),
// Config › Diagnostics and the Session tab (current session, past sessions, history and
// export). Section ids, order and stored values are the General group's, unchanged.
internal static class SettingsContent
{
    private static Section Section(string id) => Config.Group.Sections.First(s => s.Id == id);

    private static List<MenuEntry>? s_entries;

    public static IReadOnlyList<MenuEntry> Entries => s_entries ??= Build();

    /// <summary>Forgets the declarations (shutdown); they are made again on the next use.</summary>
    public static void Reset() => s_entries = null;

    private static List<MenuEntry> Build() => new()
    {
        new MenuEntry(MenuCategory.Combat, order: 80)
        {
            Title = "General",
            Pages =
            {
                new MenuPage("Profiles") { Block = "profiles", Resettable = false },
                Keybinds(),
                new MenuPage(HudSettings.Hud),
                Appearance(),
            },
        },
        new MenuEntry(MenuCategory.Sessions, order: 0)
        {
            Layout = MenuLayout.Wide,
            Title = "Current session",
            Pages = { CurrentSession() },
        },
        new MenuEntry(MenuCategory.Sessions, order: 1)
        {
            Layout = MenuLayout.Wide,
            Title = "Past sessions",
            Pages = { PastSessions() },
        },
        new MenuEntry(MenuCategory.Sessions, order: 2)
        {
            Layout = MenuLayout.Wide,
            Title = "History",
            Pages = { SessionHistory() },
        },
        new MenuEntry(MenuCategory.Combat, order: 81)
        {
            Title = "Diagnostics",
            Pages =
            {
                Overview(),
                new MenuPage("Modules") { Block = "diagnostics.features", Resettable = false },
                new MenuPage("Mods") { Block = "diagnostics.mods", Resettable = false },
                new MenuPage("Reports") { Block = "diagnostics.reports", Resettable = false },
                new MenuPage("Console") { Block = "console", Resettable = false },
                Debug(),
            },
        },
    };

    // Every key a player can set, under a heading per settings group (General first, then each feature's), with the
    // choices that go with them. The same settings stay on their own pages; both rows change one value. No page reset:
    // it would reach into every feature's settings.
    private static MenuPage Keybinds()
    {
        var page = new MenuPage("Keybinds") { Resettable = false };
        foreach (var group in SettingsRegistry.Groups)
        {
            var keys = group.All.Where(s => s is Key or Hotkey or Choice<UnityEngine.KeyCode> || s.WithKeybinds).ToList();
            if (keys.Count == 0) continue;
            page.Rows.Add(new HeadingRow(group.Name) { Key = "heading." + group.Id });
            page.Rows.AddRange(keys.Select(s => new SettingRow(s) { Key = $"{group.Id}.{s.Id}" }));
        }
        return page;
    }

    // Every debug switch in one place: each feature's Debug section and the settings that ask to be listed, under a
    // heading per settings group, with All on and All off for a full debug view. The same settings stay on their own
    // pages. No page reset, as on Keybinds.
    private static MenuPage Debug()
    {
        var page = new MenuPage("Debug") { Resettable = false };
        var switches = new List<Toggle>();
        var rows = new List<MenuRow>();
        foreach (var group in SettingsRegistry.Groups)
        {
            var debug = group.All.Where(s => s.Section.Id == "debug" || s.WithDebug).ToList();
            if (debug.Count == 0) continue;
            rows.Add(new HeadingRow(group.Name) { Key = "heading." + group.Id });
            rows.AddRange(debug.Select(s => new SettingRow(s) { Key = $"{group.Id}.{s.Id}" }));
            switches.AddRange(debug.OfType<Toggle>());
        }
        var dev = DevSwitches?.Invoke() ?? Array.Empty<ValueToggle>();
        if (dev.Count > 0)
        {
            rows.Add(new HeadingRow("Dev (dev builds only)") { Key = "heading.dev" });
            rows.AddRange(dev);
        }
        page.Rows.Add(new HintRow(() => "Everything the mod can draw or report to show what it is doing. Each switch is on its feature's page too."));
        page.Rows.AddRange(rows);
        page.Actions.Add(new ActionButton("All on", "debug.all-on") { OnClick = () => SetAll(switches, dev, true), Tip = "Turns every switch below on" });
        page.Actions.Add(new ActionButton("All off", "debug.all-off") { OnClick = () => SetAll(switches, dev, false), Tip = "Turns every switch below off" });
        return page;
    }

    /// <summary>Dev builds: switches for the dev overlays and logs, set by the dev bridge; listed on the Debug page.</summary>
    public static Func<IReadOnlyList<ValueToggle>>? DevSwitches { get; set; }

    private static void SetAll(List<Toggle> switches, IReadOnlyList<ValueToggle> dev, bool on)
    {
        foreach (var toggle in switches) toggle.Value = on;
        foreach (var toggle in dev) toggle.Set(on);
    }

    // The no-session hint, then the current session's dashboard.
    private static MenuPage CurrentSession()
    {
        var page = new MenuPage("Current session")
        {
            Resettable = false,
            Actions = { new ActionButton("New session", "sessions.split") { VisibleWhen = () => Sessions.Current != null, Tip = "Ends this session and starts a new one here" } },
        };
        page.Rows.AddRange(SessionsPage.CurrentRows());
        return page;
    }

    // The past sessions list with its filters and trend; an opened session's dashboard in place of it, Esc going back.
    private static MenuPage PastSessions()
    {
        var page = new MenuPage("Past sessions") { Resettable = false, Back = SessionsPast.Back };
        page.Rows.AddRange(SessionsPast.Rows());
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
        page.Rows.Add(new HeadingRow("Dashboard"));
        page.Rows.Add(new HintRow(() => "Figures in focus show as tiles and in the charts; the same chips are on Current session."));
        page.Rows.AddRange(SessionSettings.Dashboard.Settings.Select(s => new SettingRow(s)));
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
