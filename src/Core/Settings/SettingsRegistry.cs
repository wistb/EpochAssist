namespace EpochAssist.Core;

/// <summary>
/// A HUD quick toggle: a switch with a label and an action (setting.toggle.[group].[id]) the HUD, the footer, hotkeys
/// and the console run. A Toggle with a QuickLabel is one; a setting may offer others (a condition list's named entries).
/// </summary>
internal interface IQuickSwitch
{
    string Label { get; }
    string? Tip { get; }
    bool Value { get; }
    void Set(bool on);
    string ActionId { get; }
}

/// <summary>
/// Every registered group. Groups is all of them, for storage (profiles, exports, migrations); Shown is the ones a
/// player sees and runs (menu pages, keys, search, quick toggles, actions, console lookup): core groups and the groups and
/// sections of loaded modules.
/// </summary>
internal static class SettingsRegistry
{
    private static readonly List<SettingsGroup> s_groups = new();

    public static IReadOnlyList<SettingsGroup> Groups => s_groups;

    /// <summary>Changes whenever Shown can: a group registers, or a module loads late.</summary>
    public static int Generation { get; private set; }

    /// <summary>Groups with something shown: not drawn by a page of their own, owned by the core or a loaded module, or
    /// holding a section that is.</summary>
    public static IEnumerable<SettingsGroup> Shown => s_groups.Where(g => !g.Hidden && g.Sections.Any(IsShown));

    /// <summary>A group's sections that show: its module's and its own owner's loaded (or core).</summary>
    public static IEnumerable<Section> ShownSections(SettingsGroup group) => group.Sections.Where(IsShown);

    /// <summary>The shown sections' settings, in order.</summary>
    public static IEnumerable<Setting> ShownSettings(SettingsGroup group) => ShownSections(group).SelectMany(s => s.Settings);

    public static bool IsShown(Section section) => !section.Group.Hidden && (section.Module == null || Modules.Loaded(section.Module));

    /// <summary>Every shown quick switch, in declaration order; a setting's own switches come at its place.</summary>
    public static IEnumerable<IQuickSwitch> QuickToggles => Shown.SelectMany(ShownSettings).SelectMany(s => s.QuickSwitches);

    // Each shown section gets a reset action and its quick toggles a toggle action, owned by the settings layer; an
    // unloaded module's get theirs if it loads late (RegisterActions).
    internal static void Add(SettingsGroup group)
    {
        if (s_groups.Contains(group)) return;
        s_groups.Add(group);
        Generation++;
        AddActions(group.Sections.Where(IsShown).ToList());
    }

    /// <summary>The actions of a module that loaded after its settings registered (the first-start answer).</summary>
    public static void RegisterActions(string module)
    {
        Generation++;
        foreach (var group in s_groups) AddActions(group.Sections.Where(s => s.Module == module && IsShown(s)).ToList());
    }

    private static void AddActions(List<Section> sections)
    {
        foreach (var section in sections)
        {
            Actions.Register("settings", ResetAction(section), $"Reset {section.Title}", () =>
            {
                section.Reset();
                Toasts.Show("settings", $"{section.Title} reset to defaults.", ToastKind.Info);
            });
        }
        foreach (var quick in sections.SelectMany(s => s.Settings).SelectMany(s => s.QuickSwitches))
        {
            Actions.Register("settings", quick.ActionId, quick.Label, () => quick.Set(!quick.Value), () => quick.Value);
        }
    }

    public static string ResetAction(Section section) => $"settings.reset.{Key(section.Group)}.{Slug(section.Id)}";

    public static string ToggleAction(Toggle toggle) => ToggleAction(toggle.Section.Group, toggle.Id);

    /// <summary>A quick switch's action id: setting.toggle.[group].[id].</summary>
    public static string ToggleAction(SettingsGroup group, string id) => $"setting.toggle.{Key(group)}.{Slug(id)}";

    private static string Key(SettingsGroup group) => Slug(group.ActionKey.Length > 0 ? group.ActionKey : group.Id);

    private static string Slug(string text) => new(text.ToLowerInvariant().Where(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9')).ToArray());
}
