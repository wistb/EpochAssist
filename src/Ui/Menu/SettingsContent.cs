using EpochAssist.Core;

namespace EpochAssist.Ui;

// The menu's own entries, declared like a feature's: Config › General (Profiles, Keybinds, Mini HUD and Appearance),
// Config › Sessions (figure colours, the mini HUD figures, history and export), Config › Diagnostics and the Session tab
// (current and past sessions). Section ids, order and stored values are the General group's, unchanged.
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
                MiniHud(),
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
        // Session settings live in Config, after Map and before General; the Session tab keeps the dashboards.
        new MenuEntry(MenuCategory.Combat, order: 60)
        {
            Title = "Sessions",
            Pages = { SessionColours(), SessionMiniHud(), SessionHistory() },
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

    // Between the Session tab's dashboards and their settings on Config › Sessions, both ways.
    private const string SessionTabPath = "session/current-session", SessionSettingsPath = "combat/sessions/colours";
    private static ActionButton ToSessionSettings(string page) => new("Settings", "sessions.settings." + page)
    {
        OnClick = () => Menu.Open(SessionSettingsPath), Tip = "Opens Config › Sessions: colours, mini HUD figures and history",
    };
    private static ActionButton ToSessionTab(string page) => new("Session tab", "sessions.tab." + page)
    {
        OnClick = () => Menu.Open(SessionTabPath), Tip = "Opens the Session tab's dashboard",
    };

    // The no-session hint, then the current session's dashboard.
    private static MenuPage CurrentSession()
    {
        var page = new MenuPage("Current session")
        {
            Resettable = false,
            Actions =
            {
                new ActionButton("New session", "sessions.split") { VisibleWhen = () => Sessions.Current != null, Tip = "Ends this session and starts a new one here" },
                ToSessionSettings("current"),
            },
        };
        page.Rows.AddRange(SessionsPage.CurrentRows());
        return page;
    }

    // The past sessions list with its filters and trend; an opened session's dashboard in place of it, Esc going back.
    private static MenuPage PastSessions()
    {
        var page = new MenuPage("Past sessions") { Resettable = false, Back = SessionsPast.Back, Actions = { ToSessionSettings("past") } };
        page.Rows.AddRange(SessionsPast.Rows());
        return page;
    }

    // What is recording and kept, the sessions section's switch and limits, then Export with its range.
    private static MenuPage SessionHistory()
    {
        var page = new MenuPage("History")
        {
            Actions = { ToSessionTab("history") },
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

    // The mini HUD's settings in their folds, with the pill's sections as one list in place of their switches: dragged
    // (or moved) into order, each with its switch. The session figures join the list when they go in the pill.
    private static MenuPage MiniHud()
    {
        var page = new MenuPage(HudSettings.Hud)
        {
            Actions =
            {
                new ActionButton("Session figures", "hud.sessionfigures")
                {
                    OnClick = () => Menu.Open("combat/sessions/mini-hud"), Tip = "Opens Config › Sessions › Mini HUD: the session figures by the pill",
                },
            },
        };
        string? fold = null;
        foreach (var setting in HudSettings.Hud.Settings)
        {
            if (setting.Fold != fold)
            {
                fold = setting.Fold;
                if (fold == HudSettings.SectionOrder.Fold)
                {
                    page.Rows.Add(new HeadingRow("Sections"));
                    page.Rows.Add(SectionList());
                }
                else if (fold != null)
                {
                    var name = fold;
                    page.Rows.Add(new HeadingRow(name) { VisibleWhen = () => HudSettings.Hud.Settings.Any(m => m.Fold == name && m.Visible) });
                }
            }
            if (setting.Fold != HudSettings.SectionOrder.Fold) page.Rows.Add(new SettingRow(setting));
        }
        return page;
    }

    // The mini HUD's sections in their order: every feature with a status, and the session figures (in the pill, or as a
    // card or tiles before or after it).
    private static IReadOnlyList<string> SectionIds()
    {
        var ids = FeatureHost.All.Where(f => f.StatusName != null).Select(f => f.Id).ToList();
        ids.Add(HudSettings.SessionSection);
        return HudSettings.SectionOrder.Sort(ids, id => id);
    }

    private static ListRow SectionList() => new("Order", () => SectionIds().Cast<object>().ToList(), item =>
    {
        string id = (string)item;
        var toggle = HudSettings.SectionToggle(id);
        string name = id == HudSettings.SessionSection ? "Session figures" : FeatureHost.All.FirstOrDefault(f => f.Id == id)?.StatusName ?? id;
        // Each section's switch: its own, the session figures' (the same one as on Session › Mini HUD), or locked on.
        var show = id == HudSettings.SessionSection ? SessionSettings.HudShow : toggle;
        return new MenuRow[]
        {
            new ValueText("Section", () => name) { Key = "name", Width = 160f },
            show != null
                ? new ValueToggle("Show", () => show.Value, v => show.Value = v) { Key = "show" }
                : new ValueToggle("Show", () => true, _ => { }) { Key = "show", EnabledWhen = () => false },
        };
    })
    {
        Key = "hud.sections",
        Move = (from, to) => HudSettings.SectionOrder.Move(SectionIds(), from, to),
    };

    // Each figure's colour, defaulting to the game's, behind the switch for colours at all.
    private static MenuPage SessionColours() => new(SessionSettings.Colours) { Actions = { ToSessionTab("colours") } };

    // The session figures on the mini HUD: switch, look and rate first, then a choice per figure. The pill's own place,
    // offsets and opacity stay on Config › General › Mini HUD.
    private static MenuPage SessionMiniHud()
    {
        var page = new MenuPage(SessionSettings.Hud)
        {
            Actions =
            {
                new ActionButton("Mini HUD settings", "sessions.hudsettings")
                {
                    OnClick = () => Menu.OpenSettingsTab(HudSettings.Hud), Tip = "Opens Config › General › Mini HUD: place, opacity and sections",
                },
                ToSessionTab("minihud"),
            },
        };
        // Only while it stops the figures from showing.
        page.Rows.Add(new HintRow(() => "Session history is off, so there are no figures to show. It's on History.") { VisibleWhen = () => !SessionSettings.History.Value });
        var settings = SessionSettings.Hud.Settings;
        page.Rows.AddRange(settings.Where(s => s.Fold == null).Select(s => new SettingRow(s)));
        page.Rows.Add(new HeadingRow("Figures"));
        page.Rows.AddRange(settings.Where(s => s.Fold != null).Select(s => new SettingRow(s)));
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
